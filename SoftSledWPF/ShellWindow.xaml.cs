using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSledWPF.Components.Shell;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        public ShellWindow() {
            InitializeComponent();
            this.Loaded += ShellWindow_Loaded;
            this.PreviewKeyDown += ShellWindow_PreviewKeyDown;
            this.PreviewKeyUp += ShellWindow_PreviewKeyUp;
        }

        private void ShellWindow_Loaded(object sender, RoutedEventArgs e) {
            // Apply the persisted window mode preference. Doing this in
            // Loaded (after the window is on screen) avoids the flicker
            // you'd get from changing WindowStyle pre-show.
            var cfg = SoftSledConfigManager.ReadConfig();
            ApplyFullScreen(cfg.RunFullScreen);

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
            }
        }

        private void ToggleFullScreenTransient() {
            // F11 toggle — does NOT persist back to config. The setting
            // page is the only place that writes to config.RunFullScreen.
            ApplyFullScreen(!_isFullScreen);
        }

        // ---- Keyboard routing -----------------------------------------

        private void ShellWindow_PreviewKeyDown(object sender, KeyEventArgs e) {
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
                    // Disconnect (cleanly) and let SessionEnded pop us back.
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
            cfgPage.CloseRequested        += (s, _) => PopPage();
            cfgPage.ConfigChanged         += (s, _) => { /* future hook */ };
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
