using System;
using System.Collections.Generic;
using System.IO;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// On-the-fly MPEG-2 Transport Stream muxer for WMC RTSP playback.
    ///
    /// Replaces the dual-FFME-instance hack with a single MPEG-TS stream
    /// that FFmpeg's libavformat demuxes natively — giving us one master
    /// clock for A/V sync. Designed to be the long-term unified muxer
    /// for every codec WMC negotiates (H264, MPV, MPA, AC3, LPCM, ...);
    /// new codecs are added by mapping them to MPEG-2 systems stream
    /// types in the PMT.
    ///
    /// Stream layout (compile-time fixed for now):
    ///   PID 0x0000  PAT     (Program Association Table)
    ///   PID 0x0100  PMT     (Program Map Table for program 1)
    ///   PID 0x0101  Video   (stream_type per ConfigureVideo)
    ///   PID 0x0102  Audio   (stream_type per ConfigureAudio)
    ///
    /// PCR is carried on the video PID (as is conventional). PAT and PMT
    /// are re-emitted every ~500 ms so libav's late-joiner probe can
    /// find them.
    ///
    /// PTS values come from the per-stream RTP timestamps. We anchor at
    /// the RTP-Info <c>rtptime=</c> value supplied by the PLAY response
    /// (per stream) so audio and video PTS reference the same wall
    /// instant — exactly the same anchoring trick the MpegPsMuxer uses.
    /// </summary>
    public sealed class MpegTsMuxer {

        // ---- Wire constants ---------------------------------------------

        private const byte TS_SYNC_BYTE   = 0x47;
        private const int  TS_PACKET_SIZE = 188;

        public const ushort PID_PAT   = 0x0000;
        public const ushort PID_PMT   = 0x0100;
        public const ushort PID_VIDEO = 0x0101;
        public const ushort PID_AUDIO = 0x0102;

        // MPEG-2 Systems stream type values (ISO/IEC 13818-1 Table 2-29
        // plus a few user-private extensions used by Blu-ray / ATSC):
        private const byte STREAM_TYPE_MPEG1_VIDEO     = 0x01;
        private const byte STREAM_TYPE_MPEG2_VIDEO     = 0x02;
        private const byte STREAM_TYPE_MPEG1_AUDIO     = 0x03; // MPA Layer I/II
        private const byte STREAM_TYPE_MPEG2_AUDIO     = 0x04; // MPA Layer II/III + extensions
        private const byte STREAM_TYPE_PES_PRIVATE     = 0x06; // Per-PMT private data — we use this for raw LE PCM
        private const byte STREAM_TYPE_H264            = 0x1B;
        private const byte STREAM_TYPE_BLURAY_PCM      = 0x80;
        private const byte STREAM_TYPE_AC3             = 0x81;

        // PES stream_id ranges (ISO/IEC 13818-1 Table 2-22).
        private const byte STREAM_ID_PRIVATE_1 = 0xBD;
        private const byte STREAM_ID_VIDEO_E0  = 0xE0;
        private const byte STREAM_ID_AUDIO_C0  = 0xC0;

        // ---- Configuration ----------------------------------------------

        public enum AudioCodec {
            /// <summary>
            /// Raw little-endian PCM samples in a PES-private (stream_type
            /// 0x06) elementary stream. The PMT carries a registration
            /// descriptor hinting at "VC-1" (per ATSC) so libav at least
            /// surfaces the PID; consumers may need to override the codec
            /// via FFmpeg options. Cheap + zero byte-swapping.
            /// </summary>
            LpcmPesPrivate,
            /// <summary>
            /// Blu-ray LPCM: stream_type 0x80, 4-byte frame header + BE
            /// samples. libav decodes natively as <c>pcm_bluray</c>.
            /// </summary>
            BluRayLpcm,
            /// <summary>MPEG-1 Audio Layer I/II/III (stream_type 0x03).</summary>
            MpegAudio,
            /// <summary>AC-3 (stream_type 0x81).</summary>
            Ac3,
        }

        public enum VideoCodec {
            H264,
            Mpeg1Video,
            Mpeg2Video,
        }

        // ---- Internal state ---------------------------------------------

        // Codec + format
        private AudioCodec _audioCodec = AudioCodec.LpcmPesPrivate;
        private VideoCodec _videoCodec = VideoCodec.H264;
        private int _pcmSampleRate     = 48000;
        private int _pcmChannels       = 2;
        private int _pcmBitsPerSample  = 16;

        // PTS anchoring (RTP-Info → first-MAU offset; per-stream RTP clock).
        // The RTP timestamp clock comes from the SDP rtpmap entry: e.g.
        // vnd.ms.wm-MPA/90000 = 90 kHz, vnd.ms.wm-MPV/90000 = 90 kHz,
        // x-wmf-pf/1000 = 1 kHz (millisecond ticks). We convert each
        // stream's delta-from-base to the canonical 90 kHz MPEG-TS PTS
        // clock so PCR, audio PES PTS, and video PES PTS all share the
        // same units regardless of the wire clock.
        private bool _audioAnchored;
        private bool _videoAnchored;
        private uint _audioBaseTs;
        private uint _videoBaseTs;
        private uint _audioClockHz = 90000;   // default; overridden via SetAudioClockRate
        private uint _videoClockHz = 90000;
        private double _latestAudioPtsMs;
        private double _latestVideoPtsMs;

        // Output-PTS offsets in 90 kHz ticks. Stay at 0 in normal
        // sessions; bumped by Reanchor to preserve monotonicity across
        // server-side seeks / rate changes. See MpegPsMuxer for the
        // full rationale — the TS path needs the same machinery
        // because libav's TS demuxer is just as strict about DTS
        // monotonicity as the PS demuxer.
        private ulong _audioOutputPtsOffset90k;
        private ulong _videoOutputPtsOffset90k;

        // Continuity counter (4 bits) per PID
        private byte _ccVideo;
        private byte _ccAudio;
        private byte _ccPat;
        private byte _ccPmt;

        // PCR / table re-emit pacing
        private double _lastPatPmtPtsMs = double.MinValue;
        private double _lastPcrPtsMs    = double.MinValue;
        private const double PatPmtIntervalMs = 500.0;   // re-emit every ~0.5s
        private const double PcrIntervalMs    = 100.0;   // PCR no less often than 100ms (per spec)

        // ---- Public configuration ---------------------------------------

        public void ConfigureVideo(VideoCodec codec) { _videoCodec = codec; }

        public void ConfigureAudio(AudioCodec codec, int sampleRate, int channels, int bitsPerSample) {
            _audioCodec       = codec;
            _pcmSampleRate    = sampleRate;
            _pcmChannels      = channels;
            _pcmBitsPerSample = bitsPerSample;
        }

        /// <summary>
        /// One-shot per-stream anchor from the first PLAY response's
        /// RTP-Info. Symmetric to MpegPsMuxer: subsequent calls are
        /// silently ignored — the muxer commits to the first base it
        /// receives and stays on it for the duration of the session.
        /// Mid-session re-anchoring (after a server seek or rate
        /// change) goes through <see cref="Reanchor"/> instead, which
        /// also takes care of output-PTS monotonicity.
        /// </summary>
        public void SetAudioBaseTs(uint rtptime) {
            if (_audioAnchored) return;
            _audioBaseTs = rtptime;
            _audioAnchored = true;
        }
        public void SetVideoBaseTs(uint rtptime) {
            if (_videoAnchored) return;
            _videoBaseTs = rtptime;
            _videoAnchored = true;
        }

        /// <summary>
        /// Re-anchor the per-stream PTS bases mid-session after a
        /// server PLAY-with-Range / PLAY-with-Scale. Mirrors
        /// <see cref="MpegPsMuxer.Reanchor"/> — overwrites the base
        /// AND bumps an output-PTS offset so the next computed PTS
        /// picks up just past the last emitted PTS rather than
        /// regressing toward zero. Pass either RTP timestamp as
        /// <c>null</c> to leave that stream untouched. Bridge gap
        /// is ~100 ms.
        /// </summary>
        public void Reanchor(uint? newAudioRtpTs, uint? newVideoRtpTs) {
            const ulong BridgeGap90k = 9000;  // 100 ms @ 90 kHz

            if (newVideoRtpTs.HasValue) {
                _videoBaseTs = newVideoRtpTs.Value;
                _videoAnchored = true;
                // Latest video PTS is stored as milliseconds; convert
                // back to 90 kHz ticks before adding the bridge.
                _videoOutputPtsOffset90k =
                    (ulong)(long)(_latestVideoPtsMs * 90.0) + BridgeGap90k;
            }
            if (newAudioRtpTs.HasValue) {
                _audioBaseTs = newAudioRtpTs.Value;
                _audioAnchored = true;
                _audioOutputPtsOffset90k =
                    (ulong)(long)(_latestAudioPtsMs * 90.0) + BridgeGap90k;
            }

            // Force a fresh PAT/PMT at the next access unit so libav
            // re-reads stream tables (cheap, ~200 B) after the seek.
            _lastPatPmtPtsMs = double.MinValue;
            _lastPcrPtsMs    = double.MinValue;
        }

        /// <summary>
        /// Set the per-stream RTP timestamp clock rate (Hz). Determined
        /// by the SDP rtpmap entry — must be set BEFORE the first
        /// MuxAudioFrame/MuxVideoAccessUnit so PTS arithmetic uses the
        /// right divisor. 90000 (90 kHz) for vnd.ms.wm-MPA/MPV; 1000
        /// (1 kHz) for x-wmf-pf; 48000 for vnd.ms.wm-AC3 etc.
        /// </summary>
        public void SetAudioClockRate(uint hz) { if (hz > 0) _audioClockHz = hz; }
        public void SetVideoClockRate(uint hz) { if (hz > 0) _videoClockHz = hz; }

        // ---- Diagnostics (parallels MpegPsMuxer) ------------------------

        public bool AudioAnchored => _audioAnchored;
        public bool VideoAnchored => _videoAnchored;
        public uint AudioBaseTs   => _audioBaseTs;
        public uint VideoBaseTs   => _videoBaseTs;
        public double LatestAudioPtsMs => _latestAudioPtsMs;
        public double LatestVideoPtsMs => _latestVideoPtsMs;

        // ---- Public mux entry points ------------------------------------

        /// <summary>
        /// Wrap a video access unit (e.g. one H264 NAL group ending in an
        /// access-unit-delimiter or one MPEG-1/2 video picture) into MPEG-TS
        /// packets. Returns the concatenated 188-byte packets, including
        /// any PAT/PMT/PCR adaptation overhead. The caller pushes the
        /// returned bytes into the FFME producer.
        /// </summary>
        public byte[] MuxVideoAccessUnit(byte[] accessUnit, uint rtpTimestamp, bool isKeyframe) {
            if (accessUnit == null || accessUnit.Length == 0) return Array.Empty<byte>();
            if (!_videoAnchored) { _videoBaseTs = rtpTimestamp; _videoAnchored = true; }
            ulong pts90k = (ComputePts90k(rtpTimestamp, _videoBaseTs, _videoClockHz)
                            + _videoOutputPtsOffset90k) & 0x1FFFFFFFFUL;
            _latestVideoPtsMs = pts90k / 90.0;

            using (var ms = new MemoryStream()) {
                MaybeEmitTables(ms);
                bool emitPcr = isKeyframe
                            || _lastPcrPtsMs == double.MinValue
                            || _latestVideoPtsMs - _lastPcrPtsMs >= PcrIntervalMs;
                if (emitPcr) _lastPcrPtsMs = _latestVideoPtsMs;

                byte[] pes = BuildPes(accessUnit,
                                      streamId: STREAM_ID_VIDEO_E0,
                                      pts90k:   pts90k,
                                      // Annex-B already has its own start codes; mark as
                                      // data-aligned access unit so libav picks the AU boundary.
                                      alignmentIndicator: true);
                WritePesAsTsPackets(ms, pes, PID_VIDEO, ref _ccVideo,
                                    isStartOfAu: true, pcrPts90k: emitPcr ? (ulong?)pts90k : null);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Wrap one audio frame (or one PCM chunk) into MPEG-TS packets.
        /// For LPCM the input is raw LE samples; we either pass straight
        /// through (PES-private path) or wrap with Blu-ray LPCM framing.
        /// </summary>
        public byte[] MuxAudioFrame(byte[] frame, uint rtpTimestamp) {
            if (frame == null || frame.Length == 0) return Array.Empty<byte>();
            if (!_audioAnchored) { _audioBaseTs = rtpTimestamp; _audioAnchored = true; }
            ulong pts90k = (ComputePts90k(rtpTimestamp, _audioBaseTs, _audioClockHz)
                            + _audioOutputPtsOffset90k) & 0x1FFFFFFFFUL;
            _latestAudioPtsMs = pts90k / 90.0;

            byte[] esPayload;
            byte   streamId;
            switch (_audioCodec) {
                case AudioCodec.LpcmPesPrivate:
                    esPayload = frame;             // raw LE PCM straight through
                    streamId  = STREAM_ID_PRIVATE_1;
                    break;
                case AudioCodec.BluRayLpcm:
                    esPayload = WrapBluRayLpcm(frame);
                    streamId  = STREAM_ID_PRIVATE_1;
                    break;
                case AudioCodec.MpegAudio:
                    esPayload = frame;             // raw MPA frames
                    streamId  = STREAM_ID_AUDIO_C0;
                    break;
                case AudioCodec.Ac3:
                    esPayload = frame;             // AC-3 frames in private_stream_1 PES
                    streamId  = STREAM_ID_PRIVATE_1;
                    break;
                default:
                    esPayload = frame;
                    streamId  = STREAM_ID_AUDIO_C0;
                    break;
            }

            using (var ms = new MemoryStream()) {
                MaybeEmitTables(ms);
                byte[] pes = BuildPes(esPayload,
                                      streamId: streamId,
                                      pts90k:   pts90k,
                                      alignmentIndicator: true);
                WritePesAsTsPackets(ms, pes, PID_AUDIO, ref _ccAudio,
                                    isStartOfAu: true, pcrPts90k: null);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// End-of-stream marker. For MPEG-TS there isn't a strict EOS code
        /// the way MPEG-PS uses 0x000001B9 — libav's TS demuxer treats a
        /// short read as EOF. We emit one final PAT+PMT for late joiners
        /// and an idle TS packet.
        /// </summary>
        public byte[] EndCode() {
            using (var ms = new MemoryStream()) {
                EmitPatPmt(ms);
                ms.Write(BuildIdleTsPacket(), 0, TS_PACKET_SIZE);
                return ms.ToArray();
            }
        }

        // ---- PAT / PMT --------------------------------------------------

        private void MaybeEmitTables(Stream ms) {
            // Use the LATEST pts (audio or video) to drive table cadence —
            // works fine even if one stream stalls briefly.
            double nowMs = Math.Max(_latestAudioPtsMs, _latestVideoPtsMs);
            if (_lastPatPmtPtsMs == double.MinValue || nowMs - _lastPatPmtPtsMs >= PatPmtIntervalMs) {
                EmitPatPmt(ms);
                _lastPatPmtPtsMs = nowMs;
            }
        }

        private void EmitPatPmt(Stream ms) {
            byte[] patPacket = BuildPatTsPacket();
            ms.Write(patPacket, 0, TS_PACKET_SIZE);
            byte[] pmtPacket = BuildPmtTsPacket();
            ms.Write(pmtPacket, 0, TS_PACKET_SIZE);
        }

        /// <summary>
        /// Build a 188-byte TS packet carrying the PAT (program list).
        /// We declare a single program (number = 1) mapped to PMT PID.
        /// </summary>
        private byte[] BuildPatTsPacket() {
            // PAT section body (without the per-section header CRC):
            //   table_id (8) = 0x00
            //   section_syntax_indicator (1) = 1
            //   '0' (1) = 0
            //   reserved (2) = 11
            //   section_length (12)
            //   transport_stream_id (16) = arbitrary, 1
            //   reserved (2) = 11
            //   version_number (5) = 0
            //   current_next_indicator (1) = 1
            //   section_number (8) = 0
            //   last_section_number (8) = 0
            //   for each program:
            //     program_number (16) = 1
            //     reserved (3) = 111
            //     PID (13) = PMT_PID
            //   CRC32 (32)
            byte[] body = new byte[12];
            int p = 0;
            body[p++] = 0x00;                                       // table_id
            // section_length: 13 bytes (5 header + 4 program entry + 4 CRC) − first 3 bytes already counted
            // → 9 bytes follow the section_length field; total section = 3+9 = 12 incl CRC
            // section_length = 9 (number of bytes following the section_length field)
            ushort sectionLen = 9;
            body[p++] = (byte)(0xB0 | ((sectionLen >> 8) & 0x0F));  // syntax=1, '0', reserved=11, len high
            body[p++] = (byte)(sectionLen & 0xFF);                  // len low
            body[p++] = 0x00; body[p++] = 0x01;                     // transport_stream_id = 1
            body[p++] = 0xC1;                                       // reserved=11, version=0, current=1
            body[p++] = 0x00;                                       // section_number
            body[p++] = 0x00;                                       // last_section_number
            body[p++] = 0x00; body[p++] = 0x01;                     // program_number = 1
            body[p++] = (byte)(0xE0 | ((PID_PMT >> 8) & 0x1F));     // reserved=111, PID high
            body[p++] = (byte)(PID_PMT & 0xFF);                     // PID low
            uint crc = Crc32Mpeg(body, 0, p);
            byte[] section = new byte[p + 4];
            Buffer.BlockCopy(body, 0, section, 0, p);
            section[p++] = (byte)((crc >> 24) & 0xFF);
            section[p++] = (byte)((crc >> 16) & 0xFF);
            section[p++] = (byte)((crc >> 8)  & 0xFF);
            section[p]   = (byte)(crc & 0xFF);

            return BuildPsiTsPacket(PID_PAT, section, ref _ccPat);
        }

        /// <summary>
        /// Build a 188-byte TS packet carrying the PMT for our single
        /// program. Lists the video PID + stream type and the audio PID
        /// + stream type, including any codec-specific descriptors
        /// (registration descriptor for raw-LE-PCM, etc.).
        /// </summary>
        private byte[] BuildPmtTsPacket() {
            // We build the PMT body backwards: collect ES entries, then
            // prepend the fixed header + compute section_length + append CRC.
            // ES loop entry:
            //   stream_type (8)
            //   reserved (3) = 111
            //   elementary_PID (13)
            //   reserved (4) = 1111
            //   ES_info_length (12)
            //   <descriptors>

            byte[] videoEs = BuildEsLoopEntry(StreamTypeForVideo(_videoCodec),
                                              PID_VIDEO,
                                              descriptors: Array.Empty<byte>());

            byte[] audioDescs = BuildAudioDescriptors();
            byte[] audioEs    = BuildEsLoopEntry(StreamTypeForAudio(_audioCodec),
                                                  PID_AUDIO,
                                                  descriptors: audioDescs);

            int esLoopLen = videoEs.Length + audioEs.Length;

            // PMT header:
            //   table_id (8) = 0x02
            //   syntax (1)=1, '0' (1)=0, reserved (2)=11, section_length (12)
            //   program_number (16) = 1
            //   reserved (2) = 11, version (5) = 0, current_next (1) = 1
            //   section_number (8) = 0
            //   last_section_number (8) = 0
            //   reserved (3) = 111, PCR_PID (13) = PID_VIDEO
            //   reserved (4) = 1111, program_info_length (12) = 0
            //   <ES loop>
            //   CRC32 (32)
            // section_length = number of bytes AFTER the section_length field
            //                  = 9 (header tail) + esLoopLen + 4 (CRC)
            ushort sectionLen = (ushort)(9 + esLoopLen + 4);

            byte[] body = new byte[3 + sectionLen]; // 3 = table_id + section_length bytes
            int p = 0;
            body[p++] = 0x02;
            body[p++] = (byte)(0xB0 | ((sectionLen >> 8) & 0x0F));
            body[p++] = (byte)(sectionLen & 0xFF);
            body[p++] = 0x00; body[p++] = 0x01;       // program_number = 1
            body[p++] = 0xC1;                          // version=0, current=1
            body[p++] = 0x00; body[p++] = 0x00;        // section/last_section
            body[p++] = (byte)(0xE0 | ((PID_VIDEO >> 8) & 0x1F));
            body[p++] = (byte)(PID_VIDEO & 0xFF);
            body[p++] = 0xF0; body[p++] = 0x00;        // program_info_length = 0
            Buffer.BlockCopy(videoEs, 0, body, p, videoEs.Length); p += videoEs.Length;
            Buffer.BlockCopy(audioEs, 0, body, p, audioEs.Length); p += audioEs.Length;
            uint crc = Crc32Mpeg(body, 0, p);
            body[p++] = (byte)((crc >> 24) & 0xFF);
            body[p++] = (byte)((crc >> 16) & 0xFF);
            body[p++] = (byte)((crc >> 8)  & 0xFF);
            body[p]   = (byte)(crc & 0xFF);

            return BuildPsiTsPacket(PID_PMT, body, ref _ccPmt);
        }

        private static byte[] BuildEsLoopEntry(byte streamType, ushort pid, byte[] descriptors) {
            int descLen = descriptors == null ? 0 : descriptors.Length;
            byte[] entry = new byte[5 + descLen];
            entry[0] = streamType;
            entry[1] = (byte)(0xE0 | ((pid >> 8) & 0x1F));
            entry[2] = (byte)(pid & 0xFF);
            entry[3] = (byte)(0xF0 | ((descLen >> 8) & 0x0F));
            entry[4] = (byte)(descLen & 0xFF);
            if (descLen > 0) Buffer.BlockCopy(descriptors, 0, entry, 5, descLen);
            return entry;
        }

        /// <summary>
        /// Build any per-codec ES descriptors that should sit inside the
        /// PMT ES loop for the audio PID. Currently only the
        /// LpcmPesPrivate path needs a registration descriptor to flag
        /// the stream as "audio that needs libav-side codec override".
        /// </summary>
        private byte[] BuildAudioDescriptors() {
            switch (_audioCodec) {
                case AudioCodec.LpcmPesPrivate: {
                    // Registration descriptor (tag 0x05): identifier
                    // "ASLE" (ad-hoc "Audio S16 Little-Endian"). libav
                    // doesn't recognise this tag → falls back to
                    // requiring our consumer to force the codec via
                    // FFmpeg AVOptions. Still useful as a sentinel so
                    // ffprobe + similar tools can see it.
                    byte[] d = new byte[] {
                        0x05, 0x04,
                        (byte)'A', (byte)'S', (byte)'L', (byte)'E',
                    };
                    return d;
                }
                case AudioCodec.Ac3: {
                    // AC-3 audio_descriptor (tag 0x6A), zero length —
                    // libav recognises the type and probes the AC-3
                    // sync words inside the PES payload.
                    return new byte[] { 0x6A, 0x00 };
                }
                default:
                    return Array.Empty<byte>();
            }
        }

        // ---- PES --------------------------------------------------------

        /// <summary>
        /// Build the PES packet body: start_code_prefix + stream_id +
        /// length + PES header + payload. PTS-only flag set (no DTS — our
        /// upstream RTP timestamp is the decode + presentation time).
        /// </summary>
        private static byte[] BuildPes(byte[] payload, byte streamId, ulong pts90k, bool alignmentIndicator) {
            // PES header layout (when PTS present, no DTS):
            //   start_code_prefix (24) = 0x000001
            //   stream_id (8)
            //   PES_packet_length (16)        ← 0 = unbounded (allowed for video; we'll write the real length when small)
            //   marker '10' (2)
            //   scrambling_control (2) = 0
            //   priority (1) = 0
            //   data_alignment_indicator (1)
            //   copyright (1) = 0
            //   original_or_copy (1) = 0
            //   PTS_DTS_flags (2) = 10        ← PTS only
            //   ESCR_flag (1) = 0, ES_rate_flag (1) = 0,
            //   DSM_trick_mode (1) = 0, additional_copy_info (1) = 0,
            //   PES_CRC (1) = 0, PES_extension (1) = 0
            //   PES_header_data_length (8) = 5
            //   PTS bytes (5)

            const int pesHdrLenAfterFlags = 5; // PTS only

            // Total PES packet length excludes start_code_prefix(3) + stream_id(1) + length(2) = 6 bytes
            int payloadLen = payload?.Length ?? 0;
            int tail       = 3 /*flags*/ + pesHdrLenAfterFlags + payloadLen;
            // PES_packet_length is u16. For video where tail > 65535 we
            // can use 0 (unbounded) per MPEG-2 systems. Audio frames are
            // always small enough.
            ushort pesLen = (ushort)(tail > 0xFFFF ? 0 : tail);

            byte[] pkt = new byte[6 + tail];
            int p = 0;
            pkt[p++] = 0x00; pkt[p++] = 0x00; pkt[p++] = 0x01;
            pkt[p++] = streamId;
            pkt[p++] = (byte)((pesLen >> 8) & 0xFF);
            pkt[p++] = (byte)(pesLen & 0xFF);
            // flags byte 1
            byte flags1 = 0x80;                                  // '10' marker
            if (alignmentIndicator) flags1 |= 0x04;
            pkt[p++] = flags1;
            // flags byte 2: PTS_DTS_flags = 10
            pkt[p++] = 0x80;
            // PES_header_data_length
            pkt[p++] = (byte)pesHdrLenAfterFlags;
            // PTS 5 bytes
            WritePtsField(pkt, p, 0x02, pts90k);                // 0010 prefix = PTS only
            p += 5;
            if (payloadLen > 0) Buffer.BlockCopy(payload, 0, pkt, p, payloadLen);
            return pkt;
        }

        private static void WritePtsField(byte[] dst, int offset, byte prefix4, ulong pts33) {
            // PTS encoding per ISO/IEC 13818-1: 4-bit prefix + 3 PTS chunks
            // with marker_bit (=1) between them.
            //   byte 0: [prefix4 (4)][PTS[32..30](3)][marker '1']
            //   byte 1: PTS[29..22](8)
            //   byte 2: PTS[21..15](7)[marker '1']
            //   byte 3: PTS[14..7](8)
            //   byte 4: PTS[6..0](7)[marker '1']
            dst[offset    ] = (byte)((prefix4 << 4) | (byte)(((pts33 >> 30) & 0x07) << 1) | 0x01);
            dst[offset + 1] = (byte)((pts33 >> 22) & 0xFF);
            dst[offset + 2] = (byte)((((pts33 >> 15) & 0x7F) << 1) | 0x01);
            dst[offset + 3] = (byte)((pts33 >> 7) & 0xFF);
            dst[offset + 4] = (byte)(((pts33 & 0x7F) << 1) | 0x01);
        }

        // ---- TS packetisation -------------------------------------------

        /// <summary>
        /// Slice a complete PES packet into one or more 188-byte TS
        /// packets on the given PID. The first TS packet has PUSI=1.
        /// If <paramref name="pcrPts90k"/> is non-null, the first TS
        /// packet's adaptation field carries a PCR base+ext.
        /// </summary>
        private void WritePesAsTsPackets(Stream ms, byte[] pes, ushort pid, ref byte cc,
                                          bool isStartOfAu, ulong? pcrPts90k) {
            int pesOffset = 0;
            bool first = true;
            while (pesOffset < pes.Length) {
                byte[] packet = new byte[TS_PACKET_SIZE];
                packet[0] = TS_SYNC_BYTE;
                packet[1] = (byte)((first && isStartOfAu ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
                packet[2] = (byte)(pid & 0xFF);

                // adaptation_field_control: 01 = payload only, 11 = AF + payload
                byte afc = 0x01;
                int afLen = 0;
                byte[] adaptField = null;
                if (first && pcrPts90k.HasValue) {
                    adaptField = BuildAdaptationFieldWithPcr(pcrPts90k.Value);
                    afLen = adaptField.Length;
                    afc = 0x03;
                }

                // Stuffing: if remaining PES bytes < 184 - afLen, pad
                // the adaptation field to fill the packet exactly.
                int remaining = pes.Length - pesOffset;
                int payloadAvail = TS_PACKET_SIZE - 4 - afLen;
                if (remaining < payloadAvail) {
                    int extra = payloadAvail - remaining;
                    if (adaptField == null) {
                        adaptField = BuildAdaptationFieldStuffing(extra);
                        afc = 0x03;
                    } else {
                        adaptField = ExtendAdaptationFieldStuffing(adaptField, extra);
                    }
                    afLen = adaptField.Length;
                    payloadAvail = TS_PACKET_SIZE - 4 - afLen;
                }

                packet[3] = (byte)((afc << 4) | (cc & 0x0F));
                cc = (byte)((cc + 1) & 0x0F);

                int writePos = 4;
                if (adaptField != null) {
                    Buffer.BlockCopy(adaptField, 0, packet, writePos, adaptField.Length);
                    writePos += adaptField.Length;
                }
                int take = Math.Min(payloadAvail, remaining);
                Buffer.BlockCopy(pes, pesOffset, packet, writePos, take);
                pesOffset += take;
                first = false;

                ms.Write(packet, 0, TS_PACKET_SIZE);
            }
        }

        private byte[] BuildPsiTsPacket(ushort pid, byte[] section, ref byte cc) {
            // PSI TS packets carry a pointer_field (1 byte = 0x00) at the
            // start of payload, then the section. We assume sections fit
            // inside a single TS packet (PAT/PMT are tiny in our config).
            byte[] packet = new byte[TS_PACKET_SIZE];
            packet[0] = TS_SYNC_BYTE;
            packet[1] = (byte)(0x40 | ((pid >> 8) & 0x1F));    // PUSI=1
            packet[2] = (byte)(pid & 0xFF);
            packet[3] = (byte)(0x10 | (cc & 0x0F));            // AFC=01 (payload only)
            cc = (byte)((cc + 1) & 0x0F);

            int writePos = 4;
            packet[writePos++] = 0x00; // pointer_field
            if (section.Length > TS_PACKET_SIZE - writePos) {
                // Should never happen with our table sizes (PAT ~ 16 B,
                // PMT ~ 30 B). Truncate defensively.
                Buffer.BlockCopy(section, 0, packet, writePos, TS_PACKET_SIZE - writePos);
            } else {
                Buffer.BlockCopy(section, 0, packet, writePos, section.Length);
                writePos += section.Length;
                // Stuff the rest with 0xFF per spec.
                for (int i = writePos; i < TS_PACKET_SIZE; i++) packet[i] = 0xFF;
            }
            return packet;
        }

        private byte[] BuildIdleTsPacket() {
            byte[] packet = new byte[TS_PACKET_SIZE];
            packet[0] = TS_SYNC_BYTE;
            packet[1] = 0x1F; packet[2] = 0xFF;                  // null PID = 0x1FFF
            packet[3] = 0x10;                                    // payload only, CC=0
            for (int i = 4; i < TS_PACKET_SIZE; i++) packet[i] = 0xFF;
            return packet;
        }

        private static byte[] BuildAdaptationFieldStuffing(int totalLen) {
            // adaptation_field_length is 1 byte, value = totalLen - 1.
            // If totalLen == 1, it's a single byte AF carrying no flags.
            if (totalLen < 1) totalLen = 1;
            byte[] af = new byte[totalLen];
            af[0] = (byte)(totalLen - 1);
            if (totalLen >= 2) af[1] = 0x00; // flags all zero
            for (int i = 2; i < totalLen; i++) af[i] = 0xFF;
            return af;
        }

        private static byte[] BuildAdaptationFieldWithPcr(ulong pts90k) {
            // PCR base (33 bits) + reserved (6) + PCR ext (9) = 6 bytes
            // adaptation_field:
            //   adaptation_field_length (8) — total bytes after this field
            //   flags (8): discontinuity=0, random_access=1, ES_prio=0,
            //              PCR=1, OPCR=0, splicing=0, private=0, AF_ext=0
            //   PCR (6 bytes)
            ulong pcrBase = pts90k;          // same 33-bit base
            ushort pcrExt = 0;
            byte[] af = new byte[8];          // length byte + 1 flag byte + 6 PCR bytes
            af[0] = 7;                        // length excludes length byte itself
            af[1] = 0x50;                     // random_access=1, PCR=1
            af[2] = (byte)((pcrBase >> 25) & 0xFF);
            af[3] = (byte)((pcrBase >> 17) & 0xFF);
            af[4] = (byte)((pcrBase >>  9) & 0xFF);
            af[5] = (byte)((pcrBase >>  1) & 0xFF);
            af[6] = (byte)((((int)pcrBase & 0x01) << 7) | 0x7E | ((pcrExt >> 8) & 0x01));
            af[7] = (byte)(pcrExt & 0xFF);
            return af;
        }

        private static byte[] ExtendAdaptationFieldStuffing(byte[] af, int extraStuffing) {
            // Existing AF + extraStuffing bytes of 0xFF appended; length byte updated.
            byte[] grown = new byte[af.Length + extraStuffing];
            Buffer.BlockCopy(af, 0, grown, 0, af.Length);
            for (int i = af.Length; i < grown.Length; i++) grown[i] = 0xFF;
            grown[0] = (byte)(grown.Length - 1);
            return grown;
        }

        // ---- Codec-specific helpers -------------------------------------

        private byte StreamTypeForVideo(VideoCodec c) {
            switch (c) {
                case VideoCodec.H264:       return STREAM_TYPE_H264;
                case VideoCodec.Mpeg1Video: return STREAM_TYPE_MPEG1_VIDEO;
                case VideoCodec.Mpeg2Video: return STREAM_TYPE_MPEG2_VIDEO;
                default:                    return STREAM_TYPE_H264;
            }
        }

        private byte StreamTypeForAudio(AudioCodec c) {
            switch (c) {
                case AudioCodec.LpcmPesPrivate: return STREAM_TYPE_PES_PRIVATE;
                case AudioCodec.BluRayLpcm:     return STREAM_TYPE_BLURAY_PCM;
                case AudioCodec.MpegAudio:      return STREAM_TYPE_MPEG1_AUDIO;
                case AudioCodec.Ac3:            return STREAM_TYPE_AC3;
                default:                        return STREAM_TYPE_PES_PRIVATE;
            }
        }

        /// <summary>
        /// Wrap a PCM frame in Blu-ray LPCM framing (4-byte header + BE
        /// samples). Used only when <see cref="AudioCodec.BluRayLpcm"/> is
        /// selected. The 4-byte header tells the decoder sample rate,
        /// channel layout, and bits-per-sample.
        ///
        /// BD-LPCM only supports 48/96/192 kHz. If the source PCM is at
        /// any other rate (e.g. WMC frequently sends 44.1 kHz audio for
        /// CD-quality tracks), we resample to 48 kHz first with linear
        /// interpolation. Quality is not audiophile but good enough for
        /// streaming TV / movies. A higher-order resampler (e.g.
        /// polyphase FIR) can replace this later if anyone cares.
        /// </summary>
        private byte[] WrapBluRayLpcm(byte[] leSamples) {
            // 1. Resample if input rate isn't a BD-LPCM rate.
            byte[] samples       = leSamples;
            int    effRate       = _pcmSampleRate;
            if (effRate != 48000 && effRate != 96000 && effRate != 192000) {
                samples = ResamplePcm16LeStereo(leSamples, effRate, 48000, _pcmChannels);
                effRate = 48000;
            }
            int n = samples.Length;

            // 2. Encode channel layout / sample rate / bits.
            byte chanAssign;
            switch (_pcmChannels) {
                case 1: chanAssign = 1; break;   // mono
                case 2: chanAssign = 3; break;   // stereo
                case 6: chanAssign = 9; break;   // 5.1
                default: chanAssign = 3; break;
            }
            byte sampleFreq;
            switch (effRate) {
                case 48000:  sampleFreq = 1; break;
                case 96000:  sampleFreq = 4; break;
                case 192000: sampleFreq = 5; break;
                default:     sampleFreq = 1; break; // unreachable after resample
            }
            byte bps;
            switch (_pcmBitsPerSample) {
                case 16: bps = 1; break;
                case 20: bps = 2; break;
                case 24: bps = 3; break;
                default: bps = 1; break;
            }

            // 3. Byte-swap LE → BE per sample.
            byte[] beSamples = new byte[n];
            if (_pcmBitsPerSample == 16) {
                for (int i = 0; i + 1 < n; i += 2) {
                    beSamples[i]     = samples[i + 1];
                    beSamples[i + 1] = samples[i];
                }
            } else if (_pcmBitsPerSample == 24) {
                for (int i = 0; i + 2 < n; i += 3) {
                    beSamples[i]     = samples[i + 2];
                    beSamples[i + 1] = samples[i + 1];
                    beSamples[i + 2] = samples[i];
                }
            } else {
                Buffer.BlockCopy(samples, 0, beSamples, 0, n);
            }

            // 4. Build 4-byte header + sample data. audio_data_payload_size
            //    is the byte count of the PCM portion (header excluded).
            byte[] header = new byte[4];
            header[0] = (byte)((n >> 8) & 0xFF);
            header[1] = (byte)(n & 0xFF);
            header[2] = (byte)(((chanAssign & 0x0F) << 4) | (sampleFreq & 0x0F));
            header[3] = (byte)(((bps & 0x03) << 6) | (0 << 5));    // start_flag=0, reserved=0
            byte[] result = new byte[4 + n];
            Buffer.BlockCopy(header,   0, result, 0, 4);
            Buffer.BlockCopy(beSamples, 0, result, 4, n);
            return result;
        }

        /// <summary>
        /// Linear-interpolation resampler for interleaved signed 16-bit
        /// little-endian PCM. <paramref name="channels"/> is the channel
        /// count (1 = mono, 2 = stereo, ...). Output is still
        /// interleaved s16le. Used by the BD-LPCM path to lift 44.1 kHz
        /// (and any other non-BD rate) up to 48 kHz before wrapping.
        /// </summary>
        private static byte[] ResamplePcm16LeStereo(byte[] src, int srcRate, int dstRate, int channels) {
            if (srcRate == dstRate || channels <= 0) return src;
            int frameBytes = channels * 2;          // 16-bit samples per channel
            int srcFrames  = src.Length / frameBytes;
            if (srcFrames < 2) return src;
            long dstFrames = ((long)srcFrames * dstRate + (srcRate / 2)) / srcRate;
            byte[] dst = new byte[dstFrames * frameBytes];

            double step = (double)srcRate / dstRate; // src frames per dst frame
            double pos  = 0;
            for (long i = 0; i < dstFrames; i++) {
                int   lo   = (int)pos;
                if (lo >= srcFrames - 1) lo = srcFrames - 2;
                double frac = pos - lo;
                int srcLoOff = lo * frameBytes;
                int srcHiOff = srcLoOff + frameBytes;
                int dstOff   = (int)(i * frameBytes);
                for (int c = 0; c < channels; c++) {
                    int s0Off = srcLoOff + c * 2;
                    int s1Off = srcHiOff + c * 2;
                    short s0 = (short)(src[s0Off] | (src[s0Off + 1] << 8));
                    short s1 = (short)(src[s1Off] | (src[s1Off + 1] << 8));
                    int    s = (int)Math.Round(s0 + (s1 - s0) * frac);
                    if (s >  32767) s =  32767;
                    if (s < -32768) s = -32768;
                    int dOff = dstOff + c * 2;
                    dst[dOff]     = (byte)(s & 0xFF);
                    dst[dOff + 1] = (byte)((s >> 8) & 0xFF);
                }
                pos += step;
            }
            return dst;
        }

        // ---- PTS / CRC --------------------------------------------------

        /// <summary>
        /// Wrap-around-safe PTS computation. RTP timestamps are u32 in
        /// the SDP-advertised codec clock (e.g. 90 kHz for wm-MPA/MPV,
        /// 1 kHz for x-wmf-pf). We compute the signed delta from the
        /// per-stream base, then scale to the canonical 90 kHz MPEG-TS
        /// PTS clock. Returns a 33-bit PTS.
        /// </summary>
        private static ulong ComputePts90k(uint rtpTs, uint baseTs, uint clockHz) {
            int signedDelta = (int)(rtpTs - baseTs);    // unsigned diff cast to signed for wrap-safety
            if (signedDelta < 0) signedDelta = 0;       // clamp out-of-order packets to 0
            if (clockHz == 0) clockHz = 90000;
            // 90 kHz canonical clock conversion. signedDelta * 90000 fits
            // in long for any reasonable rtpTs delta.
            ulong pts = (ulong)((long)signedDelta * 90000L / clockHz);
            return pts & 0x1FFFFFFFFul;                 // 33-bit mask
        }

        // CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection,
        // no final XOR) — used by all PSI sections (PAT, PMT, ...).
        private static uint Crc32Mpeg(byte[] data, int off, int len) {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < len; i++) {
                crc ^= (uint)data[off + i] << 24;
                for (int b = 0; b < 8; b++) {
                    crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
                }
            }
            return crc;
        }
    }
}
