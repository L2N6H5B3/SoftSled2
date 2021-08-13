using Rtsp;
using SoftSled.Components;
using System;
using System.Collections.Generic;
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

        private string vChanRootDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\VChan\\";

        public FrmMain() {
            InitializeComponent();
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

                m_logger.LogDebug("Asked for capability: " + capChar1 + capChar2);

                List<String> disabledCaps = new List<string>();
                disabledCaps.Add("BI"); // BIG - we cannot do Xbox 360 rendering
                disabledCaps.Add("PH");
                disabledCaps.Add("POP");
                disabledCaps.Add("HO");
                disabledCaps.Add("AR");
                disabledCaps.Add("CR");
                disabledCaps.Add("CP");
                //disabledCaps.Add("CD");
                disabledCaps.Add("DR");
                disabledCaps.Add("DV"); // DVD - DVD Playback
                disabledCaps.Add("FP");
                disabledCaps.Add("HC");
                disabledCaps.Add("HT");
                disabledCaps.Add("DO");
                disabledCaps.Add("SC");
                disabledCaps.Add("NL");
                disabledCaps.Add("RS");
                disabledCaps.Add("VO");
                disabledCaps.Add("W3");
                disabledCaps.Add("RU"); // RUI - we cannot do Xbox 360 rendering
                disabledCaps.Add("WI"); // WID - disable widescreen for the time being.
                disabledCaps.Add("TV"); // TVS 
                disabledCaps.Add("TB"); // TBP - disable the media center toolbar. 
                disabledCaps.Add("AN"); // ANI - intensive animations over RDP look awful!
                disabledCaps.Add("2D"); // 2D - Light Animations
                disabledCaps.Add("VI"); // VIZ - can't do wmp visualisations over RDP!
                disabledCaps.Add("MU"); // TVS
                disabledCaps.Add("XT");
                disabledCaps.Add("ZO");
                disabledCaps.Add("NL");
                disabledCaps.Add("BL");
                disabledCaps.Add("WE");
                disabledCaps.Add("SY");
                disabledCaps.Add("SO");
                disabledCaps.Add("RE");
                disabledCaps.Add("SU");
                disabledCaps.Add("MA");
                //disabledCaps.Add("AU"); // AUD - Audio Playback
                //disabledCaps.Add("GD"); // GDI - Disable GDI Rendering

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

            // Service Handle = Dispenser
            if (dispatchServiceHandle == 0) {

                // CreateService Response
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
                // DeleteService Response
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
                // Error Response
                else {
                    m_logger.LogDebug("AVCTRL: Request Error " + dispatchRequestHandle);


                }
            }
            // DMCT Service Handle
            else if (dispatchServiceHandle == DMCTServiceHandle) {

            }
            // DSPA Service Handle
            else if (dispatchServiceHandle == DSPAServiceHandle) {
                // Get DSPA Service Data
                int dspaServicePayloadSize = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                int dspaServiceChildCount = Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                Guid createServiceClassID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                Guid createServiceServiceID = GetGuid(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                int createServiceServiceHandle = Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                m_logger.LogDebug("AVCTRL: Request CreateService " + createServiceServiceHandle);
            }
            // DRMRI Service Handle
            else if (dispatchServiceHandle == DRMRIServiceHandle) {

            }
            // DSMN Service Handle
            else if (dispatchServiceHandle == DSMNServiceHandle) {

            }
            // Service Handle = Created Service
            else {

                // Get the actual AVCTRL ITER from incoming bytes
                avCtrlIter = incomingBuff[13];
                // Set the Receive Action Type
                ReceiveActionType actionType = ReceiveActionType.Unknown;

                // Get byte count of incoming data
                int byteCount = incomingBuff[25];
                // Create byte array to hold incoming data
                byte[] incomingData = new byte[byteCount];
                // Create array to hold filtered data
                byte[] filteredData = new byte[byteCount];
                // Check if there is data to receive
                if (byteCount > 0) {
                    // Iterate through each expected byte of data
                    for (int index = 0; index < byteCount; index++) {
                        // Try and Catch Out of Bounds Exception
                        try {
                            // Add the expected data byte to the incoming data array
                            incomingData[index] = incomingBuff[index + 28];
                        } catch (IndexOutOfRangeException) {
                            System.Diagnostics.Debug.WriteLine("outOfRange... " + (index + 28));
                        }
                    }

                    // Check if this is a Data Request or Data Announce
                    if (incomingData[0] == 0 && incomingData[1] == 0 && incomingData[2] == 0) {
                        // Create array to hold filtered data
                        filteredData = new byte[incomingData.Length - 4];
                        // Get Data
                        for (int index = 4; index < incomingData.Length; index++) {
                            // Add this data segment to the filtered data array
                            filteredData[index - 4] += incomingData[index];
                        }

                        // If this is a Data Request or Data Announce
                        switch (incomingData[3]) {
                            case 7:
                                // Set Action to Boolean Request
                                actionType = ReceiveActionType.BooleanRequest;
                                // Break from Switch Case
                                break;
                            case 14:
                                // Set Action to String Request
                                actionType = ReceiveActionType.StringRequest;
                                // Break from Switch Case
                                break;
                            case 90:
                                // Set Action to RTSP Announce
                                actionType = ReceiveActionType.RSTPUrl;
                                // Break from Switch Case
                                break;
                        }
                    }
                }


                // DEBUG PURPOSES ONLY
                string incomingByteArray = "";
                foreach (byte b in incomingData) {
                    incomingByteArray += b.ToString("X2") + " ";
                }
                // DEBUG PURPOSES ONLY

                // Set base filename for responses
                string baseFileName = vChanRootDir + @"avctrl\";

                // Initialise Response
                byte[] response = null;

                // Get the Request by the Request Type
                if (actionType == ReceiveActionType.BooleanRequest) {

                    // Hold count of clean data
                    int cleanCount = filteredData.Length;
                    // Get count of clean data
                    for (int index = 0; index < filteredData.Length; index++) {
                        // If the current index data is NULL
                        if (filteredData[index] == 0) {
                            // Set the clean data count to the index
                            cleanCount = index;
                            // Break from the loop
                            break;
                        }
                    }

                    // Create array to hold cleaned data
                    byte[] cleanedData = new byte[cleanCount];
                    // Get Data
                    for (int index = 0; index < cleanedData.Length; index++) {
                        // Add this data segment to the filtered data array
                        cleanedData[index] += filteredData[index];
                    }
                    // Get the action data string from the cleaned data array
                    string actionData = Encoding.ASCII.GetString(cleanedData);

                    // Get the Response file
                    response = File.ReadAllBytes(baseFileName + actionData.ToLower());


                } else if (actionType == ReceiveActionType.StringRequest) {

                    // Hold count of clean data
                    int cleanCount = filteredData.Length;
                    // Get count of clean data
                    for (int index = 0; index < filteredData.Length; index++) {
                        // If the current index data is NULL
                        if (filteredData[index] == 0) {
                            // Set the clean data count to the index
                            cleanCount = index;
                            // Break from the loop
                            break;
                        }
                    }

                    // Create array to hold cleaned data
                    byte[] cleanedData = new byte[cleanCount];
                    // Get Data
                    for (int index = 0; index < cleanedData.Length; index++) {
                        // Add this data segment to the filtered data array
                        cleanedData[index] += filteredData[index];
                    }
                    // Get the action data string from the cleaned data array
                    string actionData = Encoding.ASCII.GetString(cleanedData);

                    if (avCtrlIter == 4) {
                        // Get the Response file
                        response = File.ReadAllBytes(baseFileName + actionData.ToLower());

                        // We need to insert the remote host IP into our 4th iteration response.
                        byte[] hostIp = Encoding.ASCII.GetBytes(SoftSledConfigManager.ReadConfig().RdpLoginHost);

                        // Create temporary byte array to hold outgoing data
                        byte[] temp = new byte[response.Length + hostIp.Length];
                        // Iterate through each byte of data
                        for (int index = 0; index < temp.Length; index++) {
                            // If the index is less than the length of the file
                            if (index < response.Length) {
                                // Add the contents from the file
                                temp[index] = response[index];
                            } else {
                                // Add the contents from the host IP address
                                temp[index] = hostIp[index - response.Length];
                            }
                        }

                        // Set the response
                        response = temp;
                        // Set the data byte length
                        response[25] = Convert.ToByte(response[25] + hostIp.Length);

                    } else {
                        // Get the Response file
                        response = File.ReadAllBytes(baseFileName + "5");
                    }

                } else if (actionType == ReceiveActionType.RSTPUrl) {

                    // Get the Response file
                    response = File.ReadAllBytes(baseFileName + "S_OK");

                    // Hold count of clean data
                    int cleanCount = filteredData.Length;
                    // Get count of clean data
                    for (int index = 0; index < filteredData.Length; index++) {
                        // If the current index data is NULL
                        if (filteredData[index] == 0) {
                            // Set the clean data count to the index
                            cleanCount = index;
                            // Break from the loop
                            break;
                        }
                    }

                    // Create array to hold cleaned data
                    byte[] cleanedData = new byte[cleanCount];
                    // Get Data
                    for (int index = 0; index < cleanedData.Length; index++) {
                        // Add this data segment to the filtered data array
                        cleanedData[index] += filteredData[index];
                    }

                    // Get the RTSP URL from cleaned data array
                    rtspUrl = Encoding.ASCII.GetString(cleanedData);

                    System.Diagnostics.Debug.WriteLine(rtspUrl);

                    //RTSPClient client = new RTSPClient();
                    //client.Connect(rtspUrl, RTSPClient.RTP_TRANSPORT.UDP);

                    System.Diagnostics.Process.Start(@"C:\Users\Luke\Downloads\ffmpeg-4.4-full_build\ffmpeg-4.4-full_build\bin\ffplay.exe", rtspUrl);

                    //axWindowsMediaPlayer1.URL = rtspUrl;
                } else if (actionType == ReceiveActionType.Unknown) {
                    if (avCtrlIter == 7) {
                        response = File.ReadAllBytes(baseFileName + "7");
                    } else {
                        response = File.ReadAllBytes(baseFileName + "main");
                    }
                }

                // Set the AVCTRL ITER within the Response
                response[21] = Convert.ToByte(avCtrlIter);

                string outgoingString = Encoding.ASCII.GetString(response);

                // DEBUG PURPOSES ONLY
                string byteArray = "";
                foreach (byte b in response) {
                    byteArray += b.ToString("X2") + " ";
                }
                // DEBUG PURPOSES ONLY

                rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(response));
                m_logger.LogDebug("RDP: Sent avctrl iteration " + avCtrlIter.ToString());

                // Increment AvCtrlIter to indicate the current point in the process
                avCtrlIter++;



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
                result[i - startPosition] = byteArray[i];
            }

            return result;
        }

        public static int Get4ByteInt(byte[] byteArray, int startPosition) {

            byte[] result = GetByteSubArray(byteArray, startPosition, 4);

            if (BitConverter.IsLittleEndian) {
                Array.Reverse(result);
            }

            return BitConverter.ToInt32(result, 0);
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