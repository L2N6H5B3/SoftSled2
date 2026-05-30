using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SoftSledWPF {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {

        // ===================================================================
        //  App-lifetime file logger
        //  ------------------------------------------------------------------
        //  Created at OnStartup so the file is open BEFORE anything that
        //  could throw runs. Exposed statically so:
        //    * the three global unhandled-exception handlers below can
        //      write crash details into it
        //    * <see cref="Shell.ExtenderSessionControl"/>.InitialiseLogger
        //      can compose it with the textbox logger so every textbox log
        //      line also lands in the file
        //  Null when <see cref="SoftSledConfig.LogToFile"/> is false at
        //  app launch (toggling the checkbox at runtime needs an app
        //  restart to attach a file sink — the Debugging-page text reflects
        //  that).
        // ===================================================================
        public static FileLogger AppLog { get; private set; }

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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hReservedNull,
            uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint FormatMessage(uint dwFlags, IntPtr lpSource,
            uint dwMessageId, uint dwLanguageId, StringBuilder lpBuffer,
            uint nSize, IntPtr Arguments);

        // ntdll!RtlGetVersion — bypasses the manifest-driven version
        // shimming that makes Environment.OSVersion lie. Returns the
        // ACTUAL kernel version on modern Windows. The OSVERSIONINFOEX
        // struct layout is documented in winnt.h.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOEX {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX versionInfo);

        // Bottom-up dependency order. softsled-rdp.dll → freerdp-client3 →
        // freerdp3 → winpr3 → libssl → libcrypto + openh264 + zd. If a
        // transitive dep fails, the FIRST failure in this list pinpoints
        // it — LoadLibrary's "module not found" error always names the
        // *top-level* DLL you asked for, hiding the actual culprit.
        private static readonly string[] ExpectedNativeDlls = {
            "libcrypto-3-x64.dll",
            "libssl-3-x64.dll",
            "z.dll",
            "openh264-7.dll",
            "winpr3.dll",
            "freerdp3.dll",
            "freerdp-client3.dll",
            "softsled-rdp.dll",
        };

        protected override void OnStartup(StartupEventArgs e) {
            // File logger FIRST so SetDllDirectory + native-probe diagnostics
            // land on disk. None of those steps P/Invoke into our shim, so
            // they can run before the loader hint is configured.
            TryInitAppLog();
            SubscribeGlobalExceptionHandlers();

            // True Windows version — Environment.OSVersion is shimmed back
            // to 6.2 (Win8) on Win 10/11 unless the manifest declares
            // supportedOS GUIDs. RtlGetVersion bypasses the shim.
            LogRealOsVersion();

            var exeDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            var nativeDir = Path.Combine(exeDir, "native", arch);
            ApplySetDllDirectory(nativeDir);

            // Diagnostic native-DLL probe. Runs every launch and is cheap
            // (loaded modules increment a ref count, the eventual P/Invoke
            // reuses them). When loading fails, the log line names the
            // first transitive dependency that couldn't be resolved —
            // which is usually the actual missing module, NOT the one
            // the .NET DllImport exception cites.
            ProbeNativeDlls(nativeDir);

            try { AppLog?.LogInfo("[app] OnStartup completed — entering main message loop"); } catch { }

            base.OnStartup(e);
        }

        private static void ApplySetDllDirectory(string nativeDir) {
            try {
                if (Directory.Exists(nativeDir)) {
                    if (!SetDllDirectory(nativeDir)) {
                        int err = Marshal.GetLastWin32Error();
                        AppLog?.LogError($"[app] SetDllDirectory(\"{nativeDir}\") FAILED — Win32 error {err}: {WinErrorMessage(err)}");
                    } else {
                        AppLog?.LogInfo($"[app] SetDllDirectory(\"{nativeDir}\") OK");
                    }
                } else {
                    AppLog?.LogError($"[app] native directory does NOT exist: {nativeDir}");
                }
            } catch (Exception ex) {
                AppLog?.LogError("[app] ApplySetDllDirectory threw: " + ex);
            }
        }

        private static void ProbeNativeDlls(string nativeDir) {
            try {
                // System runtime check FIRST. If a system runtime DLL is
                // missing, every native DLL that depends on it (OpenSSL,
                // FreeRDP, our shim) will fail to load with error 126
                // even though the file itself is present in native\x64.
                // OpenH264 has minimal runtime deps so it'll load even
                // when these are missing — a successful openh264 + failed
                // libcrypto pattern is the smoking gun for this case.
                ProbeSystemRuntime();

                if (!Directory.Exists(nativeDir)) {
                    AppLog?.LogError($"[probe] skipping — native directory does not exist: {nativeDir}");
                    return;
                }

                // Inventory: list every *.dll in the directory with size
                // + last-write timestamp so a stale / partial copy is
                // obvious by inspection.
                try {
                    var files = Directory.GetFiles(nativeDir, "*.dll",
                                                   SearchOption.TopDirectoryOnly);
                    AppLog?.LogInfo($"[probe] native dir contains {files.Length} *.dll files:");
                    foreach (var f in files) {
                        var fi = new FileInfo(f);
                        AppLog?.LogInfo($"[probe]   {fi.Name,-30} {fi.Length,12:N0} bytes  {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    }
                } catch (Exception ex) {
                    AppLog?.LogError("[probe] inventory failed: " + ex.Message);
                }

                // Two-pass probe per DLL:
                //   PASS 1: LoadLibraryEx(LOAD_LIBRARY_AS_DATAFILE) — file
                //           is opened but no dependencies are resolved.
                //           Tells us the file is readable + has a valid
                //           PE header. SUCCESS here + failure in pass 2
                //           = dependency issue (the most common case).
                //   PASS 2: regular LoadLibrary — full dependency resolve
                //           and DllMain run. Mirrors what P/Invoke would
                //           do later.
                //
                // The difference between the two is exactly what we need
                // to know: "missing file" vs "missing dependency."
                const uint LOAD_LIBRARY_AS_DATAFILE       = 0x00000002;
                foreach (var dll in ExpectedNativeDlls) {
                    string fullPath = Path.Combine(nativeDir, dll);
                    IntPtr dataHandle = LoadLibraryEx(fullPath, IntPtr.Zero,
                                                     LOAD_LIBRARY_AS_DATAFILE);
                    bool fileOk = dataHandle != IntPtr.Zero;
                    int  fileErr = fileOk ? 0 : Marshal.GetLastWin32Error();

                    IntPtr fullHandle = LoadLibrary(dll);
                    bool fullOk = fullHandle != IntPtr.Zero;
                    int  fullErr = fullOk ? 0 : Marshal.GetLastWin32Error();

                    if (fullOk) {
                        AppLog?.LogInfo($"[probe] {dll,-22} file=OK  load=OK");
                    } else if (fileOk) {
                        // File opens but full load fails → DEPENDENCY problem.
                        // Read its import table directly to identify which
                        // specific imported DLL the OS can't resolve.
                        AppLog?.LogError(
                            $"[probe] {dll,-22} file=OK  load=FAIL " +
                            $"(err {fullErr} 0x{fullErr:X8}: {WinErrorMessage(fullErr)}) — probing imports:");
                        ProbeImportedDlls(fullPath);
                    } else {
                        AppLog?.LogError(
                            $"[probe] {dll,-22} file=FAIL " +
                            $"(err {fileErr} 0x{fileErr:X8}: {WinErrorMessage(fileErr)})" +
                            $"  load=FAIL (err {fullErr} 0x{fullErr:X8})");
                    }
                }
            } catch (Exception ex) {
                AppLog?.LogError("[probe] ProbeNativeDlls threw: " + ex);
            }
        }

        /// <summary>
        /// Log the REAL Windows version via RtlGetVersion — works around
        /// the manifest-driven shim that pegs Environment.OSVersion at
        /// 6.2 (Win8) for any process without an app.manifest that
        /// declares <c>supportedOS</c> GUIDs. The shimmed value is mostly
        /// harmless but makes the log header misleading when triaging
        /// "does this app even work on the user's Windows version?"
        /// </summary>
        private static void LogRealOsVersion() {
            try {
                var info = new RTL_OSVERSIONINFOEX();
                info.dwOSVersionInfoSize = (uint)Marshal.SizeOf(info);
                int status = RtlGetVersion(ref info);
                if (status == 0) {
                    // Windows 11 reports as 10.0.22000+ — Microsoft never
                    // bumped the major version. Distinguish by build:
                    //   < 10240          : Win 8 / 8.1
                    //   10240..21999     : Win 10
                    //   22000+           : Win 11
                    string name;
                    if (info.dwMajorVersion < 10)               name = "Windows 8.1 or earlier";
                    else if (info.dwBuildNumber >= 22000)       name = "Windows 11";
                    else if (info.dwBuildNumber >= 10240)       name = "Windows 10";
                    else                                        name = $"Windows {info.dwMajorVersion}.{info.dwMinorVersion}";
                    AppLog?.LogInfo($"[app] real OS version: {name} (NT {info.dwMajorVersion}.{info.dwMinorVersion} build {info.dwBuildNumber})");
                } else {
                    AppLog?.LogError($"[app] RtlGetVersion returned status 0x{status:X8}");
                }
            } catch (Exception ex) {
                AppLog?.LogError("[app] LogRealOsVersion threw: " + ex);
            }
        }

        /// <summary>
        /// Inventory of system runtime DLLs that OpenSSL 3.x, FreeRDP, and
        /// our own shim all depend on. A missing or stale entry in
        /// <c>C:\Windows\System32</c> for any of these is the usual reason
        /// for "everything fails to load except openh264." Checks both
        /// presence (FileInfo) AND loadability (LoadLibrary). The
        /// LoadLibrary check is what matters — a 0-byte placeholder file
        /// would pass the existence check but fail load.
        /// </summary>
        private static void ProbeSystemRuntime() {
            // The critical four. vcruntime140_1.dll specifically requires
            // a VC++ Redistributable >= 2019; users with the older 2015
            // standalone redist will be missing it.
            string[] criticalDlls = {
                "vcruntime140.dll",        // MSVC 2015+ runtime
                "vcruntime140_1.dll",      // MSVC 2019+ runtime (newer)
                "msvcp140.dll",            // MSVC C++ standard library
                "ucrtbase.dll",            // Universal C Runtime
            };

            string system32 = Environment.SystemDirectory; // C:\Windows\System32
            AppLog?.LogInfo($"[probe] checking system runtime DLLs in {system32}:");
            foreach (var dll in criticalDlls) {
                string path = Path.Combine(system32, dll);
                bool exists = File.Exists(path);
                long size = exists ? new FileInfo(path).Length : 0;
                IntPtr handle = LoadLibrary(dll); // searches System32 normally
                if (handle == IntPtr.Zero) {
                    int err = Marshal.GetLastWin32Error();
                    AppLog?.LogError(
                        $"[probe]   {dll,-22} exists={exists} size={size:N0} " +
                        $"LoadLibrary FAILED — Win32 error {err} (0x{err:X8}): {WinErrorMessage(err)}" +
                        (!exists ? "  ← MISSING — install latest VC++ x64 Redistributable" : ""));
                } else {
                    AppLog?.LogInfo($"[probe]   {dll,-22} exists={exists} size={size:N0}  OK");
                }
            }
        }

        /// <summary>
        /// Walk a DLL's PE import directory and probe each imported DLL
        /// individually. The first FAILED entry is almost certainly the
        /// transitive dependency the OS loader can't resolve — which is
        /// exactly what we need to know when LoadLibrary returns 126 on
        /// the top-level DLL.
        /// </summary>
        private static void ProbeImportedDlls(string dllPath) {
            try {
                var imports = ReadPeImports(dllPath);
                if (imports == null) {
                    AppLog?.LogError("[probe]   (PE parse failed — couldn't read import table)");
                    return;
                }
                if (imports.Count == 0) {
                    AppLog?.LogInfo("[probe]   (no imports listed in PE — unusual but not necessarily wrong)");
                    return;
                }
                AppLog?.LogInfo($"[probe]   import table lists {imports.Count} DLLs:");
                foreach (var importedDll in imports) {
                    IntPtr h = LoadLibrary(importedDll);
                    if (h == IntPtr.Zero) {
                        int err = Marshal.GetLastWin32Error();
                        AppLog?.LogError(
                            $"[probe]     {importedDll,-40} FAIL " +
                            $"(err {err} 0x{err:X8}: {WinErrorMessage(err)})" +
                            $"  ← MISSING DEPENDENCY");
                    } else {
                        AppLog?.LogInfo($"[probe]     {importedDll,-40} OK");
                    }
                }
            } catch (Exception ex) {
                AppLog?.LogError("[probe]   ProbeImportedDlls threw: " + ex.Message);
            }
        }

        /// <summary>
        /// Minimal PE / COFF parser — extracts the list of imported DLL
        /// names from a Portable Executable's import directory. Returns
        /// null if the file isn't a valid PE or its import directory is
        /// unreadable. Supports both PE32 (32-bit) and PE32+ (64-bit)
        /// images.
        /// </summary>
        private static List<string> ReadPeImports(string filePath) {
            try {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs)) {
                    // DOS header: e_lfanew at offset 0x3C → file offset of PE signature
                    fs.Position = 0x3C;
                    int peOffset = br.ReadInt32();

                    // "PE\0\0" signature
                    fs.Position = peOffset;
                    if (br.ReadUInt32() != 0x00004550U) return null;

                    // COFF file header (20 bytes after the signature)
                    br.ReadUInt16(); // Machine
                    ushort numSections = br.ReadUInt16();
                    fs.Position += 12; // TimeDateStamp + PointerToSymbolTable + NumberOfSymbols
                    ushort sizeOfOptionalHeader = br.ReadUInt16();
                    br.ReadUInt16(); // Characteristics

                    // Optional header
                    long optHeaderStart = fs.Position;
                    ushort magic = br.ReadUInt16();
                    bool pe32Plus = (magic == 0x20B);

                    // Data directories live at +96 in PE32, +112 in PE32+
                    int dataDirectoriesOffset = pe32Plus ? 112 : 96;
                    fs.Position = optHeaderStart + dataDirectoriesOffset;

                    // Skip the Export directory (Index 0); read the Import
                    // directory (Index 1) — both are RVA + size pairs (8 B each).
                    fs.Position += 8;
                    uint importRva = br.ReadUInt32();
                    br.ReadUInt32(); // import directory size — not needed

                    if (importRva == 0) return new List<string>(); // no imports

                    // Read section headers so we can convert RVAs to file offsets.
                    fs.Position = optHeaderStart + sizeOfOptionalHeader;
                    var sections = new List<(uint vAddr, uint vSize, uint rawAddr, uint rawSize)>();
                    for (int i = 0; i < numSections; i++) {
                        fs.Position += 8;            // Name[8]
                        uint vSize = br.ReadUInt32();
                        uint vAddr = br.ReadUInt32();
                        uint rawSize = br.ReadUInt32();
                        uint rawAddr = br.ReadUInt32();
                        fs.Position += 16;           // remaining IMAGE_SECTION_HEADER fields
                        sections.Add((vAddr, vSize, rawAddr, rawSize));
                    }

                    long importFileOffset = RvaToFileOffset(importRva, sections);
                    if (importFileOffset == 0) return null;

                    // Walk IMAGE_IMPORT_DESCRIPTOR table (20 B each, null entry terminates)
                    var imports = new List<string>();
                    fs.Position = importFileOffset;
                    while (true) {
                        br.ReadUInt32();             // OriginalFirstThunk
                        br.ReadUInt32();             // TimeDateStamp
                        br.ReadUInt32();             // ForwarderChain
                        uint nameRva = br.ReadUInt32();
                        br.ReadUInt32();             // FirstThunk

                        if (nameRva == 0) break;     // terminator

                        long nameFileOffset = RvaToFileOffset(nameRva, sections);
                        if (nameFileOffset == 0) continue;

                        long saved = fs.Position;
                        fs.Position = nameFileOffset;
                        var sb = new StringBuilder();
                        for (;;) {
                            byte b = br.ReadByte();
                            if (b == 0) break;
                            sb.Append((char)b);
                        }
                        imports.Add(sb.ToString());
                        fs.Position = saved;
                    }
                    return imports;
                }
            } catch {
                return null;
            }
        }

        private static long RvaToFileOffset(uint rva,
            List<(uint vAddr, uint vSize, uint rawAddr, uint rawSize)> sections) {
            foreach (var s in sections) {
                if (rva >= s.vAddr && rva < s.vAddr + s.vSize) {
                    return s.rawAddr + (rva - s.vAddr);
                }
            }
            return 0;
        }

        /// <summary>FormatMessage wrapper that converts a Win32 error
        /// code into the human-readable system message.</summary>
        private static string WinErrorMessage(int errorCode) {
            const uint FORMAT_MESSAGE_FROM_SYSTEM    = 0x00001000;
            const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;
            try {
                var sb = new StringBuilder(512);
                uint chars = FormatMessage(
                    FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                    IntPtr.Zero, (uint)errorCode, 0, sb, (uint)sb.Capacity, IntPtr.Zero);
                return chars > 0 ? sb.ToString().Trim() : "(no message)";
            } catch {
                return "(FormatMessage threw)";
            }
        }

        protected override void OnExit(ExitEventArgs e) {
            try { AppLog?.LogInfo($"[app] OnExit fired (exit code = {e.ApplicationExitCode}) — graceful shutdown"); } catch { }
            try { AppLog?.Dispose(); } catch { }
            AppLog = null;
            base.OnExit(e);
        }

        // ===================================================================
        //  File-logger init
        // ===================================================================

        private static void TryInitAppLog() {
            try {
                var cfg = SoftSledConfigManager.ReadConfig();
                if (!cfg.LogToFile) return;

                string dir = cfg.LogFileDirectory;
                if (string.IsNullOrWhiteSpace(dir)) {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SoftSled", "Logs");
                }
                string fileName = $"softsled-{DateTime.Now:yyyyMMdd-HHmmss}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}.log";
                string fullPath = Path.Combine(dir, fileName);

                AppLog = new FileLogger(fullPath);
                AppLog.IsLoggingDebug = true;
                AppLog.LogInfo($"[app] file logger armed at {fullPath}");
            } catch (Exception ex) {
                // Don't let log-init failure block app startup. The textbox
                // logger comes up later anyway.
                System.Diagnostics.Debug.WriteLine("[App.TryInitAppLog] " + ex);
            }
        }

        // ===================================================================
        //  Global unhandled-exception handlers
        // ===================================================================

        private void SubscribeGlobalExceptionHandlers() {
            // 1. Last-chance for ANY thread (worker, finaliser, background).
            //    Fires after Dispatcher / Task handlers don't observe.
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            // 2. WPF dispatcher (UI thread) — covers click handlers,
            //    BeginInvoke continuations, all WPF event handlers.
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 3. Tasks that threw but were never awaited / observed. Fires
            //    on the finaliser thread when the Task object is collected.
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) {
            try {
                var ex = e.ExceptionObject as Exception;
                string detail = ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "(no exception object)";
                AppLog?.LogError(
                    "[unhandled-exception] AppDomain — " +
                    $"IsTerminating={e.IsTerminating}, " +
                    $"thread={System.Threading.Thread.CurrentThread.ManagedThreadId}" +
                    Environment.NewLine +
                    detail);
                // AutoFlush in FileLogger means this is on disk already.
            } catch {
                // Last-chance handler must NEVER throw — that would crash
                // the CLR's failure-handling path itself.
            }
            // We can't prevent termination when IsTerminating=true; CLR
            // is about to kill the process. Best we can do is land the
            // log line on disk before that happens.
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
            try {
                AppLog?.LogError(
                    "[unhandled-exception] WPF dispatcher — " +
                    $"thread={System.Threading.Thread.CurrentThread.ManagedThreadId}" +
                    Environment.NewLine +
                    e.Exception);
            } catch { /* see note above */ }
            // Leave e.Handled=false (the default) so the exception bubbles
            // up to AppDomain.UnhandledException and terminates the process
            // — same behaviour as before the handler existed, just with a
            // captured log line. Suppressing here would change observable
            // behaviour while debugging the actual problem.
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e) {
            try {
                AppLog?.LogError(
                    "[unhandled-exception] Unobserved Task — " +
                    "(non-fatal: a Task threw but no await observed it)" +
                    Environment.NewLine +
                    e.Exception);
            } catch { }
            // Observe — otherwise a host with ThrowUnobservedTaskExceptions=true
            // in app.config would terminate the process for a non-fatal
            // background failure. Our intent is "capture but keep running".
            e.SetObserved();
        }
    }
}
