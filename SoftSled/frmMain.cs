using LibVLCSharp.Shared;
using Rtsp;
using SoftSled.Components;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;

namespace SoftSled {
    public partial class FrmMain : Form {
        // Private members
        private Logger m_logger;
        private ExtenderDevice m_device;
        private McxVirtualChannelHandler m_channelHandler;
        private int devCapsIter;
        private int mcxSessIter;
        private bool isConnecting = false;
        readonly FileStream writer;
        private static RtspListener rtsp_client;
        private bool rdpInitialised = false;
        private string rtspUrl;
        private TcpClient rtspClient;

        // McxSess Virtual Channel
        private int DSMNServiceHandle;

        // AvCtrl Virtual Channel
        private int DMCTServiceHandle;
        private int DSPAServiceHandle;
        private int DRMRIServiceHandle;
        private int DMCTRegisterMediaEventCallbackCookie;
        private string DMCTOpenMediaURL;

        private Media currentMedia;

        private string vChanRootDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\VChan\\";

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

        #region Main GUI Commands
        private void FrmMain_Load(object sender, EventArgs e) {
            InitialiseLogger();

            m_logger.LogInfo("OpenSoftSled (http://github.com/l2n6h5b3/SoftSled2)");

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            if (!config.IsPaired) {
                m_logger.LogInfo("Extender is not paired!");
                SetStatus("Extender is not paired");
            } else {
                m_logger.LogInfo("Extender is paired with " + config.RdpLoginHost);
                SetStatus("Extender ready to connect");
            }

        }


        private void btnExtenderConnect_Click(object sender, EventArgs e) {

            // Reset Iteration Variables
            devCapsIter = 1;
            mcxSessIter = 1;

            IPAddress localhost = null;
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList) {
                if (ip.AddressFamily == AddressFamily.InterNetwork) {
                    localhost = ip;
                } else {
                    //throw new Exception("No network adapters with an IPv4 address in the system!");
                }
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
            devCapsIter = 1;

            m_device = new ExtenderDevice(m_logger);
            m_device.Start();

            m_channelHandler = new McxVirtualChannelHandler();

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

            //TcpListener tcp1 = new TcpListener(localhost, 3776);
            //TcpListener tcp2 = new TcpListener(localhost, 3777);
            //TcpListener tcp3 = new TcpListener(localhost, 3778);
            //TcpListener tcp4 = new TcpListener(localhost, 2177);

            //new Thread(new ParameterizedThreadStart(Listen)).Start(tcp1);
            //new Thread(new ParameterizedThreadStart(Listen)).Start(tcp2);
            //new Thread(new ParameterizedThreadStart(Listen)).Start(tcp3);
            //new Thread(new ParameterizedThreadStart(Listen)).Start(tcp4);

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
            // Set Start Program
            rdpClient.SecuredSettings.StartProgram = "%windir%\\ehome\\ehshell.exe";

            // McxSess - used by mcrmgr
            // MCECaps - not known where used
            // devcaps - used by ehshell to determine extender capabilities
            // avctrl - used for av signalling
            // VCHD - something to do with av signalling

            // NOTICE, if you want ehshell.exe to start up in normal Remote Desktop mode, remove the devcaps channel definition bellow. 
            //rdpClient.CreateVirtualChannels("McxSess,MCECaps,avctrl,VCHD");

            // Create Virtual Channels
            rdpClient.CreateVirtualChannels("McxSess,MCECaps,devcaps,avctrl,VCHD");

            // Set RDP Initialised
            rdpInitialised = true;
        }

        void RdpClient_OnChannelReceivedData(object sender, AxMSTSCLib.IMsTscAxEvents_OnChannelReceivedDataEvent e) {
            try {
                if (chkInVchanDebug.Checked && e.chanName != "McxSess")
                    m_logger.LogInfo("RDP: Received data on channel " + e.chanName);


                if (e.chanName == "devcaps") {
                    HandleDevCapsIncoming(e);
                } else if (e.chanName == "McxSess") {
                    HandleMcxSessIncoming(e.data);
                } else if (e.chanName == "avctrl") {
                    HandleAvctrlIncoming(e.data);
                } else {
                    MessageBox.Show("unhandled data on channel " + e.chanName);
                }


            } catch (Exception ee) {
                MessageBox.Show(ee.Message + " " + ee.StackTrace);
            }

        }
        private static void DumpIncoming(string data, string chan) {
            string outFileName = Microsoft.VisualBasic.Interaction.InputBox("Enter filename for vchannel dump on channel " + chan, "", "", 400, 400);

            if (!String.IsNullOrEmpty(outFileName)) {
                using (FileStream fs = File.Create(outFileName)) {
                    byte[] buff = Encoding.Unicode.GetBytes(data);
                    fs.Write(buff, 0, buff.Length);
                }
            }

        }
        void RdpClient_OnDisconnected(object sender, AxMSTSCLib.IMsTscAxEvents_OnDisconnectedEvent e) {

            // Reset Iteration Variables
            devCapsIter = 1;
            mcxSessIter = 1;

            m_logger.LogInfo("RDP: Disconnected");
            if (isConnecting == true) {
                SetStatus("Forcibly disconnected from Remote Desktop Host");
                isConnecting = false;
            }

        }
        void RdpClient_OnConnected(object sender, EventArgs e) {
            m_logger.LogInfo("RDP: Connected");
            SetStatus("Remote Desktop Connected! Waiting for Media Center...");
        }
        #endregion


