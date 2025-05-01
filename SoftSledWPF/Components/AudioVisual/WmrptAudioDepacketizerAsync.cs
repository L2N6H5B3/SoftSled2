//using System;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.IO;
//using System.Runtime.InteropServices;
//using System.Threading;
//using System.Threading.Tasks;

//namespace SoftSled.Components.AudioVisual {
//    /// <summary>
//    /// Parses the WMRTP header for audio streams (like PCM/WAV encapsulated in x-wmf-pf)
//    /// and extracts the raw audio payload data. Handles fragmentation based on the F field.
//    /// NOTE: This is a simplified implementation focusing on fragmentation.
//    /// Robust handling of all optional fields, extensions, packet loss,
//    /// and reordering requires more complex logic.
//    /// </summary>
//    public class WmrptAudioDepacketizer {
//        // --- WMRTP Header Bit Masks/Shifts (Based on Figures 6, 7, 8) ---
//        // (These are identical to the video version)
//        private const byte BF1_ST_MASK = 0x80; // Send Time Present
//        private const byte BF1_CP_MASK = 0x40; // Correspondence Present
//        private const byte BF1_R1_MASK = 0x20; // Reserved/Extension 1
//        private const byte BF1_R2_MASK = 0x10; // Reserved/Extension 2
//        private const byte BF1_R3_MASK = 0x08; // Reserved/Extension 3
//        private const byte BF1_R4_MASK = 0x04; // Reserved 4
//        private const byte BF1_R5_MASK = 0x02; // Reserved 5
//        private const byte BF1_B2P_MASK = 0x01; // Bit Field 2 Present

//        private const byte BF2_F_SHIFT = 6;    // Fragmentation Field (2 bits)
//        private const byte BF2_F_MASK = 0xC0;
//        private const byte BF2_OP_MASK = 0x20; // Offset Present
//        private const byte BF2_S_MASK = 0x10;  // Sync Point
//        private const byte BF2_D1_MASK = 0x08; // Discontinuity 1
//        private const byte BF2_D2_MASK = 0x04; // Droppable
//        private const byte BF2_E_MASK = 0x02;  // Encryption
//        private const byte BF2_B3P_MASK = 0x01; // Bit Field 3 Present

//        private const byte BF3_D3_MASK = 0x80; // Decode Time Present
//        private const byte BF3_P_MASK = 0x40;  // Presentation Time Present
//        private const byte BF3_N_MASK = 0x20;  // NPT Present
//        private const byte BF3_R6_MASK = 0x10; // Reserved/Extension 6
//        private const byte BF3_R7_MASK = 0x08; // Reserved/Extension 7
//        private const byte BF3_R8_MASK = 0x04; // Reserved/Extension 8
//        private const byte BF3_R9_MASK = 0x02; // Reserved/Extension 9
//        private const byte BF3_X_MASK = 0x01;  // Extension Present
//        // --- End WMRTP Header Bit Masks/Shifts ---

//        // Buffer for reassembling fragmented audio data
//        private Dictionary<uint, List<byte[]>> _fragmentBuffers = new Dictionary<uint, List<byte[]>>();

//        /// <summary>
//        /// Event raised when a complete audio frame/chunk has been reassembled or received.
//        /// The byte array contains the raw audio data (e.g., PCM samples).
//        /// </summary>
//        public event EventHandler<byte[]> AudioDataReady; // Renamed event

//        /// <summary>
//        /// Asynchronously processes the payload of a received RTP packet (containing WMRTP data).
//        /// </summary>
//        /// <param name="rtpPayload">The byte array containing the RTP payload (data AFTER the 12-byte RTP header).</param>
//        /// <param name="rtpPayloadLength">The length of the data in rtpPayload.</param>
//        /// <param name="rtpSsrc">The SSRC from the RTP header (used for fragment buffering).</param>
//        /// <param name="rtpSequenceNumber">The sequence number from the RTP header (for logging/debugging).</param>
//        /// <param name="rtpTimestamp">The Timestamp from the RTP header (for sync).</param>
//        /// <returns>A Task representing the asynchronous operation.</returns>
//        public async Task ProcessWmrptPayloadAsync(byte[] rtpPayload, int rtpPayloadLength, uint rtpSsrc, ushort rtpSequenceNumber, uint rtpTimestamp) {
//            // Wrap the synchronous logic in Task.Run to ensure it doesn't block the caller if needed,
//            // or just execute it directly if the overhead of Task.Run is undesirable.
//            // Option 1: Execute directly (method runs synchronously but returns a Task)
//            //ProcessWmrptPayloadInternal(rtpPayload, rtpPayloadLength, rtpSsrc, rtpSequenceNumber, rtpTimestamp);
//            //await Task.CompletedTask; // Satisfies the async requirement without actual async work

