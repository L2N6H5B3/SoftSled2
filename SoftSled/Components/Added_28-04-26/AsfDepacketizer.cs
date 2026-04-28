using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SoftSled.Components {

    public class AsfDepacketizer {
        // Represents an extracted elementary payload
        public struct AsfPayload {
            public byte StreamNumber;
            public bool IsKeyFrame;
            public uint MediaObjectNumber;
            public uint OffsetIntoMediaObject;
            public uint ReplicatedDataLength;
            public ReadOnlyMemory<byte> ReplicatedData;
            public ReadOnlyMemory<byte> PayloadData;
        }

        private enum LengthType { None = 0, Byte = 1, Word = 2, DWord = 3 }

        /// <summary>
        /// Parses an ASF Data Packet (typically the RTP payload of x-wmf-pf/1000)
        /// </summary>
        public List<AsfPayload> ParsePacket(byte[] packetData) {
            var payloads = new List<AsfPayload>();
            ReadOnlySpan<byte> span = packetData;

            // 1. Error Correction Data [cite: 718, 719]
            byte b0 = span[0];
            bool errorCorrectionPresent = (b0 & 0x80) != 0; // bit 7 

            if (errorCorrectionPresent) {
                byte ecFlags = span[0];
                int ecLengthType = (ecFlags >> 5) & 0x03;
                int ecDataLen = ecFlags & 0x0F;

                // Advance past Error Correction Flags
                span = span.Slice(1);

                if (ecLengthType == 0) // Length is inline 
                {
                    span = span.Slice(ecDataLen);
                } else {
                    // Unlikely in x-wmf-pf, but handle gracefully
                    span = span.Slice(0);
                }
            }

            // 2. Payload Parsing Information
            byte lengthTypeFlags = span[0];
            byte propertyFlags = span[1];
            span = span.Slice(2);

            bool multiplePayloadsPresent = (lengthTypeFlags & 0x01) != 0;
            LengthType sequenceType = (LengthType)((lengthTypeFlags >> 1) & 0x03);
            LengthType paddingLengthType = (LengthType)((lengthTypeFlags >> 3) & 0x03);
            LengthType packetLengthType = (LengthType)((lengthTypeFlags >> 5) & 0x03);

            LengthType repDataLenType = (LengthType)((propertyFlags) & 0x03);
            LengthType offsetLenType = (LengthType)((propertyFlags >> 2) & 0x03);
            LengthType objectNumLenType = (LengthType)((propertyFlags >> 4) & 0x03);
            LengthType streamNumLenType = (LengthType)((propertyFlags >> 6) & 0x03);

            // Read variable length fields [cite: 756, 759, 762]
            uint packetLength = ReadVariableField(ref span, packetLengthType);
            uint sequence = ReadVariableField(ref span, sequenceType);
            uint paddingLength = ReadVariableField(ref span, paddingLengthType);

            // Read Send Time and Duration [cite: 767, 769]
            uint sendTime = BinaryPrimitives.ReadUInt32LittleEndian(span);
            span = span.Slice(4);
            ushort duration = BinaryPrimitives.ReadUInt16LittleEndian(span);
            span = span.Slice(2);

            // 3. Payload Data Extraction [cite: 770, 771]
            if (multiplePayloadsPresent) {
                // Multiple payloads parsing 
                byte payloadFlags = span[0];
                span = span.Slice(1);

                int numberOfPayloads = payloadFlags & 0x3F;
                LengthType payloadLengthType = (LengthType)((payloadFlags >> 6) & 0x03);

                for (int i = 0; i < numberOfPayloads; i++) {
                    payloads.Add(ParseSinglePayload(ref span, streamNumLenType, objectNumLenType, offsetLenType, repDataLenType, payloadLengthType, packetData));
                }
            } else {
                // Single payload parsing 
                // Payload length is inferred by remaining packet length minus padding 
                payloads.Add(ParseSinglePayload(ref span, streamNumLenType, objectNumLenType, offsetLenType, repDataLenType, LengthType.None, packetData, paddingLength));
            }

            return payloads;
        }

        private AsfPayload ParseSinglePayload(
            ref ReadOnlySpan<byte> span,
            LengthType streamNumLenType,
            LengthType objectNumLenType,
            LengthType offsetLenType,
            LengthType repDataLenType,
            LengthType payloadLenType,
            byte[] originalArray,
            uint paddingLength = 0) {
            var payload = new AsfPayload();

            // Stream Number & Key Frame Bit
            uint streamInfo = ReadVariableField(ref span, streamNumLenType);
            payload.StreamNumber = (byte)(streamInfo & 0x7F);
            payload.IsKeyFrame = (streamInfo & 0x80) != 0;

            payload.MediaObjectNumber = ReadVariableField(ref span, objectNumLenType);
            payload.OffsetIntoMediaObject = ReadVariableField(ref span, offsetLenType);
            payload.ReplicatedDataLength = ReadVariableField(ref span, repDataLenType);

            // Extract Replicated Data
            if (payload.ReplicatedDataLength > 0) {
                int repDataOffset = originalArray.Length - span.Length;
                payload.ReplicatedData = new ReadOnlyMemory<byte>(originalArray, repDataOffset, (int)payload.ReplicatedDataLength);
                span = span.Slice((int)payload.ReplicatedDataLength);
            }

            uint payloadDataLength = 0;
            if (payloadLenType != LengthType.None) {
                // Multiple payloads define their own length 
                payloadDataLength = ReadVariableField(ref span, payloadLenType);
            } else {
                // Single payload length is whatever is left minus padding 
                payloadDataLength = (uint)span.Length - paddingLength;
            }

            // Extract Payload Data
            int payloadOffset = originalArray.Length - span.Length;
            payload.PayloadData = new ReadOnlyMemory<byte>(originalArray, payloadOffset, (int)payloadDataLength);

            span = span.Slice((int)payloadDataLength);

            return payload;
        }

        private uint ReadVariableField(ref ReadOnlySpan<byte> span, LengthType type) {
            uint value = 0;
            switch (type) {
                case LengthType.Byte:
                    value = span[0];
                    span = span.Slice(1);
                    break;
                case LengthType.Word:
                    value = BinaryPrimitives.ReadUInt16LittleEndian(span);
                    span = span.Slice(2);
                    break;
                case LengthType.DWord:
                    value = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    span = span.Slice(4);
                    break;
                case LengthType.None:
                    value = 0;
                    break;
            }
            return value;
        }
    }
}

