using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SoftSled.Components.AudioVisual {
    /// <summary>
    /// WMRTP / x-wmf-pf depacketizer for video streams. Parses the RTP Payload Format
    /// header (Bit Field 1/2/3) per the Microsoft WMA/WMV RTP Payload Format spec v1.0,
    /// reassembles fragmented MAUs, and emits raw codec data (e.g. H.264 NAL units).
    ///
    /// Compliance notes vs the original implementation:
    ///  - Handles MULTIPLE PAYLOADS per RTP packet (OP=1 grouping). Spec §3.2 / Figure 4.
    ///  - Tracks per-SSRC expected RTP sequence number. Sequence gaps mid-fragmentation
    ///    drop the partial buffer and flag the next emitted MAU as post-loss.
    ///  - Propagates Sync Point (S), Discontinuity (D1) and Encryption (E) bits to the
    ///    consumer via <see cref="EventData"/>. Decoder needs S to recover cleanly after
    ///    loss; without it, non-sync MAUs feed the decoder and produce visual artifacts.
    ///  - Propagates the RTP Marker bit for redundant fragmentation-completion checking.
    ///  - All multi-byte fields are read in network byte order (big-endian) per spec.
    /// </summary>
    public class WmrptVideoDepacketizer {

        // --- Bit Field 1 (first byte after RTP header — Packet Specific Info section) ---
        private const byte BF1_ST  = 0x80; // Send Time Present (32-bit)
        private const byte BF1_CP  = 0x40; // Correspondence Present (96-bit)
        private const byte BF1_R1  = 0x20; // Reserved 1 — if set, 32-bit field follows
        private const byte BF1_R2  = 0x10; // Reserved 2 — if set, 32-bit field follows
        private const byte BF1_R3  = 0x08; // Reserved 3 — if set, 32-bit field follows
        // R4 (0x04) and R5 (0x02) are reserved-must-be-zero with no associated field
        private const byte BF1_B2P = 0x01; // Bit Field 2 Present

        // --- Bit Field 2 (per-payload — MAU Properties section) ---
        private const int  BF2_F_SHIFT = 6;     // Fragmentation field is the top 2 bits
        private const byte BF2_F_MASK  = 0xC0;
        private const byte BF2_OP      = 0x20;  // Offset Present (16-bit Offset field follows BF2)
        private const byte BF2_S       = 0x10;  // Sync Point — valid only when F == 1 or F == 3
        private const byte BF2_D1      = 0x08;  // Discontinuity — valid only when F == 1 or F == 3
        private const byte BF2_D2      = 0x04;  // Droppable
        private const byte BF2_E       = 0x02;  // Encryption
        private const byte BF2_B3P     = 0x01;  // Bit Field 3 Present

        // --- Bit Field 3 (per-payload — MAU Timing section) ---
        private const byte BF3_D3 = 0x80; // Decode Time Present (32-bit)
        private const byte BF3_P  = 0x40; // Presentation Time Present (32-bit)
        private const byte BF3_N  = 0x20; // NPT Present (64-bit)
        private const byte BF3_R6 = 0x10; // Reserved 6 — 32-bit field if set
        private const byte BF3_R7 = 0x08;
        private const byte BF3_R8 = 0x04;
        private const byte BF3_R9 = 0x02;
        private const byte BF3_X  = 0x01; // Extension Present

        private const int F_MIDDLE_FRAGMENT = 0;
        private const int F_FIRST_FRAGMENT  = 1;
        private const int F_LAST_FRAGMENT   = 2;
        private const int F_COMPLETE_MAU    = 3;

        /// <summary>Per-SSRC reassembly state.</summary>
        private class StreamState {
            public List<byte[]> Fragments;           // null = not currently assembling
            public ushort?      ExpectedNextSeq;      // null = no prior packet seen on this SSRC
            public bool         FirstFragmentSync;    // S bit captured from the F=1 packet
            public bool         FirstFragmentDiscont; // D1 bit captured from the F=1 packet
            public bool         FirstFragmentEncrypt; // E bit captured from the F=1 packet
            public uint         FirstFragmentTs;      // RTP timestamp from the F=1 packet
            public bool         PendingPostLossFlag;  // next emitted MAU was preceded by gap/loss
        }

        private readonly Dictionary<uint, StreamState> _streams = new Dictionary<uint, StreamState>();

        public event EventHandler<EventData> NalUnitReady;

        /// <summary>
        /// Process the WMRTP payload of one RTP packet (data after the 12-byte RTP header
        /// and after any RTP extension).
        /// </summary>
        /// <param name="rtpMarker">Value of the RTP Marker (M) bit. Per WMRTP spec, set to 1
        /// when any payload in the packet is a complete MAU or the last fragment of a MAU.</param>
        public void ProcessWmrptPayload(byte[] rtpPayload, int rtpPayloadLength,
                                         uint rtpSsrc, ushort rtpSequenceNumber,
                                         uint rtpTimestamp, bool rtpMarker) {
            if (rtpPayload == null || rtpPayloadLength < 1) return;

            StreamState stream = GetOrCreateStream(rtpSsrc);

            // -------- Sequence-number gap detection --------
            // Detects RTP packet loss; if loss happens mid-fragmentation, the partial buffer
            // gets dropped (the next F=1 will start a fresh assembly) and the next emitted
            // MAU is flagged as PostLoss so the consumer can decide whether to discard until
            // the next sync point.
            if (stream.ExpectedNextSeq.HasValue) {
                ushort expected = stream.ExpectedNextSeq.Value;
                if (rtpSequenceNumber != expected) {
                    int gap = unchecked((short)(rtpSequenceNumber - expected));
                    Trace.WriteLine($"WMRTP Video: SN gap on SSRC {rtpSsrc} — expected {expected}, got {rtpSequenceNumber} (delta {gap})");
                    if (stream.Fragments != null) {
                        Trace.WriteLine($"WMRTP Video: dropping partial fragment buffer ({stream.Fragments.Count} frags) due to SN gap");
                        stream.Fragments = null;
                    }
                    stream.PendingPostLossFlag = true;
                }
            }
            stream.ExpectedNextSeq = unchecked((ushort)(rtpSequenceNumber + 1));

            // -------- Bit Field 1 (mandatory at start of first WMRTP header in packet) --------
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
                Trace.WriteLine($"WMRTP Video Error SN {rtpSequenceNumber}: BF1 + optional fields overrun ({currentOffset} > {rtpPayloadLength})");
                return;
            }
            if (!b2pPresent) {
                // No payloads in this packet (only Send Time / Correspondence updates).
                // Spec §3.6 allows BF1 alone; no MAU data to emit.
                return;
            }

            // -------- Loop over all WMRTP payloads in this RTP packet --------
            // First payload starts here; subsequent ones (per OP=1 grouping) start with BF2.
            bool sawAnyTerminator = false;
            while (currentOffset < rtpPayloadLength) {
                bool ok = ProcessOnePayload(rtpPayload, rtpPayloadLength, ref currentOffset,
                                             stream, rtpSsrc, rtpSequenceNumber, rtpTimestamp,
                                             out bool emittedMau);
                if (!ok) return; // header parse failure, abandon the packet
                if (emittedMau) sawAnyTerminator = true;
            }

            // Cross-check the marker bit. WMRTP spec line 643:
            //   "M is set to 1 if any of the payloads in the RTP packet contain a complete
            //    MAU, or the last fragment of a MAU."
            if (rtpMarker && !sawAnyTerminator)
                Trace.WriteLine($"WMRTP Video: SN {rtpSequenceNumber} M=1 but no F=2/F=3 payload found");
        }

        /// <summary>
        /// Parse one BF2-rooted payload starting at <paramref name="currentOffset"/>; advance
        /// it past the payload's bytes. Returns false on a header parse error.
        /// </summary>
        private bool ProcessOnePayload(byte[] buf, int bufLen, ref int currentOffset,
                                        StreamState stream, uint ssrc, ushort seqNum, uint rtpTs,
                                        out bool emittedMau) {
            emittedMau = false;
            int payloadHeaderStart = currentOffset;

            // -------- Bit Field 2 --------
            byte bitField2 = buf[currentOffset++];
            int  fragType   = (bitField2 & BF2_F_MASK) >> BF2_F_SHIFT;
            bool opPresent  = (bitField2 & BF2_OP)  != 0;
            bool sBit       = (bitField2 & BF2_S)   != 0;
            bool d1Bit      = (bitField2 & BF2_D1)  != 0;
            bool eBit       = (bitField2 & BF2_E)   != 0;
            bool b3pPresent = (bitField2 & BF2_B3P) != 0;

            // S, D1, D2 bits are only valid on F=1 (first fragment) or F=3 (complete MAU).
            // Spec §3.2 line 749/355: "receivers must ignore these bits if F == 0 or F == 2."
            bool firstOrComplete = (fragType == F_FIRST_FRAGMENT || fragType == F_COMPLETE_MAU);

            int offsetFieldStart = currentOffset; // remember position even if OP=0
            int payloadOffsetField = -1;          // sentinel: -1 means OP=0
            if (opPresent) {
                if (currentOffset + 2 > bufLen) {
                    Trace.WriteLine($"WMRTP Video Error SN {seqNum}: short Offset field");
                    return false;
                }
                payloadOffsetField = (buf[currentOffset] << 8) | buf[currentOffset + 1];
                currentOffset += 2;
            }

            // -------- Bit Field 3 (optional MAU Timing) --------
            // Only present on the first fragment / complete MAU per spec §3.2:
            //   "The fields in the MAU Timing section should only be specified in the
            //    RTP Payload Format header for the payload that contains the first fragment of a MAU."
            // We don't enforce this strictly — but timing should be ignored on F=0/F=2.
            if (b3pPresent) {
                if (currentOffset >= bufLen) {
                    Trace.WriteLine($"WMRTP Video Error SN {seqNum}: short Bit Field 3");
                    return false;
                }
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
                            Trace.WriteLine($"WMRTP Video Error SN {seqNum}: short Extension header");
                            return false;
                        }
                        byte extHeader = buf[currentOffset++];
                        bool lastExt   = (extHeader & 0x80) != 0;
                        // byte extType = (byte)(extHeader & 0x7F);
                        byte extLength = buf[currentOffset++];
                        currentOffset += extLength;
                        if (currentOffset > bufLen) {
                            Trace.WriteLine($"WMRTP Video Error SN {seqNum}: Extension overrun");
                            return false;
                        }
                        if (lastExt) break;
                    }
                }
                if (currentOffset > bufLen) return false;
            }

            // -------- Determine this payload's data byte range --------
            int payloadDataStart = currentOffset;
            int payloadDataEnd;
            if (opPresent) {
                // Spec §3.6.2: Offset is "the size of the MAU Timing section (if any) plus the
                // size of the current payload", measured from the byte after the Offset field.
                payloadDataEnd = offsetFieldStart + 2 + payloadOffsetField;
                if (payloadDataEnd < payloadDataStart || payloadDataEnd > bufLen) {
                    Trace.WriteLine($"WMRTP Video Error SN {seqNum}: Offset field {payloadOffsetField} → end {payloadDataEnd} out of range");
                    return false;
                }
            } else {
                // No Offset field → this payload runs to end of RTP payload.
                payloadDataEnd = bufLen;
            }
            int payloadDataLen = payloadDataEnd - payloadDataStart;
            if (payloadDataLen < 0) return false;

            // -------- Encryption handling --------
            // E bit valid only on F=1/F=3. We don't currently support DRM, so encrypted
            // payloads can't be decoded — drop them (and discard any partial buffer for the
            // same MAU so we don't deliver half-encrypted data to the decoder).
            if (firstOrComplete && eBit) {
                Trace.WriteLine($"WMRTP Video: SN {seqNum} E=1 (encrypted) — dropping payload (DRM not supported)");
                if (stream.Fragments != null) stream.Fragments = null;
                stream.PendingPostLossFlag = true;
                currentOffset = payloadDataEnd;
                return true;
            }

            // -------- Extract the payload bytes --------
            byte[] data;
            if (payloadDataLen > 0) {
                data = new byte[payloadDataLen];
                Buffer.BlockCopy(buf, payloadDataStart, data, 0, payloadDataLen);
            } else {
                data = new byte[0];
            }
            currentOffset = payloadDataEnd;

            // -------- Reassembly state machine --------
            switch (fragType) {
                case F_COMPLETE_MAU:
                    if (stream.Fragments != null) {
                        Trace.WriteLine($"WMRTP Video Warning SN {seqNum}: F=3 received while assembling — discarding {stream.Fragments.Count} prior frags");
                        stream.Fragments = null;
                    }
                    EmitMau(data, rtpTs, syncPoint: sBit, discontinuity: d1Bit || stream.PendingPostLossFlag,
                            encrypted: eBit, postLoss: stream.PendingPostLossFlag);
                    stream.PendingPostLossFlag = false;
                    emittedMau = true;
                    break;

                case F_FIRST_FRAGMENT:
                    if (stream.Fragments != null)
                        Trace.WriteLine($"WMRTP Video Warning SN {seqNum}: F=1 received while assembling — overwriting prior buffer");
                    stream.Fragments = new List<byte[]> { data };
                    stream.FirstFragmentSync     = sBit;
                    stream.FirstFragmentDiscont  = d1Bit;
                    stream.FirstFragmentEncrypt  = eBit;
                    stream.FirstFragmentTs       = rtpTs;
                    break;

                case F_MIDDLE_FRAGMENT:
                    if (stream.Fragments == null) {
                        Trace.WriteLine($"WMRTP Video Warning SN {seqNum}: F=0 with no buffer — dropping");
                        stream.PendingPostLossFlag = true;
                    } else {
                        stream.Fragments.Add(data);
                    }
                    break;

                case F_LAST_FRAGMENT:
                    if (stream.Fragments == null) {
                        Trace.WriteLine($"WMRTP Video Warning SN {seqNum}: F=2 with no buffer — dropping");
                        stream.PendingPostLossFlag = true;
                    } else {
                        stream.Fragments.Add(data);
                        try {
                            byte[] complete = Reassemble(stream.Fragments);
                            // Use the timestamp from the FIRST fragment per spec — that's where
                            // the timing section lived. Falls back to current rtpTs if zero.
                            uint mauTs = stream.FirstFragmentTs != 0 ? stream.FirstFragmentTs : rtpTs;
                            EmitMau(complete, mauTs,
                                    syncPoint: stream.FirstFragmentSync,
                                    discontinuity: stream.FirstFragmentDiscont || stream.PendingPostLossFlag,
                                    encrypted: stream.FirstFragmentEncrypt,
                                    postLoss: stream.PendingPostLossFlag);
                            stream.PendingPostLossFlag = false;
                            emittedMau = true;
                        } catch (Exception ex) {
                            Trace.WriteLine($"WMRTP Video Error SN {seqNum}: reassembly failed: {ex.Message}");
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
            NalUnitReady?.Invoke(this, new EventData {
                data           = mau,
                timestamp      = ts,
                SyncPoint      = syncPoint,
                Discontinuity  = discontinuity,
                Encrypted      = encrypted,
                PostLoss       = postLoss,
            });
        }

        /// <summary>Clear all per-SSRC state (e.g. on stream stop or seek).</summary>
        public void Reset() {
            _streams.Clear();
            Trace.WriteLine("WMRTP Video Depacketizer state cleared.");
        }
    }
}
