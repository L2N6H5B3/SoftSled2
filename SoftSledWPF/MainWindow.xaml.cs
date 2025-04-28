using AxMSTSCLib;
using MSTSCLib;
using SoftSled.Components.Communication;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Extender;
using SoftSled.Components.VirtualChannel;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace SoftSledWPF {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {

        // Private members
        private Logger m_logger;
        private ExtenderDevice m_device;
        private bool isConnecting = false;
        private bool rdpInitialised = false;

        public AxMsRdpClient7NotSafeForScripting rdpClient;
        public System.Windows.Forms.Panel testPanel;

        private RDPVCInterface rdpVCInterface;

        private VirtualChannelAvCtrlHandler AvCtrlHandler;
        private VirtualChannelDevCapsHandler DevCapsHandler;
        private VirtualChannelMcxSessHandler McxSessHandler;

        //public H264DecoderView _decoderView;

        public MainWindow() {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed; // Add handler for cleanup
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            InitialiseLogger();

            //_decoderView = new H264DecoderView(Application.Current.Dispatcher);

            //int videoWidth = 1280;
            //int videoHeight = 720;

            //if (_decoderView.Initialize(videoWidth, videoHeight)) {
            //    // Bind the Image source to the decoder's output
            //    VideoImageDisplay.Source = _decoderView.VideoSource;
            //    _decoderView.Start();

            //    // === Example: Start feeding NAL units (replace with your actual source) ===
            //    // Start a background task or event handler that calls:
            //    // _decoderView.ReceiveNalUnit(your_nal_byte_array);
            //    // ExampleTimerFeed(); // Call a method that simulates receiving NALs
            //    // =========================================================================
            //} else {
            //    MessageBox.Show("Failed to initialize video decoder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            //}

            // Create RDPVCInterface to handle Virtual Channel Communications
            //rdpVCInterface = new RDPVCInterface(m_logger);
            rdpVCInterface = new RDPVCInterface();
            rdpVCInterface.DataReceived += RdpVCInterface_DataReceived;


            // Create VirtualChannel Handlers
            McxSessHandler = new VirtualChannelMcxSessHandler(m_logger);
            DevCapsHandler = new VirtualChannelDevCapsHandler(m_logger);
            //AvCtrlHandler = new VirtualChannelAvCtrlHandler(m_logger, _decoderView);
            AvCtrlHandler = new VirtualChannelAvCtrlHandler(m_logger);
            McxSessHandler.VirtualChannelSend += On_VirtualChannelSend;
            DevCapsHandler.VirtualChannelSend += On_VirtualChannelSend;
            AvCtrlHandler.VirtualChannelSend += On_VirtualChannelSend;

            // Create VirtualChannel Handlers EventHandlers
            McxSessHandler.StatusChanged += McxSessHandler_StatusChanged;

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            if (!config.IsPaired) {
                m_logger.LogInfo("Extender is not paired!");
                //SetStatus("Extender is not paired");
            } else {
                m_logger.LogInfo("Extender is paired with " + config.RdpLoginHost);
                //SetStatus("Extender ready to connect");
            }


            // Create the RDP Client ActiveX control.
            this.rdpClient = new AxMsRdpClient7NotSafeForScripting();

            // Important: Add the control to the WindowsFormsHost element's Child property
            this.rdpHost.Child = this.rdpClient;

            // Initialize the control (optional, but recommended)
            // Must cast the Child back to the specific type
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).BeginInit();
            this.rdpClient.Enabled = true;
            this.rdpClient.Visible = false;
            // Add any other initialization properties here if needed
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).EndInit();

            // Optional: Subscribe to RDP events
            this.rdpClient.OnDisconnected += RdpClient_OnDisconnected;
            this.rdpClient.OnLoginComplete += RdpClient_OnLoginComplete;
            // Add other event handlers as needed...
        }

        void InitialiseLogger() {
            // For now simply hardcode the logger.
            m_logger = new TextBoxLogger(loggerTextBox, this);
            m_logger.IsLoggingDebug = true;
        }

        private void RdpVCInterface_DataReceived(object sender, DataReceived e) {
            try {
                //var res = rdpClient.GetVirtualChannelOptions("McxSess");
                if (e.channelName == "McxSess") {
                    McxSessHandler.ProcessData(e.data);
                } else if (e.channelName == "devcaps") {
                    DevCapsHandler.ProcessData(e.data);
                } else if (e.channelName == "avctrl") {
                    AvCtrlHandler.ProcessData(e.data);
                } else {
                    MessageBox.Show("Unhandled data on channel " + e.channelName);
                    m_logger.LogDebug($"{e.channelName} Bytes: " + BitConverter.ToString(e.data));
                }

            } catch (Exception ee) {
                MessageBox.Show(ee.Message + " " + ee.StackTrace);
            }
        }

        private void On_VirtualChannelSend(object sender, VirtualChannelSendArgs e) {
            rdpVCInterface.SendOnVirtualChannel(e.channelName, e.data);
        }

        private void McxSessHandler_StatusChanged(object sender, StatusChangedArgs e) {

            // Set Status
            //SetStatus(e.statusText);

            // If the Shell is open
            if (e.shellOpen) {
                //SetPanOverlayVisible(false);
                SetRdpClientVisible(true);
                // Play Opening Music
                //PlayOpening();
            } else if (e.shellOpen && rdpClient.Visible == true) {
                //SetPanOverlayVisible(false);
                SetRdpClientVisible(true);
            } else {
                //SetPanOverlayVisible(true);
                SetRdpClientVisible(false);
            }

        }


        private void BtnConnect_Click(object sender, RoutedEventArgs e) {

            IPAddress localhost = null;
            var host = Dns.GetHostEntry(Dns.GetHostName());

            // Get IPv4 Address
            var IPv4Address = host.AddressList.FirstOrDefault(xx => xx.AddressFamily == AddressFamily.InterNetwork);
            // Check if there is an IPv4 Address
            if (IPv4Address != null) {
                localhost = IPv4Address;
            } else {
                throw new Exception("No network adapters with an IPv4 address in the system!");
            }

            if (m_device != null) {
                m_device.Stop();
            }

            SoftSledConfig currConfig = SoftSledConfigManager.ReadConfig();
            if (!currConfig.IsPaired) {
                MessageBox.Show("SoftSled is currently not paired with Windows Media Center. Enter the 'Extender Setup' mode to pair.");
                return;
            }

            loggerTextBox.Text = "";

            m_device = new ExtenderDevice(m_logger);
            m_device.Start();

            // If RDP not Initialised
            if (!rdpInitialised) {
                // Initialise RDP
                InitialiseRdpClient();
            }

            // Set RDP Server Address
            rdpClient.Server = currConfig.RdpLoginHost;
            // Set RDP Username
            rdpClient.UserName = currConfig.RdpLoginUserName;
            // Set RDP Password
            rdpClient.AdvancedSettings2.ClearTextPassword = currConfig.RdpLoginPassword;
            // Set RDP Color Depth
            rdpClient.ColorDepth = 32;
            rdpClient.AdvancedSettings7.AudioRedirectionMode = 0;
            // Connect RDP
            rdpClient.Connect();

            //SetStatus("Remote Desktop Connecting...");
            isConnecting = true;



            //if (this.rdpClient == null || string.IsNullOrWhiteSpace(txtServer.Text)) {
            //    MessageBox.Show("RDP client not initialized or server name is missing.");
            //    return;
            //}

            //try {
            //    // Basic connection settings
            //    this.rdpClient.Server = txtServer.Text;
            //    // NOTE: Avoid hardcoding usernames/passwords. Prompt user securely or use SSO.
            //    // this.rdpClient.UserName = "YourUsername";

            //    // Example of advanced settings (use the correct AdvancedSettings object, e.g., 7, 8, 9)
            //    IMsRdpClientAdvancedSettings7 advancedSettings =
            //        (IMsRdpClientAdvancedSettings7)this.rdpClient.AdvancedSettings7;

            //    // !! SECURITY WARNING !! Avoid ClearTextPassword in production!
            //    // advancedSettings.ClearTextPassword = "YourPassword";

            //    // Recommended: Use CredSSP (Network Level Authentication) if available
            //    advancedSettings.EnableCredSspSupport = true;

            //    // Other common settings
            //    // advancedSettings.RedirectDrives = true;
            //    // advancedSettings.RedirectPrinters = false;
            //    // this.rdpClient.DesktopWidth = 1024;
            //    // this.rdpClient.DesktopHeight = 768;
            //    // this.rdpClient.ColorDepth = 24;

            //    this.rdpClient.Connect();
            //} catch (Exception ex) {
            //    MessageBox.Show($"Error connecting: {ex.Message}");
            //}
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) {
            DisconnectRdp();
        }

        private void DisconnectRdp() {
            if (this.rdpClient != null && this.rdpClient.Connected == 1) // Check if connected (1=Connected, 0=Not Connected)
           {
                try {
                    this.rdpClient.Disconnect();
                } catch (Exception ex) {
                    // Log or handle disconnection error
                    System.Diagnostics.Debug.WriteLine($"Error disconnecting: {ex.Message}");
                }
            }
        }

        private void BtnExtenderSetup_Click(object sender, EventArgs e) {
            if (m_device != null) {
                MessageBox.Show("Device is already broadcasting!");
                return;
            }

            m_device = new ExtenderDevice(m_logger);
            m_device.Start();

            MessageBox.Show("SoftSled is broadcasting! Use the key 1234-3706 to pair the device");
        }

        #region RDPClient ActiveX Events ######################################

        private void InitialiseRdpClient() {

            // Add EventHandlers
            rdpClient.OnConnected += new EventHandler(RdpClient_OnConnected);
            rdpClient.OnDisconnected += new AxMSTSCLib.IMsTscAxEvents_OnDisconnectedEventHandler(RdpClient_OnDisconnected);

            // Set Port
            rdpClient.AdvancedSettings3.RDPPort = 3390;
            //rdpClient.AdvancedSettings3.RDPPort = 3389;
            rdpClient.AdvancedSettings7.PluginDlls = "RDPVCManager.dll";
            rdpClient.AdvancedSettings7.RedirectClipboard = false;
            rdpClient.AdvancedSettings7.RedirectPrinters = false;
            //rdpClient.ColorDepth = 16;


            // McxSess - Used by McrMgr for Extender Session Control
            // MCECaps - not known where used
            // devcaps - Used by EhShell to determine Extender capabilities
            // avctrl - Used for AV Signalling
            // VCHD - something to do with av signalling
            // splash - appears to be used with both the RUI and BIG DevCaps options, but only when both capabilities are enabled (Big-Endian Remote Rendering). Likely no use for this project.

            // NOTICE, if you want ehshell.exe to start up in normal Remote Desktop mode, remove the devcaps channel definition bellow. 
            //rdpClient.CreateVirtualChannels("McxSess,MCECaps,avctrl,VCHD");
            //rdpClient.CreateVirtualChannels("McxSess,MCECaps,devcaps,avctrl,VCHD");
            //rdpClient.CreateVirtualChannels("McxSess,MCECaps,devcaps,avctrl,VCHD,splash");

            // Set RDP Initialised
            rdpInitialised = true;
        }

        void RdpClient_OnDisconnected(object sender, AxMSTSCLib.IMsTscAxEvents_OnDisconnectedEvent e) {
            btnConnect.IsEnabled = true;
            btnDisconnect.IsEnabled = false;

            //// Stop playing Media
            //_mp.Stop();

            m_logger.LogInfo($"RDP: Disconnected ({e.discReason})");
            if (isConnecting == true) {
                //SetStatus("Forcibly disconnected from Remote Desktop Host");
                isConnecting = false;
            }

        }

        void RdpClient_OnConnected(object sender, EventArgs e) {
            m_logger.LogInfo("RDP: Connected");
            //SetStatus("Remote Desktop Connected! Waiting for Media Center...");

            btnConnect.IsEnabled = false;
            btnDisconnect.IsEnabled = true;
        }

        #endregion ############################################################


        // --- Event Handlers ---

        private void RdpClient_OnLoginComplete(object sender, EventArgs e) {
            // Handle successful login (runs on UI thread)
            System.Diagnostics.Debug.WriteLine("RDP Login Complete.");
        }

        //private void RdpClient_OnDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e) {
        //    // Handle disconnection (runs on UI thread)
        //    // e.discReason provides details about why the disconnect happened
        //    MessageBox.Show($"Disconnected from RDP session. Reason code: {e.discReason}");
        //    System.Diagnostics.Debug.WriteLine($"RDP Disconnected. Reason: {e.discReason}");
        //}


        // --- Cleanup ---

        private void MainWindow_Closed(object sender, EventArgs e) {
            // Ensure disconnection and proper disposal on window close
            DisconnectRdp();

            if (this.rdpClient != null) {
                // Unsubscribe from events to prevent memory leaks
                this.rdpClient.OnDisconnected -= RdpClient_OnDisconnected;
                this.rdpClient.OnLoginComplete -= RdpClient_OnLoginComplete;
                // Unsubscribe others...

                this.rdpClient.Dispose(); // Dispose the ActiveX control
                this.rdpClient = null;
            }

            if (this.rdpHost != null) {
                this.rdpHost.Dispose(); // Dispose the host control
                this.rdpHost = null;
            }
        }

        private void btnPair_Click(object sender, RoutedEventArgs e) {

        }

        delegate void dRdpClientVisible(bool show);
        void SetRdpClientVisible(bool show) {
            if (!Dispatcher.CheckAccess()) {
                dRdpClientVisible d = new dRdpClientVisible(SetRdpClientVisible);
                Dispatcher.Invoke(d, new object[] { show });
            } else {
                rdpClient.Visible = show;
            }
        }


        //private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        //    //// Delay the call slightly using the dispatcher
        //    //// Use Loaded priority first, if that still fails, try Background
        //    //Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
        //    //    new Action(() => ApplyTransparencyToHostedControl()));
        //}

        //// You could also call this from a Button Click event handler
        //private void ApplyTransparencyToHostedControl() {
        //    if (testPanel == null || !testPanel.IsHandleCreated) {
        //        MessageBox.Show("Hosted control or its handle is not ready yet.");
        //        return;
        //    }



        //    IntPtr hwnd = testPanel.Handle;
        //    if (hwnd == IntPtr.Zero) {
        //        MessageBox.Show("Failed to get HWND handle for the hosted control.");
        //        return;
        //    }

        //    System.Diagnostics.Debug.WriteLine($"ApplyTransparency - HWND: {hwnd.ToInt64()}, IsHandleCreated: {testPanel.IsHandleCreated}"); // Debug output

        //    try {

        //        // 1. Get current extended window styles USING THE HELPER
        //        IntPtr currentExStyle = NativeMethods.GetWindowLongPtrHelper(hwnd, NativeMethods.GWL_EXSTYLE); // Use Helper!

        //        // Check for error from GetWindowLongPtrHelper (optional but good)
        //        if (currentExStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) {
        //            MessageBox.Show($"Failed to get window extended style. Error code: {Marshal.GetLastWin32Error()}");
        //            return;
        //        }

        //        // 2. Add the WS_EX_LAYERED style
        //        IntPtr newExStyle = new IntPtr(currentExStyle.ToInt64() | NativeMethods.WS_EX_LAYERED);

        //        // 3. Set the new extended window styles USING THE HELPER
        //        IntPtr resultSetStyle = NativeMethods.SetWindowLongPtrHelper(hwnd, NativeMethods.GWL_EXSTYLE, newExStyle); // Use Helper!
        //        if (resultSetStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) {
        //            MessageBox.Show($"Failed to set WS_EX_LAYERED style. Error code: {Marshal.GetLastWin32Error()}");
        //            return;
        //        }

        //        IntPtr checkExStyle = NativeMethods.GetWindowLongPtrHelper(hwnd, NativeMethods.GWL_EXSTYLE);
        //        if ((checkExStyle.ToInt64() & NativeMethods.WS_EX_LAYERED) == 0) {
        //            MessageBox.Show("Error: WS_EX_LAYERED style check failed AFTER setting it.");
        //            return; // Don't proceed if style isn't confirmed
        //        }
        //        System.Diagnostics.Debug.WriteLine("Style check PASSED. WS_EX_LAYERED is present.");
        //        // *** END VERIFICATION STEP ***

        //        //// 1. Get current extended window styles
        //        //IntPtr currentExStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);

        //        //// 2. Add the WS_EX_LAYERED style
        //        //IntPtr newExStyle = new IntPtr(currentExStyle.ToInt64() | NativeMethods.WS_EX_LAYERED);

        //        //// 3. Set the new extended window styles
        //        //IntPtr resultSetStyle = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, newExStyle);
        //        //if (resultSetStyle == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) {
        //        //    MessageBox.Show($"Failed to set WS_EX_LAYERED style. Error code: {Marshal.GetLastWin32Error()}");
        //        //    return;
        //        //}

        //        // 4. Define the color key (e.g., Magenta)
        //        System.Drawing.Color keyColor = System.Drawing.Color.Black; // Change this to your desired transparent color
        //        uint keyColorWin32 = NativeMethods.ToWin32Color(keyColor);

        //        // 5. Set the layered window attributes for color keying
        //        bool setResult = NativeMethods.SetLayeredWindowAttributes(hwnd, keyColorWin32, 0, NativeMethods.LWA_COLORKEY);
        //        if (!setResult) {
        //            MessageBox.Show($"Failed to set layered window attributes. Error code: {Marshal.GetLastWin32Error()}");
        //        } else {
        //            // Optional: Indicate success if desired
        //             MessageBox.Show("Transparency Key Applied Successfully!");
        //        }
        //    } catch (Exception ex) {
        //        MessageBox.Show($"An error occurred: {ex.Message}");
        //    }
        //}


        //private void SetupRdpControl() {

        //    //testPanel = new System.Windows.Forms.Panel();
        //    //testPanel.BackColor = System.Drawing.Color.Blue; // Make it visible
        //    ////testPanel.Dock = DockStyle.Fill; // Test docking too

        //    //wfHost.Child = testPanel; // Assign the PANEL as the child


        //    //rdpControl = new AxMsRdpClient7NotSafeForScripting();

        //    //// Initialize the ActiveX control if needed (often done automatically)
        //    //((System.ComponentModel.ISupportInitialize)(rdpControl)).BeginInit();
        //    //rdpControl.Enabled = true;
        //    //// Assign the ActiveX wrapper control to the WindowsFormsHost
        //    //wfHost.Child = rdpControl;

        //    //((System.ComponentModel.ISupportInitialize)(rdpControl)).EndInit();


        //    //// Initial size sync (optional but can help)
        //    //SynchronizeRdpSize(wfHost, wfHost.RenderSize);

        //    //// Initialize RDP settings etc.
        //    //rdpControl.Server = "10.1.1.33";
        //    //rdpControl.UserName = "BradNet-Admin";
        //    //rdpControl.AdvancedSettings7.ClearTextPassword = "nesjeorithsA1";
        //    //rdpControl.AdvancedSettings7.SmartSizing = true;

        //    //rdpControl.Connect();
        //}


        //private void WfHost_SizeChanged(object sender, SizeChangedEventArgs e) {
        //    SynchronizeRdpSize(sender as System.Windows.Forms.Integration.WindowsFormsHost, e.NewSize);
        //}

        //private void SynchronizeRdpSize(System.Windows.Forms.Integration.WindowsFormsHost host, Size newSize) {
        //    if (host?.Child is AxMsRdpClient7NotSafeForScripting childControl) {
        //        // Check for valid size (prevents issues during load/unload)
        //        if (newSize.Width > 0 && newSize.Height > 0 &&
        //            IsFinite(newSize.Width) && IsFinite(newSize.Height)) {
        //            // Set the size explicitly
        //            childControl.Width = (int)newSize.Width;
        //            childControl.Height = (int)newSize.Height;

        //            // -- Optional: Use BeginInvoke for potential timing issues --
        //            // If the direct setting still seems off, sometimes marshalling
        //            // the call to the WinForms UI thread helps.
        //            // childControl.BeginInvoke(new Action(() => {
        //            //     if (childControl.IsHandleCreated && !childControl.IsDisposed)
        //            //     {
        //            //        childControl.Width = (int)newSize.Width;
        //            //        childControl.Height = (int)newSize.Height;
        //            //     }
        //            // }));
        //        }
        //    }
        //}

        //// Helper to check for valid finite numbers
        //private bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        //// --- Remember to unsubscribe when the window closes ---
        //protected override void OnClosed(EventArgs e) {
        //    if (wfHost != null) {
        //        wfHost.SizeChanged -= WfHost_SizeChanged;
        //        // Dispose child properly
        //        wfHost.Child?.Dispose();
        //        wfHost.Dispose();
        //    }
        //    base.OnClosed(e);
        //}

    }
}