//            // Option 2: Use Task.Run to offload CPU-bound work (useful if caller is UI thread)
//            await Task.Run(() => ProcessWmrptPayloadInternal(rtpPayload, rtpPayloadLength, rtpSsrc, rtpSequenceNumber, rtpTimestamp));
//        }

//        /// <summary>
//        /// Processes the payload of a received RTP packet (containing WMRTP data).
//        /// </summary>
//        /// <param name="rtpPayload">The byte array containing the RTP payload (data AFTER the 12-byte RTP header).</param>
//        /// <param name="rtpPayloadLength">The length of the data in rtpPayload.</param>
//        /// <param name="rtpSsrc">The SSRC from the RTP header (used for fragment buffering).</param>
//        /// <param name="rtpSequenceNumber">The sequence number from the RTP header (for logging/debugging).</param>
//        /// <param name="rtpTimestamp">The Timestamp from the RTP header (for sync).</param>
//        public void ProcessWmrptPayloadInternal(byte[] rtpPayload, int rtpPayloadLength, uint rtpSsrc, ushort rtpSequenceNumber, uint rtpTimestamp) // Kept ushort for sequence number
//        {
//            if (rtpPayload == null || rtpPayloadLength < 1) // Need at least Bit Field 1
//            {
//                // Trace.WriteLine($"WMRTP Audio Depack: Received empty or null payload for SN {rtpSequenceNumber}."); // Less verbose
//                return;
//            }

//            // --- Added Logging ---
//            // Trace.WriteLine($"--- Processing WMRTP Audio SN {rtpSequenceNumber}, SSRC {rtpSsrc}, RTP Payload Length {rtpPayloadLength} ---");
//            // ---

//            int initialOffset = 0;
//            int currentOffset = initialOffset; // Position within the rtpPayload buffer

//            // --- Parse Packet Specific Info Section ---
//            if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Payload too short for Bit Field 1."); return; }
//            byte bitField1 = rtpPayload[currentOffset++];
//            // --- Added Logging ---
//            // Trace.WriteLine($"  BF1: 0x{bitField1:X2}");
//            // ---

//            bool stPresent = (bitField1 & BF1_ST_MASK) != 0;
//            bool cpPresent = (bitField1 & BF1_CP_MASK) != 0;
//            bool r1Present = (bitField1 & BF1_R1_MASK) != 0;
//            bool r2Present = (bitField1 & BF1_R2_MASK) != 0;
//            bool r3Present = (bitField1 & BF1_R3_MASK) != 0;
//            bool b2pPresent = (bitField1 & BF1_B2P_MASK) != 0;
//            if (stPresent) currentOffset += 4;
//            if (cpPresent) currentOffset += 12;
//            if (r1Present) currentOffset += 4;
//            if (r2Present) currentOffset += 4;
//            if (r3Present) currentOffset += 4;

//            // --- Added Logging ---
//            // Trace.WriteLine($"  Offset after Packet Specific: {currentOffset}");
//            // ---

//            if (!b2pPresent) {
//                Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Bit Field 2 not present (B2P=0). Cannot process payload structure.");
//                return;
//            }

//            // --- Parse MAU Properties Section ---
//            if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Payload too short for Bit Field 2."); return; }
//            byte bitField2 = rtpPayload[currentOffset++];
//            // --- Added Logging ---
//            // Trace.WriteLine($"  BF2: 0x{bitField2:X2}");
//            // ---

//            int fragmentationType = (bitField2 & BF2_F_MASK) >> BF2_F_SHIFT;
//            bool opPresent = (bitField2 & BF2_OP_MASK) != 0;
//            bool b3pPresent = (bitField2 & BF2_B3P_MASK) != 0;
//            ushort payloadOffsetField = 0; // This is the 'Offset' field value from the spec
//            if (opPresent) {
//                if (currentOffset + 2 > rtpPayloadLength) { Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Payload too short for Offset field."); return; }
//                payloadOffsetField = (ushort)((rtpPayload[currentOffset] << 8) | rtpPayload[currentOffset + 1]);
//                currentOffset += 2;
//                // --- Added Logging ---
//                // Trace.WriteLine($"  OP=1, Offset Field Value: {payloadOffsetField}");
//                // ---
//            }
//            // --- Added Logging ---
//            // else { Trace.WriteLine($"  OP=0"); }
//            // Trace.WriteLine($"  F={fragmentationType}, B3P={b3pPresent}");
//            // Trace.WriteLine($"  Offset after MAU Properties: {currentOffset}");
//            // ---

//            // --- Parse MAU Timing Section (if present) ---
//            int timingSectionLength = 0;
//            int timingStartOffset = currentOffset; // Remember where timing section starts
//            if (b3pPresent) {
//                if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Payload too short for Bit Field 3."); return; }
//                byte bitField3 = rtpPayload[currentOffset++];
//                // --- Added Logging ---
//                // Trace.WriteLine($"  BF3: 0x{bitField3:X2}");
//                // ---

