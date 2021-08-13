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

namespace SoftSled {
    public partial class FrmMain : Form {
        // Private members
        private Logger m_logger;
        private ExtenderDevice m_device;
        private McxVirtualChannelHandler m_channelHandler;
        private int devCapsIter = 1;
        private int mcxSessIter = 1;
        private int avCtrlIter = 1;
        private bool isConnecting = false;
        readonly FileStream writer;
        private static RtspListener rtsp_client;
        private bool rdpInitialised = false;
        private string rtspUrl;
        private TcpClient rtspClient;

        private int DMCTServiceHandle;
        private int DSPAServiceHandle;
        private int DRMRIServiceHandle;
        private int DSMNServiceHandle;

        private int DMCTRegisterMediaEventCallbackCookie;
        private int DMCTOpenMediaRequestedPlayRate;
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

            TcpListener tcp1 = new TcpListener(localhost, 3776);
            TcpListener tcp2 = new TcpListener(localhost, 3777);
            TcpListener tcp3 = new TcpListener(localhost, 3778);
            TcpListener tcp4 = new TcpListener(localhost, 2177);

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

        #endregion


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
                    //m_logger.LogInfo("RDP: Received data on channel " + e.chanName);


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
            byte[] vChanResponse = File.ReadAllBytes(vChanRootDir + "McxSess\\Response");

            if (mcxSessIter == 1)
                SetStatus("Starting Experience...");

            if (mcxSessIter == 5) {
                // The fifth iteration is length of only 31 bytes.
                // We need to strip one byte as the stock file is 32 bytes.
                byte[] newBuff = new byte[31];
                Array.Copy(vChanResponse, newBuff, 31);

                vChanResponse = newBuff;

                // on 5th iteration, 18th byte should be 1, on every other instance, 0
                // No idea why this is so!
                // The stock file has a value of 0 for this byte.
                vChanResponse[18] = Convert.ToByte(1);

                // We take this iteration as the point when the MC experience is visible inside RDP.
                SetStatus("");
                panOverlay.Visible = false;
                rdpClient.Visible = true;
                m_logger.LogInfo("Experience started");
            }

            // Set sequencing integer
            vChanResponse[21] = Convert.ToByte(mcxSessIter);

            rdpClient.SendOnVirtualChannel("McxSess", Encoding.Unicode.GetString(vChanResponse));
            //m_logger.LogInfo("RDP: Sent McxSess iteration " + mcxSessIter.ToString());

            mcxSessIter++;
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
                string capChar3 = Encoding.ASCII.GetString(vChanIncomingBuff, vChanIncomingBuff.Length, 1).ToUpper();

                //m_logger.LogDebug("Asked for capability: " + capChar1 + capChar2);

                List<String> disabledCaps = new List<string>();
                disabledCaps.Add("PHO"); // Are advanced photo features allowed?
                //disabledCaps.Add("EXT"); // Are Extender Settings allowed?
                disabledCaps.Add("MAR"); // Are over-scan margins needed?
                disabledCaps.Add("POP"); // Are Pop ups allowed?
                disabledCaps.Add("ZOM"); // Is video zoom mode allowed?
                disabledCaps.Add("NLZ"); // Is nonlinear zoom supported?
                disabledCaps.Add("RSZ"); // Is raw stretched zoom supported?
                disabledCaps.Add("WID"); // Is wide screen enabled?
                disabledCaps.Add("H10"); // Is 10 feet help allowed? 
                disabledCaps.Add("WEB"); // Is 10 feet web content allowed? 
                disabledCaps.Add("H02"); // Is 2 feet help allowed? 
                disabledCaps.Add("WE2"); // Is 2 feet web content allowed? 
                //disabledCaps.Add("AUD"); // Is audio allowed?
                disabledCaps.Add("AUR"); // Is audio Non WMP?
                disabledCaps.Add("ARA"); // Is auto restart allowed?
                disabledCaps.Add("BLB"); // Is black letters box needed?
                disabledCaps.Add("CCC"); // Is CC rendered by the client?
                disabledCaps.Add("CRC"); // Is CD burning allowed?
                disabledCaps.Add("CPY"); // Is CD copying allowed?
                disabledCaps.Add("CDA"); // Is CD playback allowed?
                disabledCaps.Add("CLO"); // Is the close button shown?
                disabledCaps.Add("DRC"); // Is DVD burning allowed?
                disabledCaps.Add("DVD"); // Is DVD playback allowed?
                disabledCaps.Add("FPD"); // Is FPD allowed?
                //disabledCaps.Add("GDI"); // Is GDI renderer used?
                //disabledCaps.Add("HDV"); // Is HD content allowed?
                //disabledCaps.Add("HDN"); // Is HD content allowed by the network?
                //disabledCaps.Add("SDN"); // Is SD content allowed by the network?
                //disabledCaps.Add("REM"); // Is input treated as if from a remote?
                disabledCaps.Add("ANI"); // Is intensive animation allowed?
                disabledCaps.Add("2DA"); // Is 2D animation allowed?
                disabledCaps.Add("HTM"); // Is HTML supported?
                disabledCaps.Add("DES"); // Is MCE a Windows shell?
                disabledCaps.Add("DOC"); // Is my Documents populated?
                disabledCaps.Add("SCR"); // Is a native screensaver required?
                disabledCaps.Add("ONS"); // Is online spotlight allowed?
                //disabledCaps.Add("SUP"); // Is RDP super bit allowed?
                disabledCaps.Add("BIG"); // Is remote UI renderer big-endian?
                disabledCaps.Add("RUI"); // Is remote UI rendering supported?
                disabledCaps.Add("SDM"); // Is a screen data mode workaround needed?
                disabledCaps.Add("TBA"); // Is a Toolbar allowed?
                disabledCaps.Add("SYN"); // Is transfer to a device allowed?
                disabledCaps.Add("APP"); // Is tray applet allowed?
                disabledCaps.Add("TVS"); // Is a TV skin used?
                //disabledCaps.Add("SOU"); // Is UI sound supported?
                //disabledCaps.Add("VID"); // Is video allowed?
                disabledCaps.Add("W32"); // Is Win32 content allowed?
                disabledCaps.Add("WIN"); // Is window mode allowed?
                disabledCaps.Add("VIZ"); // Is WMP visualisation allowed?
                //disabledCaps.Add("VOL"); // Is volume UI allowed?
                //disabledCaps.Add("MUT"); // Is mute ui allowed?

