using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace SoftSled.HostSetup {

    /// <summary>
    /// The two host-side actions that let SoftSled pair with a Windows Media
    /// Center PC:
    ///   1. Patch %WINDIR%\ehome\Mcx2Prov.exe in place (one byte) to skip the
    ///      CRL check that SoftSled's CRL-less certs would otherwise fail.
    ///   2. Import the SoftSled CA certificate into the Local Machine
    ///      "Trusted Root Certification Authorities" store.
    /// Both require administrator rights (the app manifest forces elevation).
    /// Only the CA cert is embedded; Mcx2Prov.exe is patched in place on the
    /// host (see <see cref="Mcx2ProvPatcher"/>), so no Microsoft binary is
    /// redistributed and the host's own build is preserved.
    /// </summary>
    internal static class SetupActions {

        private const string CaCertResource = "softsled_ca.cer";

        // ---- Admin check ----------------------------------------------

        public static bool IsAdministrator() {
            try {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            } catch {
                return false;
            }
        }

        // ---- Action 1: patch Mcx2Prov.exe in place --------------------

        /// <summary>The on-disk path of the host's Mcx2Prov.exe.</summary>
        public static string Mcx2ProvPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                         "ehome", "Mcx2Prov.exe");

        public static bool PatchMcx2Prov(Action<string> log) {
            string target = Mcx2ProvPath;
            log("• Patching Mcx2Prov.exe (skip the CRL check for SoftSled certs)");
            log("    Target: " + target);

            string ehome = Path.GetDirectoryName(target);
            if (!Directory.Exists(ehome)) {
                log("    ERROR: " + ehome + " was not found.");
                log("    Windows Media Center does not appear to be installed on this PC.");
                return false;
            }
            if (!File.Exists(target)) {
                log("    ERROR: " + target + " was not found.");
                return false;
            }

            byte[] bytes;
            try {
                bytes = File.ReadAllBytes(target);
            } catch (Exception ex) {
                log("    ERROR: could not read the file: " + ex.Message);
                return false;
            }

            // Locate / classify the patch site before touching anything.
            switch (Mcx2ProvPatcher.Inspect(bytes)) {
                case Mcx2ProvPatcher.PatchState.Patched:
                    log("    Already patched — nothing to do. [OK]");
                    return true;
                case Mcx2ProvPatcher.PatchState.NotFound:
                    log("    ERROR: the CRL-check patch site was not found in this binary.");
                    log("    This build of Mcx2Prov.exe isn't recognised (expected Windows 7 x64).");
                    log("    No changes were made.");
                    return false;
                case Mcx2ProvPatcher.PatchState.Ambiguous:
                    log("    ERROR: the patch signature matched more than once — refusing to guess.");
                    log("    No changes were made.");
                    return false;
                case Mcx2ProvPatcher.PatchState.Unexpected:
                    log("    ERROR: the patch site was found but holds an unexpected value.");
                    log("    No changes were made.");
                    return false;
            }

            // PatchState.Original — safe to patch.
            try {
                // The original is owned by TrustedInstaller with a DACL that
                // blocks writes even for administrators, so take ownership and
                // grant the Administrators group full control first (done
                // in-process — no takeown/icacls child processes).
                log("    Taking ownership / granting Administrators full control...");
                HostFileAccess.GrantAdminsFullControl(target);

                // Keep one pristine backup of the original (never overwrite a
                // backup, so re-running the tool can't lose the original).
                string backup = target + ".softsled-backup";
                if (!File.Exists(backup)) {
                    File.Copy(target, backup);
                    log("    Backed up the original to " + Path.GetFileName(backup));
                } else {
                    log("    Existing backup found — leaving it intact.");
                }

                Mcx2ProvPatcher.Apply(bytes, patch: true);   // flips the single byte
                File.WriteAllBytes(target, bytes);
                log("    Patched the CRL check in place (1 byte changed). [OK]");
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
            }
        }

        // ---- Action 2: import the CA certificate ----------------------

        public static bool ImportCaCertificate(Action<string> log) {
            log("• Importing the SoftSled CA certificate (Local Machine → Trusted Root)");

            X509Certificate2 cert;
            try {
                cert = LoadEmbeddedCaCert();
                log("    Certificate: " + cert.Subject);
                log("    Thumbprint:  " + cert.Thumbprint);
            } catch (Exception ex) {
                log("    ERROR loading the embedded certificate: "
                    + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            // Add (idempotent). Report the exception TYPE on failure so we can
            // tell EntryPointNotFound / access-denied / etc. apart.
            try {
                bool already = WithStore(OpenFlags.ReadWrite, store => {
                    bool present = Contains(store, cert.Thumbprint);
                    if (!present) store.Add(cert);
                    return present;
                });
                if (already) {
                    log("    Already present in Trusted Root — nothing to do. [OK]");
                    return true;
                }
            } catch (Exception ex) {
                log("    ERROR adding to the store: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            // Verify by re-reading, so we report what actually persisted rather
            // than trusting that the add call returned cleanly.
            try {
                bool present = WithStore(OpenFlags.ReadOnly, store => Contains(store, cert.Thumbprint));
                if (present) {
                    log("    Imported into Local Machine Trusted Root. [OK]");
                    return true;
                }
                log("    ERROR: the certificate did not persist in the store after add.");
                return false;
            } catch (Exception ex) {
                log("    WARNING: added without error, but could not verify the store: "
                    + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        // ---- Detection ------------------------------------------------

        /// <summary>True if the host's Mcx2Prov.exe has the CRL-check patch
        /// applied (the single patch byte is in its patched state).</summary>
        public static bool IsMcx2ProvInstalled() {
            return Mcx2ProvPatcher.InspectFile(Mcx2ProvPath)
                   == Mcx2ProvPatcher.PatchState.Patched;
        }

        /// <summary>True if a tool-made backup of the original Mcx2Prov.exe
        /// exists (so it can be restored on uninstall).</summary>
        public static bool HasMcx2ProvBackup() => File.Exists(Mcx2ProvPath + ".softsled-backup");

        /// <summary>True if the SoftSled CA cert is in Local Machine Trusted
        /// Root. Read-only store access — does not require elevation.</summary>
        public static bool IsCaCertInstalled() {
            try {
                var cert = LoadEmbeddedCaCert();
                return WithStore(OpenFlags.ReadOnly, store => Contains(store, cert.Thumbprint));
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

            if (!File.Exists(target)) {
                log("    ERROR: " + target + " was not found.");
                return false;
            }

            try {
                log("    Taking ownership / granting Administrators full control...");
                HostFileAccess.GrantAdminsFullControl(target);

                // Prefer the pristine backup if we made one.
                if (File.Exists(backup)) {
                    File.Copy(backup, target, true);
                    File.Delete(backup);
                    log("    Restored from backup; backup removed. [OK]");
                    return true;
                }

                // No backup — revert the single patch byte in place. Because
                // the patch is one byte with a known before/after, this fully
                // restores the original without needing the backup file.
                byte[] bytes = File.ReadAllBytes(target);
                switch (Mcx2ProvPatcher.Inspect(bytes)) {
                    case Mcx2ProvPatcher.PatchState.Original:
                        log("    No backup, but the file is already un-patched. [OK]");
                        return true;
                    case Mcx2ProvPatcher.PatchState.Patched:
                        Mcx2ProvPatcher.Apply(bytes, patch: false);
                        File.WriteAllBytes(target, bytes);
                        log("    No backup found — reverted the patched byte in place. [OK]");
                        return true;
                    default:
                        log("    No backup found and the patch site is not in a known state — " +
                            "cannot revert automatically.");
                        return false;
                }
            } catch (Exception ex) {
                log("    ERROR: " + ex.Message);
                return false;
            }
        }

        public static bool RemoveCaCertificate(Action<string> log) {
            log("• Removing the SoftSled CA certificate from Trusted Root");
            try {
                var cert = LoadEmbeddedCaCert();
                WithStore(OpenFlags.ReadWrite, store => {
                    var match = store.Certificates.Find(
                        X509FindType.FindByThumbprint, cert.Thumbprint, false);
                    if (match.Count == 0) {
                        log("    Not present — nothing to remove. [OK]");
                    } else {
                        store.RemoveRange(match);
                        log("    Removed from Local Machine Trusted Root. [OK]");
                    }
                });
                return true;
            } catch (Exception ex) {
                log("    ERROR: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // ---- Helpers --------------------------------------------------

        // X509Store only implements IDisposable from .NET 4.6 onwards. WMC
        // hosts commonly run .NET 4.0 (Win7), where a `using`/Dispose() on the
        // store throws EntryPointNotFoundException on scope exit — which is
        // exactly what masked an otherwise-successful cert import. So we open
        // the store, run the body, and Close() explicitly (Close has existed
        // since .NET 2.0); we never call Dispose().
        private static T WithStore<T>(OpenFlags flags, Func<X509Store, T> body) {
            var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(flags);
            try {
                return body(store);
            } finally {
                try { store.Close(); } catch { /* best-effort commit/close */ }
            }
        }

        private static void WithStore(OpenFlags flags, Action<X509Store> body) {
            WithStore<bool>(flags, s => { body(s); return false; });
        }

        private static bool Contains(X509Store store, string thumbprint) {
            return store.Certificates
                        .Find(X509FindType.FindByThumbprint, thumbprint, false)
                        .Count > 0;
        }

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
    }
}
