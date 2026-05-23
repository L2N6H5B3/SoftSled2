using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Extender;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SoftSledWPF.Components.Shell {
    /// <summary>
    /// Inline pairing flow. On entry, spins up <see cref="ExtenderDevice"/>
    /// to broadcast on UPnP and shows the calculated WPS-style PIN. Polls
    /// <see cref="SoftSledConfig.IsPaired"/> every 500 ms — once WMC has
    /// completed the trust-agreement / advertise round-trips the device
    /// flips IsPaired and we raise <see cref="PairingCompleted"/>, which
    /// the shell uses to push the user into the live session.
    /// </summary>
    public partial class PairingPage : UserControl {
        private readonly Logger _logger;
        private ExtenderDevice _device;
        private DispatcherTimer _pollTimer;

        /// <summary>Pairing succeeded — host pushed creds into config.</summary>
        public event EventHandler PairingCompleted;
        /// <summary>User pressed Cancel/ESC — shell should pop back.</summary>
        public event EventHandler PairingCancelled;

        public PairingPage(Logger logger) {
            InitializeComponent();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.Loaded += PairingPage_Loaded;
            this.Unloaded += PairingPage_Unloaded;
        }

        private void PairingPage_Loaded(object sender, RoutedEventArgs e) {
            // If the user navigates to this page despite already being
            // paired, short-circuit straight to completion so the shell
            // can advance.
            var cfg = SoftSledConfigManager.ReadConfig();
            if (cfg.IsPaired) {
                Dispatcher.BeginInvoke(new Action(() =>
                    PairingCompleted?.Invoke(this, EventArgs.Empty)));
                return;
            }

            try {
                _device = new ExtenderDevice(_logger);
                _device.Start();
                PinText.Text = _device.GetPairingCode();
                StatusText.Text = "Waiting for Windows Media Center to pair...";
            } catch (Exception ex) {
                _logger.LogError($"Pairing start failed: {ex.Message}");
                StatusText.Text = "Failed to start pairing — see log.";
                MessageBox.Show("Failed to start pairing: " + ex.Message);
                return;
            }

            // 500 ms is plenty given pairing typically takes a few seconds
            // of SOAP round-trips on the user's local network.
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            CancelButton.Focus();
        }

        private void PollTimer_Tick(object sender, EventArgs e) {
            try {
                var cfg = SoftSledConfigManager.ReadConfig();
                if (cfg.IsPaired) {
                    _pollTimer.Stop();
                    StatusText.Text = $"Paired with {cfg.RdpLoginHost} — starting session...";
                    // Defer one tick to let the user see the success banner.
                    Dispatcher.BeginInvoke(new Action(() =>
                        PairingCompleted?.Invoke(this, EventArgs.Empty)));
                }
            } catch (Exception ex) {
                _logger.LogError($"Pairing poll error: {ex.Message}");
            }
        }

        private void PairingPage_Unloaded(object sender, RoutedEventArgs e) {
            try { _pollTimer?.Stop(); } catch { }
            _pollTimer = null;
            try { _device?.Stop(); } catch { }
            _device = null;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            PairingCancelled?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Allow the shell to surface ESC as a cancel.</summary>
        public void CancelFromShell() {
            PairingCancelled?.Invoke(this, EventArgs.Empty);
        }
    }
}
