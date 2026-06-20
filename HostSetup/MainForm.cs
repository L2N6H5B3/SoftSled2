using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SoftSled.HostSetup {

    /// <summary>
    /// Single-window host setup utility. Detects the current install state,
    /// and lets the user Install (replace Mcx2Prov.exe + import the CA cert)
    /// or Uninstall (restore the original Mcx2Prov.exe from backup + remove
    /// the CA cert) — both run on a background thread and stream into a log.
    /// </summary>
    internal sealed class MainForm : Form {

        private readonly Label   _lblMcx;
        private readonly Label   _lblCert;
        private readonly Label   _summary;
        private readonly Button  _installButton;
        private readonly Button  _uninstallButton;
        private readonly Button  _closeButton;
        private readonly TextBox _log;

        public MainForm() {
            Text = "SoftSled Host Setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(660, 566);
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;

            var heading = new Label {
                Text = "Windows Media Center Host Setup",
                Font = new Font("Segoe UI Light", 18f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(20, 16),
                Size = new Size(620, 34),
            };

            var intro = new Label {
                Text =
                    "This tool prepares this PC (the Windows Media Center host) so SoftSled can pair " +
                    "with it. It patches  %WINDIR%\\ehome\\Mcx2Prov.exe  in place (one byte, backing up " +
                    "the original first) to skip the CRL check that SoftSled's certificates would fail, " +
                    "and imports the SoftSled CA certificate into the Local Machine Trusted Root store. " +
                    "Uninstall reverses both. Run this on the Media Center PC, not the device running " +
                    "SoftSled.",
                AutoSize = false,
                Location = new Point(20, 54),
                Size = new Size(620, 92),
            };

            var statusHeading = new Label {
                Text = "Current status",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(20, 150),
                Size = new Size(620, 20),
            };

            _lblMcx = new Label {
                AutoSize = false, Location = new Point(20, 174), Size = new Size(620, 22),
            };
            _lblCert = new Label {
                AutoSize = false, Location = new Point(20, 198), Size = new Size(620, 22),
            };
            _summary = new Label {
                AutoSize = false, Location = new Point(20, 226), Size = new Size(620, 22),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };

            _log = new TextBox {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(245, 245, 245),
                Font = new Font("Consolas", 9f),
                Location = new Point(20, 254),
                Size = new Size(620, 244),
                TabStop = false,
            };

            _installButton = new Button {
                Text = "Install", Location = new Point(300, 510), Size = new Size(96, 28),
            };
            _installButton.Click += InstallButton_Click;

            _uninstallButton = new Button {
                Text = "Uninstall", Location = new Point(404, 510), Size = new Size(112, 28),
            };
            _uninstallButton.Click += UninstallButton_Click;

            _closeButton = new Button {
                Text = "Close", Location = new Point(524, 510), Size = new Size(114, 28),
            };
            _closeButton.Click += (s, e) => Close();

            Controls.Add(heading);
            Controls.Add(intro);
            Controls.Add(statusHeading);
            Controls.Add(_lblMcx);
            Controls.Add(_lblCert);
            Controls.Add(_summary);
            Controls.Add(_log);
            Controls.Add(_installButton);
            Controls.Add(_uninstallButton);
            Controls.Add(_closeButton);

            Load += (s, e) => RefreshState();
        }

        // ---- State detection ------------------------------------------

        private void RefreshState() {
            bool admin  = SetupActions.IsAdministrator();
            bool mcx    = SetupActions.IsMcx2ProvInstalled();
            bool backup = SetupActions.HasMcx2ProvBackup();
            bool cert   = SetupActions.IsCaCertInstalled();

            SetStatusLabel(_lblMcx,  "Patched Mcx2Prov.exe:",    mcx,  mcx  ? "Installed" : "Not installed");
            SetStatusLabel(_lblCert, "SoftSled CA certificate:", cert, cert ? "Trusted in root store" : "Not present");

            if (!admin) {
                _summary.ForeColor = Color.Firebrick;
                _summary.Text = "NOT running as administrator — actions will fail. Re-launch as admin.";
            } else if (mcx && cert) {
                _summary.ForeColor = Color.ForestGreen;
                _summary.Text = "This PC is set up for SoftSled.";
            } else if (!mcx && !cert) {
                _summary.ForeColor = Color.DimGray;
                _summary.Text = "Not set up yet.";
            } else {
                _summary.ForeColor = Color.DarkOrange;
                _summary.Text = "Partially set up.";
            }

            _installButton.Enabled   = admin;
            _installButton.Text      = (mcx && cert) ? "Re-install" : "Install";
            // Anything to undo? the patch, a backup we could restore, or the cert.
            _uninstallButton.Enabled = admin && (mcx || backup || cert);
            _closeButton.Enabled     = true;
        }

        private static void SetStatusLabel(Label l, string name, bool ok, string state) {
            l.Text = "    " + name + "   " + state;
            l.ForeColor = ok ? Color.ForestGreen : Color.FromArgb(110, 110, 110);
        }

        // ---- Actions --------------------------------------------------

        private void InstallButton_Click(object sender, EventArgs e) {
            RunAsync("Installing SoftSled host setup...",
                     SetupActions.PatchMcx2Prov,
                     SetupActions.ImportCaCertificate);
        }

        private void UninstallButton_Click(object sender, EventArgs e) {
            RunAsync("Uninstalling SoftSled host setup...",
                     SetupActions.RestoreMcx2Prov,
                     SetupActions.RemoveCaCertificate);
        }

        private void RunAsync(string startMessage, params Func<Action<string>, bool>[] steps) {
            _installButton.Enabled = false;
            _uninstallButton.Enabled = false;
            _closeButton.Enabled = false;
            _log.Clear();
            Log(startMessage);
            Log("");

            ThreadPool.QueueUserWorkItem(_ => {
                bool all = true;
                foreach (var step in steps) {
                    bool ok = false;
                    try { ok = step(Log); }
                    catch (Exception ex) { Log("    UNEXPECTED ERROR: " + ex.Message); }
                    all &= ok;
                    Log("");
                }
                Log(all ? "Done." : "Finished with errors — see the messages above.");

                BeginInvoke(new Action(RefreshState));
            });
        }

        /// <summary>Append a line to the log box (thread-safe).</summary>
        private void Log(string line) {
            if (InvokeRequired) {
                BeginInvoke(new Action<string>(Log), line);
                return;
            }
            _log.AppendText(line + Environment.NewLine);
        }
    }
}
