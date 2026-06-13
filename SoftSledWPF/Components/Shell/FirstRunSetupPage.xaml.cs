using SoftSled.Components.Configuration;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoftSledWPF.Components.Shell {

    /// <summary>
    /// First-run setup wizard. Walks the user through the prerequisite
    /// settings that most affect the experience — network type (which drives
    /// Remote Rendering), TV vs monitor (TV skin), the session resolution
    /// (auto-detected) + full-screen mode, the local animation level (only
    /// when Remote Rendering is off), and startup/connection behaviour
    /// (auto-connect / auto-exit).
    ///
    /// <para>Shown automatically by the shell on first launch
    /// (<see cref="SoftSledConfig.InitialSetupComplete"/> == false) and
    /// re-runnable from the first item in Settings. On Finish it writes the
    /// chosen settings + marks setup complete and raises
    /// <see cref="Completed"/>; Cancel raises <see cref="Cancelled"/> without
    /// writing anything.</para>
    /// </summary>
    public partial class FirstRunSetupPage : UserControl {

        /// <summary>Raised when the user finishes the wizard. Config has
        /// already been written (including InitialSetupComplete = true).</summary>
        public event EventHandler Completed;

        /// <summary>Raised when the user cancels. No config is written.</summary>
        public event EventHandler Cancelled;

        /// <summary>Raised when the full-screen tickbox is toggled, so the
        /// shell can apply it live (transient, like F11). The persisted value
        /// is still written on Finish / reverted on Cancel by the shell.</summary>
        public event Action<bool> FullScreenToggled;

        // Suppresses FullScreenToggled while LoadFromConfig seeds the tickbox.
        private bool _loading;

        // Order: Welcome → Network → Display → Resolution → [Animations if
        // Wi-Fi] → Connection. Animations is skipped on wired (Remote
        // Rendering owns the animation pipeline there).
        private enum Step { Welcome, Network, Display, Resolution, Animations, Connection }
        private Step _step = Step.Welcome;

        public FirstRunSetupPage() {
            InitializeComponent();
            Loaded += (s, e) => {
                LoadFromConfig();
                GoToStep(Step.Welcome);
            };
            // Live full-screen toggle — fires for any change path (mouse,
            // Space, the Enter handler below) except while loading the saved
            // value. The shell applies it immediately.
            RoutedEventHandler onFsChanged = (s, e) => {
                if (!_loading) FullScreenToggled?.Invoke(ChkWizFullScreen.IsChecked == true);
            };
            ChkWizFullScreen.Checked   += onFsChanged;
            ChkWizFullScreen.Unchecked += onFsChanged;
            // WMC remote uses Enter as the universal "activate". WPF buttons
            // only fire on Space (or Enter when IsDefault) and radio buttons
            // only check on Space, so route Enter through the focused control.
            PreviewKeyDown += (s, e) => {
                var f = Keyboard.FocusedElement;

                // Enter = universal "activate" (WMC remote).
                if (e.Key == Key.Enter) {
                    if (f is RadioButton rb) {
                        // Select, then advance to Next so Enter-Enter confirms
                        // the option and moves on.
                        rb.IsChecked = true;
                        NextButton.Focus();
                        e.Handled = true;
                    } else if (f is CheckBox cb) {
                        cb.IsChecked = !(cb.IsChecked == true);   // toggle; stay
                        e.Handled = true;
                    } else if (f is Button btn) {
                        btn.RaiseEvent(new RoutedEventArgs(
                            System.Windows.Controls.Primitives.ButtonBase.ClickEvent, btn));
                        e.Handled = true;
                    } else if (_step == Step.Resolution && ResolutionList.IsKeyboardFocusWithin) {
                        NextButton.Focus();
                        e.Handled = true;
                    }
                    return;
                }
                // Right jumps from the step content across to Next — except in
                // the resolution list, where it first hops to the full-screen
                // tickbox below. Up/Down move between options (WPF default).
                if (e.Key == Key.Right) {
                    if (_step == Step.Resolution && ResolutionList.IsKeyboardFocusWithin) {
                        ChkWizFullScreen.Focus();
                        e.Handled = true;
                    } else if (f is RadioButton || f is CheckBox) {
                        NextButton.Focus();
                        e.Handled = true;
                    }
                    return;
                }
                // Down past the last option/control lands on Next (WPF's
                // default would fall onto Back, the first footer button).
                if (e.Key == Key.Down) {
                    if (f is RadioButton && f == GetCurrentStepLastRadio()) {
                        NextButton.Focus();
                        e.Handled = true;
                    } else if (_step == Step.Resolution
                               && ResolutionList.IsKeyboardFocusWithin
                               && ResolutionList.SelectedIndex == ResolutionList.Items.Count - 1) {
                        ChkWizFullScreen.Focus();
                        e.Handled = true;
                    } else if (_step == Step.Resolution && f == ChkWizFullScreen) {
                        NextButton.Focus();
                        e.Handled = true;
                    } else if (_step == Step.Connection && f == ChkWizAutoExit) {
                        NextButton.Focus();
                        e.Handled = true;
                    }
                    return;
                }
                // Up from the full-screen tickbox returns into the list.
                if (e.Key == Key.Up && _step == Step.Resolution && f == ChkWizFullScreen) {
                    ResolutionList.Focus();
                    e.Handled = true;
                    return;
                }
                // Left from the leftmost action button (Back, or Next on the
                // welcome page where Back is hidden) returns to the step
                // content — completing the round-trip.
                if (e.Key == Key.Left
                    && (f == BackButton
                        || (f == NextButton && BackButton.Visibility != Visibility.Visible))) {
                    FocusStepContent();
                    e.Handled = true;
                }
            };
        }

        // ---- Pre-seed the controls from the current config so re-running
        //      the wizard from Settings reflects the existing choices. ----
        private void LoadFromConfig() {
            _loading = true;
            try { LoadFromConfigCore(); }
            finally { _loading = false; }
        }

        private void LoadFromConfigCore() {
            SoftSledConfig cfg;
            try { cfg = SoftSledConfigManager.ReadConfig(); }
            catch { cfg = new SoftSledConfig(); }

            // Network ↔ Remote Rendering (wired enables it).
            RbWired.IsChecked = cfg.EnableRemoteRendering;
            RbWifi.IsChecked  = !cfg.EnableRemoteRendering;

            // Display ↔ TV skin.
            RbTv.IsChecked      = cfg.UseTvSkin;
            RbMonitor.IsChecked = !cfg.UseTvSkin;

            // Animation level ↔ (2D, Intense) pair.
            if (cfg.EnableIntenseAnimations)      RbAnimFull.IsChecked    = true;
            else if (cfg.Enable2DAnimations)      RbAnimMinimal.IsChecked = true;
            else                                  RbAnimNone.IsChecked    = true;

            // Resolution — detect the screen, map to a supported size, and
            // preselect it (first run) or the saved choice (re-run).
            ResolutionList.ItemsSource = ScreenResolutions.All.Select(r => r.Display).ToList();
            ScreenResolutions.DetectPrimaryScreen(out int dw, out int dh);
            var mapped = ScreenResolutions.MapToSupported(dw, dh);
            int sel = cfg.InitialSetupComplete
                      ? ScreenResolutions.IndexOf(cfg.SessionWidth, cfg.SessionHeight)
                      : -1;
            if (sel < 0) sel = ScreenResolutions.IndexOf(mapped.Width, mapped.Height);
            if (sel < 0) sel = 0;
            ResolutionList.SelectedIndex = sel;
            ResolutionIntro.Text =
                $"We detected your display as {dw} × {dh}. SoftSled will request " +
                $"{mapped.Width} × {mapped.Height} from Windows Media Center — pick a " +
                "different resolution below if that's not right.";

            // Full screen + startup behaviour.
            ChkWizFullScreen.IsChecked  = cfg.RunFullScreen;
            ChkWizAutoConnect.IsChecked = cfg.AutoStartWmcOnOpen;
            ChkWizAutoExit.IsChecked    = cfg.CloseOnWmcClose;
        }

        // ---- Step navigation ------------------------------------------

        private bool Wired => RbWired.IsChecked == true;

        /// <summary>True when the Animations step applies — only when the
        /// user is on Wi-Fi, i.e. Remote Rendering will be off (RR owns the
        /// animation pipeline when on, so the local choice is moot).</summary>
        private bool AnimationsStepApplies => !Wired;

        private void GoToStep(Step step) {
            _step = step;

            // Step title shown in the header (above the divider).
            switch (step) {
                case Step.Welcome:    StepTitle.Text = "SoftSled Setup"; break;
                case Step.Network:    StepTitle.Text = "Networking";     break;
                case Step.Display:    StepTitle.Text = "Television";      break;
                case Step.Resolution: StepTitle.Text = "Resolution";      break;
                case Step.Animations: StepTitle.Text = "Animations";      break;
                case Step.Connection: StepTitle.Text = "Startup";         break;
            }

            WelcomePanel.Visibility    = step == Step.Welcome    ? Visibility.Visible : Visibility.Collapsed;
            NetworkPanel.Visibility    = step == Step.Network    ? Visibility.Visible : Visibility.Collapsed;
            DisplayPanel.Visibility    = step == Step.Display    ? Visibility.Visible : Visibility.Collapsed;
            ResolutionPanel.Visibility = step == Step.Resolution ? Visibility.Visible : Visibility.Collapsed;
            AnimationsPanel.Visibility = step == Step.Animations ? Visibility.Visible : Visibility.Collapsed;
            ConnectionPanel.Visibility = step == Step.Connection ? Visibility.Visible : Visibility.Collapsed;

            // Hide Back entirely on the first page — there's nowhere to go.
            BackButton.Visibility = step == Step.Welcome ? Visibility.Collapsed : Visibility.Visible;
            NextButton.Content    = IsLastStep(step) ? "Finish" : "Next";

            // Focus the step's primary control so the remote works without a
            // mouse (and keep the chosen resolution scrolled into view).
            Dispatcher.BeginInvoke(new Action(() => {
                if (step == Step.Resolution && ResolutionList.SelectedItem != null) {
                    ResolutionList.ScrollIntoView(ResolutionList.SelectedItem);
                }
                FocusStepContent();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Focus the primary content control for the current step.</summary>
        private void FocusStepContent() {
            switch (_step) {
                case Step.Welcome:    NextButton.Focus(); break;
                case Step.Resolution: ResolutionList.Focus(); break;
                case Step.Connection: ChkWizAutoConnect.Focus(); break;
                default:              GetCurrentStepFocusRadio()?.Focus(); break;
            }
        }

        /// <summary>The radio option that should hold focus on the current
        /// step — the checked one, falling back to the first. Null on steps
        /// with no options (Welcome).</summary>
        private RadioButton GetCurrentStepFocusRadio() {
            switch (_step) {
                case Step.Network:
                    return RbWifi.IsChecked == true ? RbWifi : RbWired;
                case Step.Display:
                    return RbTv.IsChecked == true ? RbTv : RbMonitor;
                case Step.Animations:
                    if (RbAnimFull.IsChecked == true) return RbAnimFull;
                    if (RbAnimNone.IsChecked == true) return RbAnimNone;
                    return RbAnimMinimal;
                default:
                    return null;
            }
        }

        /// <summary>The last (bottom-most) radio option on the current step,
        /// so Down past it can be routed to the Next button.</summary>
        private RadioButton GetCurrentStepLastRadio() {
            switch (_step) {
                case Step.Network:    return RbWifi;
                case Step.Display:    return RbMonitor;
                case Step.Animations: return RbAnimNone;
                default:              return null;
            }
        }

        // Connection is always the final step.
        private bool IsLastStep(Step step) => step == Step.Connection;

        private void NextButton_Click(object sender, RoutedEventArgs e) {
            switch (_step) {
                case Step.Welcome:    GoToStep(Step.Network); break;
                case Step.Network:    GoToStep(Step.Display); break;
                case Step.Display:    GoToStep(Step.Resolution); break;
                case Step.Resolution:
                    GoToStep(AnimationsStepApplies ? Step.Animations : Step.Connection);
                    break;
                case Step.Animations: GoToStep(Step.Connection); break;
                case Step.Connection: Finish(); break;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            switch (_step) {
                case Step.Network:    GoToStep(Step.Welcome); break;
                case Step.Display:    GoToStep(Step.Network); break;
                case Step.Resolution: GoToStep(Step.Display); break;
                case Step.Animations: GoToStep(Step.Resolution); break;
                case Step.Connection:
                    GoToStep(AnimationsStepApplies ? Step.Animations : Step.Resolution);
                    break;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Shell back-key entry point. Steps back one page, or
        /// cancels when already on the first page. Returns true (always
        /// handled).</summary>
        public bool HandleBack() {
            if (_step == Step.Welcome) {
                Cancelled?.Invoke(this, EventArgs.Empty);
            } else {
                BackButton_Click(this, null);
            }
            return true;
        }

        // ---- Finish: persist the chosen settings ----------------------

        private void Finish() {
            SoftSledConfig cfg;
            try { cfg = SoftSledConfigManager.ReadConfig(); }
            catch { cfg = new SoftSledConfig(); }

            // Networking → Remote Rendering. Wired = stable low-latency =
            // let WMC render remotely; Wi-Fi = keep it local for responsiveness.
            cfg.EnableRemoteRendering = Wired;

            // Display → TV skin.
            cfg.UseTvSkin = RbTv.IsChecked == true;

            // Animation level — only meaningful when Remote Rendering is off
            // (it owns the animation pipeline when on). Map the three choices
            // onto the (Intense, 2D) pair:
            //   Full    → Intense + 2D
            //   Minimal → 2D only
            //   None    → neither
            if (!Wired) {
                if (RbAnimFull.IsChecked == true) {
                    cfg.EnableIntenseAnimations = true;
                    cfg.Enable2DAnimations      = true;
                } else if (RbAnimMinimal.IsChecked == true) {
                    cfg.EnableIntenseAnimations = false;
                    cfg.Enable2DAnimations      = true;
                } else {
                    cfg.EnableIntenseAnimations = false;
                    cfg.Enable2DAnimations      = false;
                }
            }

            // Resolution + full screen.
            int ri = ResolutionList.SelectedIndex;
            if (ri >= 0 && ri < ScreenResolutions.All.Length) {
                cfg.SessionWidth  = ScreenResolutions.All[ri].Width;
                cfg.SessionHeight = ScreenResolutions.All[ri].Height;
            }
            cfg.RunFullScreen = ChkWizFullScreen.IsChecked == true;

            // Startup / connection behaviour.
            cfg.AutoStartWmcOnOpen = ChkWizAutoConnect.IsChecked == true;
            cfg.CloseOnWmcClose    = ChkWizAutoExit.IsChecked == true;

            cfg.InitialSetupComplete = true;

            try {
                SoftSledConfigManager.WriteConfig(cfg);
            } catch (Exception ex) {
                MessageBox.Show("Couldn't save your setup choices: " + ex.Message);
            }

            Completed?.Invoke(this, EventArgs.Empty);
        }
    }
}
