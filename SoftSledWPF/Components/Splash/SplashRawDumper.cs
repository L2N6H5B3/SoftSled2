using SoftSled.Components.Diagnostics;
using System;
using System.IO;
using System.Threading;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// Opt-in diagnostic capture for the splash VC. Enabled via
    /// <c>SOFTSLED_SPLASH_RAW_DUMP=&lt;directory&gt;</c>. When set, writes
    /// two files into that directory:
    /// <list type="bullet">
    ///   <item><description><c>softsled-splash-wire.bin</c> — exact bytes
    ///   arriving on the VC, concatenated in order.</description></item>
    ///   <item><description><c>softsled-splash-events.log</c> — human-readable
    ///   decode of framing + payload-header lines, one per parsed unit.
    ///   Indispensable for planning slice 2 once we see what classes /
    ///   msgids actually appear in practice.</description></item>
    /// </list>
    ///
    /// Mirrors the file naming + env-var convention of
    /// <c>WmcFastpathRawDumper</c> so anyone familiar with the AV-side
    /// debugging tools finds this immediately.
    /// </summary>
    internal sealed class SplashRawDumper : IDisposable {

        private readonly FileStream _wireStream;
        private readonly StreamWriter _events;
        private readonly object       _wireLock   = new object();
        private readonly object       _eventsLock = new object();

        public static SplashRawDumper CreateFromEnv(Logger logger) {
            string dir = Environment.GetEnvironmentVariable("SOFTSLED_SPLASH_RAW_DUMP");
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try {
                Directory.CreateDirectory(dir);
                logger?.LogInfo("SPLASH: raw dump enabled → " + dir);
                return new SplashRawDumper(dir);
            } catch (Exception ex) {
                logger?.LogError("SPLASH: failed to enable raw dump: " + ex.Message);
                return null;
            }
        }

        private SplashRawDumper(string dir) {
            string wirePath  = Path.Combine(dir, "softsled-splash-wire.bin");
            string evtPath   = Path.Combine(dir, "softsled-splash-events.log");
            _wireStream = new FileStream(wirePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _events     = new StreamWriter(new FileStream(
                            evtPath, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                AutoFlush = false,
            };
            // Background flush so the file is recent without crippling IO.
            ThreadPool.QueueUserWorkItem(_ => FlushLoop());
        }

        public void OnVcBytes(byte[] chunk) {
            if (chunk == null || chunk.Length == 0) return;
            lock (_wireLock) {
                _wireStream.Write(chunk, 0, chunk.Length);
            }
        }

        public void OnEvent(string line) {
            lock (_eventsLock) {
                _events.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                _events.Write("  ");
                _events.WriteLine(line);
            }
        }

        public void OnBuffer(SplashWireReassembler.BufferReadyArgs e) {
            OnEvent($"Buffer kind={e.Kind} idBuffer={e.IdBuffer} ctxSrc={e.IdContextSrc} ctxDst={e.IdContextDst} flags=0x{e.NFlags:X8} len={e.Payload.Length}");
        }

        private void FlushLoop() {
            while (true) {
                try { Thread.Sleep(2000); } catch { return; }
                try {
                    lock (_wireLock)   { _wireStream?.Flush(); }
                    lock (_eventsLock) { _events?.Flush(); }
                } catch { return; }
            }
        }

        public void Dispose() {
            try { lock (_wireLock)   { _wireStream?.Flush(); _wireStream?.Dispose(); } } catch { }
            try { lock (_eventsLock) { _events?.Flush();     _events?.Dispose();    } } catch { }
        }
    }
}
