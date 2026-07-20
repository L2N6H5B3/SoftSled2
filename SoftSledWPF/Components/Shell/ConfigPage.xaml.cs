using SoftSled.Components.Configuration;
using SoftSled.Components.Input;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoftSledWPF.Components.Shell {
    /// <summary>
    /// WMC-styled nested settings page. The root menu lists categories
    /// (General / Pairing / Video / Audio / UI / Remote / Debugging);
    /// selecting one swaps the
    /// visible sub-view. Backspace/ESC pops one level — when already at
    /// the root we raise <see cref="CloseRequested"/> so the shell pops
    /// us back to the landing page.
    ///
    /// Tickboxes read live from <see cref="SoftSledConfig"/> on entry and
    /// persist via <see cref="SoftSledConfigManager.WriteConfig"/> on
    /// every click — there is no separate Save button.
    /// </summary>
    public partial class ConfigPage : UserControl {

        /// <summary>Sub-view currently visible. Used by ESC handler.</summary>
        private enum View { Root, General, Pairing, Video, Audio, Ui, Remote, Debugging }
        private View _currentView = View.Root;
        private SoftSledConfig _config;
        private bool _suppressWrite;

        // Live remote (owned by ShellWindow) so the Remote page can drive
        // "learn" capture + reload the active mappings after an edit. Null when
        // the shell hasn't attached one (e.g. design time).
        private McxRemoteInput _remote;
        // The command whose button we're currently learning; null when idle.
        private string _learningCommandKey;
        // Auto-cancel a pending learn after a few seconds. Needed because while
        // learning we capture EVERY key (so ESC/F12 are bindable), which means
        // there's no keyboard way to back out — the timeout is the remote-only
        // user's escape hatch.
        private System.Windows.Threading.DispatcherTimer _learnTimeout;

        /// <summary>Shell hands us the live remote so the Remote settings page
        /// can capture button presses and refresh mappings.</summary>
        public void AttachRemote(McxRemoteInput remote) => _remote = remote;

        /// <summary>Shell hooks this to know when to swap back to landing.</summary>
        public event EventHandler CloseRequested;

        /// <summary>Raised when the user unpairs — shell may want to update banners.</summary>
        public event EventHandler ConfigChanged;

        /// <summary>Raised when the user toggles the full-screen tickbox so
        /// the shell can re-apply window state without waiting for restart.</summary>
        public event EventHandler<bool> RunFullScreenChanged;

        /// <summary>Raised when the user picks the "Setup" item — the shell
        /// shows the first-run setup wizard over this page.</summary>
        public event EventHandler SetupRequested;

        /// <summary>Re-read config into the UI. Called by the shell after the
        /// setup wizard returns so any changes it made are reflected here.</summary>
        public void RefreshFromConfig() => ReloadConfigIntoUi();

        /// <summary>One row in the resolution picker.</summary>
        private struct Resolution {
            public int Width, Height;
            public string Label;   // Short marketing name ("HD", "WUXGA")
            public string Aspect;  // "4:3", "16:9", "16:10"
            public Resolution(int w, int h, string label, string aspect) {
                Width = w; Height = h; Label = label; Aspect = aspect;
            }
            public string Display =>
                $"{Width} × {Height}    {Aspect}" +
                (string.IsNullOrEmpty(Label) ? "" : "   " + Label);
        }

        // Static catalogue of pickable resolutions. Grouped by aspect so
        // the list reads top-to-bottom from smallest 4:3 to largest 16:10.
        // Keep entries that the average extender host is realistically
        // willing to negotiate — going much above 4K causes some hosts
        // to fall back without a clear error.
        private static readonly Resolution[] _resolutions = {
            // 4:3
            new Resolution(640,  480,  "VGA",        "4:3"),
            new Resolution(800,  600,  "SVGA",       "4:3"),
            new Resolution(1024, 768,  "XGA",        "4:3"),
            new Resolution(1280, 960,  "",           "4:3"),
            new Resolution(1400, 1050, "SXGA+",      "4:3"),
            new Resolution(1600, 1200, "UXGA",       "4:3"),
            // 16:9
            new Resolution(1280, 720,  "HD",         "16:9"),
            new Resolution(1366, 768,  "",           "16:9"),
            new Resolution(1600, 900,  "HD+",        "16:9"),
            new Resolution(1920, 1080, "Full HD",    "16:9"),
            new Resolution(2560, 1440, "QHD",        "16:9"),
            new Resolution(3840, 2160, "4K UHD",     "16:9"),
            // 16:10
            new Resolution(1280, 800,  "",           "16:10"),
            new Resolution(1440, 900,  "",           "16:10"),
            new Resolution(1680, 1050, "WSXGA+",     "16:10"),
            new Resolution(1920, 1200, "WUXGA",      "16:10"),
            new Resolution(2560, 1600, "WQXGA",      "16:10"),
        };

        public ConfigPage() {
            InitializeComponent();
            this.Loaded += ConfigPage_Loaded;
            // WPF CheckBox toggles on Space by default but not Enter. The
            // WMC remote uses Enter as the universal "activate", so we
            // intercept it on the page and route it back through the
            // CheckBox's own click logic (which fires Click -> OnConfigChanged).
            this.PreviewKeyDown += ConfigPage_PreviewKeyDown;
        }

        private void ConfigPage_PreviewKeyDown(object sender, KeyEventArgs e) {
            if (e.Key != Key.Enter) return;
            if (Keyboard.FocusedElement is CheckBox cb) {
                cb.IsChecked = !(cb.IsChecked == true);
                // Manually raise Click so OnConfigChanged runs and persists.
                cb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, cb));
                e.Handled = true;
            } else if (Keyboard.FocusedElement is Button btn && btn.IsEnabled) {
                // The WMC remote's OK key arrives as Enter; WPF only auto-clicks
                // the default button on Enter, so route it to whichever button
                // has focus (Learn / Clear / Reset / etc.).
                btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, btn));
                e.Handled = true;
            }
        }

        private void ConfigPage_Loaded(object sender, RoutedEventArgs e) {
            ReloadConfigIntoUi();
            ShowView(View.Root);
        }

        private void ReloadConfigIntoUi() {
            _config = SoftSledConfigManager.ReadConfig();
            _suppressWrite = true;
            try {
                ChkAutoStart.IsChecked         = _config.AutoStartWmcOnOpen;
                ChkFullScreen.IsChecked        = _config.RunFullScreen;
                ChkCloseOnWmcClose.IsChecked   = _config.CloseOnWmcClose;
                ChkKeepScreenAwake.IsChecked   = _config.KeepScreenAwake;
                ChkMediaMode.IsChecked         = _config.MediaPlaybackModeEnabled;
                ChkRemoteRendering.IsChecked   = _config.EnableRemoteRendering;
                Chk2DAnimations.IsChecked      = _config.Enable2DAnimations;
                ChkIntenseAnimations.IsChecked = _config.EnableIntenseAnimations;
                ChkOverscan.IsChecked          = _config.EnableOverscanMargin;
                ChkHdContent.IsChecked         = _config.EnableHdContent;
                ChkUiSounds.IsChecked          = _config.EnableUiSounds;
                ChkPopups.IsChecked            = _config.EnablePopups;
                ChkToolbar.IsChecked           = _config.EnableToolbar;
                ChkMouseInput.IsChecked        = _config.EnableMouseInput;
                ChkLogDevCaps.IsChecked        = _config.LogDevCapsChannel;
                ChkLogMcxSess.IsChecked        = _config.LogMcxSessChannel;
                ChkLogAvCtrl.IsChecked         = _config.LogAvCtrlChannel;
                ChkLogRdpFastpath.IsChecked    = _config.LogRdpFastpath;
                ChkLogRdpFps.IsChecked         = _config.LogRdpFps;
                ChkLogAvPlayback.IsChecked     = _config.LogAvPlayback;
                ChkLogToFile.IsChecked         = _config.LogToFile;
                DiagFolderPath.Text            = SoftSled.Components.Configuration
                                                          .DiagnosticsPaths.RootFrom(
                                                              _config.DiagnosticsDirectory);

                // Advanced env-var-driven toggles (Debugging sub-view).
                ChkDumpSplashRaw.IsChecked     = _config.EnableSplashRawDump;
                ChkDumpFastpathRaw.IsChecked   = _config.EnableFastpathRawDump;
                ChkDumpRtspWire.IsChecked      = _config.EnableRtspWireDump;
                ChkAudioTrace.IsChecked        = _config.EnableAudioTrace;
                ChkAlwaysShowRdp.IsChecked     = _config.AlwaysShowRdp;
                RefreshResolutionButton();
                RefreshAudioSyncDisplay();
                RefreshJitterBufferDisplay();
                UpdateAnimationDependencies();

                PairingStatusText.Text = _config.IsPaired
                    ? $"Paired with {_config.RdpLoginHost} (user {_config.RdpLoginUserName})"
                    : "Not paired — choose Start Extender from the main menu to pair.";
                UnpairButton.IsEnabled = _config.IsPaired;
            } finally {
                _suppressWrite = false;
            }
        }

        // ---- Navigation between sub-views -----------------------------

        private void ShowView(View view) {
            _currentView = view;
            RootView.Visibility      = view == View.Root      ? Visibility.Visible : Visibility.Collapsed;
            GeneralView.Visibility   = view == View.General   ? Visibility.Visible : Visibility.Collapsed;
            PairingView.Visibility   = view == View.Pairing   ? Visibility.Visible : Visibility.Collapsed;
            VideoView.Visibility     = view == View.Video     ? Visibility.Visible : Visibility.Collapsed;
            AudioView.Visibility     = view == View.Audio     ? Visibility.Visible : Visibility.Collapsed;
            UiView.Visibility        = view == View.Ui        ? Visibility.Visible : Visibility.Collapsed;
            RemoteView.Visibility    = view == View.Remote    ? Visibility.Visible : Visibility.Collapsed;
            DebuggingView.Visibility = view == View.Debugging ? Visibility.Visible : Visibility.Collapsed;

            BreadcrumbText.Text = view == View.Root ? "" : view.ToString().ToLowerInvariant();
            HeaderText.Text     = view == View.Root ? "settings" : "settings";

            // Re-focus appropriately so the remote keeps working without a click.
            switch (view) {
                case View.Root:
                    RootMenu.Focus();
                    if (RootMenu.SelectedItem is ListBoxItem rlbi) rlbi.Focus();
                    break;
                case View.General:
                    ChkAutoStart.Focus();
                    break;
                case View.Pairing:
                    UnpairButton.Focus();
                    break;
                case View.Video:
                    ChkRemoteRendering.Focus();
                    break;
                case View.Audio:
                    BtnAudioSyncReset.Focus();
                    break;
                case View.Ui:
                    ChkUiSounds.Focus();
                    break;
                case View.Remote:
                    BtnRemoteReset.Focus();
                    break;
                case View.Debugging:
                    ChkLogDevCaps.Focus();
                    break;
            }
        }

        private void RootMenu_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter || e.Key == Key.Space) {
                ActivateRoot(RootMenu.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        }

        private void RootMenu_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            // Single-click activation: walk back up from the hit-tested
            // element to the owning ListBoxItem so clicks on empty list
            // space are ignored.
            var item = ItemsControl.ContainerFromElement(
                RootMenu, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item != null) ActivateRoot(item);
        }

        private void ActivateRoot(ListBoxItem item) {
            if (item == ItemSetup)           SetupRequested?.Invoke(this, EventArgs.Empty);
            else if (item == ItemGeneral)    ShowView(View.General);
            else if (item == ItemPairing)    ShowView(View.Pairing);
            else if (item == ItemVideo)      ShowView(View.Video);
            else if (item == ItemAudio)      ShowView(View.Audio);
            else if (item == ItemUi)         ShowView(View.Ui);
            else if (item == ItemRemote)   { BuildRemoteRows(); ShowView(View.Remote); }
            else if (item == ItemDebugging)  ShowView(View.Debugging);
        }

        // ---- Live tickbox persistence ---------------------------------

        private void OnConfigChanged(object sender, RoutedEventArgs e) {
            if (_suppressWrite || _config == null) return;

            bool prevFullScreen = _config.RunFullScreen;

            _config.AutoStartWmcOnOpen      = ChkAutoStart.IsChecked == true;
            _config.RunFullScreen           = ChkFullScreen.IsChecked == true;
            _config.CloseOnWmcClose         = ChkCloseOnWmcClose.IsChecked == true;
            _config.KeepScreenAwake         = ChkKeepScreenAwake.IsChecked == true;
            _config.MediaPlaybackModeEnabled = ChkMediaMode.IsChecked == true;
            _config.EnableRemoteRendering   = ChkRemoteRendering.IsChecked == true;
            _config.Enable2DAnimations      = Chk2DAnimations.IsChecked == true;
            _config.EnableIntenseAnimations = ChkIntenseAnimations.IsChecked == true;
            _config.EnableOverscanMargin    = ChkOverscan.IsChecked == true;
            _config.EnableHdContent         = ChkHdContent.IsChecked == true;
            _config.EnableUiSounds          = ChkUiSounds.IsChecked == true;
            _config.EnablePopups            = ChkPopups.IsChecked == true;
            _config.EnableToolbar           = ChkToolbar.IsChecked == true;
            _config.EnableMouseInput        = ChkMouseInput.IsChecked == true;
            _config.LogDevCapsChannel       = ChkLogDevCaps.IsChecked == true;
            _config.LogMcxSessChannel       = ChkLogMcxSess.IsChecked == true;
            _config.LogAvCtrlChannel        = ChkLogAvCtrl.IsChecked == true;
            _config.LogRdpFastpath          = ChkLogRdpFastpath.IsChecked == true;
            _config.LogRdpFps               = ChkLogRdpFps.IsChecked == true;
            _config.LogAvPlayback           = ChkLogAvPlayback.IsChecked == true;
            _config.LogToFile               = ChkLogToFile.IsChecked == true;
            _config.EnableSplashRawDump     = ChkDumpSplashRaw.IsChecked == true;
            _config.EnableFastpathRawDump   = ChkDumpFastpathRaw.IsChecked == true;
            _config.EnableRtspWireDump      = ChkDumpRtspWire.IsChecked == true;
            _config.EnableAudioTrace        = ChkAudioTrace.IsChecked == true;
            _config.AlwaysShowRdp           = ChkAlwaysShowRdp.IsChecked == true;

            try {
                SoftSledConfigManager.WriteConfig(_config);
            } catch (Exception ex) {
                MessageBox.Show("Failed to save settings: " + ex.Message);
                return;
            }

            ConfigChanged?.Invoke(this, EventArgs.Empty);
            if (prevFullScreen != _config.RunFullScreen) {
                RunFullScreenChanged?.Invoke(this, _config.RunFullScreen);
            }

            // Apply the keep-awake toggle live so it takes effect without a
            // restart. We're on the UI thread here, which is the long-lived
            // thread DisplayKeepAwake's request must be scoped to.
            if (_config.KeepScreenAwake) {
                SoftSled.Components.Utility.DisplayKeepAwake.Acquire();
            } else {
                SoftSled.Components.Utility.DisplayKeepAwake.Release();
            }

            // Remote rendering takes ownership of the animation pipeline
            // on the host side, so the local 2D / Intense tickboxes have
            // no effect while it's on. Re-evaluate after any change.
            UpdateAnimationDependencies();
        }

        /// <summary>
        /// Disable the 2D / Intense animation tickboxes whenever Remote
        /// Rendering is enabled — they only affect the local rendering
        /// path. Their persisted values are left untouched so flipping
        /// Remote Rendering back off restores the previous state.
        /// </summary>
        private void UpdateAnimationDependencies() {
            bool remote = ChkRemoteRendering.IsChecked == true;
            Chk2DAnimations.IsEnabled      = !remote;
            ChkIntenseAnimations.IsEnabled = !remote;
        }

        // ---- Unpair flow ----------------------------------------------

        private void UnpairButton_Click(object sender, RoutedEventArgs e) {
            ConfirmOverlay.Visibility = Visibility.Visible;
            ConfirmNo.Focus();
        }

        private void ConfirmYes_Click(object sender, RoutedEventArgs e) {
            try {
                _config.IsPaired = false;
                _config.DeviceUDN = "";
                _config.RdpLoginHost = "";
                _config.RdpLoginUserName = "";
                _config.RdpLoginPassword = "";
                SoftSledConfigManager.WriteConfig(_config);
                ConfigChanged?.Invoke(this, EventArgs.Empty);
            } catch (Exception ex) {
                MessageBox.Show("Failed to unpair: " + ex.Message);
            } finally {
                ConfirmOverlay.Visibility = Visibility.Collapsed;
                ReloadConfigIntoUi();
                UnpairButton.Focus();
            }
        }

        private void ConfirmNo_Click(object sender, RoutedEventArgs e) {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            UnpairButton.Focus();
        }

        // ---- Resolution picker ----------------------------------------

        /// <summary>
        /// Update the picker button label to reflect whatever is currently
        /// in <see cref="_config"/>. Called on reload and after a pick.
        /// </summary>
        private void RefreshResolutionButton() {
            if (_config == null) { BtnResolution.Content = "—"; return; }
            BtnResolution.Content =
                $"{_config.SessionWidth} × {_config.SessionHeight}";
        }

        // ---- Audio sync offset adjuster -------------------------------

        /// <summary>
        /// Maximum allowed manual offset (per direction). 250 ms is
        /// the upper end of what's plausible for HDMI / AVR latency;
        /// anything beyond that is a pipeline problem, not an
        /// offsettable display lag.
        /// </summary>
        // Widened from 250 → 500: the live in-session nudge (Ctrl+]/[) writes
        // its dialled-in trim back here, and the residual pipeline lag can sit
        // a little above the old ±250 "AVR latency" bound on some setups.
        private const int AudioSyncOffsetClampMs = 500;

        private void RefreshAudioSyncDisplay() {
            if (_config == null) { AudioSyncValueText.Text = "0 ms"; return; }
            int ms = _config.AudioSyncOffsetMs;
            AudioSyncValueText.Text = ms > 0
                ? $"+{ms} ms"
                : (ms == 0 ? "0 ms" : $"{ms} ms");
        }

        /// <summary>
        /// Apply a delta to the audio-sync offset, clamp, persist,
        /// and refresh the display. Shared by the ±10/±50/reset
        /// button handlers — reset passes the negated current value
        /// to force back to zero.
        /// </summary>
        private void AdjustAudioSyncOffset(int deltaMs) {
            if (_suppressWrite || _config == null) return;
            int next = _config.AudioSyncOffsetMs + deltaMs;
            if (next < -AudioSyncOffsetClampMs) next = -AudioSyncOffsetClampMs;
            if (next >  AudioSyncOffsetClampMs) next =  AudioSyncOffsetClampMs;
            if (next == _config.AudioSyncOffsetMs) return;
            _config.AudioSyncOffsetMs = next;
            try { SoftSledConfigManager.WriteConfig(_config); }
            catch (Exception ex) {
                MessageBox.Show("Failed to save audio sync offset: " + ex.Message);
                return;
            }
            RefreshAudioSyncDisplay();
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BtnAudioSyncMinusBig_Click(object sender, RoutedEventArgs e) => AdjustAudioSyncOffset(-50);
        private void BtnAudioSyncMinus_Click   (object sender, RoutedEventArgs e) => AdjustAudioSyncOffset(-10);
        private void BtnAudioSyncPlus_Click    (object sender, RoutedEventArgs e) => AdjustAudioSyncOffset(+10);
        private void BtnAudioSyncPlusBig_Click (object sender, RoutedEventArgs e) => AdjustAudioSyncOffset(+50);

        private void BtnAudioSyncReset_Click(object sender, RoutedEventArgs e) {
            if (_config == null) return;
            AdjustAudioSyncOffset(-_config.AudioSyncOffsetMs);
        }

        // ----- Video jitter buffer (libav + D3DImage player) -----

        private const int JitterBufferDefaultMs = 250;
        private const int JitterBufferMaxMs = 4000;
        private const int JitterBufferStepMs = 250;

        private void RefreshJitterBufferDisplay() {
            int ms = _config?.VideoJitterBufferMs ?? JitterBufferDefaultMs;
            JitterBufferValueText.Text = $"{ms} ms";
        }

        private void AdjustJitterBuffer(int deltaMs) {
            if (_suppressWrite || _config == null) return;
            int next = _config.VideoJitterBufferMs + deltaMs;
            if (next < 0) next = 0;
            if (next > JitterBufferMaxMs) next = JitterBufferMaxMs;
            if (next == _config.VideoJitterBufferMs) return;
            _config.VideoJitterBufferMs = next;
            try { SoftSledConfigManager.WriteConfig(_config); }
            catch (Exception ex) {
                MessageBox.Show("Failed to save video jitter buffer: " + ex.Message);
                return;
            }
            RefreshJitterBufferDisplay();
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BtnJitterMinus_Click(object sender, RoutedEventArgs e) => AdjustJitterBuffer(-JitterBufferStepMs);
        private void BtnJitterPlus_Click (object sender, RoutedEventArgs e) => AdjustJitterBuffer(+JitterBufferStepMs);

        private void BtnJitterReset_Click(object sender, RoutedEventArgs e) {
            if (_config == null) return;
            AdjustJitterBuffer(JitterBufferDefaultMs - _config.VideoJitterBufferMs);
        }

        /// <summary>
        /// Open the diagnostics root — the session logs (Logs\) and the raw
        /// dumps (Dumps\) both live under it. Falls back to launching the
        /// parent if the leaf doesn't exist yet: it's created lazily by
        /// whichever sink writes first.
        /// </summary>
        private void OnOpenDiagFolderClick(object sender, RoutedEventArgs e) {
            OpenFolderOrExplain("diagnostics",
                SoftSled.Components.Configuration.DiagnosticsPaths
                    .RootFrom(_config?.DiagnosticsDirectory));
        }

        /// <summary>
        /// "Change diagnostics folder" — opens a folder picker, stores the
        /// chosen root in <see cref="SoftSledConfig.DiagnosticsDirectory"/>,
        /// and refreshes the displayed path. Logs move on the next app launch
        /// (the AppLog is opened at App.OnStartup, so mid-session changes
        /// don't move the open file); dumps move on the next session start
        /// (the dump dirs are pushed into env vars then).
        /// </summary>
        private void OnChangeDiagFolderClick(object sender, RoutedEventArgs e) {
            if (_config == null) return;
            string chosen = PickFolder("Choose folder for SoftSled diagnostic output",
                                       _config.DiagnosticsDirectory);
            if (chosen == null) return;
            _config.DiagnosticsDirectory = chosen;
            try { SoftSledConfigManager.WriteConfig(_config); }
            catch (Exception ex) { MessageBox.Show("Couldn't save config: " + ex.Message); return; }
            DiagFolderPath.Text = chosen;
        }

        /// <summary>
        /// Show a WinForms FolderBrowserDialog with the given title /
        /// initial selection. Returns the chosen path, or null when the
        /// user cancels. Uses WinForms because WPF on .NET Framework
        /// 4.6.1 has no built-in folder picker — we already reference
        /// System.Windows.Forms for related shell work, so no new dep.
        /// </summary>
        private static string PickFolder(string description, string initialPath) {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog()) {
                dlg.Description = description;
                dlg.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(initialPath)
                    && System.IO.Directory.Exists(initialPath)) {
                    dlg.SelectedPath = initialPath;
                }
                var result = dlg.ShowDialog();
                if (result != System.Windows.Forms.DialogResult.OK) return null;
                return dlg.SelectedPath;
            }
        }

        /// <summary>Shared "open in explorer" plumbing for the Debugging-page folder buttons.</summary>
        private static void OpenFolderOrExplain(string kind, string dir) {
            if (string.IsNullOrEmpty(dir)) {
                MessageBox.Show($"Couldn't resolve the {kind} folder path. " +
                                "Check the config Debugging section.");
                return;
            }
            try {
                if (!System.IO.Directory.Exists(dir)) {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName        = dir,
                    UseShellExecute = true,
                });
            } catch (Exception ex) {
                MessageBox.Show($"Couldn't open the {kind} folder:\n{dir}\n\n{ex.Message}");
            }
        }

        private void BtnResolution_Click(object sender, RoutedEventArgs e) {
            // Populate fresh each time so future code-driven changes to
            // the _resolutions catalogue surface without an app restart.
            ResolutionList.Items.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < _resolutions.Length; i++) {
                var r = _resolutions[i];
                var item = new ListBoxItem { Content = r.Display, Tag = r };
                ResolutionList.Items.Add(item);
                if (r.Width == _config.SessionWidth &&
                    r.Height == _config.SessionHeight) {
                    selectedIndex = i;
                }
            }
            // If the persisted resolution isn't in the catalogue (e.g.
            // user hand-edited the config), still let them open the list
            // and pick a known one — just don't pre-select anything.
            if (selectedIndex >= 0) {
                ResolutionList.SelectedIndex = selectedIndex;
                ResolutionList.ScrollIntoView(ResolutionList.SelectedItem);
            }

            ResolutionOverlay.Visibility = Visibility.Visible;

            // Defer focus so the items have a chance to materialise their
            // containers — focusing too early picks up the ListBox itself
            // and arrow keys don't move selection until the user clicks.
            Dispatcher.BeginInvoke(new Action(() => {
                ResolutionList.Focus();
                if (ResolutionList.SelectedItem is ListBoxItem lbi) lbi.Focus();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ResolutionList_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter || e.Key == Key.Space) {
                CommitResolution(ResolutionList.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        }

        private void ResolutionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            var item = ItemsControl.ContainerFromElement(
                ResolutionList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item != null) CommitResolution(item);
        }

        private void ResolutionCancel_Click(object sender, RoutedEventArgs e) {
            CloseResolutionOverlay();
        }

        private void CommitResolution(ListBoxItem item) {
            if (item == null || _config == null) return;
            if (!(item.Tag is Resolution r)) return;

            _config.SessionWidth  = r.Width;
            _config.SessionHeight = r.Height;
            try {
                SoftSledConfigManager.WriteConfig(_config);
            } catch (Exception ex) {
                MessageBox.Show("Failed to save resolution: " + ex.Message);
                return;
            }
            ConfigChanged?.Invoke(this, EventArgs.Empty);
            RefreshResolutionButton();
            CloseResolutionOverlay();
        }

        private void CloseResolutionOverlay() {
            ResolutionOverlay.Visibility = Visibility.Collapsed;
            BtnResolution.Focus();
        }

        // ---- Remote button mapping ------------------------------------

        /// <summary>
        /// (Re)build the per-command rows from the remote command catalogue.
        /// Each row shows the command, the button currently bound to it, and
        /// Learn / Clear actions. Called on entering the view and after any
        /// change so the displayed bindings stay current.
        /// </summary>
        // Learn button per command key, so a rebuild can restore focus to the
        // row the user just acted on instead of snapping back to the top.
        private readonly System.Collections.Generic.Dictionary<string, Button> _learnButtonsByKey
            = new System.Collections.Generic.Dictionary<string, Button>();

        private void BuildRemoteRows(string focusCommandKey = null) {
            if (_config == null) _config = SoftSledConfigManager.ReadConfig();
            RemoteList.Children.Clear();
            _learnButtonsByKey.Clear();

            var bodyStyle   = (Style)TryFindResource("WmcBodyStyle");
            var buttonStyle = (Style)TryFindResource("WmcButtonStyle");
            var subtleBrush = TryFindResource("WmcSubtleTextBrush") as System.Windows.Media.Brush;

            foreach (var def in RemoteCommandCatalog.Defs) {
                int usage = RemoteCommandCatalog.GetEffectiveUsage(_config, def.Key);
                bool isDefault = RemoteCommandCatalog.IsDefault(_config, def.Key);

                var row = new Grid { Margin = new Thickness(10, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock {
                    Style = bodyStyle,
                    Text = def.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                var bound = new TextBlock {
                    Style = bodyStyle,
                    Foreground = subtleBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = RemoteCommandCatalog.DescribeUsage(usage)
                           + (usage < 0 ? "" : isDefault ? "   (default)" : "   (custom)"),
                };
                Grid.SetColumn(bound, 1);
                row.Children.Add(bound);

                var learn = new Button {
                    Style = buttonStyle,
                    Content = "Learn",
                    Tag = def.Key,
                    MinWidth = 120,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                learn.Click += OnRemoteLearnClick;
                Grid.SetColumn(learn, 2);
                row.Children.Add(learn);
                _learnButtonsByKey[def.Key] = learn;

                var clear = new Button {
                    Style = buttonStyle,
                    Content = "Clear",
                    Tag = def.Key,
                    MinWidth = 100,
                    Margin = new Thickness(8, 0, 0, 0),
                    // Nothing to clear when already unassigned.
                    IsEnabled = usage >= 0,
                };
                clear.Click += OnRemoteClearClick;
                Grid.SetColumn(clear, 3);
                row.Children.Add(clear);

                var reset = new Button {
                    Style = buttonStyle,
                    Content = "Default",
                    Tag = def.Key,
                    MinWidth = 110,
                    Margin = new Thickness(8, 0, 0, 0),
                    // Already at its shipped default — nothing to reset.
                    IsEnabled = !isDefault,
                };
                reset.Click += OnRemoteDefaultClick;
                Grid.SetColumn(reset, 4);
                row.Children.Add(reset);

                RemoteList.Children.Add(row);
            }

            // Keep focus on the row the user just acted on (rather than snapping
            // back to the top of the list). Deferred so the freshly-added
            // containers have completed layout before we focus / scroll.
            if (focusCommandKey != null
                && _learnButtonsByKey.TryGetValue(focusCommandKey, out Button focusBtn)) {
                Dispatcher.BeginInvoke(new Action(() => {
                    focusBtn.BringIntoView();
                    focusBtn.Focus();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void OnRemoteLearnClick(object sender, RoutedEventArgs e) {
            if (!(sender is Button b) || !(b.Tag is string key)) return;
            var def = RemoteCommandCatalog.FindByKey(key);
            if (def == null) return;

            if (_remote == null) {
                MessageBox.Show("The remote isn't available right now, so a button can't be captured.");
                return;
            }

            _learningCommandKey = key;
            RemoteLearnPrompt.Text =
                $"Press the button (or key, e.g. Esc) you want to use for “{def.Label}”.\n" +
                "Cancels automatically if nothing is pressed.";
            RemoteLearnOverlay.Visibility = Visibility.Visible;
            RemoteLearnCancel.Focus();

            // Capture the next button press. The callback runs on the WndProc/UI
            // thread, so it's safe to touch the UI directly.
            _remote.BeginLearn(OnRemoteButtonLearned);

            if (_learnTimeout == null) {
                _learnTimeout = new System.Windows.Threading.DispatcherTimer {
                    Interval = TimeSpan.FromSeconds(8),
                };
                _learnTimeout.Tick += (s, ev) => CancelLearn();
            }
            _learnTimeout.Stop();
            _learnTimeout.Start();
        }

        private void OnRemoteButtonLearned(int usageKey) {
            // Marshal to the UI thread just in case a future caller changes the
            // dispatch thread; today it's already the UI thread.
            if (!Dispatcher.CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => OnRemoteButtonLearned(usageKey)));
                return;
            }
            _learnTimeout?.Stop();
            string key = _learningCommandKey;
            _learningCommandKey = null;
            RemoteLearnOverlay.Visibility = Visibility.Collapsed;
            if (key == null || _config == null) return;

            RemoteCommandCatalog.SetBinding(_config, key, usageKey);
            if (SaveRemoteConfig()) _remote?.ReloadBindings();
            BuildRemoteRows(focusCommandKey: key);
        }

        private void OnRemoteClearClick(object sender, RoutedEventArgs e) {
            if (!(sender is Button b) || !(b.Tag is string key) || _config == null) return;
            RemoteCommandCatalog.ClearBinding(_config, key);
            if (SaveRemoteConfig()) _remote?.ReloadBindings();
            BuildRemoteRows(focusCommandKey: key);
        }

        private void OnRemoteDefaultClick(object sender, RoutedEventArgs e) {
            if (!(sender is Button b) || !(b.Tag is string key) || _config == null) return;
            RemoteCommandCatalog.ResetBinding(_config, key);
            if (SaveRemoteConfig()) _remote?.ReloadBindings();
            BuildRemoteRows(focusCommandKey: key);
        }

        private void BtnRemoteReset_Click(object sender, RoutedEventArgs e) {
            if (_config == null) return;
            RemoteCommandCatalog.ResetToDefaults(_config);
            if (SaveRemoteConfig()) _remote?.ReloadBindings();
            BuildRemoteRows();
            BtnRemoteReset.Focus();
        }

        private void RemoteLearnCancel_Click(object sender, RoutedEventArgs e) => CancelLearn();

        private void CancelLearn() {
            _learnTimeout?.Stop();
            _learningCommandKey = null;
            _remote?.CancelLearn();
            RemoteLearnOverlay.Visibility = Visibility.Collapsed;
            BtnRemoteReset.Focus();
        }

        /// <summary>Persist config after a remote-mapping edit. Returns false
        /// (and shows a message) if the write failed.</summary>
        private bool SaveRemoteConfig() {
            try {
                SoftSledConfigManager.WriteConfig(_config);
                ConfigChanged?.Invoke(this, EventArgs.Empty);
                return true;
            } catch (Exception ex) {
                MessageBox.Show("Failed to save remote mappings: " + ex.Message);
                return false;
            }
        }

        // ---- Back navigation routed from the shell --------------------

        /// <summary>
        /// Called by ShellWindow when ESC or Backspace is pressed and not
        /// otherwise handled. Returns true if we consumed the back (popped
        /// a sub-view); false if the shell should pop us off the stack.
        /// </summary>
        public bool HandleBack() {
            // Modal overlays always win (close them before falling back
            // to the sub-view / root navigation).
            if (ResolutionOverlay.Visibility == Visibility.Visible) {
                CloseResolutionOverlay();
                return true;
            }
            if (ConfirmOverlay.Visibility == Visibility.Visible) {
                ConfirmOverlay.Visibility = Visibility.Collapsed;
                UnpairButton.Focus();
                return true;
            }
            if (RemoteLearnOverlay.Visibility == Visibility.Visible) {
                CancelLearn();
                return true;
            }
            if (_currentView != View.Root) {
                ShowView(View.Root);
                return true;
            }
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return true; // shell still hears CloseRequested and pops
        }
    }
}
