using System;
using Microsoft.Win32;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;

namespace SoftSled.Components.Utility {

    /// <summary>
    /// Registers (or removes) SoftSled's Windows boot auto-start entry under the
    /// per-user Run key so it launches at login. Driven by the General settings
    /// page whenever <see cref="BootStartMode"/> changes.
    ///
    /// <para>Two launch shapes: <c>Ui</c> writes the plain exe path (normal shell
    /// on screen); <c>Tray</c> appends <c>--tray</c> so the shell starts hidden in
    /// the system tray, idle, waiting for the remote's Green button. <c>Off</c>
    /// deletes the value. Per-user HKCU means no admin rights are needed.</para>
    /// </summary>
    public static class StartupRegistration {

        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Value name under the Run key. Matches the AssemblyName so it reads
        // sensibly in msconfig / Task Manager's Startup tab.
        private const string ValueName = "SoftSled";

        /// <summary>Command-line flag that tells a launched instance to start
        /// hidden in the tray rather than showing the shell window.</summary>
        public const string TrayArg = "--tray";

        /// <summary>
        /// Apply the desired boot-start mode to the HKCU Run key. Best-effort —
        /// a registry failure is logged but never allowed to disturb the
        /// settings save.
        /// </summary>
        public static void Apply(BootStartMode mode, Logger log = null) {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath)) {
                    if (key == null) {
                        log?.LogError("[startup] could not open HKCU Run key");
                        return;
                    }

                    if (mode == BootStartMode.Off) {
                        if (key.GetValue(ValueName) != null) {
                            key.DeleteValue(ValueName, throwOnMissingValue: false);
                            log?.LogInfo("[startup] boot auto-start removed");
                        }
                        return;
                    }

                    string exe = ExePath();
                    if (string.IsNullOrEmpty(exe)) {
                        log?.LogError("[startup] could not resolve executable path — Run entry unchanged");
                        return;
                    }

                    // Quote the path (may contain spaces) and append --tray for
                    // the hidden-tray launch shape.
                    string command = "\"" + exe + "\"";
                    if (mode == BootStartMode.Tray) command += " " + TrayArg;

                    key.SetValue(ValueName, command, RegistryValueKind.String);
                    log?.LogInfo($"[startup] boot auto-start set: {command}");
                }
            } catch (Exception ex) {
                log?.LogError("[startup] Apply threw: " + ex.Message);
            }
        }

        /// <summary>The full path to the running executable. Prefers the process
        /// main module (the real .exe) and falls back to the entry assembly.</summary>
        private static string ExePath() {
            try {
                string p = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(p)) return p;
            } catch { /* fall through */ }
            try {
                return System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            } catch {
                return null;
            }
        }
    }
}
