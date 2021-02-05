using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using SoftSled.Components;
using System.IO;
using System.Threading;
using System.Reflection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net;
using Rtsp;

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
        private TcpClient rtspClient;

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

            rdpClient.Server = currConfig.RdpLoginHost;
            rdpClient.UserName = currConfig.RdpLoginUserName;
            rdpClient.AdvancedSettings2.ClearTextPassword = currConfig.RdpLoginPassword;

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
                disabledCaps.Add("CD");
                disabledCaps.Add("DR");
                disabledCaps.Add("DV");
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
                disabledCaps.Add("VI"); // VIZ - can't do wmp visualisations over RDP!
                disabledCaps.Add("MU"); // TVS
                disabledCaps.Add("XT");

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

            //File.WriteAllBytes("C:\\Users\\Luke\\source\\repos\\SoftSled2\\avctrlIncoming_" + avCtrlIter, Encoding.Unicode.GetBytes(data)); 

            byte[] incomingBuff = Encoding.Unicode.GetBytes(data);
            string incomingString = Encoding.ASCII.GetString(incomingBuff);
            File.WriteAllText("C:\\Users\\Luke\\source\\repos\\SoftSled2\\avctrlIncoming_" + avCtrlIter, incomingString);

            string fileName = vChanRootDir + "avctrl\\av r ";
            
            if (avCtrlIter == 4) {
                fileName += "4";
                //  File.WriteAllText("g:\\4th", data);
            } else if (avCtrlIter == 5) {
                fileName += "5";
            } else if (avCtrlIter == 6) {
                fileName += "6";
            } else if (avCtrlIter == 7) {
                fileName += "7";
            }
              //else if (avCtrlIter == 8) 
              //  fileName += "8";
              else
                fileName += "main";

            if (avCtrlIter == 8) {
                byte[] rtspBuff = new byte[85];

                // Get the RTSP URL
                string rtspUrl = Encoding.ASCII.GetString(Encoding.Unicode.GetBytes(data), 32, 97);

                System.Diagnostics.Debug.WriteLine(rtspUrl);

                RtspInitial(rtspUrl);
            }

            byte[] file = File.ReadAllBytes(fileName);
            file[21] = Convert.ToByte(avCtrlIter);

            if (avCtrlIter == 4) {
                // We need to insert the remote host IP into our 4th iteration response.

                byte[] hostIp = Encoding.ASCII.GetBytes(SoftSledConfigManager.ReadConfig().RdpLoginHost);
                //Array.Copy(hostIp, 0, file, 36, hostIp.Length);

                string beforeOutString = Encoding.ASCII.GetString(file);

                int count = 0;
                for (int i = 36; i < file.Length; i++) {
                    file[i] = hostIp[count];
                    count++;
                }

                string outString = Encoding.ASCII.GetString(file);
            }


            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(file));
            m_logger.LogDebug("RDP: Sent avctrl iteration " + avCtrlIter.ToString());

            avCtrlIter++;
        }

        private static void DoRtspInitialOld(string url) {
            // Connect to a RTSP Server
            var tcp_socket = new Rtsp.RtspTcpTransport(SoftSledConfigManager.ReadConfig().RdpLoginHost, 8554);

            if (tcp_socket.Connected == false) {
                System.Diagnostics.Debug.WriteLine("Error - did not connect");
                return;
            } else {
                System.Diagnostics.Debug.WriteLine("RTSP Connected");
            }

            // Connect a RTSP Listener to the TCP Socket to send messages and listen for replies
            rtsp_client = new Rtsp.RtspListener(tcp_socket);

            rtsp_client.MessageReceived += Rtsp_client_MessageReceived;
            //rtsp_client.DataReceived += Rtsp_client_DataReceived;

            rtsp_client.Start(); // start reading messages from the server

            //// send the Describe
            //Rtsp.Messages.RtspRequest describe_message = new Rtsp.Messages.RtspRequestDescribe();
            //describe_message.RtspUri = new Uri(url);
            //describe_message.AddHeader("Accept: application/sdp");
            //describe_message.AddHeader("CSeq: 1");
            //describe_message.AddHeader("Accept-Language: en-us, *;q=0.1");
            //describe_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
            //describe_message.AddHeader("User-Agent: MCExtender/1.50.X.090522.00");
            //describe_message.AddHeader("DLNA-ProtocolInfo: rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL\nrtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3\nrtsp-rtp-udp:*:audio/vnd.dolby.dd-rtp:DLNA.ORG_PN=AC3\nrtsp-rtp-udp:*:audio/L16:DLNA.ORG_PN=LPCM\nhttp-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM\nrtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;DLNA.ORG_PN=MPEG_ES_PAL;DLNA.ORG_PN=MPEG_ES_NTSC;DLNA.ORG_PN=MPEG_ES_PAL_XAC3;DLNA.ORG_PN=MPEG_ES_NTSC_XAC3\nrtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=WMVHIGH_LSL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED;MICROSOFT.COM_PN=VC1_APL3_FULL;MICROSOFT.COM_PN=VC1_APL3_PRO\nrtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3\nrtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=AVC_MP4_MP_HD_MPEG1_L3;MICROSOFT.COM_PN=AVC_MP4_MP_HD_AC3\nhttp-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL");
            //rtsp_client.SendMessage(describe_message);

            // send the Describe
            Rtsp.Messages.RtspRequest setup_message = new Rtsp.Messages.RtspRequestSetup();
            setup_message.RtspUri = new Uri("rtsp://10.1.1.100:8554/McxDMS/Mcx1-HTPC/audio");
            setup_message.AddHeader("CSeq: 2");
            setup_message.AddHeader("Accept-Language: en-us, *;q=0.1");
            setup_message.AddHeader("Buffer-Info.dlna.org: dejitter=6624000;CDB=6553600;BTM=0;TD=2000;BFR=0");
            setup_message.AddHeader("Supported: dlna.announce, dlna.rtx-dup");
            setup_message.AddHeader("User-Agent: MCExtender/1.50.X.090522.00");
            setup_message.AddHeader("DLNA-ProtocolInfo: rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL\nrtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3\nrtsp-rtp-udp:*:audio/vnd.dolby.dd-rtp:DLNA.ORG_PN=AC3\nrtsp-rtp-udp:*:audio/L16:DLNA.ORG_PN=LPCM\nhttp-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM\nrtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;DLNA.ORG_PN=MPEG_ES_PAL;DLNA.ORG_PN=MPEG_ES_NTSC;DLNA.ORG_PN=MPEG_ES_PAL_XAC3;DLNA.ORG_PN=MPEG_ES_NTSC_XAC3\nrtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=WMVHIGH_LSL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED;MICROSOFT.COM_PN=VC1_APL3_FULL;MICROSOFT.COM_PN=VC1_APL3_PRO\nrtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3\nrtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=AVC_MP4_MP_HD_MPEG1_L3;MICROSOFT.COM_PN=AVC_MP4_MP_HD_AC3\nhttp-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL");
            rtsp_client.SendMessage(setup_message);
            // The reply will include the SDP data
        }

        private static void Rtsp_client_MessageReceived(object sender, Rtsp.RtspChunkEventArgs e) {
            Rtsp.Messages.RtspResponse message = e.Message as Rtsp.Messages.RtspResponse;

            System.Diagnostics.Debug.WriteLine("Received " + message.OriginalRequest.ToString());

            //if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestOptions) {
            //    // send the DESCRIBE
            //    Rtsp.Messages.RtspRequest describe_message = new Rtsp.Messages.RtspRequestDescribe();
            //    describe_message.RtspUri = new Uri(url);
            //    rtsp_client.SendMessage(describe_message);
            //}

            //if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestDescribe) {
            //    // Got a reply for DESCRIBE
            //    // Examine the SDP
            //    Console.Write(System.Text.Encoding.UTF8.GetString(message.Data));

            //    Rtsp.Sdp.SdpFile sdp_data;
            //    using (StreamReader sdp_stream = new StreamReader(new MemoryStream(message.Data))) {
            //        sdp_data = Rtsp.Sdp.SdpFile.Read(sdp_stream);
            //    }

            //    // Process each 'Media' Attribute in the SDP.
            //    // If the attribute is for Video, then send a SETUP
            //    for (int x = 0; x < sdp_data.Medias.Count; x++) {
            //        if (sdp_data.Medias[x].GetMediaType() == Rtsp.Sdp.Media.MediaType.video) {
            //            // seach the atributes for control, fmtp and rtpmap
            //            String control = "";  // the "track" or "stream id"
            //            String fmtp = ""; // holds SPS and PPS
            //            String rtpmap = ""; // holds the Payload format, 96 is often used with H264
            //            foreach (Rtsp.Sdp.Attribut attrib in sdp_data.Medias[x].Attributs) {
            //                if (attrib.Key.Equals("control")) control = attrib.Value;
            //                if (attrib.Key.Equals("fmtp")) fmtp = attrib.Value;
            //                if (attrib.Key.Equals("rtpmap")) rtpmap = attrib.Value;
            //            }

            //            // Get the Payload format number for the Video Stream
            //            String[] split_rtpmap = rtpmap.Split(' ');
            //            video_payload = 0;
            //            bool result = Int32.TryParse(split_rtpmap[0], out video_payload);

            //            // Send SETUP for the Video Stream
            //            // using Interleaved mode (RTP frames over the RTSP socket)
            //            Rtsp.Messages.RtspRequest setup_message = new Rtsp.Messages.RtspRequestSetup();
            //            setup_message.RtspUri = new Uri(url + "/" + control);
            //            setup_message.AddHeader("Transport: RTP/AVP/TCP;interleaved=0");
            //            rtsp_client.SendMessage(setup_message);
            //        }
            //    }
            //}

            //if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestSetup) {
            //    // Got Reply to SETUP
            //    Console.WriteLine("Got reply from Setup. Session is " + message.Session);

            //    String session = message.Session; // Session value used with Play, Pause, Teardown

            //    // Send PLAY
            //    Rtsp.Messages.RtspRequest play_message = new Rtsp.Messages.RtspRequestPlay();
            //    play_message.RtspUri = new Uri(url);
            //    play_message.Session = session;
            //    rtsp_client.SendMessage(play_message);
            //}

            //if (message.OriginalRequest != null && message.OriginalRequest is Rtsp.Messages.RtspRequestPlay) {
            //    // Got Reply to PLAY
            //    Console.WriteLine("Got reply from Play  " + message.Command);
            //}
        }


        private void RtspInitialtest(string url) {

            string baseUrl = url.Split('?')[0];

            // Line Breaks
            string CRLF = "\r\n";
            string LF = "\\n";

            // Describe Headers
            string describeAddressHeader       = $"DESCRIBE {url} RTSP/1.0{CRLF}";
            string describeAcceptHeader         = $"Accept: application/sdp{CRLF}";
            string describeCseqHeader           = $"CSeq: 1{CRLF}";

            // Setup Headers
            string setupAddressHeader = $"DESCRIBE {baseUrl}/audio RTSP/1.0{CRLF}";
            string setupBufferInfoHeader = $"Accept: application/sdp{CRLF}";
            string setupCseqHeader = $"CSeq: 2{CRLF}";

            // Generic Headers
            string acceptLangHeader = $"Accept-Language: en-us, *;q=0.1{CRLF}";
            string supportedHeader = $"Supported: dlna.announce, dlna.rtx-dup{CRLF}";
            string userAgentHeader = $"User-Agent: MCExtender/1.0.0.0{CRLF}";
            string dlnaHeader           = $"DLNA-ProtocolInfo: rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL{LF}rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3{LF}rtsp-rtp-udp:*:audio/vnd.dolby.dd-rtp:DLNA.ORG_PN=AC3{LF}rtsp-rtp-udp:*:audio/L16:DLNA.ORG_PN=LPCM{LF}http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM{LF}rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;DLNA.ORG_PN=MPEG_ES_PAL;DLNA.ORG_PN=MPEG_ES_NTSC;DLNA.ORG_PN=MPEG_ES_PAL_XAC3;DLNA.ORG_PN=MPEG_ES_NTSC_XAC3{LF}rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=WMVHIGH_LSL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED;MICROSOFT.COM_PN=VC1_APL3_FULL;MICROSOFT.COM_PN=VC1_APL3_PRO{LF}rtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3{LF}rtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=AVC_MP4_MP_HD_MPEG1_L3;MICROSOFT.COM_PN=AVC_MP4_MP_HD_AC3{LF}http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL{CRLF}";
            
            // Create Request String
            string request = describeAddressHeader + describeAcceptHeader + describeCseqHeader + acceptLangHeader + supportedHeader + userAgentHeader;

            Console.WriteLine("Sending to server: {0}", request);

            rtspClient = new TcpClient(SoftSledConfigManager.ReadConfig().RdpLoginHost, 8554);
            NetworkStream stream = rtspClient.GetStream();
            StreamReader reader = new StreamReader(stream);
            StreamWriter writer = new StreamWriter(stream);
            writer.AutoFlush = true;
            writer.WriteLine(request);

            string response = reader.ReadLine();

            Console.WriteLine("Received from server: {0}", response);

        }

        private static void DoRtspInitialbroken(string url) {
            string CRLF = "\r\n";
            string LF = "\n";
            string DLNA = $"DLNA-ProtocolInfo: rtsp-rtp-udp:*:audio/x-ms-wma:DLNA.ORG_PN=WMAFULL;DLNA.ORG_PN=WMAPRO;MICROSOFT.COM_PN=WMALSL{LF}rtsp-rtp-udp:*:audio/mpeg:DLNA.ORG_PN=MP3{LF}rtsp-rtp-udp:*:audio/vnd.dolby.dd-rtp:DLNA.ORG_PN=AC3{LF}rtsp-rtp-udp:*:audio/L16:DLNA.ORG_PN=LPCM{LF}http-get:*:audio/L16:MICROSOFT.COM_PN=WAV_PCM{LF}rtsp-rtp-udp:*:video/mpeg:MICROSOFT.COM_PN=DVRMS_MPEG2;DLNA.ORG_PN=MPEG_ES_PAL;DLNA.ORG_PN=MPEG_ES_NTSC;DLNA.ORG_PN=MPEG_ES_PAL_XAC3;DLNA.ORG_PN=MPEG_ES_NTSC_XAC3{LF}rtsp-rtp-udp:*:video/x-ms-wmv:DLNA.ORG_PN=WMVHIGH_PRO;DLNA.ORG_PN=WMVHIGH_FULL;MICROSOFT.COM_PN=WMVHIGH_LSL;MICROSOFT.COM_PN=VC1_APL2_FULL;MICROSOFT.COM_PN=VC1_APL2_PRO;MICROSOFT.COM_PN=VC1_APL2_LSL;MICROSOFT.COM_PN=WMVIMAGE1_MED;MICROSOFT.COM_PN=WMVIMAGE2_MED;MICROSOFT.COM_PN=VC1_APL3_FULL;MICROSOFT.COM_PN=VC1_APL3_PRO{LF}rtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_MPEG1_L3;MICROSOFT.COM_PN=MPEG4_P2_MP4_ASP_L5_AC3{LF}rtsp-rtp-udp:*:video/mp4:MICROSOFT.COM_PN=AVC_MP4_MP_HD_MPEG1_L3;MICROSOFT.COM_PN=AVC_MP4_MP_HD_AC3{LF}http-get:*:video/mpeg:DLNA.ORG_PN=MPEG1;DLNA.ORG_PN=MPEG_PS_NTSC;DLNA.ORG_PN=MPEG_PS_PAL";
            // No resposes so far when making the request beneath. 
            string initial = $"DESCRIBE {url} RTSP/1.0{CRLF}Accept: application/sdp{CRLF}CSeq: 1{CRLF}Accept-Language: en-us, *;q=0.1{CRLF}Supported: dlna.announce, dlna.rtx-dup{CRLF}User-Agent: MCExtender/1.0.0.0{CRLF}";

            //            string initial = @"DESCRIBE " + url + @" RTSP/1.0
            //Accept: application/sdp
            //CSeq: 1
            //Accept-Language: en-us, *;q=0.1
            //Supported: dlna.announce, dlna.rtx-dup
            //User-Agent: MCExtender/1.50.X.090522.00
            
            System.Diagnostics.Debug.WriteLine(initial);
            TcpClient tcp = new TcpClient(SoftSledConfigManager.ReadConfig().RdpLoginHost, 8554);
            NetworkStream ns = tcp.GetStream();

            byte[] initialBuff = Encoding.ASCII.GetBytes(initial);
            ns.Write(initialBuff, 0, initialBuff.Length);

            System.Diagnostics.Debug.WriteLine("here 1");

            
            System.Diagnostics.Debug.WriteLine($"Connected: {tcp.Connected}");

            while (true) {
                if (!tcp.Connected) {
                    System.Diagnostics.Debug.WriteLine("Disconnected");
                }

                byte[] buff = new byte[512];
                int read = ns.Read(buff, 0, 512);
                if (read > 0) {
                    System.Diagnostics.Debug.WriteLine("here 3");
                    MessageBox.Show(Encoding.ASCII.GetString(buff));
                }

                buff = new byte[512];
            }
        }




        byte[] LoadDevCapsVChan(string fileName) {
            string path = vChanRootDir + "devcaps\\" + fileName;
            return File.ReadAllBytes(path);
        }
        #endregion

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

    }
}