                bool response = false;
                if (disabledCaps.Contains(capChar1 + capChar2 + capChar3))
                    vChanResponseBuff = LoadDevCapsVChan("Disabled");
                else {
                    vChanResponseBuff = LoadDevCapsVChan("Enabled");
                    response = true;
                }

                // We need to modify the sequencing integer inside the response.
                vChanResponseBuff[21] = Convert.ToByte(devCapsIter);

                //m_logger.LogDebug("RDP: " + response.ToString().ToUpper() + " for capability " + capChar1 + capChar2);
            }

            rdpClient.SendOnVirtualChannel("devcaps", Encoding.Unicode.GetString(vChanResponseBuff));
            //m_logger.LogDebug("RDP: Sent devcaps citeration " + devCapsIter.ToString());


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
                    byte[] response = VChan.AVCTRL.CreateServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

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
                // Error Request
                else {
                    m_logger.LogDebug("AVCTRL: Request Error " + dispatchRequestHandle);


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

                    // Create Media Object
                    currentMedia = new Media(_libVLC, new Uri(OpenMediaPayloadURL));
                    currentMedia.Parse();

                    // Initialise OpenMedia Response
                    byte[] response = VChan.AVCTRL.OpenMediaResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                    // DEBUG PURPOSES ONLY
                    string byteArray = "";
                    foreach (byte b in response) {
                        byteArray += b.ToString("X2") + " ";
                    }
                    // DEBUG PURPOSES ONLY

                    // Send the SetDWORDProperty Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response OpenMedia " + OpenMediaPayloadURL);

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

                    //// Start the child process.
                    //ffplay = new Process();
                    //// Redirect the output stream of the child process.
                    //p.StartInfo.UseShellExecute = false;
                    //p.StartInfo.RedirectStandardOutput = true;
                    //p.StartInfo.FileName = "ffplay.exe";
                    //p.StartInfo.Arguments = $"-i {DMCTOpenMediaURL} -show_entries format=duration -v quiet -of csv=\"p = 0\"";
                    //p.StartInfo.Arguments = $"-i {DMCTOpenMediaURL} -hide_banner -loglevel 8 -stats  2.09 A-V: -0.004 fd=   6 aq=   31KB vq=   84KB sq=    0B f=0/0";
                    //p.Start();

                    //Process.Start(@"C:\Users\Luke\Downloads\ffmpeg-4.4-full_build\ffmpeg-4.4-full_build\bin\ffplay.exe", DMCTOpenMediaURL);

                    _mp.Play(currentMedia);

                    // Initialise Start Response
                    byte[] response = VChan.AVCTRL.StartResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        DMCTOpenMediaRequestedPlayRate
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                    // DEBUG PURPOSES ONLY
                    string byteArray = "";
                    foreach (byte b in response) {
                        byteArray += b.ToString("X2") + " ";
                    }
                    // DEBUG PURPOSES ONLY

                    // Send the SetDWORDProperty Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response Start " + dispatchRequestHandle);


                }
                // GetDuration Request
                else if (dispatchFunctionHandle == 5) {

                    m_logger.LogDebug("AVCTRL: Request GetDuration " + dispatchRequestHandle);

                    long durationLongMili = Convert.ToInt64(currentMedia.Duration / 10);

                    // Initialise GetDuration Response
                    byte[] response = VChan.AVCTRL.GetDurationResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        durationLongMili
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                    // Send the GetDuration Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response GetDuration " + durationLongMili);

                }
                // GetPosition Request
                else if (dispatchFunctionHandle == 6) {

                    m_logger.LogDebug("AVCTRL: Request GetPosition " + dispatchRequestHandle);

                    long positionLongMili = Convert.ToInt64(_mp.Time / 10);

                    // Initialise GetPosition Response
                    byte[] response = VChan.AVCTRL.GetPositionResponse(
                        GetByteSubArray(incomingBuff, 10, 4),
                        positionLongMili
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                    // Send the GetPosition Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response GetPosition " + 0);

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
                byte[] response = VChan.AVCTRL.RegisterMediaEventCallbackResponse(
                    GetByteSubArray(incomingBuff, 10, 4),
                    DMCTRegisterMediaEventCallbackCookie
                );
                // Encapsulate the Response (Doesn't seem to work without this?)
                byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                // Send the RegisterMediaEventCallback Response
                rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                m_logger.LogDebug("AVCTRL: Sent Response RegisterMediaEventCallback " + dispatchRequestHandle);

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
                            byte[] response = VChan.AVCTRL.GetStringPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4),
                                SoftSledConfigManager.ReadConfig().RdpLoginHost
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

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
                            byte[] isMutedResponse = VChan.AVCTRL.GetDWORDPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4),
                                0
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedIsMutedResponse = VChan.AVCTRL.Encapsulate(isMutedResponse);

