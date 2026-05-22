using System;
using System.Collections.Generic;
using System.IO;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Minimal MPEG-1/2 Program Stream muxer for combining one MPEG video
    /// elementary stream and one MPEG audio elementary stream into a single
    /// byte stream that FFmpeg's <c>mpeg</c> demuxer can consume.
    ///
    /// Why this exists: the WMC recorded-TV path delivers
    /// <c>vnd.ms.wm-MPV</c> video + <c>vnd.ms.wm-MPA</c> audio as separate
    /// RTP streams with separate WMRTP wrappers. Each MAU is a complete
    /// MPEG access unit (start-code prefixed) carrying a 90 kHz RTP
    /// timestamp. Feeding them to FFME as two independent elements gives
    /// no A/V sync; muxing them into MPEG-PS gives FFME a single stream
    /// whose internal clock keeps video + audio aligned at decoder time.
    ///
    /// The implementation follows ISO/IEC 13818-1 §2.5 (Program Stream)
    /// but only emits the constructs FFmpeg actually parses:
    ///   * Pack header (Table 2-33, MPEG-2 form — 14 bytes)
    ///   * System header (Table 2-32 — emitted only on the first pack;
    ///     declares stream_id list + buffer bounds)
    ///   * PES packets (Table 2-17/2-21 — one per access unit; PTS in
    ///     header on every video keyframe and every audio frame).
    ///
    /// What we deliberately do NOT do:
    ///   * Program Stream Map (PSM) — not required for plain MPEG
    ///     audio/video streams with standard stream_ids.
    ///   * Accurate SCR — set to current PTS minus a small lead; FFmpeg
    ///     does not validate strict conformance.
    ///   * Variable-length PES splitting — video access units commonly
    ///     exceed PES_packet_length max (65535B) so the muxer emits
    ///     multiple PES packets per access unit when needed, with PTS
    ///     only on the first (per spec).
    ///
    /// Threading: the muxer is single-threaded by contract. Callers
    /// serialise access (in our case, both the audio and video event
    /// handlers run on the same RTSP listener thread).
    /// </summary>
    internal sealed class MpegPsMuxer {

        // Standard PS stream IDs (ISO 13818-1 Table 2-18).
        public const byte VideoStreamId = 0xE0;   // MPEG-1/2 video, first stream
        public const byte AudioStreamId = 0xC0;   // MPEG-1/2 audio, first stream

        // PES packet length field is uint16. Practical chunk size for
        // splitting large video access units. We pick something well below
        // 0xFFFF to leave room for the 14-byte PES header. 32 KB keeps the
        // PS packetisation overhead negligible (~0.1%) without hitting the
        // limit on any realistic frame.
        private const int MaxPesPayloadBytes = 32 * 1024 - 32;

        private bool _systemHeaderEmitted;

        // PTS anchoring: per-stream. Audio and video RTP streams carry
        // INDEPENDENT 32-bit timestamp epochs — each starts at a random
        // value chosen by the server. Anchoring on the first MAU of
        // whichever stream arrived first and using that base for BOTH
        // streams produced garbage cross-stream offsets (audio at PTS=0
        // but video at PTS=video_rtpTs - audio_rtpTs, which has no
        // physical meaning). We now track one base per stream so each
        // stream's PTS counts from 0 at its own first MAU.
        //
        // Both streams start at PTS=0 wall-clock-aligned to PLAY time;
        // any genuine inter-stream offset is small (tens of ms typically)
        // compared to the seconds-of-drift the old single-anchor scheme
        // produced.
        private bool _videoAnchored;
        private uint _videoBaseTs;
        private bool _audioAnchored;
        private uint _audioBaseTs;

        // Most recent PTS we emitted per stream (33-bit, 90 kHz ticks).
        // Exposed for diagnostics so the host can spot A/V drift in long
        // sessions by watching the audio vs video PTS difference grow.
        private long _latestVideoPtsTicks;
        private long _latestAudioPtsTicks;

        // ----- Diagnostic accessors ---------------------------------------

        public bool VideoAnchored => _videoAnchored;
        public bool AudioAnchored => _audioAnchored;
        public uint VideoBaseTs   => _videoBaseTs;
        public uint AudioBaseTs   => _audioBaseTs;
        public long LatestVideoPtsTicks => _latestVideoPtsTicks;
        public long LatestAudioPtsTicks => _latestAudioPtsTicks;
        /// <summary>Convenience: latest video PTS as media-time milliseconds.</summary>
        public double LatestVideoPtsMs => _latestVideoPtsTicks / 90.0;
        /// <summary>Convenience: latest audio PTS as media-time milliseconds.</summary>
        public double LatestAudioPtsMs => _latestAudioPtsTicks / 90.0;

        // SCR runs a little behind the most recent PTS we emitted, so
        // FFmpeg's PS demuxer is content that bytes arrive on time. Stored
        // as a 33-bit count of 90 kHz ticks. MONOTONIC — when video and
        // audio MAUs interleave their per-stream PTS-derived candidate
        // SCR values can land out of order; we always pick max(prev, new)
        // so libav never sees SCR jump backward (which it treats as a
        // discontinuity and may flush decoder state on).
        private long _scrTicks;

        // ----- Public API ---------------------------------------------------

        /// <summary>
        /// Mux one MPEG video access unit. Returns the bytes to push into the
        /// FFME input stream. Caller hands raw access-unit bytes (the MAU as
        /// emitted by <see cref="WmrptVideoDepacketizer"/>) plus the 90 kHz
        /// RTP timestamp from that MAU.
        /// </summary>
        public byte[] MuxVideoAccessUnit(byte[] frameBytes, uint rtpTs, bool isKeyframe) {
            if (!_videoAnchored) {
                _videoBaseTs   = rtpTs;
                _videoAnchored = true;
            }
            long pts33 = ComputePts33(rtpTs, _videoBaseTs);
            _latestVideoPtsTicks = pts33;
            return BuildPackWithPes(VideoStreamId, frameBytes, pts33, includeSystemHeader: !_systemHeaderEmitted);
        }

        /// <summary>
        /// Mux one MPEG audio frame (or a small run of frames). 90 kHz RTP
        /// timestamp from the source MAU.
        /// </summary>
        public byte[] MuxAudioFrame(byte[] frameBytes, uint rtpTs) {
            if (!_audioAnchored) {
                _audioBaseTs   = rtpTs;
                _audioAnchored = true;
            }
            long pts33 = ComputePts33(rtpTs, _audioBaseTs);
            _latestAudioPtsTicks = pts33;
            return BuildPackWithPes(AudioStreamId, frameBytes, pts33, includeSystemHeader: !_systemHeaderEmitted);
        }

        /// <summary>
        /// Explicitly set the per-stream PTS bases from RTSP's PLAY-response
        /// RTP-Info header. The server states (per-stream) which RTP
        /// timestamp corresponds to the first sequence number it will send;
        /// using those values as our bases means both streams' PTS = 0
        /// represents the SAME media moment per the server's authoritative
        /// declaration, instead of "whenever the first MAU happened to
        /// reach our wall clock" (which can differ between streams by
        /// tens of ms and produce visible A/V drift).
        ///
        /// Call once after PLAY before any Mux calls flow through. Calls
        /// after the first MAU already produced a packet for that stream
        /// are ignored — repositioning the base mid-stream would break
        /// monotonicity of the PES PTS series we've already emitted.
        /// </summary>
        public void SetAudioBaseTs(uint rtpTs) {
            if (_audioAnchored) return;
            _audioBaseTs   = rtpTs;
            _audioAnchored = true;
        }

        public void SetVideoBaseTs(uint rtpTs) {
            if (_videoAnchored) return;
            _videoBaseTs   = rtpTs;
            _videoAnchored = true;
        }

        /// <summary>
        /// Compute a 33-bit MPEG PTS from a 32-bit RTP timestamp + base.
        /// Handles the case where <paramref name="rtpTs"/> is slightly
        /// SMALLER than the base (out-of-order RTP delivery, or a base
        /// taken from RTP-Info that the very first received packet is
        /// fractionally behind): clamps to 0 instead of wrapping into the
        /// huge-positive range (which libav reads as a multi-billion-tick
        /// DTS and logs as "out of order"). For genuinely large positive
        /// deltas we mask to 33 bits per the PS spec.
        /// </summary>
        private static long ComputePts33(uint rtpTs, uint baseTs) {
            // Cast to SIGNED int32 so we can detect "slightly before base".
            // Reasonable wrap horizon: anything more than ~half the uint32
            // range below the base is treated as wraparound (i.e. we've
            // been running long enough that the unsigned counter wrapped
            // past the base), not as out-of-order delivery.
            int signedDelta = unchecked((int)(rtpTs - baseTs));
            if (signedDelta < 0) {
                // Trivially small negative — clamp to 0. PES timestamps
                // can't go backwards, and the actual time difference is
                // tiny enough to not affect playback noticeably.
                return 0L;
            }
            return ((long)(uint)signedDelta) & 0x1FFFFFFFFL;
        }

        /// <summary>
        /// Emit the MPEG-PS End Code (0x000001B9). Optional — FFmpeg copes
        /// with abrupt EOF — but cleaner. Called from Complete().
        /// </summary>
        public byte[] EndCode() => new byte[] { 0x00, 0x00, 0x01, 0xB9 };

        // ----- Internals ----------------------------------------------------

        private byte[] BuildPackWithPes(byte streamId, byte[] payload, long pts33,
                                        bool includeSystemHeader) {
            // SCR sits a few ms behind the current PTS so libav's PS
            // demuxer sees bytes as arriving "on time" rather than late.
            // 9000 ticks = 100 ms.
            //
            // Monotonicity matters: with per-stream PTS anchoring, audio
            // and video MAUs interleave with independent PTS series. The
            // raw candidate SCR (pts - 100ms) for an audio packet that
            // follows a video packet can be lower than the previous video
            // SCR even though both streams are advancing correctly. libav
            // treats backward SCR as a discontinuity and may flush state.
            // Clamping with max(_scrTicks, candidate) preserves the
            // increasing invariant.
            long candidate = pts33 - 9000;
            if (candidate < 0) candidate = 0;
            if (candidate > _scrTicks) _scrTicks = candidate;

            using (var ms = new MemoryStream(payload.Length + 256)) {
                WritePackHeader(ms, _scrTicks);
                if (includeSystemHeader) {
                    WriteSystemHeader(ms);
                    _systemHeaderEmitted = true;
                }

                // Video access units commonly exceed the PES length field's
                // 16-bit max — split into multiple PES packets. Per ISO
                // 13818-1, PTS is sent only on the first PES packet of an
                // access unit; subsequent packets carry no PTS.
                int offset = 0;
                bool firstChunk = true;
                while (offset < payload.Length) {
                    int chunkLen = Math.Min(MaxPesPayloadBytes, payload.Length - offset);
                    WritePesPacket(ms, streamId, payload, offset, chunkLen,
                                   firstChunk ? (long?)pts33 : null);
                    offset += chunkLen;
                    firstChunk = false;
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// MPEG-2 Pack header (ISO 13818-1 Table 2-33). 14 bytes:
        ///   00 00 01 BA                                     start code
        ///   2 bits '01' | 3 SCR_base[32:30] | 1 marker
        ///   15 SCR_base[29:15] | 1 marker
        ///   15 SCR_base[14:0]  | 1 marker
        ///   9  SCR_ext[8:0]    | 1 marker
        ///   22 program_mux_rate | 2 marker
        ///   5 reserved | 3 pack_stuffing_length
        /// </summary>
        private static void WritePackHeader(MemoryStream ms, long scr33) {
            const int ProgramMuxRate = 50_000;  // 50000 * 50 bytes/sec ≈ 2.5 MB/s (plenty)

            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x01); ms.WriteByte(0xBA);

            // Pack SCR is encoded in 90 kHz "base" + 27 MHz "extension" form.
            // We don't have sub-90kHz precision so ext stays 0.
            long scrBase = scr33 & 0x1FFFFFFFFL;
            // byte 4: '01' | scrBase[32..30] | '1'
            byte b4 = (byte)(0x40 | (((scrBase >> 30) & 0x7) << 3) | 0x04 | ((scrBase >> 28) & 0x3));
            ms.WriteByte(b4);
            // byte 5: scrBase[27..20]
            ms.WriteByte((byte)((scrBase >> 20) & 0xFF));
            // byte 6: scrBase[19..15] | '1' | scrBase[14..13]
            byte b6 = (byte)((((scrBase >> 15) & 0x1F) << 3) | 0x04 | ((scrBase >> 13) & 0x3));
            ms.WriteByte(b6);
            // byte 7: scrBase[12..5]
            ms.WriteByte((byte)((scrBase >> 5) & 0xFF));
            // byte 8: scrBase[4..0] | '1' | scrExt[8..7]  (ext = 0)
            byte b8 = (byte)((((scrBase) & 0x1F) << 3) | 0x04);
            ms.WriteByte(b8);
            // byte 9: scrExt[6..0] | '1'  (ext = 0)
            ms.WriteByte(0x01);
            // bytes 10-12: program_mux_rate[21..0] | '11'
            ms.WriteByte((byte)((ProgramMuxRate >> 14) & 0xFF));
            ms.WriteByte((byte)((ProgramMuxRate >>  6) & 0xFF));
            ms.WriteByte((byte)(((ProgramMuxRate & 0x3F) << 2) | 0x03));
            // byte 13: reserved(5)=1 | pack_stuffing_length(3)=0
            ms.WriteByte(0xF8);
        }

        /// <summary>
        /// MPEG-1 System header (ISO 13818-1 Table 2-32). Declares which
        /// stream_ids will appear in the program stream and gives FFmpeg
        /// per-stream P-STD buffer sizes so it can size its decode buffers.
        /// </summary>
        private static void WriteSystemHeader(MemoryStream ms) {
            // Two stream entries: video (0xE0) + audio (0xC0). Each is 3 bytes.
            const int StreamEntries = 2;
            const int HeaderLengthAfterField = 6 + StreamEntries * 3;

            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x01); ms.WriteByte(0xBB);
            ms.WriteByte((byte)((HeaderLengthAfterField >> 8) & 0xFF));
            ms.WriteByte((byte)(HeaderLengthAfterField & 0xFF));

            // rate_bound (22 bits) + marker_bit:
            //   rate_bound = program_mux_rate; use the same 50000 value.
            const int RateBound = 50_000;
            ms.WriteByte((byte)(0x80 | ((RateBound >> 15) & 0x7F)));  // marker | rb[21:15]
            ms.WriteByte((byte)((RateBound >> 7) & 0xFF));            // rb[14:7]
            ms.WriteByte((byte)(((RateBound & 0x7F) << 1) | 0x01));   // rb[6:0] | marker

            // audio_bound (6 bits) | fixed_flag (1) | CSPS_flag (1)
            //   audio_bound = 1 (max 1 audio stream simultaneously decoded)
            ms.WriteByte(0x04);  // 000001 | 0 | 0

            // system_audio_lock_flag (1) | system_video_lock_flag (1)
            //   | marker (1) | video_bound (5)
            //   video_bound = 1
            ms.WriteByte(0x21);  // 0 0 1 00001

            // packet_rate_restriction_flag (1) | reserved (7) — all 1s
            ms.WriteByte(0x7F);

            // Per-stream entries: stream_id (8 bits) | '11' (2 bits)
            // | P-STD_buffer_bound_scale (1) | P-STD_buffer_size_bound (13 bits)
            //
            // Video: scale=1 (units of 1024B), size=400 → ~400 KB. Big
            //        enough for a HD I-frame.
            ms.WriteByte(VideoStreamId);
            ms.WriteByte((byte)(0xC0 | 0x20 | ((400 >> 8) & 0x1F)));   // 11 | scale=1 | size_high
            ms.WriteByte((byte)(400 & 0xFF));
            // Audio: scale=0 (units of 128B), size=32 → ~4 KB. One MPEG
            //        audio frame is ~384–1152B, generous margin.
            ms.WriteByte(AudioStreamId);
            ms.WriteByte((byte)(0xC0 | 0x00 | ((32 >> 8) & 0x1F)));
            ms.WriteByte((byte)(32 & 0xFF));
        }

        /// <summary>
        /// One PES packet (ISO 13818-1 Table 2-17/2-21). PTS optional.
        /// </summary>
        private static void WritePesPacket(MemoryStream ms, byte streamId,
                                           byte[] payload, int payloadOffset, int payloadLength,
                                           long? pts33) {
            // PES header structure (MPEG-2 form, Table 2-21):
            //   00 00 01 SS          packet_start + stream_id
            //   LL LL                PES_packet_length (covers everything after this field)
            //   10                   '10' marker (2 bits) + scrambling(2) + priority(1)
            //                        + data_align(1) + copyright(1) + original(1)  → byte = 0x80
            //   FF                   PTS_DTS_flags(2) + ESCR_flag(1) + ES_rate(1)
            //                        + DSM_trick(1) + add_copy(1) + CRC_flag(1) + ext_flag(1)
            //   HL                   PES_header_data_length
            //   <PTS 5B if pts_dts_flags top bit set>
            //   <payload>

            int ptsHeaderLen = pts33.HasValue ? 5 : 0;
            int headerDataLength = ptsHeaderLen;
            int pesPacketLength  = 2 /* flags + flag2 */ + 1 /* header_data_length */ + headerDataLength + payloadLength;

            ms.WriteByte(0x00); ms.WriteByte(0x00); ms.WriteByte(0x01);
            ms.WriteByte(streamId);
            ms.WriteByte((byte)((pesPacketLength >> 8) & 0xFF));
            ms.WriteByte((byte)(pesPacketLength & 0xFF));

            // byte 6: '10' | scrambling=00 | priority=0 | data_align=1
            //         | copyright=0 | original=0
            //   data_alignment_indicator=1 tells the decoder this packet
            //   starts at an access-unit boundary (correct: depacketizer
            //   only emits complete MAUs).
            ms.WriteByte(0x84);

            // byte 7: PTS_DTS_flags=10 (PTS-only) or 00 (no PTS)
            //         all other optional fields off.
            byte flags2 = pts33.HasValue ? (byte)0x80 : (byte)0x00;
            ms.WriteByte(flags2);

            ms.WriteByte((byte)headerDataLength);

            if (pts33.HasValue) {
                WritePts(ms, pts33.Value, prefix4Bits: 0x2);  // '0010' = PTS-only
            }

            ms.Write(payload, payloadOffset, payloadLength);
        }

        /// <summary>
        /// Write the 5-byte PTS field (ISO 13818-1 §2.4.3.6). Bits are
        /// interleaved with marker bits per spec.
        /// </summary>
        private static void WritePts(MemoryStream ms, long pts33, byte prefix4Bits) {
            // Layout:
            //   [prefix4][pts[32..30]][m] [pts[29..22]] [pts[21..15]][m]
            //     [pts[14..7]] [pts[6..0]][m]
            byte b0 = (byte)(((prefix4Bits & 0xF) << 4) | (((pts33 >> 30) & 0x7) << 1) | 0x1);
            byte b1 = (byte)((pts33 >> 22) & 0xFF);
            byte b2 = (byte)((((pts33 >> 15) & 0x7F) << 1) | 0x1);
            byte b3 = (byte)((pts33 >>  7) & 0xFF);
            byte b4 = (byte)((((pts33      ) & 0x7F) << 1) | 0x1);
            ms.WriteByte(b0); ms.WriteByte(b1); ms.WriteByte(b2);
            ms.WriteByte(b3); ms.WriteByte(b4);
        }
    }
}
