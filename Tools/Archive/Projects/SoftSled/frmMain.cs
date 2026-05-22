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

        private RDPVCInterface rdpVCInterface;

        private VirtualChannelMceCapsSender MceCapsHandler;
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

            // Create RDPVCInterface to handle Virtual Channel Communications
            rdpVCInterface = new RDPVCInterface(m_logger);
            rdpVCInterface.DataReceived += RdpVCInterface_DataReceived;

            // Configure Buttons
            btnExtenderDisconnect.Enabled = false;

            // Create VirtualChannel Handlers
            MceCapsHandler = new VirtualChannelMceCapsSender(m_logger);
            McxSessHandler = new VirtualChannelMcxSessHandler(m_logger);
            DevCapsHandler = new VirtualChannelDevCapsHandler(m_logger);
            AvCtrlHandler = new VirtualChannelAvCtrlHandler(m_logger, _libVLC, _mp, axWindowsMediaPlayer1);
            MceCapsHandler.VirtualChannelSend += On_VirtualChannelSend;
            McxSessHandler.VirtualChannelSend += On_VirtualChannelSend;
            DevCapsHandler.VirtualChannelSend += On_VirtualChannelSend;
            AvCtrlHandler.VirtualChannelSend += On_VirtualChannelSend;

            // Create VirtualChannel Handlers EventHandlers
            McxSessHandler.StatusChanged += McxSessHandler_StatusChanged;


            // TESTING Media Player

            //axWindowsMediaPlayer1.EndOfStream += AxWindowsMediaPlayer1_EndOfStream;
            //axWindowsMediaPlayer1.MediaError += AxWindowsMediaPlayer1_MediaError;
            //axWindowsMediaPlayer1.Disconnect += AxWindowsMediaPlayer1_Disconnect;
            //axWindowsMediaPlayer1.PlayStateChange += AxWindowsMediaPlayer1_PlayStateChange;
            //axWindowsMediaPlayer1.Buffering += AxWindowsMediaPlayer1_Buffering;


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

        private void AxWindowsMediaPlayer1_Buffering(object sender, AxWMPLib._WMPOCXEvents_BufferingEvent e) {
            if (!e.start) {
                AvCtrlHandler.OnMediaEvent(MediaEvent.BUFFERING_STOP);
            }
        }

        private void AxWindowsMediaPlayer1_PlayStateChange(object sender, AxWMPLib._WMPOCXEvents_PlayStateChangeEvent e) {
            if (e.newState == 8) {
                AvCtrlHandler.OnMediaEvent(MediaEvent.END_OF_MEDIA);
            }
        }

        private void AxWindowsMediaPlayer1_ErrorEvent(object sender, EventArgs e) {
            AvCtrlHandler.OnMediaEvent(MediaEvent.DRM_LICENSE_ERROR);
        }

        private void AxWindowsMediaPlayer1_Disconnect(object sender, AxWMPLib._WMPOCXEvents_DisconnectEvent e) {
            AvCtrlHandler.OnMediaEvent(MediaEvent.RTSP_DISCONNECT);
        }

        private void AxWindowsMediaPlayer1_MediaError(object sender, AxWMPLib._WMPOCXEvents_MediaErrorEvent e) {
            AvCtrlHandler.OnMediaEvent(MediaEvent.DRM_LICENSE_ERROR);
        }

        private void AxWindowsMediaPlayer1_EndOfStream(object sender, AxWMPLib._WMPOCXEvents_EndOfStreamEvent e) {
            AvCtrlHandler.OnMediaEvent(MediaEvent.END_OF_MEDIA);
        }

        private void On_VirtualChannelSend(object sender, VirtualChannelSendArgs e) {
            rdpVCInterface.SendOnVirtualChannel(e.channelName, e.data);
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

        private void McxSessHandler_StatusChanged(object sender, StatusChangedArgs e) {

            // Set Status
            SetStatus(e.statusText);

            // If the Shell is open
            if (e.shellOpen) {
                SetPanOverlayVisible(false);
                SetRdpClientVisible(true);
                // Play Opening Music
                PlayOpening();
            } else if (e.shellOpen && rdpClient.Visible == true) {
                SetPanOverlayVisible(false);
                SetRdpClientVisible(true);
            } else {
                SetPanOverlayVisible(true);
                SetRdpClientVisible(false);
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
            rdpClient.AdvancedSettings7.AudioRedirectionMode = 0;
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

        delegate void dPanOverlay(bool show);
        void SetPanOverlay(string message) {
            Invoke(new dPanOverlay(delegate (bool ex) {
                panOverlay.Visible = ex;
            }), message);
        }

        delegate void dPanOverlayVisible(bool show);
        void SetPanOverlayVisible(bool show) {
            Invoke(new dPanOverlayVisible(delegate (bool ex) {
                panOverlay.Visible = ex;
            }), show);
        }

        delegate void dRdpClientVisible(bool show);
        void SetRdpClientVisible(bool show) {
            Invoke(new dRdpClientVisible(delegate (bool ex) {
                rdpClient.Visible = ex;
            }), show);
        }

        private void PlayOpening() {
            //string audioFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Resources", "audio", "intro_a.wav");
            //System.Media.SoundPlayer player = new System.Media.SoundPlayer(audioFile);
            //player.Play();
        }

        #endregion ############################################################

        private void button1_Click(object sender, EventArgs e) {
            MceCapsHandler.ConfigureDSPA();
        }
    }
}