        #region VirtualChannel Handlers
        private void HandleMcxSessIncoming(string data) {

            // Convert the incoming data to bytes
            byte[] incomingBuff = Encoding.Unicode.GetBytes(data);

            // Get DSLR Dispatcher Data
            int dispatchPayloadSize = Get4ByteInt(incomingBuff, 0);
            int dispatchChildCount = Get2ByteInt(incomingBuff, 4);
            bool dispatchIsTwoWay = (incomingBuff[6] + incomingBuff[7] + incomingBuff[8] + incomingBuff[9]) == 1 ? true : false;
            int dispatchRequestHandle = Get4ByteInt(incomingBuff, 10);
            int dispatchServiceHandle = Get4ByteInt(incomingBuff, 14);
            int dispatchFunctionHandle = Get4ByteInt(incomingBuff, 18);

            // DEBUG PURPOSES ONLY
            string incomingByteArray = "";
            foreach (byte b in incomingBuff) {
                incomingByteArray += b.ToString("X2") + " ";
            }
            // DEBUG PURPOSES ONLY

            // Service Handle = Dispenser
            if (dispatchServiceHandle == 0) {

                #region DSLR Service ##########################################

                // CreateService Request
                if (dispatchFunctionHandle == 0) {

                    // Get CreateService Data
                    int createServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int createServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid createServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid createServiceServiceID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int createServiceServiceHandle = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("MCXSESS: Request CreateService " + createServiceServiceHandle);

                    switch (createServiceClassID.ToString()) {
                        // DSMN ClassID
                        case "a30dc60e-1e2c-44f2-bfd1-17e51c0cdf19":
                            DSMNServiceHandle = createServiceServiceHandle;
                            // Set the Status to Starting Experience
                            SetStatus("Starting Experience...");
                            break;
                    }

                    // Initialise CreateService Response
                    byte[] response = VChan.DSLR.CreateServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the CreateService Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response CreateService " + dispatchRequestHandle);
                }
                // DeleteService Request
                else if (dispatchFunctionHandle == 2) {

                    // Get DeleteService Data
                    int deleteServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int deleteServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid deleteServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid deleteServiceServiceID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int deleteServiceServiceHandle = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("MCXSESS: Request DeleteService " + deleteServiceServiceHandle);


                    // Send the DeleteService Response
                    //rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(response));

                    m_logger.LogDebug("MCXSESS: Sent Response DeleteService " + dispatchRequestHandle);
                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            }
            // DSMN Service Handle
            else if (dispatchServiceHandle == DSMNServiceHandle) {

                #region DSMN Service ##########################################

                // ShellDisconnect Request
                if (dispatchFunctionHandle == 0) {

                    m_logger.LogDebug("MCXSESS: Request ShellDisconnect " + dispatchServiceHandle);

                    // Get ShellDisconnect Data
                    int ShellDisconnectPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int ShellDisconnectChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int ShellDisconnectPayloadDisconnectReason = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    // Set status according to Disconnect Reason
                    switch (ShellDisconnectPayloadDisconnectReason) {
                        case 0:
                            SetStatus("Disconnected: Shell exited unexpectedly");
                            break;
                        case 1:
                            SetStatus("Disconnected: Unknown error");
                            break;
                        case 2:
                            SetStatus("Disconnected: Initialisation error");
                            break;
                        case 3:
                            SetStatus("Disconnected: Shell is not responding");
                            break;
                        case 4:
                            SetStatus("Disconnected: Unauthorised UI in the session");
                            break;
                        case 5:
                            SetStatus("Disconnected: User is not allowed - the remote device was disabled on the host");
                            break;
                        case 6:
                            SetStatus("Disconnected: Certificate is invalid");
                            break;
                        case 7:
                            SetStatus("Disconnected: Shell cannot be started");
                            break;
                        case 8:
                            SetStatus("Disconnected: Shell monitor thread cannot be started");
                            break;
                        case 9:
                            SetStatus("Disconnected: Message window cannot be created");
                            break;
                        case 10:
                            SetStatus("Disconnected: Terminal Services session cannot be started");
                            break;
                        case 11:
                            SetStatus("Disconnected: Plug and Play (PNP) failed");
                            break;
                        case 12:
                            SetStatus("Disconnected: Certificate is not trusted");
                            break;
                        case 13:
                            SetStatus("Disconnected: Product regstration is expired");
                            break;
                        case 14:
                            SetStatus("Disconnected: PC gone to Sleep / Shut Down");
                            break;
                        case 15:
                            SetStatus("Disconnected: User closed the session");
                            break;
                    }

                    // Hide RDP as WMC is not Running
                    panOverlay.Visible = true;
                    rdpClient.Visible = false;
                    m_logger.LogInfo("Experience closed");

                    // Initialise ShellDisconnect Response
                    byte[] response = VChan.DSLR.ShellDisconnectResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the ShellDisconnect Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response ShellDisconnect " + dispatchServiceHandle);

                }
                // ShellIsActive Request
                else if (dispatchFunctionHandle == 2) {

                    m_logger.LogDebug("MCXSESS: Request ShellIsActive " + dispatchServiceHandle);

                    SetStatus("");
                    // Show RDP as WMC is Started
                    panOverlay.Visible = false;
                    rdpClient.Visible = true;

                    // Initialise ShellIsActive Response
                    byte[] response = VChan.DSLR.ShellIsActiveResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the ShellIsActive Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response ShellIsActive " + dispatchServiceHandle);

                }
                // Heartbeat Request
                else if (dispatchFunctionHandle == 1) {

                    m_logger.LogDebug("MCXSESS: Request Heartbeat " + dispatchServiceHandle);

                    // Get Heartbeat Data
                    int HeartbeatPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int HeartbeatChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int HeartbeatPayloadScreensaverFlag = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    // Initialise Heartbeat Response
                    byte[] response = VChan.DSLR.HeartbeatResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the Heartbeat Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response Heartbeat " + dispatchServiceHandle);

                }
                // GetQWaveSinkInfo Request
                else if (dispatchFunctionHandle == 3) {

                    m_logger.LogDebug("MCXSESS: Request GetQWaveSinkInfo " + dispatchServiceHandle);

                    // Get GetQWaveSinkInfo Data
                    int GetQWaveSinkInfoPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int GetQWaveSinkInfoChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int GetQWaveSinkInfoPayloadScreensaverFlag = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    // Initialise GetQWaveSinkInfo Response
                    byte[] response = VChan.DSLR.GetQWaveSinkInfoResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the GetQWaveSinkInfo Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Response GetQWaveSinkInfo " + dispatchServiceHandle);

                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"Unknown DSMN Request {dispatchFunctionHandle} not implemented");

                    // Initialise Generic Response
                    byte[] response = VChan.DSLR.GenericResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the Generic Response
                    rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("MCXSESS: Sent Generic Response " + dispatchFunctionHandle);

                }

                #endregion ####################################################

            }
        }

