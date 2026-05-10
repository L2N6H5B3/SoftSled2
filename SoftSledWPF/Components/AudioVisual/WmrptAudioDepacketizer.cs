using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SoftSled.Components.AudioVisual {
    /// <summary>
    /// WMRTP / x-wmf-pf depacketizer for audio streams (WMA / PCM / etc. encapsulated in
    /// x-wmf-pf). Mirrors <see cref="WmrptVideoDepacketizer"/> — see that class for the
    /// detailed compliance notes; the parsing rules are identical between audio and video.
    /// </summary>
    public class WmrptAudioDepacketizer {

        // BF1
        private const byte BF1_ST  = 0x80;
        private const byte BF1_CP  = 0x40;
        private const byte BF1_R1  = 0x20;
        private const byte BF1_R2  = 0x10;
        private const byte BF1_R3  = 0x08;
        private const byte BF1_B2P = 0x01;

        // BF2
        private const int  BF2_F_SHIFT = 6;
        private const byte BF2_F_MASK  = 0xC0;
        private const byte BF2_OP      = 0x20;
        private const byte BF2_S       = 0x10;
        private const byte BF2_D1      = 0x08;
        private const byte BF2_D2      = 0x04;
        private const byte BF2_E       = 0x02;
        private const byte BF2_B3P     = 0x01;

        // BF3
        private const byte BF3_D3 = 0x80;
        private const byte BF3_P  = 0x40;
        private const byte BF3_N  = 0x20;
        private const byte BF3_R6 = 0x10;
        private const byte BF3_R7 = 0x08;
        private const byte BF3_R8 = 0x04;
        private const byte BF3_R9 = 0x02;
        private const byte BF3_X  = 0x01;

        private const int F_MIDDLE_FRAGMENT = 0;
        private const int F_FIRST_FRAGMENT  = 1;
        private const int F_LAST_FRAGMENT   = 2;
        private const int F_COMPLETE_MAU    = 3;

        private class StreamState {
            public List<byte[]> Fragments;
            public ushort?      ExpectedNextSeq;
            public bool         FirstFragmentSync;
            public bool         FirstFragmentDiscont;
            public bool         FirstFragmentEncrypt;
            public uint         FirstFragmentTs;
            public bool         PendingPostLossFlag;
        }

        private readonly Dictionary<uint, StreamState> _streams = new Dictionary<uint, StreamState>();

        public event EventHandler<EventData> AudioDataReady;

        public void ProcessWmrptPayload(byte[] rtpPayload, int rtpPayloadLength,
                                         uint rtpSsrc, ushort rtpSequenceNumber,
                                         uint rtpTimestamp, bool rtpMarker) {
            if (rtpPayload == null || rtpPayloadLength < 1) return;

            StreamState stream = GetOrCreateStream(rtpSsrc);

            if (stream.ExpectedNextSeq.HasValue) {
                ushort expected = stream.ExpectedNextSeq.Value;
                if (rtpSequenceNumber != expected) {
                    int gap = unchecked((short)(rtpSequenceNumber - expected));
                    Trace.WriteLine($"WMRTP Audio: SN gap on SSRC {rtpSsrc} — expected {expected}, got {rtpSequenceNumber} (delta {gap})");
                    if (stream.Fragments != null) {
                        Trace.WriteLine($"WMRTP Audio: dropping partial fragment buffer ({stream.Fragments.Count} frags) due to SN gap");
                        stream.Fragments = null;
                    }
                    stream.PendingPostLossFlag = true;
                }
            }
            stream.ExpectedNextSeq = unchecked((ushort)(rtpSequenceNumber + 1));

            int currentOffset = 0;
            byte bitField1 = rtpPayload[currentOffset++];
            bool stPresent  = (bitField1 & BF1_ST)  != 0;
            bool cpPresent  = (bitField1 & BF1_CP)  != 0;
            bool r1Present  = (bitField1 & BF1_R1)  != 0;
            bool r2Present  = (bitField1 & BF1_R2)  != 0;
            bool r3Present  = (bitField1 & BF1_R3)  != 0;
            bool b2pPresent = (bitField1 & BF1_B2P) != 0;
            if (stPresent) currentOffset += 4;
            if (cpPresent) currentOffset += 12;
            if (r1Present) currentOffset += 4;
            if (r2Present) currentOffset += 4;
            if (r3Present) currentOffset += 4;
            if (currentOffset > rtpPayloadLength) {
                Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: BF1 + optional fields overrun ({currentOffset} > {rtpPayloadLength})");
                return;
            }
            if (!b2pPresent) return;

            bool sawAnyTerminator = false;
            while (currentOffset < rtpPayloadLength) {
                bool ok = ProcessOnePayload(rtpPayload, rtpPayloadLength, ref currentOffset,
                                             stream, rtpSsrc, rtpSequenceNumber, rtpTimestamp,
                                             out bool emittedMau);
                if (!ok) return;
                if (emittedMau) sawAnyTerminator = true;
            }

            if (rtpMarker && !sawAnyTerminator)
                Trace.WriteLine($"WMRTP Audio: SN {rtpSequenceNumber} M=1 but no F=2/F=3 payload found");
        }

        private bool ProcessOnePayload(byte[] buf, int bufLen, ref int currentOffset,
                                        StreamState stream, uint ssrc, ushort seqNum, uint rtpTs,
                                        out bool emittedMau) {
            emittedMau = false;

            byte bitField2 = buf[currentOffset++];
            int  fragType   = (bitField2 & BF2_F_MASK) >> BF2_F_SHIFT;
            bool opPresent  = (bitField2 & BF2_OP)  != 0;
            bool sBit       = (bitField2 & BF2_S)   != 0;
            bool d1Bit      = (bitField2 & BF2_D1)  != 0;
            bool eBit       = (bitField2 & BF2_E)   != 0;
            bool b3pPresent = (bitField2 & BF2_B3P) != 0;

            bool firstOrComplete = (fragType == F_FIRST_FRAGMENT || fragType == F_COMPLETE_MAU);

            int offsetFieldStart = currentOffset;
            int payloadOffsetField = -1;
            if (opPresent) {
                if (currentOffset + 2 > bufLen) {
                    Trace.WriteLine($"WMRTP Audio Error SN {seqNum}: short Offset field");
                    return false;
                }
                payloadOffsetField = (buf[currentOffset] << 8) | buf[currentOffset + 1];
                currentOffset += 2;
            }

            if (b3pPresent) {
                if (currentOffset >= bufLen) return false;
                byte bitField3 = buf[currentOffset++];
                bool d3Present = (bitField3 & BF3_D3) != 0;
                bool pPresent  = (bitField3 & BF3_P)  != 0;
                bool nPresent  = (bitField3 & BF3_N)  != 0;
                bool r6Present = (bitField3 & BF3_R6) != 0;
                bool r7Present = (bitField3 & BF3_R7) != 0;
                bool r8Present = (bitField3 & BF3_R8) != 0;
                bool r9Present = (bitField3 & BF3_R9) != 0;
                bool xPresent  = (bitField3 & BF3_X)  != 0;
                if (d3Present) currentOffset += 4;
                if (pPresent)  currentOffset += 4;
                if (nPresent)  currentOffset += 8;
                if (r6Present) currentOffset += 4;
                if (r7Present) currentOffset += 4;
                if (r8Present) currentOffset += 4;
                if (r9Present) currentOffset += 4;
                if (xPresent) {
                    while (currentOffset < bufLen) {
                        if (currentOffset + 2 > bufLen) {
                            Trace.WriteLine($"WMRTP Audio Error SN {seqNum}: short Extension header");
                            return false;
                        }
                        byte extHeader = buf[currentOffset++];
                        bool lastExt   = (extHeader & 0x80) != 0;
                        byte extLength = buf[currentOffset++];
                        currentOffset += extLength;
                        if (currentOffset > bufLen) return false;
                        if (lastExt) break;
                    }
                }
                if (currentOffset > bufLen) return false;
            }

            int payloadDataStart = currentOffset;
            int payloadDataEnd;
            if (opPresent) {
                payloadDataEnd = offsetFieldStart + 2 + payloadOffsetField;
                if (payloadDataEnd < payloadDataStart || payloadDataEnd > bufLen) {
                    Trace.WriteLine($"WMRTP Audio Error SN {seqNum}: Offset {payloadOffsetField} → end {payloadDataEnd} out of range");
                    return false;
                }
            } else {
                payloadDataEnd = bufLen;
            }
            int payloadDataLen = payloadDataEnd - payloadDataStart;
            if (payloadDataLen < 0) return false;

            if (firstOrComplete && eBit) {
                Trace.WriteLine($"WMRTP Audio: SN {seqNum} E=1 (encrypted) — dropping payload (DRM not supported)");
                if (stream.Fragments != null) stream.Fragments = null;
                stream.PendingPostLossFlag = true;
                currentOffset = payloadDataEnd;
                return true;
            }

            byte[] data;
            if (payloadDataLen > 0) {
                data = new byte[payloadDataLen];
                Buffer.BlockCopy(buf, payloadDataStart, data, 0, payloadDataLen);
            } else {
                data = new byte[0];
            }
            currentOffset = payloadDataEnd;

            switch (fragType) {
                case F_COMPLETE_MAU:
                    if (stream.Fragments != null) {
                        Trace.WriteLine($"WMRTP Audio Warning SN {seqNum}: F=3 received while assembling — discarding {stream.Fragments.Count} prior frags");
                        stream.Fragments = null;
                    }
                    EmitMau(data, rtpTs, sBit, d1Bit || stream.PendingPostLossFlag, eBit, stream.PendingPostLossFlag);
                    stream.PendingPostLossFlag = false;
                    emittedMau = true;
                    break;

                case F_FIRST_FRAGMENT:
                    if (stream.Fragments != null)
                        Trace.WriteLine($"WMRTP Audio Warning SN {seqNum}: F=1 received while assembling — overwriting prior buffer");
                    stream.Fragments = new List<byte[]> { data };
                    stream.FirstFragmentSync    = sBit;
                    stream.FirstFragmentDiscont = d1Bit;
                    stream.FirstFragmentEncrypt = eBit;
                    stream.FirstFragmentTs      = rtpTs;
                    break;

                case F_MIDDLE_FRAGMENT:
                    if (stream.Fragments == null) {
                        Trace.WriteLine($"WMRTP Audio Warning SN {seqNum}: F=0 with no buffer — dropping");
                        stream.PendingPostLossFlag = true;
                    } else {
                        stream.Fragments.Add(data);
                    }
                    break;

                case F_LAST_FRAGMENT:
                    if (stream.Fragments == null) {
                        Trace.WriteLine($"WMRTP Audio Warning SN {seqNum}: F=2 with no buffer — dropping");
                        stream.PendingPostLossFlag = true;
                    } else {
                        stream.Fragments.Add(data);
                        try {
                            byte[] complete = Reassemble(stream.Fragments);
                            uint mauTs = stream.FirstFragmentTs != 0 ? stream.FirstFragmentTs : rtpTs;
                            EmitMau(complete, mauTs,
                                    stream.FirstFragmentSync,
                                    stream.FirstFragmentDiscont || stream.PendingPostLossFlag,
                                    stream.FirstFragmentEncrypt,
                                    stream.PendingPostLossFlag);
                            stream.PendingPostLossFlag = false;
                            emittedMau = true;
                        } catch (Exception ex) {
                            Trace.WriteLine($"WMRTP Audio Error SN {seqNum}: reassembly failed: {ex.Message}");
                        } finally {
                            stream.Fragments = null;
                        }
                    }
                    break;
            }

            return true;
        }

        private StreamState GetOrCreateStream(uint ssrc) {
            if (!_streams.TryGetValue(ssrc, out StreamState s)) {
                s = new StreamState();
                _streams[ssrc] = s;
            }
            return s;
        }

        private static byte[] Reassemble(List<byte[]> fragments) {
            int total = 0;
            foreach (byte[] f in fragments) total += f.Length;
            byte[] result = new byte[total];
            int pos = 0;
            foreach (byte[] f in fragments) {
                Buffer.BlockCopy(f, 0, result, pos, f.Length);
                pos += f.Length;
            }
            return result;
        }

        private void EmitMau(byte[] mau, uint ts, bool syncPoint, bool discontinuity, bool encrypted, bool postLoss) {
            if (mau == null || mau.Length == 0) return;
            // Audio block-alignment hint (16-bit stereo = 4 bytes/frame). Misalignment after a
            // post-loss reassembly would produce L/R channel swap or static.
            if (postLoss && mau.Length % 4 != 0)
                Trace.WriteLine($"WMRTP Audio Warning: post-loss MAU length {mau.Length} not block-aligned (16-bit stereo)");
            AudioDataReady?.Invoke(this, new EventData {
                data          = mau,
                timestamp     = ts,
                SyncPoint     = syncPoint,
                Discontinuity = discontinuity,
                Encrypted     = encrypted,
                PostLoss      = postLoss,
            });
        }

        public void Reset() {
            _streams.Clear();
            Trace.WriteLine("WMRTP Audio Depacketizer state cleared.");
        }
    }

    /// <summary>
    /// Output of the WMRTP depacketizers — one per complete reassembled MAU. The original
    /// shape (data + timestamp) is preserved so legacy consumers keep working; the new flag
    /// fields default to false and are populated from the WMRTP header bits + any sequence
    /// gap detection performed by the depacketizer.
    /// </summary>
    public class EventData {
        public byte[] data { get; set; }
        public uint timestamp { get; set; }

        /// <summary>S bit from BF2 of the first/complete fragment. True for sync points
        /// (e.g. H.264 IDR frame). Decoders should resync from sync points after loss.</summary>
        public bool SyncPoint { get; set; }

        /// <summary>D1 bit from BF2 of the first/complete fragment, OR a sequence-number gap
        /// was detected before this MAU. Either way: data preceding this MAU was lost.</summary>
        public bool Discontinuity { get; set; }

        /// <summary>E bit from BF2. Encrypted MAUs are dropped at the depacketizer (DRM
        /// unsupported) so this currently stays false on emitted events; reserved for
        /// future DRM support.</summary>
        public bool Encrypted { get; set; }

        /// <summary>This MAU was emitted directly after a packet-loss event. Consumer should
        /// probably skip it unless <see cref="SyncPoint"/> is also true (for video).</summary>
        public bool PostLoss { get; set; }
    }
}
