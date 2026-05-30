using SoftSled.Components.Configuration;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoftSledWPF.Components.Shell {
    /// <summary>
    /// WMC-styled nested settings page. The root menu lists categories
    /// (General / Extender / Video / About); selecting one swaps the
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
        private enum View { Root, General, Pairing, Video, Audio, Ui, Debugging, About }
        private View _currentView = View.Root;
        private SoftSledConfig _config;
        private bool _suppressWrite;

        /// <summary>Shell hooks this to know when to swap back to landing.</summary>
        public event EventHandler CloseRequested;

        /// <summary>Raised when the user unpairs — shell may want to update banners.</summary>
        public event EventHandler ConfigChanged;

        /// <summary>Raised when the user toggles the full-screen tickbox so
        /// the shell can re-apply window state without waiting for restart.</summary>
        public event EventHandler<bool> RunFullScreenChanged;

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
                ChkRemoteRendering.IsChecked   = _config.EnableRemoteRendering;
                Chk2DAnimations.IsChecked      = _config.Enable2DAnimations;
                ChkIntenseAnimations.IsChecked = _config.EnableIntenseAnimations;
                ChkOverscan.IsChecked          = _config.EnableOverscanMargin;
                ChkExternalSync.IsChecked      = _config.UseExternalSyncMode;
                ChkHdContent.IsChecked         = _config.EnableHdContent;
                ChkUiSounds.IsChecked          = _config.EnableUiSounds;
                ChkPopups.IsChecked            = _config.EnablePopups;
                ChkToolbar.IsChecked           = _config.EnableToolbar;
                ChkMouseInput.IsChecked        = _config.EnableMouseInput;
                ChkLogger.IsChecked            = _config.EnableLogger;
                ChkLogDevCaps.IsChecked        = _config.LogDevCapsChannel;
                ChkLogMcxSess.IsChecked        = _config.LogMcxSessChannel;
                ChkLogAvCtrl.IsChecked         = _config.LogAvCtrlChannel;
                ChkLogRdpFastpath.IsChecked    = _config.LogRdpFastpath;
                ChkLogAvPlayback.IsChecked     = _config.LogAvPlayback;
                ChkEnableSplashPipRouting.IsChecked = _config.EnableSplashPipRouting;
                ChkLogToFile.IsChecked         = _config.LogToFile;
                LogFolderPath.Text             = SoftSledWPF.Components.Shell
                                                          .ExtenderSessionControl
                                                          .GetLogDirectoryForConfig()
                                                  ?? "(default: %LocalAppData%/SoftSled/Logs)";

                // Advanced env-var-driven toggles (Debugging sub-view).
                ChkDumpSplashRaw.IsChecked     = _config.EnableSplashRawDump;
                ChkDumpFastpathRaw.IsChecked   = _config.EnableFastpathRawDump;
                ChkDumpAudio.IsChecked         = _config.EnableAudioDump;
                ChkDumpRtspWire.IsChecked      = _config.EnableRtspWireDump;
                ChkAudioTrace.IsChecked        = _config.EnableAudioTrace;
                ChkAudioViaNAudio.IsChecked    = _config.EnableAudioViaNAudio;
                DumpFolderPath.Text            = SoftSledWPF.Components.Shell
                                                          .ExtenderSessionControl
                                                          .GetDumpsRootDirectory()
                                                  ?? "(default: %LocalAppData%/SoftSled/Dumps)";
                RefreshResolutionButton();
                RefreshAudioSyncDisplay();
                UpdateAnimationDependencies();

                PairingStatusText.Text = _config.IsPaired
                    ? $"Paired with {_config.RdpLoginHost} (user {_config.RdpLoginUserName})"
                    : "Not paired — choose Start Extender from the main menu to pair.";
                UnpairButton.IsEnabled = _config.IsPaired;

                AboutVersionText.Text = "Version " +
                    Assembly.GetExecutingAssembly().GetName().Version;
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
            DebuggingView.Visibility = view == View.Debugging ? Visibility.Visible : Visibility.Collapsed;
            AboutView.Visibility     = view == View.About     ? Visibility.Visible : Visibility.Collapsed;

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
                case View.Debugging:
                    ChkLogger.Focus();
                    break;
                case View.About:
                    // Nothing focusable — focus the page itself so back keys
                    // still route here.
                    this.Focus();
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
            if (item == ItemGeneral)         ShowView(View.General);
            else if (item == ItemPairing)    ShowView(View.Pairing);
            else if (item == ItemVideo)      ShowView(View.Video);
            else if (item == ItemAudio)      ShowView(View.Audio);
            else if (item == ItemUi)         ShowView(View.Ui);
            else if (item == ItemDebugging)  ShowView(View.Debugging);
            else if (item == ItemAbout)      ShowView(View.About);
        }

        // ---- Live tickbox persistence ---------------------------------

        private void OnConfigChanged(object sender, RoutedEventArgs e) {
            if (_suppressWrite || _config == null) return;

            bool prevFullScreen = _config.RunFullScreen;

            _config.AutoStartWmcOnOpen      = ChkAutoStart.IsChecked == true;
            _config.RunFullScreen           = ChkFullScreen.IsChecked == true;
            _config.CloseOnWmcClose         = ChkCloseOnWmcClose.IsChecked == true;
            _config.EnableRemoteRendering   = ChkRemoteRendering.IsChecked == true;
            _config.Enable2DAnimations      = Chk2DAnimations.IsChecked == true;
            _config.EnableIntenseAnimations = ChkIntenseAnimations.IsChecked == true;
            _config.EnableOverscanMargin    = ChkOverscan.IsChecked == true;
            _config.UseExternalSyncMode     = ChkExternalSync.IsChecked == true;
            _config.EnableHdContent         = ChkHdContent.IsChecked == true;
            _config.EnableUiSounds          = ChkUiSounds.IsChecked == true;
            _config.EnablePopups            = ChkPopups.IsChecked == true;
            _config.EnableToolbar           = ChkToolbar.IsChecked == true;
            _config.EnableMouseInput        = ChkMouseInput.IsChecked == true;
            _config.EnableLogger            = ChkLogger.IsChecked == true;
            _config.LogDevCapsChannel       = ChkLogDevCaps.IsChecked == true;
            _config.LogMcxSessChannel       = ChkLogMcxSess.IsChecked == true;
            _config.LogAvCtrlChannel        = ChkLogAvCtrl.IsChecked == true;
            _config.LogRdpFastpath          = ChkLogRdpFastpath.IsChecked == true;
            _config.LogAvPlayback           = ChkLogAvPlayback.IsChecked == true;
            _config.EnableSplashPipRouting  = ChkEnableSplashPipRouting.IsChecked == true;
            _config.LogToFile               = ChkLogToFile.IsChecked == true;
            _config.EnableSplashRawDump     = ChkDumpSplashRaw.IsChecked == true;
            _config.EnableFastpathRawDump   = ChkDumpFastpathRaw.IsChecked == true;
            _config.EnableAudioDump         = ChkDumpAudio.IsChecked == true;
            _config.EnableRtspWireDump      = ChkDumpRtspWire.IsChecked == true;
            _config.EnableAudioTrace        = ChkAudioTrace.IsChecked == true;
            _config.EnableAudioViaNAudio    = ChkAudioViaNAudio.IsChecked == true;

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
        private const int AudioSyncOffsetClampMs = 250;

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

        /// <summary>
        /// Open the directory where session log files are written. Falls
        /// back to launching the parent if the leaf doesn't exist yet
        /// (it's created lazily when a session opens its first file).
        /// </summary>
        private void OnOpenLogFolderClick(object sender, RoutedEventArgs e) {
            OpenFolderOrExplain("log",
                SoftSledWPF.Components.Shell.ExtenderSessionControl.GetLogDirectoryForConfig());
        }

        /// <summary>
        /// Open the root dump directory (siblings: splash/, fastpath/,
        /// audio/). Auto-creates the directory if it doesn't exist yet —
        /// individual sub-dirs are created lazily by the dumper that
        /// owns them.
        /// </summary>
        private void OnOpenDumpFolderClick(object sender, RoutedEventArgs e) {
            OpenFolderOrExplain("dump",
                SoftSledWPF.Components.Shell.ExtenderSessionControl.GetDumpsRootDirectory());
        }

        /// <summary>
        /// "Change log folder" — opens a folder picker, stores the
        /// chosen path in <see cref="SoftSledConfig.LogFileDirectory"/>,
        /// and refreshes the displayed path. Takes effect on next app
        /// launch (the AppLog is opened at App.OnStartup; mid-session
        /// changes don't move the open file).
        /// </summary>
        private void OnChangeLogFolderClick(object sender, RoutedEventArgs e) {
            if (_config == null) return;
            string chosen = PickFolder("Choose folder for SoftSled log files", _config.LogFileDirectory);
            if (chosen == null) return;
            _config.LogFileDirectory = chosen;
            try { SoftSledConfigManager.WriteConfig(_config); }
            catch (Exception ex) { MessageBox.Show("Couldn't save config: " + ex.Message); return; }
            LogFolderPath.Text = chosen;
        }

        /// <summary>
        /// "Change dumps folder" — same UX as above but stores into
        /// <see cref="SoftSledConfig.DumpsDirectory"/>. Takes effect on
        /// next session start (dump dirs are pushed into env vars at
        /// session-start time, not app-start).
        /// </summary>
        private void OnChangeDumpFolderClick(object sender, RoutedEventArgs e) {
            if (_config == null) return;
            string chosen = PickFolder("Choose folder for SoftSled dump output", _config.DumpsDirectory);
            if (chosen == null) return;
            _config.DumpsDirectory = chosen;
            try { SoftSledConfigManager.WriteConfig(_config); }
            catch (Exception ex) { MessageBox.Show("Couldn't save config: " + ex.Message); return; }
            DumpFolderPath.Text = chosen;
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
            if (_currentView != View.Root) {
                ShowView(View.Root);
                return true;
            }
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return true; // shell still hears CloseRequested and pops
        }
    }
}
