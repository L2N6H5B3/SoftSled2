using AxMSTSCLib;
using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace SoftSledWPF {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {

        public AxMsRdpClient7NotSafeForScripting rdpControl;
        public System.Windows.Forms.Panel testPanel;

        public MainWindow() {

            
            InitializeComponent();

            // Subscribe to the event AFTER InitializeComponent
            wfHost.SizeChanged += WfHost_SizeChanged;

            wfHost.Loaded += MainWindow_Loaded;

            

            // Subscribe to the event AFTER InitializeComponent
            wfHost.SizeChanged += WfHost_SizeChanged;

            // Create and setup the RDP control
            SetupRdpControl();

        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            // Delay the call slightly using the dispatcher
            // Use Loaded priority first, if that still fails, try Background
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => ApplyTransparencyToHostedControl()));
        }

        // You could also call this from a Button Click event handler
        private void ApplyTransparencyToHostedControl() {
            if (testPanel == null || !testPanel.IsHandleCreated) {
                MessageBox.Show("Hosted control or its handle is not ready yet.");
                return;
            }



            IntPtr hwnd = testPanel.Handle;
            if (hwnd == IntPtr.Zero) {
                MessageBox.Show("Failed to get HWND handle for the hosted control.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"ApplyTransparency - HWND: {hwnd.ToInt64()}, IsHandleCreated: {testPanel.IsHandleCreated}"); // Debug output

            try {

                // 1. Get current extended window styles USING THE HELPER
                IntPtr currentExStyle = NativeMethods.GetWindowLongPtrHelper(hwnd, NativeMethods.GWL_EXSTYLE); // Use Helper!

                // Check for error from GetWindowLongPtrHelper (optional but good)
                if (currentExStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) {
                    MessageBox.Show($"Failed to get window extended style. Error code: {Marshal.GetLastWin32Error()}");
                    return;
                }

                // 2. Add the WS_EX_LAYERED style
                IntPtr newExStyle = new IntPtr(currentExStyle.ToInt64() | NativeMethods.WS_EX_LAYERED);

                // 3. Set the new extended window styles USING THE HELPER
                IntPtr resultSetStyle = NativeMethods.SetWindowLongPtrHelper(hwnd, NativeMethods.GWL_EXSTYLE, newExStyle); // Use Helper!
                if (resultSetStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) {
                    MessageBox.Show($"Failed to set WS_EX_LAYERED style. Error code: {Marshal.GetLastWin32Error()}");
                    return;
                }

                IntPtr checkExStyle = NativeMethods.GetWindowLongPtrHelper(hwnd, NativeMethods.GWL_EXSTYLE);
                if ((checkExStyle.ToInt64() & NativeMethods.WS_EX_LAYERED) == 0) {
                    MessageBox.Show("Error: WS_EX_LAYERED style check failed AFTER setting it.");
                    return; // Don't proceed if style isn't confirmed
                }
                System.Diagnostics.Debug.WriteLine("Style check PASSED. WS_EX_LAYERED is present.");
                // *** END VERIFICATION STEP ***

                //// 1. Get current extended window styles
                //IntPtr currentExStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);

                //// 2. Add the WS_EX_LAYERED style
                //IntPtr newExStyle = new IntPtr(currentExStyle.ToInt64() | NativeMethods.WS_EX_LAYERED);

                //// 3. Set the new extended window styles
                //IntPtr resultSetStyle = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, newExStyle);
                //if (resultSetStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) {
                //    MessageBox.Show($"Failed to set WS_EX_LAYERED style. Error code: {Marshal.GetLastWin32Error()}");
                //    return;
                //}

                // 4. Define the color key (e.g., Magenta)
                System.Drawing.Color keyColor = System.Drawing.Color.Black; // Change this to your desired transparent color
                uint keyColorWin32 = NativeMethods.ToWin32Color(keyColor);

                // 5. Set the layered window attributes for color keying
                bool setResult = NativeMethods.SetLayeredWindowAttributes(hwnd, keyColorWin32, 0, NativeMethods.LWA_COLORKEY);
                if (!setResult) {
                    MessageBox.Show($"Failed to set layered window attributes. Error code: {Marshal.GetLastWin32Error()}");
                } else {
                    // Optional: Indicate success if desired
                     MessageBox.Show("Transparency Key Applied Successfully!");
                }
            } catch (Exception ex) {
                MessageBox.Show($"An error occurred: {ex.Message}");
            }
        }


        private void SetupRdpControl() {

            testPanel = new System.Windows.Forms.Panel();
            testPanel.BackColor = System.Drawing.Color.Blue; // Make it visible
            //testPanel.Dock = DockStyle.Fill; // Test docking too

            wfHost.Child = testPanel; // Assign the PANEL as the child


            //rdpControl = new AxMsRdpClient7NotSafeForScripting();

            //// Initialize the ActiveX control if needed (often done automatically)
            //((System.ComponentModel.ISupportInitialize)(rdpControl)).BeginInit();
            //rdpControl.Enabled = true;
            //// Assign the ActiveX wrapper control to the WindowsFormsHost
            //wfHost.Child = rdpControl;

            //((System.ComponentModel.ISupportInitialize)(rdpControl)).EndInit();


            //// Initial size sync (optional but can help)
            //SynchronizeRdpSize(wfHost, wfHost.RenderSize);

            //// Initialize RDP settings etc.
            //rdpControl.Server = "10.1.1.33";
            //rdpControl.UserName = "BradNet-Admin";
            //rdpControl.AdvancedSettings7.ClearTextPassword = "nesjeorithsA1";
            //rdpControl.AdvancedSettings7.SmartSizing = true;

            //rdpControl.Connect();
        }


        private void WfHost_SizeChanged(object sender, SizeChangedEventArgs e) {
            SynchronizeRdpSize(sender as System.Windows.Forms.Integration.WindowsFormsHost, e.NewSize);
        }

        private void SynchronizeRdpSize(System.Windows.Forms.Integration.WindowsFormsHost host, Size newSize) {
            if (host?.Child is AxMsRdpClient7NotSafeForScripting childControl) {
                // Check for valid size (prevents issues during load/unload)
                if (newSize.Width > 0 && newSize.Height > 0 &&
                    IsFinite(newSize.Width) && IsFinite(newSize.Height)) {
                    // Set the size explicitly
                    childControl.Width = (int)newSize.Width;
                    childControl.Height = (int)newSize.Height;

                    // -- Optional: Use BeginInvoke for potential timing issues --
                    // If the direct setting still seems off, sometimes marshalling
                    // the call to the WinForms UI thread helps.
                    // childControl.BeginInvoke(new Action(() => {
                    //     if (childControl.IsHandleCreated && !childControl.IsDisposed)
                    //     {
                    //        childControl.Width = (int)newSize.Width;
                    //        childControl.Height = (int)newSize.Height;
                    //     }
                    // }));
                }
            }
        }

        // Helper to check for valid finite numbers
        private bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // --- Remember to unsubscribe when the window closes ---
        protected override void OnClosed(EventArgs e) {
            if (wfHost != null) {
                wfHost.SizeChanged -= WfHost_SizeChanged;
                // Dispose child properly
                wfHost.Child?.Dispose();
                wfHost.Dispose();
            }
            base.OnClosed(e);
        }

    }
}
