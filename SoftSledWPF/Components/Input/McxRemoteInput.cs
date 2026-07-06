using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SoftSled.Components.Input {

    /// <summary>
    /// Raw Input (WM_INPUT) capture for an RC6 / Windows Media Center remote on a
    /// Microsoft eHome IR receiver.
    ///
    /// <para>An MCE remote delivers its buttons in several HID streams: transport
    /// keys on the Consumer page (0x0C), and the Media-Center-specific buttons —
    /// the Green Start button, Guide, Live TV, Recorded TV, DVD Menu, the coloured
    /// buttons, etc. — on the vendor "Windows Media Center Remote Control" usage
    /// page (0xFFBC). The plain navigation/OK/digit buttons arrive as ordinary
    /// keyboard input and already reach WMC through SoftSled's existing keyboard
    /// forwarding, so we deliberately do NOT sink the keyboard here (that would log
    /// every physical keystroke too).</para>
    ///
    /// <para>PHASE 1 (this file): a DIAGNOSTIC. It enumerates the HID devices so we
    /// can see the remote's exact usage page/usage + VID/PID/name, and logs the raw
    /// report bytes for every button press. Press each remote button once and the
    /// log gives us the exact mapping to build phase 2 (button → WMC keyboard
    /// shortcut, forwarded over RDP; Green button → start the SoftSled session).</para>
    ///
    /// <para>Registered with RIDEV_INPUTSINK so input is received even when SoftSled
    /// isn't the foreground window — needed so the Green button can launch us.</para>
    /// </summary>
    public sealed class McxRemoteInput {

        public const int WM_INPUT = 0x00FF;

        private readonly Logger _log;
        private bool _registered;

        // Report IDs the eHome remote uses (first byte of each HID report).
        private const int REPORT_CONSUMER = 0x02;  // Consumer page (0x0C), 16-bit usage
        private const int REPORT_MCE      = 0x03;  // MCE vendor page (0xFFBC), 8-bit usage

        /// <summary>Invoked (on the UI thread) with the WMC RemoteCommand id when a
        /// MAPPED remote button is pressed. The host decides what to do with it
        /// (forward to WMC over RDP in a session; Green-start when idle).</summary>
        public Action<int> RemoteCommand;

        /// <summary>Invoked (UI thread) with PS/2 Set-1 scan codes for buttons WMC
        /// does NOT bind in its table — forwarded directly as the built-in WMC
        /// shortcut (e.g. Skip Forward/Back → Ctrl+F / Ctrl+B).</summary>
        public Action<int[]> RemoteKeyChord;

        /// <summary>Invoked (UI thread) for a client-side action (e.g. leave the
        /// session) rather than something forwarded to WMC.</summary>
        public Action<RemoteLocalAction> RemoteLocal;

        // Active dispatch maps, built from RemoteCommandCatalog (the shipped
        // defaults) overlaid with the user's learned overrides in config.
        // Rebuilt by ReloadBindings() at construction and whenever the Remote
        // settings page changes a mapping. usage key = (reportId<<16)|usage
        // (REPORT_KEYBOARD is used for plain keyboard keys such as ESC).
        private System.Collections.Generic.Dictionary<int, int>   _usageToCmd
            = new System.Collections.Generic.Dictionary<int, int>();
        private System.Collections.Generic.Dictionary<int, int[]> _usageToChord
            = new System.Collections.Generic.Dictionary<int, int[]>();
        private System.Collections.Generic.Dictionary<int, RemoteLocalAction> _usageToLocal
            = new System.Collections.Generic.Dictionary<int, RemoteLocalAction>();

        /// <summary>Look up the WMC command id bound to a usage (HID or keyboard).</summary>
        public bool TryGetCommand(int usageKey, out int cmdId) => _usageToCmd.TryGetValue(usageKey, out cmdId);
        /// <summary>Look up the direct scan-code chord bound to a usage.</summary>
        public bool TryGetChord(int usageKey, out int[] chord) => _usageToChord.TryGetValue(usageKey, out chord);
        /// <summary>Look up the client-side local action bound to a usage.</summary>
        public bool TryGetLocalAction(int usageKey, out RemoteLocalAction action) => _usageToLocal.TryGetValue(usageKey, out action);

        // "Learn" mode: while active, the very next button press is captured and
        // reported to _onLearned (instead of being dispatched as a command), so
        // the settings page can bind that physical button to a chosen command.
        private bool _learning;
        private Action<int> _onLearned;

        /// <summary>True while waiting to capture a button for the Remote page.</summary>
        public bool IsLearning => _learning;

        public McxRemoteInput(Logger log) {
            _log = log;
            ReloadBindings();
        }

        /// <summary>Rebuild the active button→command maps from the catalogue +
        /// the user's saved overrides. Call after the Remote settings page edits
        /// a binding, and at construction.</summary>
        public void ReloadBindings() {
            try {
                var cfg = Configuration.SoftSledConfigManager.ReadConfig();
                RemoteCommandCatalog.BuildRuntimeMaps(cfg, out _usageToCmd, out _usageToChord, out _usageToLocal);
                _log?.LogInfo($"[mcx-remote] bindings loaded: {_usageToCmd.Count} command(s) + " +
                              $"{_usageToChord.Count} direct chord(s) + {_usageToLocal.Count} local action(s)");
            } catch (Exception ex) {
                _log?.LogError("[mcx-remote] ReloadBindings threw: " + ex.Message);
            }
        }

        /// <summary>Enter learn mode: the next button press is reported to
        /// <paramref name="onLearned"/> (on the UI/WndProc thread) as a usage key
        /// and is NOT dispatched. Replaces any pending learn.</summary>
        public void BeginLearn(Action<int> onLearned) {
            _onLearned = onLearned;
            _learning = true;
            _log?.LogInfo("[mcx-remote] learn mode: waiting for a button press");
        }

        /// <summary>Cancel a pending learn without capturing anything.</summary>
        public void CancelLearn() {
            if (!_learning) return;
            _learning = false;
            _onLearned = null;
            _log?.LogInfo("[mcx-remote] learn mode: cancelled");
        }

        /// <summary>
        /// Feed a keyboard-sourced button into a pending learn. Used by the shell
        /// for keys (ESC, F12, …) that arrive on the WPF keyboard path rather than
        /// as HID reports. Returns true if a learn was in progress and captured.
        /// </summary>
        public bool TryCompleteLearnWithKey(int usageKey) {
            if (!_learning) return false;
            CaptureLearn(usageKey);
            return true;
        }

        /// <summary>Complete the pending learn with a captured usage key (one-shot).</summary>
        private void CaptureLearn(int usageKey) {
            _learning = false;
            var cb = _onLearned;
            _onLearned = null;
            _log?.LogInfo($"[mcx-remote] learned usage key=0x{usageKey:X}");
            try { cb?.Invoke(usageKey); }
            catch (Exception ex) { _log?.LogError("[mcx-remote] learn callback threw: " + ex.Message); }
        }

        // ---- P/Invoke ---------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint   dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER {
            public uint   dwType;
            public uint   dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST {
            public IntPtr hDevice;
            public uint   dwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RID_DEVICE_INFO_HID {
            public uint   dwVendorId;
            public uint   dwProductId;
            public uint   dwVersionNumber;
            public ushort usUsagePage;
            public ushort usUsage;
        }

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT       = 0x10000003;
        private const uint RIM_TYPEMOUSE    = 0;
        private const uint RIM_TYPEKEYBOARD = 1;
        private const uint RIM_TYPEHID      = 2;

        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_DEVICEINFO = 0x2000000b;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
            IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(
            [In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand,
            IntPtr pData, ref uint pcbSize);

        // Usage pages/usages an MCE remote reports on. (page, usage) top-level
        // collections we ask Raw Input to deliver. The device enumeration below
        // confirms the real values; we register a superset so the first capture
        // works regardless.
        private static RAWINPUTDEVICE[] Targets(IntPtr hwnd) => new[] {
            new RAWINPUTDEVICE { usUsagePage = 0x0C,   usUsage = 0x01, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // Consumer Control (transport)
            new RAWINPUTDEVICE { usUsagePage = 0xFFBC, usUsage = 0x88, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // MCE Remote (Green/Guide/etc.)
            new RAWINPUTDEVICE { usUsagePage = 0x01,   usUsage = 0x80, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd }, // System Control (power)
        };

        // ---- Public surface ---------------------------------------------

        /// <summary>Log every Raw Input HID device + its usage page/usage so we can
        /// identify the eHome MCE remote and confirm which collection to register.</summary>
        public void EnumerateDevices() {
            try {
                uint count = 0;
                uint listSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
                if (GetRawInputDeviceList(null, ref count, listSize) != 0 || count == 0) {
                    _log?.LogInfo("[mcx-remote] no raw input devices enumerated");
                    return;
                }
                var list = new RAWINPUTDEVICELIST[count];
                uint got = GetRawInputDeviceList(list, ref count, listSize);
                if (got == uint.MaxValue) { _log?.LogError("[mcx-remote] GetRawInputDeviceList failed"); return; }

                _log?.LogInfo($"[mcx-remote] enumerating {got} raw input device(s):");
                for (int i = 0; i < got; i++) {
                    if (list[i].dwType != RIM_TYPEHID) continue;   // only HID (skip kbd/mouse)
                    string name = GetDeviceName(list[i].hDevice);
                    if (TryGetHidInfo(list[i].hDevice, out RID_DEVICE_INFO_HID hid)) {
                        _log?.LogInfo($"[mcx-remote]   HID usagePage=0x{hid.usUsagePage:X4} usage=0x{hid.usUsage:X2} " +
                                      $"VID=0x{hid.dwVendorId:X4} PID=0x{hid.dwProductId:X4}  {name}");
                    } else {
                        _log?.LogInfo($"[mcx-remote]   HID (info unavailable)  {name}");
                    }
                }
            } catch (Exception ex) {
                _log?.LogError("[mcx-remote] EnumerateDevices threw: " + ex);
            }
        }

        /// <summary>Register for the MCE remote Raw Input streams targeting the given
        /// window. Idempotent.</summary>
        public void Register(IntPtr hwnd) {
            if (_registered) return;
            try {
                var devs = Targets(hwnd);
                if (RegisterRawInputDevices(devs, (uint)devs.Length,
                        (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) {
                    _registered = true;
                    _log?.LogInfo("[mcx-remote] registered Raw Input for Consumer(0x0C/0x01), " +
                                  "MCE(0xFFBC/0x88), SystemControl(0x01/0x80) with INPUTSINK");
                } else {
                    int err = Marshal.GetLastWin32Error();
                    _log?.LogError($"[mcx-remote] RegisterRawInputDevices FAILED — Win32 {err}");
                }
            } catch (Exception ex) {
                _log?.LogError("[mcx-remote] Register threw: " + ex);
            }
        }

        /// <summary>Handle a WM_INPUT message (call from the window's WndProc hook).
        /// Phase 1: log the device + raw report bytes. Returns true if it was a
        /// WM_INPUT we processed.</summary>
        public bool TryHandleWmInput(int msg, IntPtr lParam) {
            if (msg != WM_INPUT) return false;
            try {
                uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                uint size = 0;
                GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
                if (size == 0) return true;

                byte[] buf = new byte[size];
                var gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try {
                    uint got = GetRawInputData(lParam, RID_INPUT, gch.AddrOfPinnedObject(), ref size, headerSize);
                    if (got != size) return true;
                } finally { gch.Free(); }

                uint dwType = BitConverter.ToUInt32(buf, 0);
                int hOff = (int)headerSize;

                if (dwType == RIM_TYPEHID) {
                    // RAWHID: dwSizeHid, dwCount, then bRawData[dwSizeHid*dwCount].
                    uint dwSizeHid = BitConverter.ToUInt32(buf, hOff);
                    uint dwCount   = BitConverter.ToUInt32(buf, hOff + 4);
                    int dataOff    = hOff + 8;
                    int dataLen    = (int)(dwSizeHid * dwCount);
                    if (dataOff + dataLen > buf.Length) dataLen = buf.Length - dataOff;
                    if (dataLen < 2) return true;

                    // First byte = report id; then the usage (consumer = 16-bit
                    // little-endian, MCE = single byte). usage 0 = key-up/release.
                    int reportId = buf[dataOff];
                    int usage = (reportId == REPORT_CONSUMER && dataLen >= 3)
                        ? (buf[dataOff + 1] | (buf[dataOff + 2] << 8))
                        : buf[dataOff + 1];
                    if (usage == 0) return true;   // release — nothing to do

                    int key = (reportId << 16) | usage;

                    // Learn mode: capture this button for the settings page and
                    // do NOT dispatch it. One-shot — clears itself.
                    if (_learning) {
                        CaptureLearn(key);
                        return true;
                    }

                    if (_usageToLocal.TryGetValue(key, out RemoteLocalAction local)) {
                        _log?.LogInfo($"[mcx-remote] btn report=0x{reportId:X2} usage=0x{usage:X4} → local[{local}]");
                        try { RemoteLocal?.Invoke(local); }
                        catch (Exception ex) { _log?.LogError("[mcx-remote] RemoteLocal handler threw: " + ex.Message); }
                    } else if (_usageToChord.TryGetValue(key, out int[] chord)) {
                        _log?.LogInfo($"[mcx-remote] btn report=0x{reportId:X2} usage=0x{usage:X4} → direct key chord");
                        try { RemoteKeyChord?.Invoke(chord); }
                        catch (Exception ex) { _log?.LogError("[mcx-remote] RemoteKeyChord handler threw: " + ex.Message); }
                    } else if (_usageToCmd.TryGetValue(key, out int cmdId)) {
                        _log?.LogInfo($"[mcx-remote] btn report=0x{reportId:X2} usage=0x{usage:X4} → RemoteCmd[{cmdId}]");
                        try { RemoteCommand?.Invoke(cmdId); }
                        catch (Exception ex) { _log?.LogError("[mcx-remote] RemoteCommand handler threw: " + ex.Message); }
                    } else {
                        _log?.LogInfo($"[mcx-remote] btn report=0x{reportId:X2} usage=0x{usage:X4} UNMAPPED " +
                                      $"({Hex(buf, dataOff, dataLen)})");
                    }
                } else if (dwType == RIM_TYPEKEYBOARD) {
                    ushort makeCode = BitConverter.ToUInt16(buf, hOff);
                    ushort flags    = BitConverter.ToUInt16(buf, hOff + 2);
                    ushort vkey     = BitConverter.ToUInt16(buf, hOff + 6);
                    uint   message  = BitConverter.ToUInt32(buf, hOff + 8);
                    _log?.LogInfo($"[mcx-remote] KBD make=0x{makeCode:X2} flags=0x{flags:X4} " +
                                  $"vkey=0x{vkey:X2} msg=0x{message:X4}");
                }
            } catch (Exception ex) {
                _log?.LogError("[mcx-remote] TryHandleWmInput threw: " + ex.Message);
            }
            return true;
        }

        // ---- Helpers ----------------------------------------------------

        private bool TryGetHidInfo(IntPtr hDevice, out RID_DEVICE_INFO_HID hid) {
            hid = default;
            // RID_DEVICE_INFO: cbSize(4) + dwType(4) + union. For HID the union is
            // RID_DEVICE_INFO_HID. Lay out a buffer big enough and read after the
            // 8-byte (cbSize+dwType) prefix.
            uint cb = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, IntPtr.Zero, ref cb);
            if (cb == 0) return false;
            IntPtr p = Marshal.AllocHGlobal((int)cb);
            try {
                Marshal.WriteInt32(p, 0, (int)cb);            // cbSize (the API wants it set)
                uint r = GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, p, ref cb);
                if (r == uint.MaxValue) return false;
                uint dwType = (uint)Marshal.ReadInt32(p, 4);
                if (dwType != RIM_TYPEHID) return false;
                hid = (RID_DEVICE_INFO_HID)Marshal.PtrToStructure(
                    p + 8, typeof(RID_DEVICE_INFO_HID));
                return true;
            } finally { Marshal.FreeHGlobal(p); }
        }

        private static string GetDeviceName(IntPtr hDevice) {
            try {
                uint cch = 0;
                GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref cch);
                if (cch == 0) return "(no name)";
                IntPtr p = Marshal.AllocHGlobal((int)cch * 2);
                try {
                    uint r = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, p, ref cch);
                    if (r == uint.MaxValue) return "(name unavailable)";
                    return Marshal.PtrToStringAnsi(p) ?? Marshal.PtrToStringUni(p) ?? "(?)";
                } finally { Marshal.FreeHGlobal(p); }
            } catch { return "(name threw)"; }
        }

        private static string Hex(byte[] data, int off, int len) {
            var sb = new StringBuilder(len * 3);
            for (int i = 0; i < len; i++) {
                if (i > 0) sb.Append('-');
                sb.Append(data[off + i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
