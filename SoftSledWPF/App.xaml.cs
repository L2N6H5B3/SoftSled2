using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace SoftSledWPF {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {

        // We ship the FreeRDP-derived native DLLs (softsled-rdp.dll, freerdp3.dll,
        // winpr3.dll, freerdp-client3.dll, libcrypto/libssl, zd) under
        // <appdir>/native/{x64,x86} rather than next to the managed exe. P/Invoke's
        // default search order (app dir, system32, %PATH%, ...) does NOT include
        // arbitrary subdirectories, so [DllImport("softsled-rdp.dll")] would fail
        // with a 0x8007007E "module not found" without help. SetDllDirectory adds
        // that subdir to the loader path for both the initial DLL and any of its
        // own dependencies (freerdp3 -> winpr3 -> libcrypto, etc.).
        //
        // We pick x64 vs x86 at runtime from the host process bitness, so the
        // managed assembly stays AnyCPU and the same build runs on either
        // architecture as long as the matching native/x{86,64} folder is present.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        protected override void OnStartup(StartupEventArgs e) {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            var nativeDir = Path.Combine(exeDir, "native", arch);
            if (Directory.Exists(nativeDir)) {
                if (!SetDllDirectory(nativeDir)) {
                    int err = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine(
                        $"SetDllDirectory({nativeDir}) failed with Win32 error {err}");
                }
            } else {
                System.Diagnostics.Debug.WriteLine(
                    $"Native DLL directory not found: {nativeDir}");
            }
            base.OnStartup(e);
        }
    }
}