//                bool d3Present = (bitField3 & BF3_D3_MASK) != 0;
//                bool pPresent = (bitField3 & BF3_P_MASK) != 0;
//                bool nPresent = (bitField3 & BF3_N_MASK) != 0;
//                bool r6Present = (bitField3 & BF3_R6_MASK) != 0;
//                bool r7Present = (bitField3 & BF3_R7_MASK) != 0;
//                bool r8Present = (bitField3 & BF3_R8_MASK) != 0;
//                bool r9Present = (bitField3 & BF3_R9_MASK) != 0;
//                bool xPresent = (bitField3 & BF3_X_MASK) != 0;
//                if (d3Present) currentOffset += 4;
//                if (pPresent) currentOffset += 4;
//                if (nPresent) currentOffset += 8;
//                if (r6Present) currentOffset += 4;
//                if (r7Present) currentOffset += 4;
//                if (r8Present) currentOffset += 4;
//                if (r9Present) currentOffset += 4;
//                if (xPresent) {
//                    // --- Proper Extension Parsing (Basic) ---
//                    int extStartOffset = currentOffset;
//                    while (currentOffset < rtpPayloadLength) // Loop through extension collections
//                    {
//                        byte extHeader = rtpPayload[currentOffset++];
//                        bool lastExt = (extHeader & 0x80) != 0;
//                        // byte extType = (byte)(extHeader & 0x7F); // If needed
//                        if (currentOffset >= rtpPayloadLength) { Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Payload too short for Extension Length."); break; }
//                        byte extLength = rtpPayload[currentOffset++];
//                        // Skip extension data
//                        currentOffset += extLength;
//                        if (currentOffset > rtpPayloadLength) { Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Extension Length ({extLength}) exceeds remaining payload."); break; }
//                        if (lastExt) break; // Exit loop if L bit was set
//                    }
//                    // --- End Proper Extension Parsing ---
//                    // Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Extension field present but content ignored by this depacketizer.");
//                }
//                timingSectionLength = currentOffset - timingStartOffset;
//                // --- Added Logging ---
//                // Trace.WriteLine($"  Timing Section Length: {timingSectionLength}");
//                // ---
//            }
//            // --- Added Logging ---
//            // else { Trace.WriteLine($"  B3P=0, No Timing Section"); }
//            // Trace.WriteLine($"  Offset after Timing Section (Payload Start): {currentOffset}");
//            // ---

//            // --- Determine Payload Boundaries ---
//            int payloadDataStartOffset = currentOffset; // Start of actual codec data
//            int payloadDataLength = 0;

//            if (opPresent) {
//                // Offset field value is size of (Timing Section + Payload Data)
//                // It's counted from the byte *after* the Offset field itself.
//                int offsetFieldStart = timingStartOffset - 2; // Start of the 16-bit Offset field
//                int endOfPayloadOffset = offsetFieldStart + 2 + payloadOffsetField; // Absolute offset in buffer where payload ends

//                // Calculate length based on start and end offsets
//                payloadDataLength = endOfPayloadOffset - payloadDataStartOffset;

//                // --- Added Logging ---
//                // Trace.WriteLine($"  Payload Length (OP=1): EndOffset ({endOfPayloadOffset}) - StartOffset ({payloadDataStartOffset}) = {payloadDataLength}");
//                // ---
//            } else {
//                // No Offset field, payload extends to the end of the RTP payload data
//                payloadDataLength = rtpPayloadLength - payloadDataStartOffset;
//                // --- Added Logging ---
//                // Trace.WriteLine($"  Payload Length (OP=0): RtpPayloadLength ({rtpPayloadLength}) - StartOffset ({payloadDataStartOffset}) = {payloadDataLength}");
//                // ---
//            }

//            if (payloadDataLength < 0) {
//                Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Calculated negative payload length ({payloadDataLength}). Header parsing failed.");
//                return;
//            }
//            // Check if calculated end exceeds buffer only if length is positive
//            if (payloadDataLength > 0 && payloadDataStartOffset + payloadDataLength > rtpPayloadLength) {
//                Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Calculated payload end ({payloadDataStartOffset + payloadDataLength}) exceeds buffer length ({rtpPayloadLength}). Header parsing failed.");
//                // Optionally log the header bytes for debugging
//                string headerBytes = BitConverter.ToString(rtpPayload, 0, Math.Min(payloadDataStartOffset, 32)).Replace("-", " ");
//                Trace.WriteLine($"  Header bytes: {headerBytes}");
//                return;
//            }

//            // --- Added Logging ---
//            //Trace.WriteLine($"WMRTP Audio SN {rtpSequenceNumber}: F={fragmentationType}, OP={opPresent}, B3P={b3pPresent}, StartOffset={payloadDataStartOffset}, Calc Length={payloadDataLength}");
//            // ---

