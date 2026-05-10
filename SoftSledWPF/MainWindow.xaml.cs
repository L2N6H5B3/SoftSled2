using SoftSled.Components.Communication;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Extender;
using SoftSled.Components.VirtualChannel;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace SoftSledWPF {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {

        // Private members
        private Logger m_logger;
        private ExtenderDevice m_device;
        private bool isConnecting = false;

        // In-process FreeRDP transport (softsled-rdp.dll + freerdp3.dll under
        // native/x64/). Replaces the mstscax + RDPVCManager.dll + named-pipe
        // bridge that earlier revisions used. Provides both virtual-channel
        // I/O and a software framebuffer surfaced via WriteableBitmap.
        private FreeRdpClient freeRdpClient;

        private VirtualChannelAvCtrlHandler AvCtrlHandler;
        private VirtualChannelDevCapsHandler DevCapsHandler;
        private VirtualChannelMcxSessHandler McxSessHandler;

        // WMC's MCX-specific 0x0D fast-path audio. Player is always-on and
        // produces live output via NAudio. Dumper is opt-in via the
        // SOFTSLED_AUDIO_DUMP env var (writes per-sound PCM .wav files for
        // RE / debugging). Held in fields so the bound delegates stay alive
        // for as long as native code holds the function pointer.
        private SoftSled.Components.AudioVisual.WmcFastpathAudioPlayer _audioPlayer;
        private SoftSled.Components.AudioVisual.WmcFastpathAudioDumper _audioDumper;
        private SoftSledNative.FastpathCallback _fastpathDispatcher;

        public MainWindow() {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;

            // Tunnel-phase keyboard hooks at the Window level so we catch
            // arrow/Enter/Escape regardless of which control technically has
            // focus. While ACTIVE we forward to FreeRDP and mark Handled to
            // suppress WPF's own use of the keys.
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            this.PreviewKeyUp   += MainWindow_PreviewKeyUp;

            Unosquare.FFME.Library.FFmpegDirectory = @"C:\ffmpeg\x86\bin";
        }

        // Track session liveness so we only forward keys after ACTIVE and
        // before DISCONNECT — sending into a closed input pipe is harmless
        // but pollutes logs.
        private volatile bool _sessionActive;

        // ------- WPF -> RDP keyboard glue -------------------------------------

        private const uint MAPVK_VK_TO_VSC_EX = 4;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private void ForwardKey(KeyEventArgs e, bool release) {
            if (!_sessionActive || freeRdpClient == null) return;

            // Alt-modified keys arrive as Key.System with the actual key in
            // SystemKey. Use whichever is meaningful.
            Key k = (e.Key == Key.System) ? e.SystemKey : e.Key;
            if (k == Key.None) return;

            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk == 0) return;

            uint sc = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
            if (sc == 0) return;

            byte scancode = (byte)(sc & 0xFF);
            bool extended = ((sc >> 8) & 0xFF) == 0xE0;

            freeRdpClient.SendKey(scancode, extended, release);
            e.Handled = true;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e) =>
            ForwardKey(e, release: false);

        private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e) =>
            ForwardKey(e, release: true);

        private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
            InitialiseLogger();

            // Create the FreeRDP client. DataReceived/StateChanged signatures
            // match the legacy RDPVCInterface, so the existing channel
            // handlers plug in unchanged. FrameReady fires once the GDI
            // framebuffer is allocated and the WriteableBitmap is ready.
            freeRdpClient = new FreeRdpClient();
            freeRdpClient.DataReceived += RdpVCInterface_DataReceived;
            freeRdpClient.StateChanged += FreeRdpClient_StateChanged;
            freeRdpClient.FrameReady   += FreeRdpClient_FrameReady;
            foreach (var ch in new[] { "McxSess", "MCECaps", "devcaps", "avctrl", "VCHD", "splash" })
                freeRdpClient.RegisterChannel(ch);

            // WMC fast-path 0x0D audio. Always wire the live player; if
            // SOFTSLED_AUDIO_DUMP=<dir> is also set, fan the same bytes
            // out to the WAV dumper for offline analysis. We register a
            // single dispatcher with the shim and route to whichever
            // consumer(s) are active.
            _audioPlayer = new SoftSled.Components.AudioVisual.WmcFastpathAudioPlayer(m_logger);
            string audioDumpDir = Environment.GetEnvironmentVariable("SOFTSLED_AUDIO_DUMP");
            if (!string.IsNullOrWhiteSpace(audioDumpDir)) {
                _audioDumper = new SoftSled.Components.AudioVisual.WmcFastpathAudioDumper(
                    m_logger, audioDumpDir);
            }
            _fastpathDispatcher = (user, code, data, length) => {
                _audioPlayer?.OnFastpath(user, code, data, length);
                _audioDumper?.OnFastpath(user, code, data, length);
            };
            freeRdpClient.SetFastpathCallback(_fastpathDispatcher);

            // Create VirtualChannel Handlers
            McxSessHandler = new VirtualChannelMcxSessHandler(m_logger);
            DevCapsHandler = new VirtualChannelDevCapsHandler(m_logger);
            AvCtrlHandler  = new VirtualChannelAvCtrlHandler(m_logger);
            McxSessHandler.VirtualChannelSend += On_VirtualChannelSend;
            DevCapsHandler.VirtualChannelSend += On_VirtualChannelSend;
            AvCtrlHandler.VirtualChannelSend  += On_VirtualChannelSend;

            McxSessHandler.StatusChanged += McxSessHandler_StatusChanged;

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            if (!config.IsPaired) {
                m_logger.LogInfo("Extender is not paired!");
            } else {
                m_logger.LogInfo("Extender is paired with " + config.RdpLoginHost);
            }
        }

        private void FreeRdpClient_FrameReady(object sender, EventArgs e) {
            // FrameReady is raised on the UI thread once the WriteableBitmap
            // has been allocated and primed with the initial framebuffer.
            rdpDisplay.Source = freeRdpClient.Bitmap;
            m_logger.LogInfo($"RDP framebuffer ready: {freeRdpClient.Bitmap.PixelWidth}x{freeRdpClient.Bitmap.PixelHeight}");
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
            freeRdpClient.SendOnVirtualChannel(e.channelName, e.data);
        }

        private void FreeRdpClient_StateChanged(object sender, StateChangedEventArgs e) {
            m_logger.LogInfo($"FreeRDP: state={e.State} detail=0x{e.Detail:X8}");
            // Gate keyboard forwarding on session liveness. Set on the
            // worker thread so KeyDown handlers see it without round-tripping
            // through the dispatcher.
            _sessionActive = (e.State == SoftSledNative.State.Active);
            // Mirror the mstscax button behaviour so the UI reflects the
            // FreeRDP-driven session state.
            Dispatcher.BeginInvoke(new Action(() => {
                if (e.State == SoftSledNative.State.Active) {
                    btnConnect.IsEnabled = false;
                    btnDisconnect.IsEnabled = true;
                } else if (e.State == SoftSledNative.State.Disconnected || e.State == SoftSledNative.State.Failed) {
                    btnConnect.IsEnabled = true;
                    btnDisconnect.IsEnabled = false;
                }
            }));
        }

        private void McxSessHandler_StatusChanged(object sender, StatusChangedArgs e) {
            // TODO: drive a status indicator / shell-open visual state from this.
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

            // FreeRDP path. Port 3390 + RDP-only security match the MCX
            // protocol requirements; for vanilla-RDP smoke tests against a
            // standard Windows host, override the port to 3389 and (if your
            // host requires it) flip rdpOnlySecurity off.
            const ushort port = 3390;
            const bool isMcxPort = (port == 3391    );

            // For MCX, libfreerdp's primary-order dispatcher hits proprietary
            // "super blt" orders ~1s after WMC starts initialising its UI; an
            // unknown order returns FALSE up the chain and tears the session
            // down. Until we land a non-fatal-on-unknown-order patch in
            // libfreerdp, decoding stays off for MCX (preserves the existing
            // headless behaviour the channel handlers rely on). Vanilla RDP
            // (port 3389 etc.) keeps decoding enabled so you get a visible
            // framebuffer.
            freeRdpClient.SetDecodeEnabled(!isMcxPort);
            m_logger.LogInfo($"FreeRDP: graphics decoding {(isMcxPort ? "DISABLED (MCX)" : "ENABLED")}");

            freeRdpClient.Configure(
                currConfig.RdpLoginHost,
                port: port,
                user: currConfig.RdpLoginUserName,
                password: currConfig.RdpLoginPassword,
                rdpOnlySecurity: true,
                ignoreCertificate: true);
            freeRdpClient.Connect();

            isConnecting = true;
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) {
            DisconnectRdp();
        }

        private void DisconnectRdp() {
            try {
                freeRdpClient?.Disconnect();
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"FreeRDP disconnect error: {ex.Message}");
            }
            // Drop the bitmap binding so WPF releases its reference; the
            // FreeRdpClient nulls its internal _bitmap on Dispose anyway.
            rdpDisplay.Source = null;
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

        // --- Cleanup ---

        private void MainWindow_Closed(object sender, EventArgs e) {
            DisconnectRdp();
            try {
                freeRdpClient?.Dispose();
            } catch { /* ignored */ }
            freeRdpClient = null;
            try { _audioPlayer?.Dispose(); } catch { }
            _audioPlayer = null;
            _audioDumper = null;
            _fastpathDispatcher = null;
        }

        private void btnPair_Click(object sender, RoutedEventArgs e) {
        }
    }
}
