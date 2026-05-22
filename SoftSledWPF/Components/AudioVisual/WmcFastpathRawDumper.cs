using SoftSled.Components.Diagnostics;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Opt-in raw dumper for ALL WMC RDP fast-path update messages — every
    /// update code, every length, no filtering. Captures both the audio
    /// 0x0D stream AND any additional non-audio fast-path traffic that
    /// appears when devcaps such as SUP (RDP super blt) are enabled.
    ///
    /// Built for reverse-engineering: WMC delivers UI-overlay / video-
    /// region metadata via still-undocumented fast-path channels. By
    /// capturing the full byte stream with timing and a human-readable
    /// summary we can analyse it offline and decide whether overlay-region
    /// geometry is in the wire data (preferred — drives a precise clip
    /// mask on the RDP image) or whether we have to fall back to chroma-
    /// keying the RDP framebuffer.
    ///
    /// Activation: set <c>SOFTSLED_FASTPATH_RAW_DUMP=&lt;dir&gt;</c> in the
    /// environment before launching. The dumper writes:
    /// <list type="bullet">
    ///   <item><description><c>fastpath-raw.bin</c> — length-prefixed binary log
    ///     of every message. Record layout: 8 B int64 little-endian wall
    ///     ticks since session start, 1 B update code, 4 B uint32 little-
    ///     endian length, then <c>length</c> payload bytes. Trivial to parse
    ///     in a hex editor or a small reader script.</description></item>
    ///   <item><description><c>fastpath-raw.log</c> — one ASCII line per message
    ///     with wall time, update code, length, and the first 32 bytes in
    ///     hex. Sortable / greppable for pattern hunting (e.g. all unique
    ///     0x0D message lengths, all distinct first-byte prefixes).</description></item>
    /// </list>
    ///
    /// Performance: writes are buffered (default <c>StreamWriter</c>/
    /// <c>FileStream</c> buffering) and flushed every 64 messages or when
    /// the buffer naturally drains. Native callback only allocates per-
    /// message; the FreeRDP worker thread is held only as long as the
    /// memcpy + buffered write takes (microseconds for typical payloads).
    /// </summary>
    internal sealed class WmcFastpathRawDumper : IDisposable {

        private readonly Logger _log;
        private readonly object _gate = new object();
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private FileStream _binFile;
        private StreamWriter _logFile;
        private int _seq;
        private int _flushCountdown = 64;
        private bool _disposed;

        public WmcFastpathRawDumper(Logger log, string dumpDir) {
            _log = log;
            try {
                Directory.CreateDirectory(dumpDir);
                string binPath = Path.Combine(dumpDir, "fastpath-raw.bin");
                string logPath = Path.Combine(dumpDir, "fastpath-raw.log");

                _binFile = new FileStream(binPath, FileMode.Create,
                                          FileAccess.Write, FileShare.Read);
                _logFile = new StreamWriter(
                    new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    System.Text.Encoding.ASCII);
                _logFile.WriteLine("# WMC RDP fast-path raw dump — ALL update codes captured");
                _logFile.WriteLine("# columns: wall(s) seq updateCode length first32B(hex)");
                _logFile.Flush();
                _log?.LogInfo($"WMC fastpath raw dumper armed → {binPath} + {logPath}");
            } catch (Exception ex) {
                _log?.LogError($"WMC fastpath raw dumper init failed: {ex.Message}");
                Dispose();
            }
        }

        /// <summary>
        /// Receives every fast-path update from the patched libfreerdp shim.
        /// Native thread; keep work short. We copy the payload bytes out of
        /// native memory immediately (the IntPtr is invalidated when this
        /// callback returns), then write under a single short lock so the
        /// underlying file pointers stay consistent across messages.
        /// </summary>
        public void OnFastpath(IntPtr user, byte updateCode, IntPtr data, UIntPtr length) {
            if (_disposed) return;

            int len = checked((int)(uint)length);
            if (len < 0) return;

            // Snapshot payload to managed memory before the native pointer
            // can be reused. Allocating per message is fine — fastpath
            // events for WMC are infrequent (typically <100/s peak).
            byte[] payload = (len > 0 && data != IntPtr.Zero) ? new byte[len] : Array.Empty<byte>();
            if (payload.Length > 0) {
                Marshal.Copy(data, payload, 0, payload.Length);
            }

            int seq = Interlocked.Increment(ref _seq);
            long ticks = _sw.ElapsedTicks;
            // Convert to a wall-clock seconds string with millisecond
            // precision before locking (formatting can allocate).
            double wallSecs = (double)ticks / System.Diagnostics.Stopwatch.Frequency;

            lock (_gate) {
                if (_disposed || _binFile == null || _logFile == null) return;

                try {
                    // Binary record: 8B ticks LE | 1B code | 4B length LE | payload
                    WriteInt64LE(_binFile, ticks);
                    _binFile.WriteByte(updateCode);
                    WriteUInt32LE(_binFile, (uint)payload.Length);
                    if (payload.Length > 0) {
                        _binFile.Write(payload, 0, payload.Length);
                    }

                    // Summary line.
                    int hexLen = Math.Min(32, payload.Length);
                    string hex = ToHex(payload, 0, hexLen);
                    _logFile.WriteLine($"{wallSecs,9:F3} {seq,6} " +
                                       $"code=0x{updateCode:X2} len={payload.Length,6} " +
                                       $"first{hexLen}B={hex}");

                    if (--_flushCountdown <= 0) {
                        _binFile.Flush();
                        _logFile.Flush();
                        _flushCountdown = 64;
                    }
                } catch (Exception ex) {
                    _log?.LogError($"[fp-raw] write failed: {ex.Message}");
                }
            }
        }

        private static void WriteInt64LE(FileStream fs, long v) {
            for (int i = 0; i < 8; i++) fs.WriteByte((byte)((v >> (i * 8)) & 0xFF));
        }

        private static void WriteUInt32LE(FileStream fs, uint v) {
            for (int i = 0; i < 4; i++) fs.WriteByte((byte)((v >> (i * 8)) & 0xFF));
        }

        private static string ToHex(byte[] b, int off, int len) {
            if (len <= 0) return "";
            var sb = new System.Text.StringBuilder(len * 3);
            for (int i = 0; i < len; i++) {
                if (i > 0) sb.Append('-');
                sb.Append(b[off + i].ToString("X2"));
            }
            return sb.ToString();
        }

        public void Dispose() {
            lock (_gate) {
                if (_disposed) return;
                _disposed = true;
                try { _binFile?.Flush(); _binFile?.Dispose(); } catch { }
                try { _logFile?.Flush(); _logFile?.Dispose(); } catch { }
                _binFile = null;
                _logFile = null;
            }
        }
    }
}
