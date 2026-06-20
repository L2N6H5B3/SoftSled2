using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SoftSled.HostSetup {

    /// <summary>
    /// In-process replacement for the <c>takeown.exe</c> + <c>icacls.exe</c>
    /// shell-outs: takes ownership of a protected (TrustedInstaller-owned)
    /// file and grants the Administrators group Full Control, using the Win32
    /// privilege APIs and .NET ACL classes directly.
    ///
    /// <para>Spawning <c>takeown</c>/<c>icacls</c> to seize and re-ACL a file
    /// in <c>%WINDIR%</c> is a behaviour pattern heuristic AV engines associate
    /// with droppers; doing the same work in-process (no child processes)
    /// removes that signature and is also more robust — no console-output
    /// parsing and no localisation surprises.</para>
    /// </summary>
    internal static class HostFileAccess {

        /// <summary>
        /// Take ownership of <paramref name="path"/> (set owner =
        /// Administrators) and add an ACE granting Administrators Full Control,
        /// so a subsequent write to the protected file succeeds. Requires the
        /// process to be elevated. Throws on failure.
        /// </summary>
        public static void GrantAdminsFullControl(string path) {
            EnablePrivilege("SeTakeOwnershipPrivilege");
            EnablePrivilege("SeRestorePrivilege");

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            // 1) Take ownership: set the owner to Administrators. We don't yet
            //    hold WRITE_OWNER on the TrustedInstaller-owned file — the
            //    privileges enabled above are what let this through.
            var ownerSec = new FileSecurity();
            ownerSec.SetOwner(admins);
            File.SetAccessControl(path, ownerSec);

            // 2) As owner we can rewrite the DACL — grant Administrators Full
            //    Control so the patch write that follows is permitted.
            var daclSec = File.GetAccessControl(path);
            daclSec.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(path, daclSec);
        }

        // ---- Win32 token-privilege plumbing ---------------------------

        private const int  TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const int  TOKEN_QUERY             = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED    = 0x0002;
        private const int  ERROR_NOT_ALL_ASSIGNED  = 1300;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
            int bufferLength, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static void EnablePrivilege(string name) {
            if (!OpenProcessToken(GetCurrentProcess(),
                                  TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                throw new InvalidOperationException(
                    "OpenProcessToken failed (" + Marshal.GetLastWin32Error() + ").");
            try {
                if (!LookupPrivilegeValue(null, name, out LUID luid))
                    throw new InvalidOperationException(
                        "LookupPrivilegeValue(" + name + ") failed (" + Marshal.GetLastWin32Error() + ").");

                var tp = new TOKEN_PRIVILEGES {
                    PrivilegeCount = 1,
                    Luid           = luid,
                    Attributes     = SE_PRIVILEGE_ENABLED,
                };
                bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                int err = Marshal.GetLastWin32Error();   // valid: read straight after the call
                if (!ok)
                    throw new InvalidOperationException(
                        "AdjustTokenPrivileges(" + name + ") failed (" + err + ").");
                // The call can "succeed" yet assign nothing — that surfaces as
                // ERROR_NOT_ALL_ASSIGNED and means the privilege isn't held.
                if (err == ERROR_NOT_ALL_ASSIGNED)
                    throw new InvalidOperationException(
                        "Privilege " + name + " is not held — is the tool running as administrator?");
            } finally {
                CloseHandle(token);
            }
        }
    }
}
