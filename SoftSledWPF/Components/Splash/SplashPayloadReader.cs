using System;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// Endianness-parameterised reader for MS-RRSP2 *payload* messages.
    ///
    /// Framing messages (handshake / Command / BufferInfo / MessageBatch /
    /// MessageBatchEntry) are always **network byte order** per spec sections
    /// 2.2.1-2.2.3 and are read with the existing big-endian helpers in
    /// <see cref="SoftSled.Components.Utility.DataUtilities"/>. Payload
    /// messages (section 2.2.4) are sent in *client byte order*, which the
    /// MS-DSPA <c>BIG</c> capability dictates:
    ///   BIG=True  -> big-endian payload
    ///   BIG=False -> little-endian payload
    ///
    /// The current SoftSled DevCaps advertises BIG=True so the live wire
    /// feeds us big-endian payload bytes today; this reader takes
    /// <paramref name="isBigEndian"/> at construction so flipping BIG later
    /// is a one-knob change.
    /// </summary>
    internal sealed class SplashPayloadReader {

        private readonly byte[] _buf;
        private readonly int    _baseOffset;
        private readonly int    _limit;
        private readonly bool   _bigEndian;
        private int             _pos;

        public SplashPayloadReader(byte[] buffer, int offset, int length, bool isBigEndian) {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || length < 0 || offset + length > buffer.Length)
                throw new ArgumentOutOfRangeException();
            _buf        = buffer;
            _baseOffset = offset;
            _limit      = offset + length;
            _bigEndian  = isBigEndian;
            _pos        = offset;
        }

        public bool IsBigEndian => _bigEndian;
        public int  Position    => _pos - _baseOffset;
        public int  Remaining   => _limit - _pos;
        public int  Length      => _limit - _baseOffset;

        public void Seek(int positionFromStart) {
            int abs = _baseOffset + positionFromStart;
            if (abs < _baseOffset || abs > _limit)
                throw new ArgumentOutOfRangeException(nameof(positionFromStart));
            _pos = abs;
        }

        public void Skip(int count) {
            EnsureAvailable(count);
            _pos += count;
        }

        public byte ReadByte() {
            EnsureAvailable(1);
            return _buf[_pos++];
        }

        public ushort ReadU16() {
            EnsureAvailable(2);
            byte a = _buf[_pos++], b = _buf[_pos++];
            return _bigEndian
                ? (ushort)((a << 8) | b)
                : (ushort)((b << 8) | a);
        }

        public short ReadI16() => unchecked((short)ReadU16());

        public uint ReadU32() {
            EnsureAvailable(4);
            byte a = _buf[_pos++], b = _buf[_pos++], c = _buf[_pos++], d = _buf[_pos++];
            return _bigEndian
                ? ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d
                : ((uint)d << 24) | ((uint)c << 16) | ((uint)b << 8) | a;
        }

        public int ReadI32() => unchecked((int)ReadU32());

        public float ReadFloat32() {
            uint bits = ReadU32();
            return SingleFromBits(bits);
        }

        private static unsafe float SingleFromBits(uint bits) {
            // BitConverter.Int32BitsToSingle isn't available on net461.
            return *((float*)&bits);
        }

        /// <summary>
        /// Read a 12-byte payload-message header (size / msgid / idObjectSubject).
        /// Note: <paramref name="_size"/> is the *total* message size including
        /// the 12-byte header itself.
        /// </summary>
        public void ReadMessageHeader(out uint _size, out int _msgid, out uint _idObjectSubject) {
            _size            = ReadU32();
            _msgid           = ReadI32();
            _idObjectSubject = ReadU32();
        }

        /// <summary>
        /// Read a 4-byte BLOBREF (size, offset). The blob bytes live at
        /// <c>bufferStart + offset</c> within the *enclosing payload buffer*
        /// (the one passed at construction).
        /// </summary>
        public void ReadBlobRef(out ushort size, out ushort offset) {
            size   = ReadU16();
            offset = ReadU16();
        }

        /// <summary>
        /// Read a Vector3 (12 B: three float32). Z is depth/ordering in the
        /// 2D scene-graph use; for visual position it's mostly 0.
        /// </summary>
        public void ReadVector3(out float x, out float y, out float z) {
            x = ReadFloat32();
            y = ReadFloat32();
            z = ReadFloat32();
        }

        /// <summary>
        /// Read an ARGB color (4 B). Byte order on the wire is A,R,G,B
        /// regardless of endian — these are independent bytes, not a packed
        /// integer.
        /// </summary>
        public void ReadArgb(out byte a, out byte r, out byte g, out byte b) {
            EnsureAvailable(4);
            a = _buf[_pos++];
            r = _buf[_pos++];
            g = _buf[_pos++];
            b = _buf[_pos++];
        }

        /// <summary>
        /// Read a ColorF (4 floats: a,r,g,b in 0..1 range).
        /// </summary>
        public void ReadColorF(out float a, out float r, out float g, out float b) {
            a = ReadFloat32();
            r = ReadFloat32();
            g = ReadFloat32();
            b = ReadFloat32();
        }

        /// <summary>
        /// Return a copy of bytes referenced by a BLOBREF whose
        /// (size, offset) point inside this reader's payload buffer.
        /// </summary>
        public byte[] ResolveBlob(ushort size, ushort offset) {
            int abs = _baseOffset + offset;
            if (offset == 0 && size == 0) return new byte[0];
            if (abs < _baseOffset || abs + size > _limit)
                throw new InvalidOperationException(
                    $"BLOBREF (size={size}, offset={offset}) escapes payload (len={Length})");
            byte[] copy = new byte[size];
            Buffer.BlockCopy(_buf, abs, copy, 0, size);
            return copy;
        }

        private void EnsureAvailable(int n) {
            if (_limit - _pos < n)
                throw new InvalidOperationException(
                    $"SplashPayloadReader: tried to read {n} byte(s), only {_limit - _pos} remaining");
        }
    }
}
