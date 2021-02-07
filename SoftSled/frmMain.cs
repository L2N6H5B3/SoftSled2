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
using SoftSled.Components.RTSP;
using WMPLib;

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
                rtspUrl = Encoding.ASCII.GetString(Encoding.Unicode.GetBytes(data), 32, 97);

                RTSPClient client = new RTSPClient();
                client.Connect(rtspUrl, RTSPClient.RTP_TRANSPORT.UDP);


            }

            byte[] file = File.ReadAllBytes(fileName);
            file[21] = Convert.ToByte(avCtrlIter);

            if (avCtrlIter == 4) {
                // We need to insert the remote host IP into our 4th iteration response.
                byte[] hostIp = Encoding.ASCII.GetBytes(SoftSledConfigManager.ReadConfig().RdpLoginHost);

                int count = 0;
                for (int i = 36; i < file.Length; i++) {
                    file[i] = hostIp[count];
                    count++;
                }

            }

            rdpClient.SendOnVirtualChannel("avctrl", Encoding.Unicode.GetString(file));
            m_logger.LogDebug("RDP: Sent avctrl iteration " + avCtrlIter.ToString());

            avCtrlIter++;
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

    }
}