                            // Send the GetDWORDProperty Response
                            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedIsMutedResponse));

                            m_logger.LogDebug("AVCTRL: Sent Response GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                            break;
                        case "Volume":

                            m_logger.LogDebug("AVCTRL: Request GetDWORDProperty " + GetDWORDPropertyPayloadPropertyName);

                            // Initialise GetDWORDProperty Response
                            byte[] volumeResponse = VChan.AVCTRL.GetDWORDPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4),
                                65535
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedVolumeResponse = VChan.AVCTRL.Encapsulate(volumeResponse);

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
                            byte[] response = VChan.AVCTRL.SetDWORDPropertyResponse(
                                GetByteSubArray(incomingBuff, 10, 4)
                            );
                            // Encapsulate the Response (Doesn't seem to work without this?)
                            byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                            // Send the SetDWORDProperty Response
                            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                            m_logger.LogDebug("AVCTRL: Sent Response SetDWORDProperty " + SetDWORDPropertyPayloadPropertyName);

                            break;
                    }
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
                    byte[] response = VChan.AVCTRL.RegisterTransmitterServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

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
                    byte[] response = VChan.AVCTRL.UnregisterTransmitterServiceResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

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
                    byte[] response = VChan.AVCTRL.InitiateRegistrationResponse(
                        GetByteSubArray(incomingBuff, 10, 4)
                    );
                    // Encapsulate the Response (Doesn't seem to work without this?)
                    byte[] encapsulatedResponse = VChan.AVCTRL.Encapsulate(response);

                    // Send the InitiateRegistration Response
                    rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(encapsulatedResponse));

                    m_logger.LogDebug("AVCTRL: Sent Response InitiateRegistration " + dispatchRequestHandle);

                }

                #endregion ####################################################

            }
            // DSMN Service Handle
            else if (dispatchServiceHandle == DSMNServiceHandle) {

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
            avCtrlIter = 1;
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

        public static string GetMediaDuration(string URL) {

            // Start the child process.
            Process p = new Process();
            // Redirect the output stream of the child process.
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.FileName = "ffprobe.exe";
            p.StartInfo.Arguments = $"-i {URL} -show_entries format=duration -v quiet -of csv=\"p = 0\"";
            p.Start();
            // Do not wait for the child process to exit before
            // reading to the end of its redirected stream.
            // p.WaitForExit();
            // Read the output stream first and then wait.
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            return output;
        }

        //public static string GetMediaPosition() {

        //    // Start the child process.
        //    Process p = new Process();
        //    // Redirect the output stream of the child process.
        //    p.StartInfo.UseShellExecute = false;
        //    p.StartInfo.RedirectStandardOutput = true;
        //    p.StartInfo.FileName = "ffprobe.exe";
        //    p.StartInfo.Arguments = $"-i {URL} -show_entries format=duration -v quiet -of csv=\"p = 0\"";
        //    p.Start();
        //    // Do not wait for the child process to exit before
        //    // reading to the end of its redirected stream.
        //    // p.WaitForExit();
        //    // Read the output stream first and then wait.
        //    string output = p.StandardOutput.ReadToEnd();
        //    p.WaitForExit();

        //    return output;
        //}
    }

    enum ReceiveActionType {
        Unknown,
        BooleanRequest,
        StringRequest,
        RSTPUrl
    }
}