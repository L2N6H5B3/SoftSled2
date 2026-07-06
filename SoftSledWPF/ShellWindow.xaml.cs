using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Input;
using SoftSledWPF.Components.Shell;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace SoftSledWPF {
    /// <summary>
    /// Single host window for the SoftSled shell. Hosts a stack of "pages"
    /// (UserControls) inside a ContentControl so navigation between
    /// landing → settings / pairing / session feels fluid (no new windows
    /// flashing). Window-level key handling routes ESC/Backspace to the
    /// active page's back logic and F11 to a fullscreen toggle.
    /// </summary>
    public partial class ShellWindow : Window {

        // Stack of pages; top of stack is currently displayed. Pure values
        // (no WPF API) so we keep navigation logic testable / predictable.
        private readonly System.Collections.Generic.Stack<UserControl> _pageStack
            = new System.Collections.Generic.Stack<UserControl>();

        // Shared logger handed to pre-session pages (e.g. PairingPage). A
        // CompositeLogger fanning out to the Debug sink (ShellLogger) AND the
        // app-lifetime file logger (App.AppLog, created in App.OnStartup) so
        // shell- and pairing-time diagnostics are captured to the logfile from
        // the outset — not only once a live session opens its own
        // TextBoxLogger. App.AppLog is null when file logging is disabled, and
        // CompositeLogger skips null children, so this is safe either way.
        private readonly Logger _logger = new CompositeLogger(new ShellLogger(), App.AppLog);

        // True when the window is currently in "fullscreen" presentation
        // (WindowStyle=None, Maximized). Toggled by F11 and by the
        // RunFullScreen config tickbox.
        private bool _isFullScreen;

        // ---- Window aspect-ratio lock ---------------------------------
        // When enabled, interactive resize is constrained (via WM_SIZING)
        // so the CLIENT area keeps the selected resolution's aspect ratio.
        // Because every page (and the live RDP framebuffer) is stretched
        // Uniform, matching the client ratio removes the black bars on the
        // edges. _lockedAspectRatio is client width / client height.
        private bool _aspectLockEnabled;
        private double _lockedAspectRatio = 16.0 / 9.0;
        // Guards the programmatic resize in ApplyAspectRatioToCurrentWindow
        // against re-entrancy from the SizeChanged it triggers.
        private bool _applyingAspect;

        public ShellWindow() {
            InitializeComponent();
            this.Loaded += ShellWindow_Loaded;
            this.PreviewKeyDown += ShellWindow_PreviewKeyDown;
            this.PreviewKeyUp += ShellWindow_PreviewKeyUp;
        }

        private void ShellWindow_Loaded(object sender, RoutedEventArgs e) {
            // Install the WM_SIZING hook so interactive resize can be
            // constrained to the selected aspect ratio. Done once, here,
            // because the HWND only exists after the window is sourced.
            InstallSizingHook();

            // Apply the persisted window mode preference. Doing this in
            // Loaded (after the window is on screen) avoids the flicker
            // you'd get from changing WindowStyle pre-show.
            var cfg = SoftSledConfigManager.ReadConfig();
            RefreshAspectLockFromConfig(cfg);
            ApplyFullScreen(cfg.RunFullScreen);
            // Snap the initial windowed size to the locked ratio (no-op in
            // full screen, where ApplyAspectRatioToCurrentWindow bails).
            ApplyAspectRatioToCurrentWindow();

            // First launch: run the setup wizard before anything else. It
            // replaces itself with the landing page on finish/skip.
            if (!cfg.InitialSetupComplete) {
                ShowFirstRunSetup(firstRun: true);
                return;
            }

            ProceedToLandingWithAutoStart();
        }

        /// <summary>
        /// Show the landing page and, when paired + auto-start is on, jump
        /// straight into the live session. Done after ShowLanding so a failed
        /// auto-start naturally returns to a populated landing page.
        /// </summary>
        private void ProceedToLandingWithAutoStart() {
            ShowLanding();
            var cfg = SoftSledConfigManager.ReadConfig();
            if (cfg.IsPaired && cfg.AutoStartWmcOnOpen) {
                Dispatcher.BeginInvoke(new Action(StartExtenderSession),
                                        System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Show the first-run setup wizard. <paramref name="firstRun"/> true =
        /// launched at startup (replaces the stack, marks setup complete on
        /// skip so it doesn't nag every launch); false = re-run from Settings
        /// (pushed over the config page, pops back when done).
        /// </summary>
        private void ShowFirstRunSetup(bool firstRun) {
            var wizard = new FirstRunSetupPage();
            // Live full-screen preview while the wizard is open (transient,
            // like F11). The final value is persisted on Finish below and
            // reverted from config on Cancel.
            wizard.FullScreenToggled += on => ApplyFullScreen(on);
            wizard.Completed += (s, _) => {
                // The wizard can change the full-screen preference — apply it now.
                try { ApplyFullScreen(SoftSledConfigManager.ReadConfig().RunFullScreen); } catch { }
                if (firstRun) {
                    ProceedToLandingWithAutoStart();
                } else {
                    PopPage();
                    (CurrentPage as ConfigPage)?.RefreshFromConfig();
                }
            };
            wizard.Cancelled += (s, _) => {
                // Undo any live full-screen preview — config wasn't written.
                try { ApplyFullScreen(SoftSledConfigManager.ReadConfig().RunFullScreen); } catch { }
                if (firstRun) {
                    // Don't re-prompt on every launch — mark complete with
                    // whatever defaults are in place. Re-runnable from Settings.
                    MarkInitialSetupComplete();
                    ProceedToLandingWithAutoStart();
                } else {
                    PopPage();
                }
            };
            if (firstRun) ReplacePage(wizard);
            else PushPage(wizard);
        }

        private void MarkInitialSetupComplete() {
            try {
                var cfg = SoftSledConfigManager.ReadConfig();
                if (!cfg.InitialSetupComplete) {
                    cfg.InitialSetupComplete = true;
                    SoftSledConfigManager.WriteConfig(cfg);
                }
            } catch { /* best-effort — worst case the wizard shows again */ }
        }

        // ---- Fullscreen / windowed toggle -----------------------------

        /// <summary>
        /// Apply (or remove) the borderless-maximised "TV" presentation.
        /// Idempotent — safe to call with the current mode.
        /// </summary>
        private void ApplyFullScreen(bool fullScreen) {
            if (fullScreen == _isFullScreen && this.IsLoaded) {
                // Already in the requested mode — but if config was just
                // toggled while loaded we still want to ensure WindowState
                // matches, because the user may have un-maximised manually.
                if (fullScreen) this.WindowState = WindowState.Maximized;
                return;
            }
            _isFullScreen = fullScreen;

            if (fullScreen) {
                this.WindowStyle = WindowStyle.None;
                this.ResizeMode  = ResizeMode.NoResize;
                // Toggle WindowState off then on so Windows re-evaluates
                // the maximised area against the (now borderless) chrome.
                this.WindowState = WindowState.Normal;
                this.WindowState = WindowState.Maximized;
                this.Topmost     = false; // explicit — never want Topmost
            } else {
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.ResizeMode  = ResizeMode.CanResize;
                this.WindowState = WindowState.Normal;
                // Returning to windowed mode — re-snap to the locked ratio
                // so the restored window doesn't keep a stale shape.
                ApplyAspectRatioToCurrentWindow();
            }
        }

        private void ToggleFullScreenTransient() {
            // F11 toggle — does NOT persist back to config. The setting
            // page is the only place that writes to config.RunFullScreen.
            ApplyFullScreen(!_isFullScreen);
        }

        // ---- Keyboard routing -----------------------------------------

        private void ShellWindow_PreviewKeyDown(object sender, KeyEventArgs e) {
            // Remote "learn" capture wins over EVERYTHING (incl. F11/F12 and the
            // ESC=leave-session gesture) so any key — ESC, F12, … — can be bound
            // on the Remote settings page. Only active while a learn is pending.
            if (_remote != null && _remote.IsLearning && !e.IsRepeat
                && Keyboard.Modifiers == ModifierKeys.None) {
                Key lk = e.Key == Key.System ? e.SystemKey : e.Key;
                if (!IsModifierKey(lk) &&
                    _remote.TryCompleteLearnWithKey(RemoteCommandCatalog.KeyboardUsageKey((int)lk))) {
                    e.Handled = true;
                    return;
                }
            }

            // In a session, a keyboard key the user has mapped on the Remote page
            // takes precedence over the built-in behaviour below — this is how ESC
            // stops hard-quitting once rebound, and how a rebound Exit Session key
            // (e.g. F12) leaves the session. Gated to no-modifier single keys (a
            // remote button is always modifier-free), so Ctrl+combos and, when a
            // key is left unmapped, F11/F12/ESC keep their defaults. In menus the
            // mapping is NOT applied, so ESC still navigates back.
            if (CurrentPage is ExtenderSessionControl && _remote != null
                && !e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None) {
                Key mk = e.Key == Key.System ? e.SystemKey : e.Key;
                if (!IsModifierKey(mk)) {
                    int kb = RemoteCommandCatalog.KeyboardUsageKey((int)mk);
                    if (_remote.TryGetLocalAction(kb, out RemoteLocalAction la)) {
                        _logger?.LogInfo($"[remote-kbd] key {mk} (kb=0x{kb:X}) → local[{la}]");
                        OnRemoteLocalAction(la); e.Handled = true; return;
                    }
                    if (_remote.TryGetChord(kb, out int[] kc)) {
                        _logger?.LogInfo($"[remote-kbd] key {mk} (kb=0x{kb:X}) → chord");
                        OnRemoteKeyChord(kc); e.Handled = true; return;
                    }
                    if (_remote.TryGetCommand(kb, out int kcmd)) {
                        _logger?.LogInfo($"[remote-kbd] key {mk} (kb=0x{kb:X}) → RemoteCmd[{kcmd}]");
                        OnRemoteCommand(kcmd); e.Handled = true; return;
                    }
                    _logger?.LogInfo($"[remote-kbd] key {mk} (kb=0x{kb:X}) → no mapping (default handling)");
                }
            }

            // F11 is a global toggle regardless of which page is active.
            if (e.Key == Key.F11) {
                ToggleFullScreenTransient();
                e.Handled = true;
                return;
            }

            // F12 opens the Video FPS Lab (libav decode + D3DImage GPU present
            // benchmark). Dev tool — not part of the normal session flow.
            if (e.Key == Key.F12) {
                try {
                    var lab = new SoftSled.Components.AudioVisual.VideoFpsLab.VideoFpsLabWindow {
                        Owner = this
                    };
                    lab.Show();
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine("[shell] FPS lab open failed: " + ex.Message);
                }
                e.Handled = true;
                return;
            }

            // While the live session is active, forward the key into RDP
            // BEFORE any WPF control sees it. ESC explicitly bypasses this
            // path so it acts as a "leave session" gesture; without that,
            // the only way out would be to alt-F4 the window.
            if (CurrentPage is ExtenderSessionControl session) {
                // Ctrl+L: local logger overlay toggle. Must be handled
                // BEFORE ForwardKey so we don't ship it into RDP — the
                // session-active path catches every key by design, which
                // is why the previous Ctrl+L attempt did nothing.
                //
                // Check the modifier (Ctrl held) AND the bare key (L) —
                // WPF reports Key.L for both 'l' and 'L' so a single
                // comparison suffices. Also short-circuits if the user
                // is pressing a different Ctrl+letter sequence.
                if (e.Key == Key.L &&
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) {
                    session.ToggleLogger();
                    e.Handled = true;
                    return;
                }

                // Ctrl+] / Ctrl+[ : live A/V sync nudge (20 ms steps). ']'
                // advances video (fixes video-lags-audio), '[' delays it.
                // Intercepted before ForwardKey so the brackets don't reach
                // WMC. The new trim is applied to the running pacer at once
                // and persisted to config. Watch lip-sync and nudge to taste.
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                    (e.Key == Key.OemCloseBrackets || e.Key == Key.OemOpenBrackets)) {
                    session.NudgeAvSync(e.Key == Key.OemCloseBrackets ? +20 : -20);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape) {
                    // Fallback: ESC still leaves the session when it isn't bound
                    // to anything on the Remote page (e.g. the user cleared the
                    // Exit Session binding) — so you can never get stuck. The
                    // mapped-key handling above runs first, so a rebound ESC or a
                    // custom Exit Session key wins over this.
                    session.Stop();
                    // Fire SessionEnded synthetically — Stop() doesn't raise
                    // FreeRDP state changes during teardown.
                    PopToLanding();
                    e.Handled = true;
                    return;
                }
                if (session.ForwardKey(e, release: false)) {
                    e.Handled = true;
                    return;
                }
            }

            // For non-session pages, ESC / Backspace = back.
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack) {
                if (HandleBack()) {
                    e.Handled = true;
                }
            }
        }

        private void ShellWindow_PreviewKeyUp(object sender, KeyEventArgs e) {
            // Session-only: forward key-up so the host sees clean
            // press/release pairs.
            if (CurrentPage is ExtenderSessionControl session) {
                if (session.ForwardKey(e, release: true)) {
                    e.Handled = true;
                }
            }
        }

        private bool HandleBack() {
            if (CurrentPage is FirstRunSetupPage wizard) {
                // Wizard handles its own back (step back, or cancel on step 1).
                return wizard.HandleBack();
            }
            if (CurrentPage is ConfigPage cfg) {
                // ConfigPage decides whether the back pops a sub-view or
                // raises CloseRequested so the shell pops the page.
                return cfg.HandleBack();
            }
            if (CurrentPage is PairingPage pp) {
                pp.CancelFromShell();
                return true;
            }
            // LandingPage swallows back — there's nowhere to go.
            return CurrentPage != null;
        }

        // ---- Navigation -----------------------------------------------

        private UserControl CurrentPage =>
            _pageStack.Count > 0 ? _pageStack.Peek() : null;

        private void PushPage(UserControl page) {
            _pageStack.Push(page);
            PageHost.Content = page;
            // Crossfade between pages so transitions feel fluid; cheap and
            // doesn't fight the renderer.
            FadeIn(page);
            // Focus the new page so the remote works straight away.
            Dispatcher.BeginInvoke(new Action(() => {
                page.Focus();
                System.Windows.Input.Keyboard.Focus(page);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void PopPage() {
            if (_pageStack.Count == 0) return;
            var leaving = _pageStack.Pop();
            DetachPageEvents(leaving);
            PageHost.Content = CurrentPage;
            if (CurrentPage != null) FadeIn(CurrentPage);
        }

        private void ReplacePage(UserControl page) {
            while (_pageStack.Count > 0) {
                var leaving = _pageStack.Pop();
                DetachPageEvents(leaving);
            }
            PushPage(page);
        }

        private void FadeIn(UserControl page) {
            page.Opacity = 0;
            page.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        // ---- Page constructors ----------------------------------------

        private void ShowLanding() {
            var landing = new LandingPage();
            landing.StartExtenderRequested += Landing_StartExtenderRequested;
            landing.SettingsRequested      += Landing_SettingsRequested;
            landing.QuitRequested          += Landing_QuitRequested;
            ReplacePage(landing);
        }

        /// <summary>
        /// Synthesised "return to landing" path used when the session ends
        /// (FreeRDP disconnect or user ESC). Different from PopPage so we
        /// also drop the session control from the stack regardless of
        /// what's above it.
        /// </summary>
        private void PopToLanding() {
            // Build a fresh landing page so its status banner reflects any
            // changes (e.g. unpair) that happened during the session.
            ShowLanding();
        }

        private void Landing_StartExtenderRequested(object sender, EventArgs e) {
            StartExtenderSession();
        }

        private void Landing_SettingsRequested(object sender, EventArgs e) {
            var cfgPage = new ConfigPage();
            // Give the Remote settings page access to the live remote so it can
            // drive "learn" capture and reload the mappings after an edit.
            cfgPage.AttachRemote(_remote);
            cfgPage.CloseRequested        += (s, _) => PopPage();
            cfgPage.ConfigChanged         += (s, _) => {
                // Resolution and the aspect-lock toggle live here — re-read
                // and re-apply so a new ratio (or enable/disable) takes
                // effect immediately without a restart.
                var c = SoftSledConfigManager.ReadConfig();
                RefreshAspectLockFromConfig(c);
                ApplyAspectRatioToCurrentWindow();
            };
            cfgPage.RunFullScreenChanged  += (s, full) => ApplyFullScreen(full);
            cfgPage.SetupRequested        += (s, _) => ShowFirstRunSetup(firstRun: false);
            PushPage(cfgPage);
        }

        private void Landing_QuitRequested(object sender, EventArgs e) {
            Close();
        }

        /// <summary>
        /// Start the extender session — kick off pairing first if needed,
        /// otherwise push the session control onto the stack and start it.
        /// </summary>
        private void StartExtenderSession() {
            var cfg = SoftSledConfigManager.ReadConfig();
            if (!cfg.IsPaired) {
                var pairing = new PairingPage(_logger);
                pairing.PairingCompleted += (s, _) => {
                    PopPage(); // remove pairing page
                    StartExtenderSession(); // re-enter, now paired
                };
                pairing.PairingCancelled += (s, _) => PopPage();
                PushPage(pairing);
                return;
            }

            var session = new ExtenderSessionControl();
            session.SessionEnded += (s, _) => {
                // SessionEnded only fires for host-initiated disconnects
                // (FreeRDP State.Disconnected / State.Failed). The ESC
                // path in PreviewKeyDown calls Stop() and PopToLanding()
                // directly without raising SessionEnded, so we can safely
                // honour CloseOnWmcClose here without quitting on user
                // back-navigation.
                var current = SoftSledConfigManager.ReadConfig();
                if (current.CloseOnWmcClose) {
                    Close();
                    return;
                }
                PopToLanding();
            };
            PushPage(session);
            session.Start();
        }

        // ---- Window aspect-ratio lock (WM_SIZING) ---------------------

        private const int WM_SIZING = 0x0214;
        // wParam edge codes passed with WM_SIZING (winuser.h WMSZ_*).
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3,
                          WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6,
                          WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        // RC6 / Media Center remote (eHome IR receiver) Raw Input capture.
        private SoftSled.Components.Input.McxRemoteInput _remote;

        private void InstallSizingHook() {
            try {
                var hwnd = new WindowInteropHelper(this).Handle;
                var src = HwndSource.FromHwnd(hwnd);
                src?.AddHook(WndProcHook);

                // Same HWND hook also carries WM_INPUT for the MCE remote. Set it
                // up here, once the window handle exists.
                _remote = new SoftSled.Components.Input.McxRemoteInput(_logger);
                _remote.RemoteCommand = OnRemoteCommand;
                _remote.RemoteKeyChord = OnRemoteKeyChord;
                _remote.RemoteLocal = OnRemoteLocalAction;
                _remote.EnumerateDevices();   // log HID devices so we can ID the remote
                _remote.Register(hwnd);       // start receiving WM_INPUT (INPUTSINK)
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("[shell] InstallSizingHook failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Pull the aspect-lock enable flag + target ratio from config. The
        /// ratio is the selected session resolution's width:height — the same
        /// shape the RDP framebuffer and the Uniform-stretched pages render at.
        /// </summary>
        // WMC RemoteCommand id for the Green Start button. When idle we use it to
        // launch into the session; in a session we send Win+Alt+Enter directly.
        private const int RemoteCmdGreenStart = 23;

        // Win+Alt+Enter for the Green button, sent directly over RDP. LWin MUST be
        // EXTENDED (0x5B | 0x100) — RDP only recognises the Windows key with the
        // E0 prefix, whereas the McxSess table binds it as a non-extended 0x5B
        // (which WMC never sees as Win). LAlt=0x38, Enter=0x1C.
        private static readonly int[] GreenStartChord = { 0x15B, 0x38, 0x1C };

        /// <summary>Handle a mapped MCE-remote button. Runs on the UI thread (the
        /// WM_INPUT hook fires there). In a live session, forward the command to
        /// WMC over RDP. When idle, the Green Start button opens SoftSled into the
        /// extender session (other buttons do nothing until a session is up).</summary>
        private void OnRemoteCommand(int cmdId) {
            // Green: send Win+Alt+Enter directly in a session; open SoftSled when idle.
            if (cmdId == RemoteCmdGreenStart) {
                if (CurrentPage is ExtenderSessionControl gsession) {
                    gsession.SendScanCodeChordToWmc(GreenStartChord);
                } else {
                    _logger?.LogInfo("[mcx-remote] Green button while idle → starting extender session");
                    try {
                        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                        Activate();
                    } catch { }
                    StartExtenderSession();
                }
                return;
            }
            if (CurrentPage is ExtenderSessionControl session) {
                session.SendRemoteCommandToWmc(cmdId);
            }
        }

        /// <summary>Off-table remote buttons (e.g. Skip/Replay) → forward the
        /// built-in WMC shortcut chord directly. Session-only.</summary>
        private void OnRemoteKeyChord(int[] codes) {
            if (CurrentPage is ExtenderSessionControl session) {
                session.SendScanCodeChordToWmc(codes);
            }
        }

        /// <summary>A client-side (not-forwarded) remote action, e.g. the
        /// rebindable Exit Session command that used to be hard-wired to ESC.</summary>
        private void OnRemoteLocalAction(RemoteLocalAction action) {
            switch (action) {
                case RemoteLocalAction.ExitSession:
                    // Mirror the old ESC-in-session behaviour: cleanly disconnect
                    // and drop back to the landing page. No-op when not in a
                    // session (nothing to leave).
                    if (CurrentPage is ExtenderSessionControl session) {
                        session.Stop();
                        PopToLanding();
                    }
                    break;
            }
        }

        /// <summary>True for keys that are only modifiers — never a learnable
        /// button on their own, and never dispatched as a mapped command.</summary>
        private static bool IsModifierKey(Key k) {
            switch (k) {
                case Key.LeftCtrl:  case Key.RightCtrl:
                case Key.LeftShift: case Key.RightShift:
                case Key.LeftAlt:   case Key.RightAlt:
                case Key.LWin:      case Key.RWin:
                case Key.System:    case Key.None:
                    return true;
                default:
                    return false;
            }
        }

        private void RefreshAspectLockFromConfig(SoftSledConfig cfg) {
            _aspectLockEnabled = cfg.LockWindowAspectRatio;
            if (cfg.SessionWidth > 0 && cfg.SessionHeight > 0) {
                _lockedAspectRatio = (double)cfg.SessionWidth / cfg.SessionHeight;
            }
        }

        /// <summary>
        /// WM_SIZING handler: rewrite the proposed window rect so the CLIENT
        /// area keeps <see cref="_lockedAspectRatio"/>. The proposed rect and
        /// GetWindowRect/GetClientRect are all in physical pixels, so the
        /// non-client overhead (border + caption) cancels cleanly and no DPI
        /// conversion is needed. Returns TRUE and marks handled when we adjust.
        /// </summary>
        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            // MCE remote Raw Input. Leave handled=false so WPF still passes
            // WM_INPUT to DefWindowProc for its required cleanup.
            if (msg == SoftSled.Components.Input.McxRemoteInput.WM_INPUT) {
                _remote?.TryHandleWmInput(msg, lParam);
                return IntPtr.Zero;
            }

            if (msg != WM_SIZING) return IntPtr.Zero;
            if (!_aspectLockEnabled || _isFullScreen || _lockedAspectRatio <= 0) return IntPtr.Zero;

            var rc = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT));
            int edge = wParam.ToInt32();

            // Constant non-client overhead (window rect minus client rect).
            if (!GetWindowRect(hwnd, out RECT wr) || !GetClientRect(hwnd, out RECT cr))
                return IntPtr.Zero;
            int ncW = (wr.right - wr.left) - (cr.right - cr.left);
            int ncH = (wr.bottom - wr.top) - (cr.bottom - cr.top);

            int clientW = (rc.right - rc.left) - ncW;
            int clientH = (rc.bottom - rc.top) - ncH;
            if (clientW < 1) clientW = 1;
            if (clientH < 1) clientH = 1;

            // Horizontal handles drive height from width; vertical handles
            // drive width from height; corners drive height from width.
            bool driveFromWidth = edge != WMSZ_TOP && edge != WMSZ_BOTTOM;
            if (driveFromWidth) clientH = (int)Math.Round(clientW / _lockedAspectRatio);
            else                clientW = (int)Math.Round(clientH * _lockedAspectRatio);

            int newW = clientW + ncW;
            int newH = clientH + ncH;

            // Anchor the edges the user is NOT dragging so the window grows
            // from the grabbed handle rather than jumping.
            switch (edge) {
                case WMSZ_LEFT:        rc.left = rc.right - newW;  rc.bottom = rc.top + newH; break;
                case WMSZ_RIGHT:       rc.right = rc.left + newW;  rc.bottom = rc.top + newH; break;
                case WMSZ_TOP:         rc.top = rc.bottom - newH;  rc.right = rc.left + newW; break;
                case WMSZ_BOTTOM:      rc.bottom = rc.top + newH;  rc.right = rc.left + newW; break;
                case WMSZ_TOPLEFT:     rc.left = rc.right - newW;  rc.top = rc.bottom - newH; break;
                case WMSZ_TOPRIGHT:    rc.right = rc.left + newW;  rc.top = rc.bottom - newH; break;
                case WMSZ_BOTTOMLEFT:  rc.left = rc.right - newW;  rc.bottom = rc.top + newH; break;
                case WMSZ_BOTTOMRIGHT: rc.right = rc.left + newW;  rc.bottom = rc.top + newH; break;
            }

            Marshal.StructureToPtr(rc, lParam, false);
            handled = true;
            return (IntPtr)1; // TRUE — we modified the rect
        }

        /// <summary>
        /// Snap the current windowed size to the locked ratio by adjusting
        /// the height to match the width. No-op in full screen (the window
        /// fills the monitor and isn't resizable) or when the lock is off.
        /// Operates in DIPs using the root layout's measured size to subtract
        /// the window chrome — DPI-independent because the ratio is scale-free.
        /// </summary>
        private void ApplyAspectRatioToCurrentWindow() {
            if (!_aspectLockEnabled || _isFullScreen || _lockedAspectRatio <= 0) return;
            if (!this.IsLoaded || _applyingAspect) return;
            if (RootLayout == null || RootLayout.ActualWidth <= 0 || RootLayout.ActualHeight <= 0) return;

            double chromeW = this.ActualWidth  - RootLayout.ActualWidth;
            double chromeH = this.ActualHeight - RootLayout.ActualHeight;
            double clientW = this.ActualWidth - chromeW;          // == RootLayout.ActualWidth
            double targetClientH = clientW / _lockedAspectRatio;
            double targetWindowH = targetClientH + chromeH;
            if (targetWindowH <= 0 || double.IsNaN(targetWindowH)) return;

            // Only write if it actually differs — avoids a layout churn loop.
            if (Math.Abs(targetWindowH - this.ActualHeight) < 1.0) return;

            _applyingAspect = true;
            try { this.Height = targetWindowH; }
            finally { _applyingAspect = false; }
        }

        // ---- Event teardown -------------------------------------------

        private void DetachPageEvents(UserControl page) {
            // For pages that own background resources, give them a chance
            // to release them on unmount even if Unloaded hasn't fired yet
            // (Unloaded is async on ContentControl swaps).
            if (page is ExtenderSessionControl session) {
                try { session.Stop(); } catch { }
            }
        }
    }

    /// <summary>
    /// Minimal Logger that writes to System.Diagnostics so the shell can
    /// hand a non-null logger to pre-session pages (e.g. PairingPage's
    /// ExtenderDevice) without needing a visible TextBox. The session
    /// page creates its own TextBoxLogger with its own surface.
    /// </summary>
    internal class ShellLogger : Logger {
        protected override void OnLogInfo(string message)
            => System.Diagnostics.Debug.WriteLine("[shell] INFO  " + message);
        protected override void OnLogDebug(string message)
            => System.Diagnostics.Debug.WriteLine("[shell] DEBUG " + message);
        protected override void OnLogError(string message)
            => System.Diagnostics.Debug.WriteLine("[shell] ERROR " + message);
    }
}
