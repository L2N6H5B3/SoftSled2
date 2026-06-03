using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Decoder for WMC's MCX-specific RDP fast-path update code 0x0D, which
    /// is a multiplexed channel carrying audio data AND overlay-region
    /// metadata. This class handles the metadata side; audio handlers
    /// (<see cref="WmcFastpathAudioPlayer"/> / <see cref="WmcFastpathAudioDumper"/>)
    /// keep handling their own message types in parallel — both subscribe
    /// to the same dispatcher and each only acts on the types it owns.
    ///
    /// Wire format (derived from raw captures of an SUP-enabled session):
    /// <code>
    /// offset   size   name                  encoding   notes
    /// 0–3      4      magic                 fixed      0x0D 0x00 0x00 0x00
    /// 4–7      4      total_body_length     u32 LE     bytes after this field
    /// 8–11     4      version               u32 BE     always 0x00000002
    /// 12–15    4      inner_length          u32 BE     = total_body_length - 8
    /// 16–19    4      inner_length_2        u32 BE     = inner_length - 8
    /// 20–23    4      message_type          u32 BE     dispatch field
    /// 24+      varies payload                          per message_type
    /// </code>
    ///
    /// Observed message types:
    /// <list type="bullet">
    ///   <item><term>type=1, len=24</term><description>Heartbeat (ping flavour)</description></item>
    ///   <item><term>type=2, len=24</term><description>Heartbeat (pong flavour, paired with type 1)</description></item>
    ///   <item><term>type=3, len=32</term><description>Video destination rectangle (4× u16 BE x,y,w,h)</description></item>
    ///   <item><term>type=5, len=44</term><description>Audio trailer/heartbeat — handled elsewhere</description></item>
    ///   <item><term>type=6, len=large</term><description>Audio PCM payload — handled elsewhere</description></item>
    ///   <item><term>type=7, len=28</term><description>Stream init (u32 BE = 5)</description></item>
    /// </list>
    ///
    /// This decoder fires <see cref="OverlayRegionChanged"/> on type=3 only.
    /// Coordinates are in WMC's RDP source space (e.g. 0..1023 × 0..767 for
    /// the standard 1024×768 session). Consumers (MainWindow) must scale
    /// them into the live RDP bitmap render area.
    /// </summary>
    internal sealed class WmcFastpathOverlayRegionDecoder {

        private const byte McxUpdateCode = 0x0D;
        private const int MinMessageLength = 24;   // smallest valid wrapper
        private const int HeaderOverheadBytes = 24; // up through and including message_type

        // Message type constants — public for testability / debug logging.
        public const uint MsgPingHeartbeat = 1;
        public const uint MsgPongHeartbeat = 2;
        public const uint MsgVideoRegion = 3;
        public const uint MsgZoomMode = 4;
        public const uint MsgAudioTrailer = 5;
        public const uint MsgAudioPayload = 6;
        public const uint MsgStreamInit = 7;

        /// <summary>Video destination rectangle in RDP source coords.</summary>
        public sealed class OverlayRegion {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public override string ToString() => $"({X},{Y} {Width}×{Height})";
        }

        /// <summary>
        /// WMC's four video zoom modes, identified by their wire value (the
        /// uint32 BE payload of message type 4). Cycled via Ctrl+Shift+Z or
        /// the Zoom button on a WMC remote.
        /// </summary>
        public enum WmcZoomMode {
            /// <summary>Zoom 1 — preserve source aspect; letterbox/pillarbox
            /// bars appear as needed. Maps to WPF <c>Stretch.Uniform</c>.</summary>
            Normal = 0,
            /// <summary>Zoom 2 — stretch / zoom-in to remove letterbox bars
            /// (blank bars on TOP and BOTTOM). Preserves aspect; crops
            /// content that overflows the destination rectangle.</summary>
            StretchTopBottom = 1,
            /// <summary>Zoom 3 — stretch horizontally to remove pillarbox
            /// (blank bars on the SIDES). Aspect ratio distorted.</summary>
            StretchSides = 2,
            /// <summary>Zoom 4 — non-linear / "dynamic" stretch: the centre
            /// of the picture keeps its aspect, edges are progressively
            /// stretched. WPF can't natively reproduce this; the host maps
            /// it to a close approximation (currently <c>Fill</c>).</summary>
            Dynamic = 3,
        }

        private readonly Logger _log;
        private OverlayRegion _lastRegion;
        private WmcZoomMode? _lastZoomMode;

        /// <summary>
        /// Fires when a type=3 message is decoded. May be called on the
        /// FreeRDP worker thread — subscribers must marshal to the UI
        /// dispatcher before touching WPF.
        /// </summary>
        public event EventHandler<OverlayRegion> OverlayRegionChanged;

        /// <summary>
        /// Fires when a type=4 message is decoded. Same threading contract
        /// as <see cref="OverlayRegionChanged"/>. WMC sends these in
        /// response to the user cycling the zoom button — independent of
        /// destination-rectangle updates, which keep flowing on type=3.
        /// </summary>
        public event EventHandler<WmcZoomMode> ZoomModeChanged;

        public WmcFastpathOverlayRegionDecoder(Logger log) {
            _log = log;
        }

        public void OnFastpath(IntPtr user, byte updateCode, IntPtr data, UIntPtr length) {
            if (updateCode != McxUpdateCode) return;

            int len = checked((int)(uint)length);
            if (len < MinMessageLength || data == IntPtr.Zero) return;

            // Verify magic + extract length. We trust the native shim that
            // `data` is readable for `len` bytes but verify our own field
            // bounds on every read to avoid memory faults on malformed input.
            // Read the first 24 bytes (header) directly via Marshal.ReadByte
            // for clarity and to avoid an extra alloc.
            if (ReadByte(data, 0) != 0x0D ||
                ReadByte(data, 1) != 0x00 ||
                ReadByte(data, 2) != 0x00 ||
                ReadByte(data, 3) != 0x00) return;

            uint totalBodyLen = ReadU32LE(data, 4);
            if (totalBodyLen + 8 != (uint)len) {
                _log?.LogDebug($"[fp-overlay] length mismatch: header says " +
                               $"body={totalBodyLen} but native gave len={len}");
                return;
            }

            uint version = ReadU32BE(data, 8);
            if (version != 2) {
                _log?.LogDebug($"[fp-overlay] unknown version {version}, ignoring");
                return;
            }

            uint msgType = ReadU32BE(data, 20);

            switch (msgType) {
                case MsgVideoRegion:
                    DecodeVideoRegion(data, len);
                    break;
                case MsgZoomMode:
                    DecodeZoomMode(data, len);
                    break;
                case MsgPingHeartbeat:
                case MsgPongHeartbeat:
                    // Quiet — these dominate the channel (~95 % of messages).
                    break;
                case MsgStreamInit:
                    _log?.LogDebug("[fp-overlay] stream init received");
                    break;
                case MsgAudioTrailer:
                case MsgAudioPayload:
                    // Owned by the audio handlers; ignore here.
                    break;
                default:
                    // Bumped from Debug to Error: anything we don't
                    // recognise here is a candidate for zoom mode, source
                    // rect, or some other display-control signal we have
                    // not yet decoded. The full bytes are in the raw
                    // dumper's binary file; include a hex preview here so
                    // the type pops out in the logger without needing to
                    // open the bin.
                    int prev = Math.Min(64, len);
                    var sb = new System.Text.StringBuilder(prev * 3);
                    for (int i = 0; i < prev; i++) {
                        if (i > 0) sb.Append('-');
                        sb.Append(Marshal.ReadByte(data, i).ToString("X2"));
                    }
                    _log?.LogError($"[fp-overlay] UNKNOWN msgType={msgType} len={len} " +
                                   $"first{prev}B={sb}");
                    break;
            }
        }

        private void DecodeVideoRegion(IntPtr data, int len) {
            // Type-3 payload: 8 bytes at offset 24 = 4× u16 BE = (x, y, w, h).
            if (len < HeaderOverheadBytes + 8) {
                _log?.LogDebug($"[fp-overlay] type=3 truncated: len={len}");
                return;
            }

            int x = ReadU16BE(data, 24);
            int y = ReadU16BE(data, 26);
            int w = ReadU16BE(data, 28);
            int h = ReadU16BE(data, 30);

            // Sanity bounds — WMC's RDP session is typically 1024×768 but we
            // don't hardcode that here; just guard against zero / silly huge.
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) {
                _log?.LogDebug($"[fp-overlay] type=3 rejected: ({x},{y} {w}×{h})");
                return;
            }

            // De-duplicate identical regions — WMC tends to repeat the same
            // rectangle multiple times. Only fire the event when something
            // actually changed.
            var region = new OverlayRegion { X = x, Y = y, Width = w, Height = h };
            var prev = _lastRegion;
            if (prev != null && prev.X == x && prev.Y == y &&
                prev.Width == w && prev.Height == h) {
                return;
            }
            _lastRegion = region;

            _log?.LogInfo($"[fp-overlay] video region → ({x},{y}) {w}×{h}");
            try {
                OverlayRegionChanged?.Invoke(this, region);
            } catch (Exception ex) {
                _log?.LogError($"[fp-overlay] subscriber threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Decode the type=4 zoom-mode message. Payload is a single uint32
        /// BE at offset 24, value 0–3 per <see cref="WmcZoomMode"/>.
        /// Spec was reverse-engineered from a capture where the user cycled
        /// the WMC Zoom button (Ctrl+Shift+Z) — observed payloads matched
        /// the user-confirmed mode mapping exactly.
        /// </summary>
        private void DecodeZoomMode(IntPtr data, int len) {
            if (len < HeaderOverheadBytes + 4) {
                _log?.LogDebug($"[fp-overlay] type=4 truncated: len={len}");
                return;
            }

            uint raw = ReadU32BE(data, 24);
            // Accept only the values we've reverse-engineered. Anything else
            // gets logged loudly so an unknown future mode (e.g. a custom
            // user-defined zoom level) doesn't silently get mapped to mode 0.
            WmcZoomMode mode;
            switch (raw) {
                case 0: mode = WmcZoomMode.Normal; break;
                case 1: mode = WmcZoomMode.StretchSides; break;
                case 2: mode = WmcZoomMode.StretchTopBottom; break;
                case 3: mode = WmcZoomMode.Dynamic; break;
                default:
                    _log?.LogError($"[fp-overlay] type=4 unknown zoom value {raw}");
                    return;
            }

            // De-duplicate: WMC sometimes repeats the current mode (e.g. on
            // re-entering playback). Only fire when something changed.
            if (_lastZoomMode.HasValue && _lastZoomMode.Value == mode) return;
            _lastZoomMode = mode;

            _log?.LogInfo($"[fp-overlay] zoom mode → {mode} ({(int)mode})");
            try {
                ZoomModeChanged?.Invoke(this, mode);
            } catch (Exception ex) {
                _log?.LogError($"[fp-overlay] ZoomModeChanged subscriber threw: {ex.Message}");
            }
        }

        // ---- Tiny native-memory readers -----------------------------------
        // Marshal.ReadByte / ReadInt16 / ReadInt32 take a byte offset and
        // perform a single unmanaged read. We wrap them with explicit
        // endian-conversion helpers because the wire format mixes LE and
        // BE on adjacent fields (length is LE, everything else is BE).

        private static byte ReadByte(IntPtr p, int off) => Marshal.ReadByte(p, off);

        private static uint ReadU32LE(IntPtr p, int off) {
            return (uint)Marshal.ReadByte(p, off)
                 | ((uint)Marshal.ReadByte(p, off + 1) << 8)
                 | ((uint)Marshal.ReadByte(p, off + 2) << 16)
                 | ((uint)Marshal.ReadByte(p, off + 3) << 24);
        }

        private static uint ReadU32BE(IntPtr p, int off) {
            return ((uint)Marshal.ReadByte(p, off) << 24)
                 | ((uint)Marshal.ReadByte(p, off + 1) << 16)
                 | ((uint)Marshal.ReadByte(p, off + 2) << 8)
                 | (uint)Marshal.ReadByte(p, off + 3);
        }

        private static int ReadU16BE(IntPtr p, int off) {
            return (Marshal.ReadByte(p, off) << 8) | Marshal.ReadByte(p, off + 1);
        }
    }
}
