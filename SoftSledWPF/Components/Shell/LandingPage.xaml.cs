using SoftSled.Components.Configuration;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoftSledWPF.Components.Shell {
    /// <summary>
    /// Top-level shell page. Three actions:
    ///   * Start Extender — pair-then-connect (or jump straight to session
    ///                      if already paired).
    ///   * Settings — open the nested config page.
    ///   * Quit — close the application.
    /// Remote-friendly: arrow keys move selection in the ListBox, Enter
    /// activates, Escape is handled at the shell level (no-op on landing).
    /// </summary>
    public partial class LandingPage : UserControl {
        public event EventHandler StartExtenderRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler QuitRequested;

        public LandingPage() {
            InitializeComponent();
            this.Loaded += LandingPage_Loaded;
        }

        private void LandingPage_Loaded(object sender, RoutedEventArgs e) {
            // Refresh the pairing status banner every time we re-enter
            // the landing page (eg. after the user unpairs from settings).
            var config = SoftSledConfigManager.ReadConfig();
            StatusText.Text = config.IsPaired
                ? $"Paired with {config.RdpLoginHost}"
                : "Not paired — choose Start Extender to begin";

            // With tray mode on, this item folds the window to the tray rather
            // than exiting (the tray's Exit item is the real quit), so label it
            // honestly. Re-read here — Loaded fires on every re-entry — so
            // toggling the setting in Settings updates the label on return.
            ItemQuit.Content = config.MinimizeToTray ? "Close to tray" : "Quit";

            // Ensure the menu has keyboard focus when the page appears so
            // arrow keys work without the user having to click first.
            MenuListBox.Focus();
            if (MenuListBox.SelectedItem == null && MenuListBox.Items.Count > 0) {
                MenuListBox.SelectedIndex = 0;
            }
            if (MenuListBox.SelectedItem is ListBoxItem lbi) {
                lbi.Focus();
            }
        }

        private void MenuListBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter || e.Key == Key.Space) {
                Activate(MenuListBox.SelectedItem as ListBoxItem);
                e.Handled = true;
            }
        }

        private void MenuListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
            // ContainerFromElement walks up the visual tree from the
            // clicked element to find the owning ListBoxItem — returns
            // null if the click landed on empty list space, which we
            // want to ignore.
            var item = ItemsControl.ContainerFromElement(
                MenuListBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item != null) Activate(item);
        }

        private void Activate(ListBoxItem item) {
            if (item == ItemStart)         StartExtenderRequested?.Invoke(this, EventArgs.Empty);
            else if (item == ItemSettings) SettingsRequested?.Invoke(this, EventArgs.Empty);
            else if (item == ItemQuit)     QuitRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
