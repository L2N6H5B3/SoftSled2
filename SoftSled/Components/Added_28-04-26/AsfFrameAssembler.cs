using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace SoftSled.Components {


    public class AsfFrameAssembler {
        private class FrameBuffer {
            public byte[] Data;
            public uint TotalExpectedSize;
            public uint BytesReceived;
            public uint PresentationTime;
        }

        // Tracks incomplete frames by their Media Object Number
        private Dictionary<uint, FrameBuffer> _pendingFrames = new Dictionary<uint, FrameBuffer>();

        // The stream we will hand to libVLC
        private Stream _outputStream;

        // We need to filter by stream number (e.g., only grab the Main Video stream)
        private byte _targetStreamNumber;

        public AsfFrameAssembler(Stream outputStream, byte targetStreamNumber) {
            _outputStream = outputStream;
            _targetStreamNumber = targetStreamNumber;
        }

        public void ProcessPayload(AsfDepacketizer.AsfPayload payload) {
            // Only process payloads for our target stream (e.g., video)
            if (payload.StreamNumber != _targetStreamNumber)
                return;

            // Ensure we have replicated data to know the frame size
            if (payload.ReplicatedDataLength < 8)
                return; // Invalid or compressed payload, needs different handling

            // Parse Replicated Data (Bytes 0-3: Object Size, Bytes 4-7: Presentation Time)
            var repDataSpan = payload.ReplicatedData.Span;
            uint mediaObjectSize = BinaryPrimitives.ReadUInt32LittleEndian(repDataSpan.Slice(0, 4));
            uint presentationTime = BinaryPrimitives.ReadUInt32LittleEndian(repDataSpan.Slice(4, 4));

            // Get or create the buffer for this specific frame
            if (!_pendingFrames.TryGetValue(payload.MediaObjectNumber, out FrameBuffer frame)) {
                frame = new FrameBuffer {
                    Data = new byte[mediaObjectSize],
                    TotalExpectedSize = mediaObjectSize,
                    BytesReceived = 0,
                    PresentationTime = presentationTime
                };
                _pendingFrames[payload.MediaObjectNumber] = frame;
            }

            // Copy the payload chunk into the exact correct offset of the frame buffer
            payload.PayloadData.Span.CopyTo(frame.Data.AsSpan((int)payload.OffsetIntoMediaObject));
            frame.BytesReceived += (uint)payload.PayloadData.Length;

            // Is the frame complete?
            if (frame.BytesReceived >= frame.TotalExpectedSize) {
                // We have a complete H.264 or AC3 frame!
                PushFrameToPlayer(frame);

                // Cleanup
                _pendingFrames.Remove(payload.MediaObjectNumber);
            }
        }

        private void PushFrameToPlayer(FrameBuffer frame) {
            // Write the completed frame bytes to the pipe that libVLC is reading from
            _outputStream.Write(frame.Data, 0, frame.Data.Length);
            _outputStream.Flush();

            // Note: In raw elementary streams, there are no timestamps. 
            // We have frame.PresentationTime, but raw H.264 doesn't have a place to put it.
            // We are relying on libVLC to play the bytes as fast as they arrive at the correct framerate.
        }
    }
}