        private void HandleDevCapsIncoming(AxMSTSCLib.IMsTscAxEvents_OnChannelReceivedDataEvent e) {
            byte[] vChanResponseBuff = null;

            if (devCapsIter == 1) {
                // The initial response data for the initialization process.
                vChanResponseBuff = LoadDevCapsVChan("Initial");
            } else {
                // For now, respond true to all capability requests except the capabilities in the white list. 
                byte[] vChanIncomingBuff = Encoding.Unicode.GetBytes(e.data);
                string capChar1 = Encoding.ASCII.GetString(vChanIncomingBuff, vChanIncomingBuff.Length - 2, 1).ToUpper();
                string capChar2 = Encoding.ASCII.GetString(vChanIncomingBuff, vChanIncomingBuff.Length - 1, 1).ToUpper();

                m_logger.LogDebug("Asked for capability: " + capChar1 + capChar2);

                List<String> disabledCaps = new List<string>();
                disabledCaps.Add("PH"); // PHO - Are advanced photo features allowed?
                //disabledCaps.Add("EX"); // EXT - Are Extender Settings allowed?
                disabledCaps.Add("MA"); // MAR - Are over-scan margins needed?
                disabledCaps.Add("PO"); // POP - Are Pop ups allowed?
                disabledCaps.Add("ZO"); // ZOM - Is video zoom mode allowed?
                disabledCaps.Add("NL"); // NLZ - Is nonlinear zoom supported?
                disabledCaps.Add("RS"); // RSZ - Is raw stretched zoom supported?
                disabledCaps.Add("WI"); // WID - Is wide screen enabled?
                disabledCaps.Add("H1"); // H10 - Is 10 feet help allowed? 
                disabledCaps.Add("WE"); // WEB - Is 10 feet web content allowed? 
                disabledCaps.Add("H0"); // H02 - Is 2 feet help allowed? 
                disabledCaps.Add("WE"); // WE2 - Is 2 feet web content allowed? 
                //disabledCaps.Add("AUD"); // AUD - Is audio allowed?
                disabledCaps.Add("AUR"); // AUR - Is audio Non WMP?
                disabledCaps.Add("AR"); // ARA - Is auto restart allowed?
                disabledCaps.Add("BL"); // BLB - Is black letters box needed?
                disabledCaps.Add("CC"); // CCC - Is CC rendered by the client?
                disabledCaps.Add("CR"); // CRC - Is CD burning allowed?
                disabledCaps.Add("CP"); // CPY - Is CD copying allowed?
                disabledCaps.Add("CD"); // CDA - Is CD playback allowed?
                disabledCaps.Add("CL"); // CLO - Is the close button shown?
                disabledCaps.Add("DR"); // DRC - Is DVD burning allowed?
                disabledCaps.Add("DV"); // DVD - Is DVD playback allowed?
                disabledCaps.Add("FP"); // FPD - Is FPD allowed?
                //disabledCaps.Add("GD"); // GDI - Is GDI renderer used?
                //disabledCaps.Add("HDV"); // HDV - Is HD content allowed?
                //disabledCaps.Add("HDN"); // HDN - Is HD content allowed by the network?
                //disabledCaps.Add("SD"); // SDN - Is SD content allowed by the network?
                //disabledCaps.Add("RE"); // REM - Is input treated as if from a remote?
                disabledCaps.Add("AN"); // ANI - Is intensive animation allowed?
                disabledCaps.Add("2D"); // 2DA - Is 2D animation allowed?
                disabledCaps.Add("HT"); // HTM - Is HTML supported?
                disabledCaps.Add("DE"); // DES - Is MCE a Windows shell?
                disabledCaps.Add("DO"); // DOC - Is my Documents populated?
                disabledCaps.Add("SC"); // SCR - Is a native screensaver required?
                disabledCaps.Add("ON"); // ONS - Is online spotlight allowed?
                //disabledCaps.Add("SU"); // SUP - Is RDP super bit allowed?
                disabledCaps.Add("BI"); // BIG - Is remote UI renderer big-endian?
                disabledCaps.Add("RU"); // RUI - Is remote UI rendering supported?
                disabledCaps.Add("SD"); // SDM - Is a screen data mode workaround needed?
                //disabledCaps.Add("TB"); // TBA - Is a Toolbar allowed?
                disabledCaps.Add("SY"); // SYN - Is transfer to a device allowed?
                disabledCaps.Add("AP"); // APP - Is tray applet allowed?
                disabledCaps.Add("TV"); // TVS - Is a TV skin used?
                disabledCaps.Add("SO"); // SOU - Is UI sound supported?
                //disabledCaps.Add("VID"); // VID - Is video allowed?
                disabledCaps.Add("W3"); // W32 - Is Win32 content allowed?
                disabledCaps.Add("WI"); // WIN - Is window mode allowed?
                disabledCaps.Add("VIZ"); // VIZ - Is WMP visualisation allowed?
                //disabledCaps.Add("VO"); // VOL - Is volume UI allowed?
                //disabledCaps.Add("MU"); // MUT - Is mute ui allowed?

                bool response = false;
                if (disabledCaps.Contains(capChar1 + capChar2))
                    vChanResponseBuff = LoadDevCapsVChan("Disabled");
                else {
                    vChanResponseBuff = LoadDevCapsVChan("Enabled");
                    response = true;
                }

                // We need to modify the sequencing integer inside the response.
                vChanResponseBuff[21] = Convert.ToByte(devCapsIter);

                m_logger.LogDebug("RDP: " + response.ToString().ToUpper() + " for capability " + capChar1 + capChar2);
            }

            rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(vChanResponseBuff));
            m_logger.LogDebug("RDP: Sent devcaps citeration " + devCapsIter.ToString());


