using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace SoftSled.HostSetup {

    /// <summary>
    /// The two host-side actions that let SoftSled pair with a Windows Media
    /// Center PC:
    ///   1. Replace %WINDIR%\ehome\Mcx2Prov.exe with the patched build.
    ///   2. Import the SoftSled CA certificate into the Local Machine
    ///      "Trusted Root Certification Authorities" store.
    /// Both require administrator rights (the app manifest forces elevation).
    /// The patched exe and the CA cert are embedded in this assembly, so the
    /// tool is a single self-contained file to copy onto the host PC.
    /// </summary>
    internal static class SetupActions {

        private const string PatchedExeResource = "Mcx2Prov_patched.exe";
        private const string CaCertResource     = "softsled_ca.cer";

        // ---- Admin check ----------------------------------------------

        public static bool IsAdministrator() {
            try {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            } catch {
                return false;
            }
        }

        // ---- Action 1: replace Mcx2Prov.exe ---------------------------

        /// <summary>The on-disk path of the host's Mcx2Prov.exe.</summary>
        public static string Mcx2ProvPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                         "ehome", "Mcx2Prov.exe");

        public static bool ReplaceMcx2Prov(Action<string> log) {
            string target = Mcx2ProvPath;
            log("• Replacing Mcx2Prov.exe");
            log("    Target: " + target);

            string ehome = Path.GetDirectoryName(target);
            if (!Directory.Exists(ehome)) {
                log("    ERROR: " + ehome + " was not found.");
                log("    Windows Media Center does not appear to be installed on this PC.");
                return false;
            }

            string patched;
            try {
                patched = ExtractResourceToTemp(PatchedExeResource);
            } catch (Exception ex) {
                log("    ERROR: could not extract the patched Mcx2Prov.exe: " + ex.Message);
                return false;
            }

            try {
                if (File.Exists(target)) {
                    // The original is owned by TrustedInstaller with a DACL that
                    // blocks writes even for administrators, so take ownership
                    // and grant the Administrators group full control first.
                    log("    Taking ownership of the existing file...");
                    RunProcess("takeown.exe", "/f \"" + target + "\"", log);
                    log("    Granting Administrators full control...");
                    RunProcess("icacls.exe", "\"" + target + "\" /grant *S-1-5-32-544:F", log);

                    // Keep one pristine backup of the original (never overwrite
                    // a backup, so re-running the tool can't lose the original).
                    string backup = target + ".softsled-backup";
                    if (!File.Exists(backup)) {
                        File.Copy(target, backup);
                        log("    Backed up the original to " + Path.GetFileName(backup));
                    } else {
                        log("    Existing backup found — leaving it intact.");
                    }
                } else {
                    log("    No existing Mcx2Prov.exe — installing the patched copy fresh.");
                }

                File.Copy(patched, target, true);
                log("    Mcx2Prov.exe replaced with the patched version. [OK]");
                return true;
            } catch (UnauthorizedAccessException ex) {
                log("    ERROR: access denied writing " + target + ".");
                log("    " + ex.Message);
                log("    Make sure the tool is running as administrator and that " +
                    "Windows Media Center is not currently pairing.");
                return false;
            } catch (Exception ex) {
                log("    ERROR: " + ex.Message);
                return false;
            } finally {
                try { File.Delete(patched); } catch { /* temp cleanup, best-effort */ }
            }
        }

        // ---- Action 2: import the CA certificate ----------------------

        public static bool ImportCaCertificate(Action<string> log) {
            log("• Importing the SoftSled CA certificate (Local Machine → Trusted Root)");
            try {
                var cert = LoadEmbeddedCaCert();
                log("    Certificate: " + cert.Subject);
                log("    Thumbprint:  " + cert.Thumbprint);

                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine)) {
                    store.Open(OpenFlags.ReadWrite);
                    var match = store.Certificates.Find(
                        X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    if (match.Count > 0) {
                        log("    Already present in Trusted Root — nothing to do. [OK]");
                    } else {
                        store.Add(cert);
                        log("    Imported into Local Machine Trusted Root. [OK]");
                    }
                    store.Close();
                }
                return true;
            } catch (Exception ex) {
                log("    ERROR: " + ex.Message);
                return false;
            }
        }

        // ---- Detection ------------------------------------------------

        /// <summary>True if the host's Mcx2Prov.exe is byte-identical to the
        /// embedded patched build (i.e. our patch is currently installed).</summary>
        public static bool IsMcx2ProvInstalled() {
            try {
                string target = Mcx2ProvPath;
                if (!File.Exists(target)) return false;
                byte[] current = File.ReadAllBytes(target);
                byte[] patched = GetResourceBytes(PatchedExeResource);
                return current.Length == patched.Length && current.SequenceEqual(patched);
            } catch {
                return false;
            }
        }

        /// <summary>True if a tool-made backup of the original Mcx2Prov.exe
        /// exists (so it can be restored on uninstall).</summary>
        public static bool HasMcx2ProvBackup() => File.Exists(Mcx2ProvPath + ".softsled-backup");

        /// <summary>True if the SoftSled CA cert is in Local Machine Trusted
        /// Root. Read-only store access — does not require elevation.</summary>
        public static bool IsCaCertInstalled() {
            try {
                var cert = LoadEmbeddedCaCert();
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine)) {
                    store.Open(OpenFlags.ReadOnly);
                    bool found = store.Certificates
                                      .Find(X509FindType.FindByThumbprint, cert.Thumbprint, false)
                                      .Count > 0;
                    store.Close();
                    return found;
                }
            } catch {
                return false;
            }
        }

        // ---- Uninstall ------------------------------------------------

        public static bool RestoreMcx2Prov(Action<string> log) {
            string target = Mcx2ProvPath;
            string backup = target + ".softsled-backup";
            log("• Restoring the original Mcx2Prov.exe");
            log("    Target: " + target);

            if (!File.Exists(backup)) {
                log("    No backup found (" + Path.GetFileName(backup) + ").");
                log("    The original was never backed up by this tool, so it can't be " +
                    "restored automatically.");
                return false;
            }

            try {
                if (File.Exists(target)) {
                    log("    Taking ownership / granting Administrators full control...");
                    RunProcess("takeown.exe", "/f \"" + target + "\"", log);
                    RunProcess("icacls.exe", "\"" + target + "\" /grant *S-1-5-32-544:F", log);
                }
                File.Copy(backup, target, true);
                File.Delete(backup);
                log("    Original Mcx2Prov.exe restored; backup removed. [OK]");
                return true;
            } catch (Exception ex) {
                log("    ERROR: " + ex.Message);
                return false;
            }
        }

        public static bool RemoveCaCertificate(Action<string> log) {
            log("• Removing the SoftSled CA certificate from Trusted Root");
            try {
                var cert = LoadEmbeddedCaCert();
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine)) {
                    store.Open(OpenFlags.ReadWrite);
                    var match = store.Certificates.Find(
                        X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    if (match.Count == 0) {
                        log("    Not present — nothing to remove. [OK]");
                    } else {
                        store.RemoveRange(match);
                        log("    Removed from Local Machine Trusted Root. [OK]");
                    }
                    store.Close();
                }
                return true;
            } catch (Exception ex) {
                log("    ERROR: " + ex.Message);
                return false;
            }
        }

        // ---- Helpers --------------------------------------------------

        private static X509Certificate2 LoadEmbeddedCaCert() {
            return new X509Certificate2(GetResourceBytes(CaCertResource));  // DER .cer
        }

        private static byte[] GetResourceBytes(string suffix) {
            using (var s = GetResourceStream(suffix))
            using (var ms = new MemoryStream()) {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static Stream GetResourceStream(string suffix) {
            var asm = Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames()
                             .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null)
                throw new FileNotFoundException("Embedded resource not found: " + suffix);
            return asm.GetManifestResourceStream(name);
        }

        private static string ExtractResourceToTemp(string suffix) {
            string path = Path.Combine(Path.GetTempPath(), "SoftSledHostSetup_" + suffix);
            using (var src = GetResourceStream(suffix))
            using (var dst = File.Create(path)) {
                src.CopyTo(dst);
            }
            return path;
        }

        private static void RunProcess(string exe, string args, Action<string> log) {
            var psi = new ProcessStartInfo(exe, args) {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            try {
                using (var p = Process.Start(psi)) {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (!string.IsNullOrWhiteSpace(stdout)) log("      " + stdout.Trim());
                    if (!string.IsNullOrWhiteSpace(stderr)) log("      " + stderr.Trim());
                }
            } catch (Exception ex) {
                log("      (" + exe + " failed: " + ex.Message + ")");
            }
        }
    }
}
