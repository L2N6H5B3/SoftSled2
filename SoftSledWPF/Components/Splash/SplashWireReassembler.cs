using System;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// Reassembles raw bytes off the <c>splash</c> virtual channel into the
    /// discrete MS-RRSP2 framing units defined in [MS-RRSP2] sections
    /// 2.2.1-2.2.3:
    ///
    ///   1. <see cref="WireState.AwaitingServerHandshake"/> — server's 44-byte
    ///      <c>RemoteServerInformation</c> reply (only ever once).
    ///   2. <see cref="WireState.AwaitingCommand"/> — 4-byte command. Either
    ///      <c>1</c> (Buffer follows) or <c>2</c> (Shutdown).
    ///   3. <see cref="WireState.AwaitingBufferInfo"/> — 20-byte BufferInfo.
    ///   4. <see cref="WireState.AwaitingBufferPayload"/> — exactly
    ///      <c>cbSizeBuffer</c> bytes of payload.
    ///
    /// All framing fields are big-endian (network byte order) regardless of
    /// the DSPA <c>BIG</c> capability — that only affects the *payload*
    /// content that this class hands off via <see cref="BufferReady"/>.
    ///
    /// Buffers can span any number of underlying VC chunks (RDP may fragment
    /// at any byte boundary). The previous implementation in
    /// <c>VirtualChannelSplashHandler.cs</c> assumed one chunk == one header,
    /// which is fragile; this class buffers across chunk boundaries and only
    /// emits events once a complete unit has been read.
    /// </summary>
    internal sealed class SplashWireReassembler {

        // -------- public surface --------

        public event EventHandler<ServerHandshakeArgs> ServerHandshakeReceived;
        public event EventHandler<BufferReadyArgs>    BufferReady;
        public event EventHandler                     ShutdownReceived;
        public event EventHandler<ParseErrorArgs>     ParseError;

        public WireState State => _state;

        /// <summary>Reset to a fresh state — VC just opened or reopened.</summary>
        public void Reset() {
            _buf      = new byte[0];
            _writePos = 0;
            _readPos  = 0;
            _state    = WireState.AwaitingServerHandshake;
            _pendingBufferInfo = default(BufferInfo);
        }

        /// <summary>Push the next chunk of VC bytes through the parser.</summary>
        public void Push(byte[] chunk, int offset, int length) {
            if (chunk == null || length <= 0) return;
            Append(chunk, offset, length);
            DrainStateMachine();
        }

        public void Push(byte[] chunk) => Push(chunk, 0, chunk?.Length ?? 0);

        // -------- state --------

        private byte[] _buf      = new byte[0];
        private int    _writePos = 0;
        private int    _readPos  = 0;
        private WireState _state = WireState.AwaitingServerHandshake;
        private BufferInfo _pendingBufferInfo;

        private void Append(byte[] chunk, int offset, int length) {
            // Compact occasionally so the buffer doesn't grow unbounded.
            // Cheap: only memmove when we've consumed > 64 KiB worth or the
            // unread tail is small relative to consumed prefix.
            if (_readPos > 65536 || (_readPos > 4096 && _writePos - _readPos < _readPos / 4)) {
                int kept = _writePos - _readPos;
                Buffer.BlockCopy(_buf, _readPos, _buf, 0, kept);
                _writePos = kept;
                _readPos  = 0;
            }
            int need = _writePos + length;
            if (need > _buf.Length) {
                int grow = _buf.Length == 0 ? 4096 : _buf.Length;
                while (grow < need) grow *= 2;
                byte[] bigger = new byte[grow];
                if (_writePos > 0) Buffer.BlockCopy(_buf, 0, bigger, 0, _writePos);
                _buf = bigger;
            }
            Buffer.BlockCopy(chunk, offset, _buf, _writePos, length);
            _writePos += length;
        }

        private int Available => _writePos - _readPos;

        private void DrainStateMachine() {
            // Loop as long as we have enough bytes to advance.
            while (true) {
                switch (_state) {
                    case WireState.AwaitingServerHandshake:
                        // The WMC server prepends an undocumented 8-byte
                        // preamble before the RemoteServerInformation
                        // (`50 F1 23 00 00 00 00 00` observed in capture
                        // — possibly a session token; never seen in the
                        // spec). Rather than hard-code 8 bytes, we
                        // align to the well-known magic 0x19740721 which
                        // sits at offset +8 within the handshake (the
                        // dwMagic field at handshake-byte 8..11). The
                        // cbSize field lives 8 bytes BEFORE that, so we
                        // back up to wherever that lands.
                        if (Available < 48) return; // need at least 4(preamble)+12(magic-window)+36(handshake)
                        if (!TryAlignToHandshakeMagic()) return; // need more data to find magic
                        EmitServerHandshake();
                        _state = WireState.AwaitingCommand;
                        break;

                    case WireState.AwaitingCommand:
                        if (Available < 4) return;
                        uint cmd = ReadU32BE();
                        if (cmd == 1) {
                            _state = WireState.AwaitingBufferInfo;
                        } else if (cmd == 2) {
                            try { ShutdownReceived?.Invoke(this, EventArgs.Empty); } catch { }
                            _state = WireState.ShutdownReceived;
                            return;
                        } else {
                            RaiseParseError($"Unknown command 0x{cmd:X8} at readPos={_readPos - 4}");
                            // Treat as fatal: subsequent bytes are undefined.
                            _state = WireState.ShutdownReceived;
                            return;
                        }
                        break;

                    case WireState.AwaitingBufferInfo:
                        if (Available < 20) return;
                        _pendingBufferInfo.IdContextSrc  = ReadU32BE();
                        _pendingBufferInfo.IdContextDest = ReadU32BE();
                        _pendingBufferInfo.IdBuffer      = ReadU32BE();
                        _pendingBufferInfo.NFlags        = ReadU32BE();
                        _pendingBufferInfo.CbSizeBuffer  = ReadU32BE();
                        if (_pendingBufferInfo.CbSizeBuffer == 0) {
                            // Empty buffer — emit anyway then go back for next command.
                            EmitBuffer(new byte[0], 0, 0);
                            _state = WireState.AwaitingCommand;
                        } else {
                            _state = WireState.AwaitingBufferPayload;
                        }
                        break;

                    case WireState.AwaitingBufferPayload:
                        uint need = _pendingBufferInfo.CbSizeBuffer;
                        if ((uint)Available < need) return;
                        EmitBuffer(_buf, _readPos, (int)need);
                        _readPos += (int)need;
                        _state    = WireState.AwaitingCommand;
                        break;

                    case WireState.ShutdownReceived:
                        return;
                }
            }
        }

        private uint ReadU32BE() {
            byte a = _buf[_readPos++], b = _buf[_readPos++], c = _buf[_readPos++], d = _buf[_readPos++];
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
        }

        /// <summary>
        /// Search forward for <c>dwMagic = 0x19740721</c> within the next
        /// ~64 bytes and reposition <see cref="_readPos"/> at the start of
        /// the handshake (i.e. 8 bytes before the magic, where cbSize lives).
        /// Returns false if we need more bytes to find the magic.
        ///
        /// Defence in depth: once we find the magic, also peek backwards
        /// to verify the preceding 4 bytes are <c>dwVersion = 0x00010006</c>.
        /// A naked magic match deep in payload bytes is astronomically
        /// unlikely (1 in 4 billion), but the version check costs nothing
        /// and rules out an "alignment lock onto garbage" failure mode.
        /// </summary>
        private bool TryAlignToHandshakeMagic() {
            const uint Magic    = 0x19740721u;
            const uint Version  = 0x00010006u;
            int searchEnd = System.Math.Min(_writePos - 4, _readPos + 64);
            for (int p = _readPos + 8; p <= searchEnd; p++) {
                uint v = ((uint)_buf[p] << 24) | ((uint)_buf[p + 1] << 16) |
                         ((uint)_buf[p + 2] << 8) | _buf[p + 3];
                if (v != Magic) continue;
                // Verify dwVersion precedes the magic — only commit on
                // both matching.
                int verPos = p - 4;
                if (verPos < _readPos) continue; // not enough lead-in to check; keep searching
                uint ver = ((uint)_buf[verPos] << 24) | ((uint)_buf[verPos + 1] << 16) |
                           ((uint)_buf[verPos + 2] << 8) | _buf[verPos + 3];
                if (ver != Version) {
                    // Magic matched but version didn't — almost
                    // certainly a coincidental byte run inside a preamble.
                    // Keep searching past this hit.
                    continue;
                }
                // dwMagic is 8 bytes into the handshake (after cbSize +
                // dwVersion). Back up 8 to land on cbSize.
                int preamble = (p - 8) - _readPos;
                if (preamble > 0) {
                    RaiseParseError($"Skipping {preamble} B handshake preamble (likely a WMC session prefix)");
                }
                _readPos = p - 8;
                return true;
            }
            // Either we don't have enough bytes yet, or the magic is past
            // our search window. If the window is full and we still didn't
            // find it, bail with an error.
            if (_writePos - _readPos > 64) {
                RaiseParseError("Handshake magic+version not found in first 64 B of server stream");
                _state = WireState.ShutdownReceived;
                return true; // caller's EmitServerHandshake will not run if state changed
            }
            return false;
        }

        private void EmitServerHandshake() {
            if (_state == WireState.ShutdownReceived) return;
            if (Available < 36) return; // post-alignment we expect at least 36 B
            // 36-byte RemoteServerInformation per spec 2.2.1.2 — all BE.
            uint cbSize             = ReadU32BE();
            uint dwVersion          = ReadU32BE();
            uint dwMagic            = ReadU32BE();
            uint idContextApp       = ReadU32BE();
            uint idContextRender    = ReadU32BE();
            uint dwReserved1        = ReadU32BE();
            int  cItemsPerGroupBits = unchecked((int)ReadU32BE());
            int  cGroupBits         = unchecked((int)ReadU32BE());
            uint idObjectBrokerCls  = ReadU32BE();

            // Defensive: if cbSize claims more than 36 B (some servers
            // pad), skip the extra bytes so we land on the next Command.
            if (cbSize > 36 && cbSize <= 64 && Available >= (int)cbSize - 36) {
                _readPos += (int)cbSize - 36;
            }

            var args = new ServerHandshakeArgs {
                CbSize             = cbSize,
                DwVersion          = dwVersion,
                DwMagic            = dwMagic,
                IdContextApp       = idContextApp,
                IdContextRender    = idContextRender,
                CItemsPerGroupBits = cItemsPerGroupBits,
                CGroupBits         = cGroupBits,
                IdObjectBrokerCls  = idObjectBrokerCls,
            };
            try { ServerHandshakeReceived?.Invoke(this, args); } catch { }
        }

        private void EmitBuffer(byte[] source, int offset, int length) {
            // Copy into an exact-sized array so the consumer can hold the
            // reference indefinitely without pinning our growing _buf.
            byte[] copy = new byte[length];
            if (length > 0) Buffer.BlockCopy(source, offset, copy, 0, length);

            BufferKind kind;
            if (_pendingBufferInfo.IdBuffer != 0) {
                kind = BufferKind.DataBuffer;
            } else if ((_pendingBufferInfo.NFlags & 0x1) != 0) {
                kind = BufferKind.MessageBatch;
            } else {
                kind = BufferKind.IndividualMessage;
            }

            var args = new BufferReadyArgs {
                Kind         = kind,
                IdContextSrc = _pendingBufferInfo.IdContextSrc,
                IdContextDst = _pendingBufferInfo.IdContextDest,
                IdBuffer     = _pendingBufferInfo.IdBuffer,
                NFlags       = _pendingBufferInfo.NFlags,
                Payload      = copy,
            };
            try { BufferReady?.Invoke(this, args); } catch (Exception ex) {
                RaiseParseError("Consumer threw: " + ex.Message);
            }
        }

        private void RaiseParseError(string msg) {
            try { ParseError?.Invoke(this, new ParseErrorArgs { Message = msg }); } catch { }
        }

        // -------- nested types --------

        public enum WireState {
            AwaitingServerHandshake,
            AwaitingCommand,
            AwaitingBufferInfo,
            AwaitingBufferPayload,
            ShutdownReceived,
        }

        public enum BufferKind {
            DataBuffer,         // idBuffer != 0 — implicit DataBuffer instance
            IndividualMessage,  // idBuffer == 0 && !IsBatch — single payload msg
            MessageBatch,       // idBuffer == 0 && IsBatch  — MessageBatch + entries
        }

        private struct BufferInfo {
            public uint IdContextSrc, IdContextDest, IdBuffer, NFlags, CbSizeBuffer;
        }

        public sealed class ServerHandshakeArgs : EventArgs {
            public uint CbSize, DwVersion, DwMagic, IdContextApp, IdContextRender, IdObjectBrokerCls;
            public int  CItemsPerGroupBits, CGroupBits;
        }

        public sealed class BufferReadyArgs : EventArgs {
            public BufferKind Kind;
            public uint       IdContextSrc, IdContextDst, IdBuffer, NFlags;
            public byte[]     Payload;
        }

        public sealed class ParseErrorArgs : EventArgs {
            public string Message;
        }
    }
}