            devCapsIter++;
        }

        private void HandleAvctrlIncoming(string data) {

            // Convert the incoming data to bytes
            byte[] incomingBuff = Encoding.Unicode.GetBytes(data);

            // Get DSLR Dispatcher Data
            int dispatchPayloadSize = Get4ByteInt(incomingBuff, 0);
            int dispatchChildCount = Get2ByteInt(incomingBuff, 4);
            bool dispatchIsTwoWay = (incomingBuff[6] + incomingBuff[7] + incomingBuff[8] + incomingBuff[9]) == 1 ? true : false;
            int dispatchRequestHandle = Get4ByteInt(incomingBuff, 10);
            int dispatchServiceHandle = Get4ByteInt(incomingBuff, 14);
            int dispatchFunctionHandle = Get4ByteInt(incomingBuff, 18);

            byte[] dispatchRequestHandleArray = GetByteSubArray(incomingBuff, 10, 4);

            
            // DEBUG PURPOSES ONLY
            Debug.WriteLine("");
            Debug.WriteLine("--------------------");
            Debug.WriteLine($"AVCTRL ITER RECEIVED: {dispatchRequestHandle}");
            Debug.WriteLine($"AVCTRL ITER BYTES RECEIVED: {dispatchRequestHandleArray[0]} {dispatchRequestHandleArray[1]} {dispatchRequestHandleArray[2]} {dispatchRequestHandleArray[3]}");
            Debug.WriteLine($"ServiceHandle: {dispatchServiceHandle}");
            Debug.WriteLine($"FunctionHandle: {dispatchFunctionHandle}");

            //// DEBUG PURPOSES ONLY
            //string byteArray = "";
            //foreach (byte b in incomingBuff) {
            //    byteArray += b.ToString("X2") + " ";
            //}
            //// DEBUG PURPOSES ONLY

            // Service Handle = Dispenser
            if (dispatchServiceHandle == 0) {

                #region DSLR Service ##########################################

                // CreateService Request
                if (dispatchFunctionHandle == 0) {

                    // Get CreateService Data
                    int createServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int createServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid createServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid createServiceServiceID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int createServiceServiceHandle = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("AVCTRL: Request CreateService " + createServiceServiceHandle);

                    switch (createServiceClassID.ToString()) {
                        // DMCT ClassID
                        case "18c7c708-c529-4639-a846-5847f31b1e83":
                            DMCTServiceHandle = createServiceServiceHandle;
                            break;
                        // DSPA ClassID
                        case "077bfd3a-7028-4913-bd14-53963dc37754":
                            DSPAServiceHandle = createServiceServiceHandle;
                            break;
                        // DRMRI ClassID
                        case "b707af79-ca99-42d1-8c60-469fe112001e":
                            DRMRIServiceHandle = createServiceServiceHandle;
                            break;
                        // DSMN ClassID
                        case "a30dc60e-1e2c-44f2-bfd1-17e51c0cdf19":
                            DSMNServiceHandle = createServiceServiceHandle;
                            break;
                    }

                    // Initialise CreateService Response
                    byte[] response = VChan.DSLR.CreateServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the CreateService Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response CreateService " + dispatchRequestHandle);
                }
                // DeleteService Request
                else if (dispatchFunctionHandle == 2) {

                    // Get DeleteService Data
                    int deleteServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int deleteServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid deleteServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid deleteServiceServiceID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                    int deleteServiceServiceHandle = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                    m_logger.LogDebug("AVCTRL: Request DeleteService " + deleteServiceServiceHandle);


                    // Send the DeleteService Response
                    //rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(response));

                    m_logger.LogDebug("AVCTRL: Sent Response DeleteService " + dispatchRequestHandle);
                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            }
            // DMCT Service Handle
            else if (dispatchServiceHandle == DMCTServiceHandle) {

                #region DMCT Service ##########################################

                // OpenMedia Request
                if (dispatchFunctionHandle == 0) {

                    // Get OpenMedia Data
                    int OpenMediaPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int OpenMediaChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int OpenMediaPayloadURLLength = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    string OpenMediaPayloadURL = GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, OpenMediaPayloadURLLength);
                    int OpenMediaPayloadSurfaceID = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + OpenMediaPayloadURLLength);
                    int OpenMediaPayloadTimeOut = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + OpenMediaPayloadURLLength + 4);

                    m_logger.LogDebug("AVCTRL: Request OpenMedia " + OpenMediaPayloadURL);

                    DMCTOpenMediaURL = OpenMediaPayloadURL;

                    System.Diagnostics.Debug.WriteLine(DMCTOpenMediaURL);

                    // Create Media Object
                    currentMedia = new Media(_libVLC, new Uri(DMCTOpenMediaURL));
                    currentMedia.Parse();

                    // Initialise OpenMedia Response
                    byte[] response = VChan.DSLR.OpenMediaResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // DEBUG PURPOSES ONLY
                    string byteArray = "";
                    foreach (byte b in response) {
                        byteArray += b.ToString("X2") + " ";
                    }
                    // DEBUG PURPOSES ONLY

                    // Send the SetDWORDProperty Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response OpenMedia " + DMCTOpenMediaURL);

                }
                // CloseMedia Request
                else if (dispatchFunctionHandle == 1) {

                    m_logger.LogDebug("AVCTRL: Request CloseMedia " + dispatchRequestHandle);

                    _mp.Pause();

                    // Initialise CloseMedia Response
                    byte[] response = VChan.DSLR.CloseMediaResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the CloseMedia Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response CloseMedia " + dispatchRequestHandle);

                }
                // Start Request
                else if (dispatchFunctionHandle == 2) {

                    // Get Start Data
                    int StartPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int StartChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    long StartPayloadStartTime = Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    long StartPayloadUseOptimisedPreroll = Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8);
                    int StartPayloadRequestedPlayRate = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8 + 8);
                    long StartPayloadAvailableBandwidth = Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8 + 8 + 4);

                    m_logger.LogDebug("AVCTRL: Request Start " + dispatchRequestHandle);

                    _mp.Play(currentMedia);

                    // Initialise Start Response
                    byte[] response = VChan.DSLR.StartResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        StartPayloadRequestedPlayRate
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the Start Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response Start " + dispatchRequestHandle);

                }
                // Pause Request
                else if (dispatchFunctionHandle == 3) {

                    m_logger.LogDebug("AVCTRL: Request Pause " + dispatchRequestHandle);

                    _mp.Pause();

                    // Initialise Pause Response
                    byte[] response = VChan.DSLR.PauseResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the Pause Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response Pause " + dispatchRequestHandle);

                }
                // Stop Request
                else if (dispatchFunctionHandle == 3) {

                    m_logger.LogDebug("AVCTRL: Request Stop " + dispatchRequestHandle);

                    _mp.Stop();

                    // Initialise Stop Response
                    byte[] response = VChan.DSLR.StopResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the Stop Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response Stop " + dispatchRequestHandle);

                }
                // GetDuration Request
                else if (dispatchFunctionHandle == 5) {

                    m_logger.LogDebug("AVCTRL: Request GetDuration " + dispatchRequestHandle);

                    long durationLongMili = Convert.ToInt64(currentMedia.Duration / 10);

                    // Initialise GetDuration Response
                    byte[] response = VChan.DSLR.GetDurationResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        durationLongMili
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the GetDuration Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response GetDuration " + durationLongMili);

                }
                // GetPosition Request
                else if (dispatchFunctionHandle == 6) {

                    m_logger.LogDebug("AVCTRL: Request GetPosition " + dispatchRequestHandle);

                    long positionLongMili = Convert.ToInt64(_mp.Time / 10);

                    Debug.WriteLine($"Position After:  {positionLongMili}");

                    // Initialise GetPosition Response
                    byte[] response = VChan.DSLR.GetPositionResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        positionLongMili
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);
                    // Send the GetPosition Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response GetPosition " + dispatchRequestHandle);
                }
                // RegisterMediaEventCallback Request
                else if (dispatchFunctionHandle == 8) {

                    // Get RegisterMediaEventCallback Data
                    int RegisterMediaEventCallbackPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int RegisterMediaEventCallbackChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid RegisterMediaEventCallbackClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    Guid RegisterMediaEventCallbackServiceID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);

                    m_logger.LogDebug("AVCTRL: Request RegisterMediaEventCallback " + dispatchRequestHandle);

                    DMCTRegisterMediaEventCallbackCookie = 14733;

                    // Initialise RegisterMediaEventCallback Response
                    byte[] response = VChan.DSLR.RegisterMediaEventCallbackResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        DMCTRegisterMediaEventCallbackCookie
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the RegisterMediaEventCallback Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response RegisterMediaEventCallback " + dispatchRequestHandle);

                }
                // UnregisterMediaEventCallback Request
                else if (dispatchFunctionHandle == 9) {

                    System.Diagnostics.Debug.WriteLine("UnregisterMediaEventCallback Request not implemented");

                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"Unknown DMCT Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            }
            // DSPA Service Handle
            else if (dispatchServiceHandle == DSPAServiceHandle) {

                #region DSPA Service ##########################################

                // GetStringProperty Request
                if (dispatchFunctionHandle == 0) {

                    // Get GetStringProperty Data
                    int GetStringPropertyPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int GetStringPropertyChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int GetStringPropertyPayloadLength = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    string GetStringPropertyPayloadPropertyName = GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetStringPropertyPayloadLength);

                    switch (GetStringPropertyPayloadPropertyName) {
                        // Property Bag Service
                        case "XspHostAddress":

                            m_logger.LogDebug("AVCTRL: Request GetStringProperty " + GetStringPropertyPayloadPropertyName);

                            // Initialise GetStringProperty Response
                            byte[] response = VChan.DSLR.GetStringPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4),
                                SoftSledConfigManager.ReadConfig().RdpLoginHost
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                            // Send the GetStringProperty Response
                            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                            m_logger.LogDebug("AVCTRL: Sent Response GetStringProperty " + GetStringPropertyPayloadPropertyName);

                            break;
                    }
                }
                // GetDWORDProperty Request
                else if (dispatchFunctionHandle == 2) {

                    // Get GetDWORDProperty Data
                    int GetDWORDPropertyPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int GetDWORDPropertyChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int GetDWORDPropertyPayloadLength = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    string GetDWORDPropertyPayloadPropertyName = GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetDWORDPropertyPayloadLength);

                    switch (GetDWORDPropertyPayloadPropertyName) {
                        case "IsMuted":

                            m_logger.LogDebug("AVCTRL: Request GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                            // Initialise GetDWORDProperty Response
                            byte[] isMutedResponse = VChan.DSLR.GetDWORDPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4),
                                0
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedIsMutedResponse = VChan.DSLR.Encapsulate(isMutedResponse);

                            // Send the GetDWORDProperty Response
                            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedIsMutedResponse));

                            m_logger.LogDebug("AVCTRL: Sent Response GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                            break;
                        case "Volume":

                            m_logger.LogDebug("AVCTRL: Request GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                            // Initialise GetDWORDProperty Response
                            byte[] volumeResponse = VChan.DSLR.GetDWORDPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4),
                                65535
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedVolumeResponse = VChan.DSLR.Encapsulate(volumeResponse);

                            // Send the GetDWORDProperty Response
                            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedVolumeResponse));

                            m_logger.LogDebug("AVCTRL: Sent Response GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                            break;
                    }
                }
                // SetDWORDProperty Request
                else if (dispatchFunctionHandle == 3) {

                    // Get SetDWORDProperty Data
                    int SetDWORDPropertyPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int SetDWORDPropertyChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    int SetDWORDPropertyPayloadLength = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                    string SetDWORDPropertyPayloadPropertyName = GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, SetDWORDPropertyPayloadLength);
                    int SetDWORDPropertyPayloadPropertyValue = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + SetDWORDPropertyPayloadLength);

                    switch (SetDWORDPropertyPayloadPropertyName) {
                        case "IsMuted":

                            m_logger.LogDebug("AVCTRL: Request SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                            // Initialise SetDWORDProperty Response
                            byte[] response = VChan.DSLR.SetDWORDPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4)
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                            // Send the SetDWORDProperty Response
                            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                            m_logger.LogDebug("AVCTRL: Sent Response SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                            break;
                    }
                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"Unknown DSPA Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            }
            // DRMRI Service Handle
            else if (dispatchServiceHandle == DRMRIServiceHandle) {

                #region DRMRI Service #########################################

                // RegisterTransmitterService Request
                if (dispatchFunctionHandle == 0) {

                    // Get RegisterTransmitterService Data
                    int RegisterTransmitterServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int RegisterTransmitterServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid RegisterTransmitterServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    m_logger.LogDebug("AVCTRL: Request RegisterTransmitterService " + dispatchRequestHandle);

                    // Initialise RegisterTransmitterService Response
                    byte[] response = VChan.DSLR.RegisterTransmitterServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the RegisterTransmitterService Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response RegisterTransmitterService " + dispatchRequestHandle);

                }
                // UnregisterTransmitterService Request
                else if (dispatchFunctionHandle == 1) {

                    // Get UnregisterTransmitterService Data
                    int RegisterTransmitterServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int RegisterTransmitterServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                    Guid RegisterTransmitterServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                    m_logger.LogDebug("AVCTRL: Request UnregisterTransmitterService " + dispatchRequestHandle);

                    // Initialise UnregisterTransmitterService Response
                    byte[] response = VChan.DSLR.UnregisterTransmitterServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the UnregisterTransmitterService Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response UnregisterTransmitterService " + dispatchRequestHandle);

                }
                // InitiateRegistration Request
                else if (dispatchFunctionHandle == 2) {

                    // Get InitiateRegistration Data
                    int InitiateRegistrationPayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                    int InitiateRegistrationChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);

                    m_logger.LogDebug("AVCTRL: Request InitiateRegistration " + dispatchRequestHandle);

                    // Initialise InitiateRegistration Response
                    byte[] response = VChan.DSLR.InitiateRegistrationResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.DSLR.Encapsulate(response);

                    // Send the InitiateRegistration Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response InitiateRegistration " + dispatchRequestHandle);

                }
                // Unknown Request
                else {

                    System.Diagnostics.Debug.WriteLine($"Unknown DRMRI Request {dispatchFunctionHandle} not implemented");

                }

                #endregion ####################################################

            }
            // DSMN Service Handle
            else if (dispatchServiceHandle == DSMNServiceHandle) {

                System.Diagnostics.Debug.WriteLine($"Unknown DSMN Request {dispatchFunctionHandle} not implemented");

            } else {

                System.Diagnostics.Debug.WriteLine($"Unknown {dispatchServiceHandle} Request {dispatchFunctionHandle} not implemented");

            }
        }


        byte[] LoadDevCapsVChan(string fileName) {
            string path = vChanRootDir + "devcaps\\" + fileName;
            return File.ReadAllBytes(path);
        }

        #endregion ############################################################


        #region Misc Form Events ##############################################

        private void lnkGiveFocus_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            rdpClient.Focus();
        }

        private void linkLabel2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            txtLog.Text = "";
        }

        private void lnkSendCtrlAltDelete_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            // This doesn't seem to be working...
            rdpClient.Focus();
            SendKeys.Send("%^+{END}");
        }

        private void chkLogDebug_CheckedChanged(object sender, EventArgs e) {
            m_logger.IsLoggingDebug = chkLogDebug.Checked;
        }

        private void lnkShowCtrlHideInfo_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            rdpClient.Visible = true;
            panOverlay.Visible = false;
        }

        private void button1_Click_1(object sender, EventArgs e) {
        }

        #endregion ############################################################


        delegate void dTextWrite(string message);
        void SetStatus(string message) {
            Invoke(new dTextWrite(delegate (string ex) {
                lbGenStatus.Text = ex;
                if (!lbGenStatus.Visible)
                    lbGenStatus.Visible = true;
            }), message);
        }



        public static byte[] GetByteSubArray(byte[] byteArray, int startPosition, int byteCount) {

            byte[] result = new byte[byteCount];

            for (int i = startPosition; i < startPosition + byteCount; i++) {
                try {
                    result[i - startPosition] = byteArray[i];
                } catch (IndexOutOfRangeException) {

                }
            }

            return result;
        }

        public static string GetByteArrayString(byte[] byteArray, int startPosition, int length) {

            byte[] result = GetByteSubArray(byteArray, startPosition, length);

            return Encoding.ASCII.GetString(result);
        }

        public static int Get4ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 4);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt32(result, 0);
        }

        public static long Get8ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 8);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt64(result, 0);
        }

        public static int Get2ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 2);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt16(result, 0);
        }

        public static Guid GetGuid(byte[] byteArray, int startPosition) {

            int byteCount = 16;

            byte[] data1 = new byte[4];
            byte[] data2 = new byte[2];
            byte[] data3 = new byte[2];
            byte[] data4 = new byte[8];

            for (int i = startPosition; i < startPosition + byteCount; i++) {
                if (i - startPosition < 4) {
                    data1[i - startPosition] = byteArray[i];
                } else if (i - startPosition < 6) {
                    data2[i - startPosition - 4] = byteArray[i];
                } else if (i - startPosition < 8) {
                    data3[i - startPosition - 6] = byteArray[i];
                } else {
                    data4[i - startPosition - 8] = byteArray[i];
                }
            }

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(data1);
                Array.Reverse(data2);
                Array.Reverse(data3);
            }

            // Create Base Byte Array
            byte[] baseArray = new byte[0];
            // Formulate Array
            IEnumerable<byte> result = data1
                // Add Data 2
                .Concat(data2)
                // Add Data 3
                .Concat(data3)
                // Add Data 4
                .Concat(data4);

            // Return the created GUID
            return new Guid(result.ToArray());
        }
    }

    enum ReceiveActionType {
        Unknown,
        BooleanRequest,
        StringRequest,
        RSTPUrl
    }
}