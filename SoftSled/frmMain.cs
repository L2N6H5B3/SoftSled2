using LibVLCSharp.Shared;
using SoftSled.Components;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace SoftSled {
    public partial class FrmMain : Form {
        // Private members
        private Logger m_logger;
        private ExtenderDevice m_device;
        private bool isConnecting = false;
        private bool rdpInitialised = false;

        private VirtualChannelAvCtrlHandler AvCtrlHandler;
        private VirtualChannelDevCapsHandler DevCapsHandler;
        private VirtualChannelMcxSessHandler McxSessHandler;

        public LibVLC _libVLC;
        public MediaPlayer _mp;

        public FrmMain() {
            if (!DesignMode) {
                Core.Initialize();
            }

            InitializeComponent();
            _libVLC = new LibVLC();
            _mp = new MediaPlayer(_libVLC);
            videoView1.MediaPlayer = _mp;
        }

        #region Main GUI Commands #############################################

        private void FrmMain_Load(object sender, EventArgs e) {
            InitialiseLogger();

            // Configure Buttons
            btnExtenderDisconnect.Enabled = false;

            // Create VirtualChannel Handlers
            AvCtrlHandler = new VirtualChannelAvCtrlHandler(m_logger, rdpClient, _libVLC, _mp);
            DevCapsHandler = new VirtualChannelDevCapsHandler(m_logger, rdpClient);
            McxSessHandler = new VirtualChannelMcxSessHandler(m_logger, rdpClient);

            // Create VirtualChannel Handlers EventHandlers
            McxSessHandler.StatusChanged += McxSessHandler_StatusChanged;

            m_logger.LogInfo("Open SoftSled (http://github.com/l2n6h5b3/SoftSled2)");

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            if (!config.IsPaired) {
                m_logger.LogInfo("Extender is not paired!");
                SetStatus("Extender is not paired");
            } else {
                m_logger.LogInfo("Extender is paired with " + config.RdpLoginHost);
                SetStatus("Extender ready to connect");
            }
        }

        private void McxSessHandler_StatusChanged(object sender, StatusChangedArgs e) {
            // Set Status
            SetStatus(e.statusText);

            // If the Shell is open
            if (e.shellOpen) {
                panOverlay.Visible = false;
                rdpClient.Visible = true;
                // Play Opening Music
                PlayOpening();
            } else if (e.shellOpen && rdpClient.Visible == true) {
                panOverlay.Visible = false;
                rdpClient.Visible = true;
            } else {
                panOverlay.Visible = true;
                rdpClient.Visible = false;
            }
        }

        private void btnExtenderConnect_Click(object sender, EventArgs e) {

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

            txtLog.Text = "";

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
            // Connect RDP
            rdpClient.Connect();

            SetStatus("Remote Desktop Connecting...");
            isConnecting = true;

        }

        private void BtnExtenderDisconnect_Click(object sender, EventArgs e) {
            if (m_device != null) {
                m_device.Stop();
                if (rdpClient.Connected == 1)
                    rdpClient.Disconnect();
            }
            m_device = null;
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

        void InitialiseLogger() {
            // For now simply hardcode the logger.
            m_logger = new TextBoxLogger(txtLog, this);
            m_logger.IsLoggingDebug = true;
        }

        #endregion ############################################################


        #region RDPClient ActiveX Events ######################################

        private void InitialiseRdpClient() {

            // Add EventHandlers
            rdpClient.OnConnected += new EventHandler(RdpClient_OnConnected);
            rdpClient.OnDisconnected += new AxMSTSCLib.IMsTscAxEvents_OnDisconnectedEventHandler(RdpClient_OnDisconnected);
            rdpClient.OnChannelReceivedData += new AxMSTSCLib.IMsTscAxEvents_OnChannelReceivedDataEventHandler(RdpClient_OnChannelReceivedData);

            // Set Port
            rdpClient.AdvancedSettings3.RDPPort = 3390;

            // McxSess - Used by McrMgr for Extender Session Control
            // MCECaps - not known where used
            // devcaps - Used by EhShell to determine Extender capabilities
            // avctrl - Used for AV Signalling
            // VCHD - something to do with av signalling

            // NOTICE, if you want ehshell.exe to start up in normal Remote Desktop mode, remove the devcaps channel definition bellow. 
            //rdpClient.CreateVirtualChannels("McxSess,MCECaps,avctrl,VCHD");
            //rdpClient.CreateVirtualChannels("McxSess,MCECaps,devcaps,avctrl,VCHD");

            // Create Virtual Channels
            rdpClient.CreateVirtualChannels("McxSess,devcaps,avctrl");

            // Set RDP Initialised
            rdpInitialised = true;
        }

        void RdpClient_OnChannelReceivedData(object sender, AxMSTSCLib.IMsTscAxEvents_OnChannelReceivedDataEvent e) {
            try {

                if (e.chanName == "avctrl") {
                    AvCtrlHandler.ProcessData(e.data);
                } else if (e.chanName == "devcaps") {
                    DevCapsHandler.ProcessData(e.data);
                } else if (e.chanName == "McxSess") {
                    McxSessHandler.ProcessData(e.data);
                } else {
                    MessageBox.Show("unhandled data on channel " + e.chanName);
                    m_logger.LogDebug($"{e.chanName} Bytes: " + BitConverter.ToString(Encoding.Unicode.GetBytes(e.data)));
                }

            } catch (Exception ee) {
                MessageBox.Show(ee.Message + " " + ee.StackTrace);
            }
        }


        void RdpClient_OnDisconnected(object sender, AxMSTSCLib.IMsTscAxEvents_OnDisconnectedEvent e) {

            btnDoExtenderConnect.Enabled = true;
            btnExtenderDisconnect.Enabled = false;

            // Stop playing Media
            _mp.Stop();

            m_logger.LogInfo($"RDP: Disconnected ({e.discReason})");
            if (isConnecting == true) {
                SetStatus("Forcibly disconnected from Remote Desktop Host");
                isConnecting = false;
            }

        }

        void RdpClient_OnConnected(object sender, EventArgs e) {
            m_logger.LogInfo("RDP: Connected");
            SetStatus("Remote Desktop Connected! Waiting for Media Center...");

            btnDoExtenderConnect.Enabled = false;
            btnExtenderDisconnect.Enabled = true;

        }

        #endregion ############################################################


        #region Misc Form Events ##############################################

        private void lnkGiveFocus_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            rdpClient.Focus();
        }

        private void lnkSendCtrlAltDelete_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            // This doesn't seem to be working...
            rdpClient.Focus();
            SendKeys.SendWait("{BACKSPACE}");
            //SendKeys.Send("%^+{END}");
        }

        private void chkLogDebug_CheckedChanged(object sender, EventArgs e) {
            m_logger.IsLoggingDebug = chkLogDebug.Checked;
        }

        private void lnkShowCtrlHideInfo_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            rdpClient.Visible = true;
            panOverlay.Visible = false;
        }

        delegate void dTextWrite(string message);
        void SetStatus(string message) {
            Invoke(new dTextWrite(delegate (string ex) {
                lbGenStatus.Text = ex;
                if (!lbGenStatus.Visible)
                    lbGenStatus.Visible = true;
            }), message);
        }

        private void PlayOpening() {
            string audioFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Resources", "audio", "intro_a.wav");
            System.Media.SoundPlayer player = new System.Media.SoundPlayer(audioFile);
            player.Play();
        }

        #endregion ############################################################

    }
}