using System;
using System.IO;

namespace SoftSled.Components.Configuration {
    /// <summary>
    /// Single source of truth for where diagnostic output lands.
    ///
    /// <para>ONE configured root (<see cref="SoftSledConfig.DiagnosticsDirectory"/>)
    /// holds everything, split by kind:</para>
    /// <list type="bullet">
    ///   <item><description><c>&lt;root&gt;\Logs\softsled-&lt;stamp&gt;-pid&lt;n&gt;.log</c>
    ///     — the per-session/app file log.</description></item>
    ///   <item><description><c>&lt;root&gt;\Dumps\&lt;splash|fastpath|rtsp&gt;\…</c>
    ///     — the raw diagnostic dumps.</description></item>
    /// </list>
    ///
    /// <para>An empty root means the platform default,
    /// <c>%LocalAppData%\SoftSled</c>. Callers used to each re-implement this
    /// fallback against one of two separate config fields, which is how the
    /// per-play RTSP diagnostics and the RTSP wire dump ended up in two
    /// different "rtsp" folders whenever the two fields disagreed. Route
    /// everything through here instead.</para>
    /// </summary>
    internal static class DiagnosticsPaths {

        /// <summary>The configured diagnostics root, or the platform default
        /// when unset. Never throws; falls back to the default on any config
        /// read failure.</summary>
        internal static string Root() {
            string dir = null;
            try { dir = SoftSledConfigManager.ReadConfig()?.DiagnosticsDirectory; } catch { }
            return RootFrom(dir);
        }

        /// <summary>Resolve a root from an already-read config value, so hot
        /// paths that hold a config don't re-read it off disk.</summary>
        internal static string RootFrom(string configuredRoot) {
            if (!string.IsNullOrWhiteSpace(configuredRoot)) return configuredRoot;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftSled");
        }

        /// <summary>Directory the session/app log files are written to.</summary>
        internal static string LogsDir(string configuredRoot = null)
            => Path.Combine(RootFrom(configuredRoot ?? TryReadRoot()), "Logs");

        /// <summary>Root directory for the raw dumps. Per-kind subfolders
        /// (splash/, fastpath/, rtsp/) are created by their owning dumper.</summary>
        internal static string DumpsRoot(string configuredRoot = null)
            => Path.Combine(RootFrom(configuredRoot ?? TryReadRoot()), "Dumps");

        /// <summary>Full path for a fresh log file. Timestamped + pid-stamped so
        /// concurrent runs don't clobber each other and a crash can be correlated
        /// by its timestamp.</summary>
        internal static string NewLogFilePath(string configuredRoot = null) {
            string fileName = $"softsled-{DateTime.Now:yyyyMMdd-HHmmss}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}.log";
            return Path.Combine(LogsDir(configuredRoot), fileName);
        }

        private static string TryReadRoot() {
            try { return SoftSledConfigManager.ReadConfig()?.DiagnosticsDirectory; } catch { return null; }
        }
    }
}
