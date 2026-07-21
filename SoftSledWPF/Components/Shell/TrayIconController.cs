using System;
using System.Drawing;
using System.Windows.Forms;
using SoftSled.Components.Diagnostics;

namespace SoftSledWPF.Components.Shell {

    /// <summary>
    /// A thin wrapper over a WinForms <see cref="NotifyIcon"/> giving SoftSled a
    /// system-tray presence while it idles resident (window hidden, no RDP
    /// session) so the MCE remote's Green button can wake it.
    ///
    /// <para>Created on the WPF UI thread (which has the message pump NotifyIcon
    /// needs). Raises <see cref="OpenRequested"/> when the user double-clicks the
    /// icon or picks "Open SoftSled", and <see cref="ExitRequested"/> for a real
    /// quit. The shell owns the window lifecycle — this class only surfaces intent
    /// and shows/hides the icon.</para>
    /// </summary>
    public sealed class TrayIconController : IDisposable {

        private readonly Logger _log;
        private NotifyIcon _icon;

        /// <summary>Raised (UI thread) when the user asks to bring the window
        /// back — tray double-click or the "Open SoftSled" menu item.</summary>
        public event EventHandler OpenRequested;

        /// <summary>Raised (UI thread) when the user picks "Exit" — a genuine
        /// application shutdown, not a hide-to-tray.</summary>
        public event EventHandler ExitRequested;

        public TrayIconController(Logger log) {
            _log = log;
        }

        /// <summary>True once the tray icon has been created and shown.</summary>
        public bool IsShown => _icon != null && _icon.Visible;

        /// <summary>Create and show the tray icon. Idempotent — a second call
        /// just ensures it is visible.</summary>
        public void Show() {
            try {
                if (_icon == null) {
                    var menu = new ContextMenuStrip();
                    var openItem = new ToolStripMenuItem("Open SoftSled");
                    openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
                    openItem.Click += (s, e) => Raise(OpenRequested);
                    var exitItem = new ToolStripMenuItem("Exit");
                    exitItem.Click += (s, e) => Raise(ExitRequested);
                    menu.Items.Add(openItem);
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(exitItem);

                    _icon = new NotifyIcon {
                        Text = "SoftSled",
                        Icon = LoadIcon(),
                        ContextMenuStrip = menu,
                    };
                    // Double-click (not single, to leave room for the context
                    // menu) restores the window.
                    _icon.DoubleClick += (s, e) => Raise(OpenRequested);
                }
                _icon.Visible = true;
                _log?.LogInfo("[tray] icon shown");
            } catch (Exception ex) {
                _log?.LogError("[tray] Show threw: " + ex.Message);
            }
        }

        /// <summary>Hide the icon without destroying it (kept for a later Show).</summary>
        public void Hide() {
            try {
                if (_icon != null) _icon.Visible = false;
            } catch (Exception ex) {
                _log?.LogError("[tray] Hide threw: " + ex.Message);
            }
        }

        private static Icon LoadIcon() {
            // The running exe carries wmc.ico as its ApplicationIcon — pull it
            // straight off the executable so the tray matches the taskbar.
            try {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe)) {
                    var ico = Icon.ExtractAssociatedIcon(exe);
                    if (ico != null) return ico;
                }
            } catch { /* fall through to a stock icon */ }
            return SystemIcons.Application;
        }

        private void Raise(EventHandler handler) {
            try { handler?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _log?.LogError("[tray] handler threw: " + ex.Message); }
        }

        public void Dispose() {
            try {
                if (_icon != null) {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                    _log?.LogInfo("[tray] icon disposed");
                }
            } catch (Exception ex) {
                _log?.LogError("[tray] Dispose threw: " + ex.Message);
            }
        }
    }
}
