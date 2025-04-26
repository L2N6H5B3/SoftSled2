using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SoftSled.WmrptHandling // New namespace
{
    /// <summary>
    /// Parses the WMRTP header and extracts the payload data.
    /// Handles basic fragmentation based on the F field.
    /// NOTE: This is a simplified implementation focusing on fragmentation.
    /// Robust handling of all optional fields, extensions, packet loss,
    /// and reordering requires more complex logic.
    /// </summary>
    public class WmrptDepacketizer {
        // --- WMRTP Header Bit Masks/Shifts (Based on Figures 6, 7, 8) ---

        // Bit Field 1 (Byte 0 of WMRTP Header)
        private const byte BF1_ST_MASK = 0x80; // Send Time Present
        private const byte BF1_CP_MASK = 0x40; // Correspondence Present
        private const byte BF1_R1_MASK = 0x20; // Reserved/Extension 1
        private const byte BF1_R2_MASK = 0x10; // Reserved/Extension 2
        private const byte BF1_R3_MASK = 0x08; // Reserved/Extension 3
        private const byte BF1_R4_MASK = 0x04; // Reserved 4
        private const byte BF1_R5_MASK = 0x02; // Reserved 5
        private const byte BF1_B2P_MASK = 0x01; // Bit Field 2 Present

        // Bit Field 2 (If present, follows Packet Specific Info section)
        private const byte BF2_F_SHIFT = 6;    // Fragmentation Field (2 bits)
        private const byte BF2_F_MASK = 0xC0;
        private const byte BF2_OP_MASK = 0x20; // Offset Present
        private const byte BF2_S_MASK = 0x10;  // Sync Point
        private const byte BF2_D1_MASK = 0x08; // Discontinuity 1
        private const byte BF2_D2_MASK = 0x04; // Droppable
        private const byte BF2_E_MASK = 0x02;  // Encryption
        private const byte BF2_B3P_MASK = 0x01; // Bit Field 3 Present

        // Bit Field 3 (If present, follows MAU Properties section)
        private const byte BF3_D3_MASK = 0x80; // Decode Time Present
        private const byte BF3_P_MASK = 0x40;  // Presentation Time Present
        private const byte BF3_N_MASK = 0x20;  // NPT Present
        private const byte BF3_R6_MASK = 0x10; // Reserved/Extension 6
        private const byte BF3_R7_MASK = 0x08; // Reserved/Extension 7
        private const byte BF3_R8_MASK = 0x04; // Reserved/Extension 8
        private const byte BF3_R9_MASK = 0x02; // Reserved/Extension 9
        private const byte BF3_X_MASK = 0x01;  // Extension Present
        // --- End WMRTP Header Bit Masks/Shifts ---

        // Buffer for reassembling fragmented NAL units
        // Key: SSRC (from RTP header) or potentially another unique identifier if needed
        // Value: List of byte arrays holding fragments
        private Dictionary<uint, List<byte[]>> _fragmentBuffers = new Dictionary<uint, List<byte[]>>();
        // TODO: Need a mechanism to time out and clear old/incomplete fragments

        /// <summary>
        /// Event raised when a complete NAL unit has been reassembled or received.
        /// The byte array contains the raw NAL unit data (without Annex B start code).
        /// </summary>
        public event EventHandler<byte[]> NalUnitReady;

        /// <summary>
        /// Processes the payload of a received RTP packet (containing WMRTP data).
        /// </summary>
        /// <param name="rtpPayload">The byte array containing the RTP payload (data AFTER the 12-byte RTP header).</param>
        /// <param name="rtpPayloadLength">The length of the data in rtpPayload.</param>
        /// <param name="rtpSsrc">The SSRC from the RTP header (used for fragment buffering).</param>
        /// <param name="rtpSequenceNumber">The sequence number from the RTP header (for logging/debugging).</param>
        public void ProcessWmrptPayload(byte[] rtpPayload, int rtpPayloadLength, uint rtpSsrc, ushort rtpSequenceNumber) {
            if (rtpPayload == null || rtpPayloadLength < 1) // Need at least Bit Field 1
            {
                Trace.WriteLine($"WMRTP Depacketizer: Received empty or null payload for SN {rtpSequenceNumber}.");
                return;
            }

            int currentOffset = 0; // Position within the rtpPayload buffer

            // --- Parse Packet Specific Info Section ---
            if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Payload too short for Bit Field 1."); return; }
            byte bitField1 = rtpPayload[currentOffset++];

            bool stPresent = (bitField1 & BF1_ST_MASK) != 0;
            bool cpPresent = (bitField1 & BF1_CP_MASK) != 0;
            bool r1Present = (bitField1 & BF1_R1_MASK) != 0;
            bool r2Present = (bitField1 & BF1_R2_MASK) != 0;
            bool r3Present = (bitField1 & BF1_R3_MASK) != 0;
            // R4, R5 are reserved
            bool b2pPresent = (bitField1 & BF1_B2P_MASK) != 0;

            // Skip optional fields in Packet Specific Info section
            if (stPresent) currentOffset += 4; // Skip Send Time (32 bits)
            if (cpPresent) currentOffset += 12; // Skip Correspondence (96 bits)
            if (r1Present) currentOffset += 4; // Skip Reserved/Extension 1 (32 bits)
            if (r2Present) currentOffset += 4; // Skip Reserved/Extension 2 (32 bits)
            if (r3Present) currentOffset += 4; // Skip Reserved/Extension 3 (32 bits)

            if (!b2pPresent) {
                // This should generally not happen if there's a payload, but handle it.
                // The rest of the payload *might* be data if B2P is 0? Spec is unclear here.
                // Assuming for now that if B2P=0, there's no MAU Properties or Timing info.
                // We might need to treat the remaining data as a complete payload if F=3 was implied.
                // For now, we'll assume B2P must be 1 if there's actual codec data.
                Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Bit Field 2 not present (B2P=0). Cannot process payload structure.");
                return;
            }

            // --- Parse MAU Properties Section ---
            if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Payload too short for Bit Field 2."); return; }
            byte bitField2 = rtpPayload[currentOffset++];

            int fragmentationType = (bitField2 & BF2_F_MASK) >> BF2_F_SHIFT; // 0, 1, 2, or 3
            bool opPresent = (bitField2 & BF2_OP_MASK) != 0;
            // bool sPresent = (bitField2 & BF2_S_MASK) != 0; // Sync Point (useful later)
            // bool d1Present = (bitField2 & BF2_D1_MASK) != 0; // Discontinuity (useful later)
            // bool d2Present = (bitField2 & BF2_D2_MASK) != 0; // Droppable (useful later)
            // bool ePresent = (bitField2 & BF2_E_MASK) != 0; // Encryption (useful later)
            bool b3pPresent = (bitField2 & BF2_B3P_MASK) != 0;

            ushort payloadOffsetField = 0; // Offset *within* the WMRTP structure
            if (opPresent) {
                if (currentOffset + 2 > rtpPayloadLength) { Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Payload too short for Offset field."); return; }
                // Read 16-bit offset (Network Byte Order - Big Endian)
                payloadOffsetField = (ushort)((rtpPayload[currentOffset] << 8) | rtpPayload[currentOffset + 1]);
                currentOffset += 2;
            }

            // --- Parse MAU Timing Section (if present) ---
            int timingSectionLength = 0;
            if (b3pPresent) {
                int timingStartOffset = currentOffset;
                if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Payload too short for Bit Field 3."); return; }
                byte bitField3 = rtpPayload[currentOffset++];

                bool d3Present = (bitField3 & BF3_D3_MASK) != 0;
                bool pPresent = (bitField3 & BF3_P_MASK) != 0;
                bool nPresent = (bitField3 & BF3_N_MASK) != 0;
                bool r6Present = (bitField3 & BF3_R6_MASK) != 0;
                bool r7Present = (bitField3 & BF3_R7_MASK) != 0;
                bool r8Present = (bitField3 & BF3_R8_MASK) != 0;
                bool r9Present = (bitField3 & BF3_R9_MASK) != 0;
                bool xPresent = (bitField3 & BF3_X_MASK) != 0;

                // Skip optional fields based on flags
                if (d3Present) currentOffset += 4; // Skip Decode Time (32 bits)
                if (pPresent) currentOffset += 4;  // Skip Presentation Time (32 bits)
                if (nPresent) currentOffset += 8;  // Skip NPT (64 bits)
                if (r6Present) currentOffset += 4; // Skip Reserved/Extension 6
                if (r7Present) currentOffset += 4; // Skip Reserved/Extension 7
                if (r8Present) currentOffset += 4; // Skip Reserved/Extension 8
                if (r9Present) currentOffset += 4; // Skip Reserved/Extension 9

                if (xPresent) {
                    // Parse Extension field(s) - COMPLEX, requires loop based on L bit
                    // For now, we just skip it if OP is present, or assume it goes to end if OP absent
                    // A proper implementation needs to parse Extension Length to skip correctly.
                    // This simplified version might fail if X is present AND OP is absent.
                    Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Extension field present but not fully parsed by this simplified depacketizer.");
                    // We'll rely on payloadOffsetField or end-of-packet to find payload start
                }
                timingSectionLength = currentOffset - timingStartOffset;
            }

            // --- Determine Payload Boundaries ---
            int payloadDataStartOffset = currentOffset; // Start of actual codec data
            int payloadDataLength = 0;

            if (opPresent) {
                // Offset field tells us the size of timing section + payload data
                int timingPlusPayloadSize = payloadOffsetField;
                payloadDataLength = timingPlusPayloadSize - timingSectionLength;
            } else {
                // No Offset field, payload extends to the end of the RTP payload data
                payloadDataLength = rtpPayloadLength - payloadDataStartOffset;
            }

            if (payloadDataLength < 0) {
                Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Calculated negative payload length ({payloadDataLength}). Header parsing failed.");
                return;
            }
            if (payloadDataStartOffset + payloadDataLength > rtpPayloadLength) {
                Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Calculated payload end ({payloadDataStartOffset + payloadDataLength}) exceeds buffer length ({rtpPayloadLength}). Header parsing failed.");
                return;
            }

            // --- Extract Payload Data ---
            byte[] currentPayloadData = new byte[payloadDataLength];
            if (payloadDataLength > 0) {
                Buffer.BlockCopy(rtpPayload, payloadDataStartOffset, currentPayloadData, 0, payloadDataLength);
            }


            // --- Handle Fragmentation ---
            List<byte[]> buffer = null;
            bool bufferExisted = _fragmentBuffers.TryGetValue(rtpSsrc, out buffer);

            switch (fragmentationType) {
                case 3: // Complete MAU (NAL Unit)
                    if (bufferExisted) {
                        Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Received complete NAL Unit (F=3) for SSRC {rtpSsrc}, but buffer already existed. Discarding previous fragments.");
                        _fragmentBuffers.Remove(rtpSsrc);
                    }
                    // Raise event with the complete NAL unit
                    OnNalUnitReady(currentPayloadData);
                    break;

                case 1: // First Fragment
                    if (bufferExisted) {
                        Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Received first fragment (F=1) for SSRC {rtpSsrc}, but buffer already existed. Starting new fragment sequence.");
                    }
                    // Start new buffer
                    buffer = new List<byte[]>();
                    buffer.Add(currentPayloadData);
                    _fragmentBuffers[rtpSsrc] = buffer;
                    break;

                case 0: // Middle Fragment
                    if (!bufferExisted) {
                        Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Received middle fragment (F=0) for SSRC {rtpSsrc}, but no buffer existed (first fragment lost?). Discarding.");
                    } else {
                        // Append to existing buffer
                        buffer.Add(currentPayloadData);
                    }
                    break;

                case 2: // Last Fragment
                    if (!bufferExisted) {
                        Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Received last fragment (F=2) for SSRC {rtpSsrc}, but no buffer existed (previous fragments lost?). Discarding.");
                    } else {
                        // Append last fragment
                        buffer.Add(currentPayloadData);
                        // Reassemble and raise event
                        try {
                            byte[] completeNalUnit = ReassembleFragments(buffer);
                            OnNalUnitReady(completeNalUnit);
                        } catch (Exception ex) {
                            Trace.WriteLine($"WMRTP Error SN {rtpSequenceNumber}: Failed to reassemble fragments for SSRC {rtpSsrc}: {ex.Message}");
                        } finally {
                            // Clear buffer for this SSRC
                            _fragmentBuffers.Remove(rtpSsrc);
                        }
                    }
                    break;
            }

            // TODO: Implement logic to handle multiple header/payload pairs if OP is present and indicates more data in the RTP packet.
            // This simplified version only processes the first header/payload found.
            if (opPresent && (payloadDataStartOffset + payloadDataLength < rtpPayloadLength)) {
                Trace.WriteLine($"WMRTP Warning SN {rtpSequenceNumber}: Multiple payloads indicated (OP=1) but only processing the first one in this simplified handler.");
            }
        }

        /// <summary>
        /// Reassembles fragments into a single byte array.
        /// </summary>
        private byte[] ReassembleFragments(List<byte[]> fragments) {
            if (fragments == null || fragments.Count == 0) {
                return new byte[0];
            }

            int totalLength = 0;
            foreach (byte[] frag in fragments) {
                totalLength += frag.Length;
            }

            byte[] result = new byte[totalLength];
            int currentPosition = 0;
            foreach (byte[] frag in fragments) {
                Buffer.BlockCopy(frag, 0, result, currentPosition, frag.Length);
                currentPosition += frag.Length;
            }
            return result;
        }

        /// <summary>
        /// Safely raises the NalUnitReady event.
        /// </summary>
        protected virtual void OnNalUnitReady(byte[] nalUnit) {
            if (nalUnit != null && nalUnit.Length > 0) {
                NalUnitReady?.Invoke(this, nalUnit);
            }
        }

        /// <summary>
        /// Clears any pending fragments (e.g., on stream stop).
        /// </summary>
        public void Reset() {
            _fragmentBuffers.Clear();
            Trace.WriteLine("WMRTP Depacketizer buffers cleared.");
        }
    }
}
