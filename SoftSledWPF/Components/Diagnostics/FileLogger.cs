using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace SoftSled.Components.Diagnostics {
    /// <summary>
    /// Logger that appends every line to a UTF-8 file on a DEDICATED background
    /// writer thread. <see cref="Logger.LogInfo"/> &amp; friends only format the
    /// line (capturing the timestamp on the caller's thread) and enqueue it, then
    /// return immediately — no file IO happens on the caller.
    ///
    /// <para>WHY async: callers log from latency-critical threads, most notably
    /// the UDP RTP receive loop (<c>UdpSocket</c> reads a packet then runs the
    /// depacketizer + its logging synchronously before the next <c>Receive</c>).
    /// The previous synchronous+AutoFlush design turned a burst of loss warnings
    /// into a burst of flushed file writes ON that thread; while it wrote, the
    /// socket wasn't being drained, so the (small) kernel UDP buffer overflowed
    /// and MORE packets were dropped — the loss logging amplified the very loss it
    /// reported (log 20260727-191259: 98 loss lines in one second during a burst).
    /// Moving the IO off the hot path breaks that feedback loop.</para>
    ///
    /// <para>Flush policy: the writer flushes when it catches up (queue idle),
    /// every <see cref="FlushEveryLines"/> lines under sustained load, and on
    /// dispose. So a hard crash loses at most a small, recent tail rather than
    /// nothing being flushed — the old AutoFlush guaranteed the very last line but
    /// at the cost above; this keeps near-crash lines while removing the stall.</para>
    ///
    /// <para>Backpressure: the queue is bounded. Logging must NEVER block a caller,
    /// so when the disk can't keep up the line is dropped and counted rather than
    /// blocking; a periodic notice records how many were dropped. FileShare.Read
    /// keeps the live file tail-able.</para>
    /// </summary>
    public sealed class FileLogger : Logger, IDisposable {

        private const int QueueCapacity   = 65536; // ~a few MB of pending text worst case
        private const int FlushEveryLines = 512;   // bound crash-loss under sustained load
        private const int IdleFlushMs     = 250;   // flush this long after the last write when idle

        private readonly StreamWriter _writer;
        private readonly BlockingCollection<string> _queue =
            new BlockingCollection<string>(QueueCapacity);
        private readonly Thread _writerThread;
        private long _dropped;
        private long _reportedDropped;
        private volatile bool _disposed;

        /// <summary>Absolute path of the underlying file (for UI / Open-Folder).</summary>
        public string Path { get; }

        /// <summary>First IO exception encountered, if any.</summary>
        public Exception LastError { get; private set; }

        public FileLogger(string path) {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // FileShare.Read so external tools (Notepad++, baretail,
            // `Get-Content -Wait`) can read the live file. AutoFlush is OFF now —
            // the writer thread controls flushing (see class remarks).
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) {
                AutoFlush = false,
            };
            // Header written synchronously at construction (before the writer
            // thread starts — no race), then flushed, so the file identifies the
            // build immediately even if nothing else is ever logged.
            try {
                _writer.WriteLine("# ===================================================");
                _writer.WriteLine($"# softsled log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                try {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    _writer.WriteLine($"# build version    : {asm.GetName().Version}");
                    _writer.WriteLine($"# build location   : {asm.Location}");
                } catch { /* best-effort metadata */ }
                _writer.WriteLine($"# os               : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")} process on {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")} OS)");
                _writer.WriteLine($"# clr              : {Environment.Version}");
                _writer.WriteLine($"# machine          : {Environment.MachineName}");
                _writer.WriteLine($"# user             : {Environment.UserName}");
                _writer.WriteLine($"# working dir      : {Environment.CurrentDirectory}");
                _writer.WriteLine($"# log file         : {path}");
                _writer.WriteLine("# ===================================================");
                _writer.Flush();
            } catch (Exception ex) {
                LastError = ex;
            }

            _writerThread = new Thread(WriterLoop) {
                IsBackground = true,
                Name = "FileLogger",
                Priority = ThreadPriority.BelowNormal,
            };
            _writerThread.Start();
        }

        // ---- Logger contract (caller threads: format + enqueue only) --

        protected override void OnLogDebug(string message) {
            if (!IsLoggingDebug) return;
            Enqueue("Debug", message);
        }

        protected override void OnLogInfo(string message) {
            Enqueue("Info", message);
        }

        protected override void OnLogError(string message) {
            Enqueue("Error", message);
        }

        // ---- Internal -------------------------------------------------

        private void Enqueue(string level, string message) {
            if (_disposed) return;
            // Format on the CALLER thread so the timestamp reflects when the event
            // happened, not when the writer thread later drains it.
            string line = GetPrefix() + level + ": " + message;
            // Never block the caller: drop + count if the queue is full (disk
            // can't keep up). TryAdd returns immediately; it throws only if the
            // collection was completed by a concurrent Dispose — swallow that.
            try {
                if (!_queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
            } catch { /* disposed mid-log */ }
        }

        private void WriterLoop() {
            bool pendingFlush = false;
            int sinceFlush = 0;
            try {
                while (true) {
                    if (_queue.TryTake(out string line, IdleFlushMs)) {
                        try {
                            _writer.WriteLine(line);
                            pendingFlush = true;
                            if (++sinceFlush >= FlushEveryLines) {
                                _writer.Flush(); pendingFlush = false; sinceFlush = 0;
                            }
                        } catch (Exception ex) { LastError = ex; }
                    } else {
                        // Idle: caught up — flush so recent lines hit disk, and
                        // surface any dropped-line count once things are quiet.
                        if (pendingFlush) { try { _writer.Flush(); } catch { } pendingFlush = false; sinceFlush = 0; }
                        MaybeReportDrops();
                        if (_disposed && _queue.Count == 0) break;
                    }
                }
            } catch (Exception ex) {
                LastError = ex;
            }
        }

        private void MaybeReportDrops() {
            long d = Interlocked.Read(ref _dropped);
            if (d != _reportedDropped) {
                try {
                    _writer.WriteLine($"{GetPrefix()}Error: [logger] dropped {d - _reportedDropped} log line(s) " +
                                      $"(disk not keeping up; total {d})");
                    _writer.Flush();
                } catch { }
                _reportedDropped = d;
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            // Let the writer drain the queue and flush; then close the file.
            try { _writerThread?.Join(2000); } catch { }
            try {
                _writer.WriteLine($"# log closed {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                _writer.Flush();
                _writer.Dispose();
            } catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