//            // --- Extract Payload Data ---
//            byte[] currentPayloadData = new byte[payloadDataLength];
//            if (payloadDataLength > 0) {
//                Buffer.BlockCopy(rtpPayload, payloadDataStartOffset, currentPayloadData, 0, payloadDataLength);
//            } else {
//                // Don't process empty payloads further
//                // Trace.WriteLine($"WMRTP Audio SN {rtpSequenceNumber}: Skipping zero length payload.");
//                return;
//            }


//            // --- Handle Fragmentation ---
//            // (Identical logic as before, but raises AudioDataReady)
//            List<byte[]> buffer = null;
//            bool bufferExisted = _fragmentBuffers.TryGetValue(rtpSsrc, out buffer);

//            switch (fragmentationType) {
//                case 3: // Complete Audio Chunk
//                    if (bufferExisted) {
//                        Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Received complete chunk (F=3) for SSRC {rtpSsrc}, discarding previous fragments.");
//                        _fragmentBuffers.Remove(rtpSsrc);
//                    }
//                    OnAudioDataReady(currentPayloadData);
//                    break;

//                case 1: // First Fragment
//                    if (bufferExisted) {
//                        Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Received first fragment (F=1) for SSRC {rtpSsrc}, overwriting previous buffer.");
//                    }
//                    buffer = new List<byte[]>();
//                    buffer.Add(currentPayloadData);
//                    _fragmentBuffers[rtpSsrc] = buffer;
//                    break;

//                case 0: // Middle Fragment
//                    if (!bufferExisted) {
//                        Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Received middle fragment (F=0) for SSRC {rtpSsrc}, but no buffer existed. Discarding.");
//                    } else {
//                        buffer.Add(currentPayloadData);
//                    }
//                    break;

//                case 2: // Last Fragment
//                    if (!bufferExisted) {
//                        Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Received last fragment (F=2) for SSRC {rtpSsrc}, but no buffer existed. Discarding.");
//                    } else {
//                        buffer.Add(currentPayloadData);
//                        try {
//                            byte[] completeAudioChunk = ReassembleFragments(buffer);
//                            OnAudioDataReady(completeAudioChunk);
//                        } catch (Exception ex) {
//                            Trace.WriteLine($"WMRTP Audio Error SN {rtpSequenceNumber}: Failed to reassemble fragments for SSRC {rtpSsrc}: {ex.Message}");
//                        } finally {
//                            _fragmentBuffers.Remove(rtpSsrc);
//                        }
//                    }
//                    break;
//            }

//            // Basic handling for multiple payloads (OP=1) - just logs a warning
//            if (opPresent && (payloadDataStartOffset + payloadDataLength < rtpPayloadLength)) {
//                Trace.WriteLine($"WMRTP Audio Warning SN {rtpSequenceNumber}: Multiple payloads indicated (OP=1) but only processing the first one.");
//            }
//        }

//        /// <summary>
//        /// Reassembles fragments into a single byte array.
//        /// </summary>
//        private byte[] ReassembleFragments(List<byte[]> fragments) {
//            // Identical implementation
//            if (fragments == null || fragments.Count == 0) return new byte[0];
//            int totalLength = 0;
//            foreach (byte[] frag in fragments) totalLength += frag.Length;
//            byte[] result = new byte[totalLength];
//            int currentPosition = 0;
//            foreach (byte[] frag in fragments) {
//                Buffer.BlockCopy(frag, 0, result, currentPosition, frag.Length);
//                currentPosition += frag.Length;
//            }
//            return result;
//        }

//        /// <summary>
//        /// Safely raises the AudioDataReady event.
//        /// </summary>
//        protected virtual void OnAudioDataReady(byte[] audioData) // Renamed method
//        {
//            if (audioData != null && audioData.Length > 0) {
//                // --- Added Logging ---
//                // Trace.WriteLine($"WMRTP Audio Depack: Outputting complete audio chunk, Length={audioData.Length}");
//                // ---
//                // Check alignment before raising event
//                if (audioData.Length % 4 != 0) // Assuming 16-bit stereo (4 bytes per frame)
//                {
//                    Trace.WriteLine($"WMRTP Audio Depack WARNING: Outputting audio chunk with length {audioData.Length}, which is not divisible by block alignment (4).");
//                }
//                // --- End Added Logging ---

//                AudioDataReady?.Invoke(this, audioData); // Raise renamed event
//            }
//        }

//        /// <summary>
//        /// Clears any pending fragments (e.g., on stream stop).
//        /// </summary>
//        public void Reset() {
//            // Identical implementation
//            _fragmentBuffers.Clear();
//            Trace.WriteLine("WMRTP Audio Depacketizer buffers cleared.");
//        }
//    }
//}
