using System;
using System.IO;
using System.Text;

namespace SoftSled.Components.Diagnostics {
    /// <summary>
    /// Logger that appends every line to a UTF-8 file. Auto-flushes after
    /// every write so an app crash mid-log doesn't lose the tail (the
    /// last line is exactly what we usually need to diagnose what went
    /// wrong).
    ///
    /// <para>Thread safety: a single shared lock serialises all writes.
    /// Callers can hit Log* from worker threads (RTP receive, RTCP
    /// timer, fastpath callback, FFME worker, etc.) without coordination.</para>
    ///
    /// <para>Failure handling: if file IO throws (disk full, permission
    /// denied, …) the failure is swallowed so logging never becomes the
    /// reason a session dies. The first failure is captured into
    /// <see cref="LastError"/> for callers that want to surface it
    /// (e.g. an "open log folder" button).</para>
    /// </summary>
    public sealed class FileLogger : Logger, IDisposable {

        private readonly object _lock = new object();
        private readonly StreamWriter _writer;
        private bool _disposed;

        /// <summary>Absolute path of the underlying file (for UI / Open-Folder).</summary>
        public string Path { get; }

        /// <summary>First IO exception encountered, if any.</summary>
        public Exception LastError { get; private set; }

        /// <summary>
        /// Open <paramref name="path"/> for append-write. Creates parent
        /// directories as needed. Writes a header line with timestamp +
        /// build version so any captured log identifies which build ran.
        /// </summary>
        public FileLogger(string path) {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // FileShare.Read so external tools (tail -f equivalents on
            // Windows: Notepad++, baretail, PowerShell `Get-Content -Wait`)
            // can read the live file. AutoFlush=true so a crash mid-line
            // still lets us see what we'd written up to the last newline.
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) {
                AutoFlush = true,
            };
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
            } catch (Exception ex) {
                LastError = ex;
            }
        }

        // ---- Logger contract ------------------------------------------

        protected override void OnLogDebug(string message) {
            if (!IsLoggingDebug) return;
            WriteLine("Debug", message);
        }

        protected override void OnLogInfo(string message) {
            WriteLine("Info", message);
        }

        protected override void OnLogError(string message) {
            WriteLine("Error", message);
        }

        // ---- Internal -------------------------------------------------

        private void WriteLine(string level, string message) {
            if (_disposed) return;
            try {
                lock (_lock) {
                    if (_disposed) return;
                    _writer.WriteLine(GetPrefix() + level + ": " + message);
                    // AutoFlush is on, but explicit flush after WriteLine
                    // is belt + braces for unbuffered safety across BCL
                    // versions.
                    _writer.Flush();
                }
            } catch (Exception ex) {
                LastError = ex;
            }
        }

        public void Dispose() {
            lock (_lock) {
                if (_disposed) return;
                _disposed = true;
                try {
                    _writer.WriteLine($"# log closed {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                } catch { }
                try { _writer.Flush(); } catch { }
                try { _writer.Dispose(); } catch { }
            }
        }
    }
}
