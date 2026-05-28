using SoftSled.Components.Communication;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.Extender;
using SoftSled.Components.VirtualChannel;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SoftSledWPF.Components.Utility;

namespace SoftSledWPF.Components.Shell {
    /// <summary>
    /// Reusable user control that hosts a live MCX Extender session
    /// (FFME video pipeline + RDP framebuffer + MS-RRSP2 splash overlay).
    /// Extracted from the old FullScreenWindow so the shell can swap it
    /// in and out without recreating the WPF window itself.
    ///
    /// Lifecycle: shell calls <see cref="Start"/> once the control is in
    /// the visual tree; the control connects to the host configured in
    /// <see cref="SoftSledConfig"/> and raises <see cref="SessionEnded"/>
    /// when the user backs out (ESC) or the FreeRDP transport drops.
    /// </summary>
    public partial class ExtenderSessionControl : UserControl {

        private Logger m_logger;
        private ExtenderDevice m_device;
        private ExtenderCapabilities m_capabilities;

        private FreeRdpClient freeRdpClient;
        private VirtualChannelAvCtrlHandler AvCtrlHandler;
        private VirtualChannelDevCapsHandler DevCapsHandler;
        private VirtualChannelMcxSessHandler McxSessHandler;
        private VirtualChannelSplashHandler SplashHandler;

        private SoftSled.Components.Splash.SplashController _splashController;
        private SoftSled.Components.AudioVisual.SurfaceRouter _surfaceRouter;
        private const bool SplashPayloadBigEndian = true;

        private SoftSled.Components.AudioVisual.WmcFastpathAudioPlayer _audioPlayer;
        private SoftSled.Components.AudioVisual.WmcFastpathAudioDumper _audioDumper;
        private SoftSled.Components.AudioVisual.WmcFastpathRawDumper _rawDumper;
        private SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder _overlayDecoder;
        private SoftSledNative.FastpathCallback _fastpathDispatcher;
        private SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion _lastOverlay;
        private SoftSled.Components.AudioVisual.FfmeMediaController _ffmeController;
        // Phase 1 external-sync controller — alternative to FFME for
        // audio-only MP3 sessions. Constructed when
        // SoftSledConfig.UseExternalSyncMode is set; null otherwise.
        private SoftSled.Components.AudioVisual.ExternalSync.ExternalSyncMediaController _extSyncController;

        private System.Threading.Tasks.TaskCompletionSource<bool> _videoOpenComplete;

        private System.IO.StreamWriter _ffmeLogWriter;
        private System.Threading.Timer _ffmeLogFlushTimer;
        private readonly object _ffmeLogGate = new object();
        // _sessionActive gates keyboard forwarding to RDP. Driven from the
        // McxSess StatusChanged stream — true only while the WMC shell is
        // open (i.e. ready to consume input), false during connect, after
        // shell-close, or after disconnect. See McxSessHandler_StatusChanged.
        private volatile bool _sessionActive;
        // _transportActive tracks the FreeRDP transport state, used by
        // overlay logic that cares about "is the wire still up" rather
        // than "is WMC ready to take keys".
        private volatile bool _transportActive;
        private bool _started;
        private bool _ffmeInitialised;
        // Spinner state — the animation is applied directly to the
        // SpinnerRotate RotateTransform via BeginAnimation, so we don't need
        // a Storyboard. This bool just tracks "already running" so repeat
        // ShowConnectingOverlay calls don't restart the rotation from 0.
        private bool _spinnerRunning;

        // Mouse-forwarding state. _mouseEnabled mirrors SoftSledConfig.EnableMouseInput
        // and is re-read on Start so a toggle in the config UI takes effect on
        // next session. _pressedMouseFlags tracks which buttons we believe the
        // server thinks are currently down, so MouseLeave can synthesise the
        // releases instead of leaving the host stuck. _lastSentRX/Y dedupe
        // moves at the framebuffer-pixel granularity (avoids spamming the
        // shim for subpixel WPF moves when the display is upscaled).
        private bool _mouseEnabled = true;
        private ushort _pressedMouseFlags;
        private ushort _pressedXFlags;
        private int _lastSentRX = -1, _lastSentRY = -1;

        /// <summary>
        /// Raised when the live session ends — either because FreeRDP
        /// transitioned to Disconnected/Failed, or because the user
        /// explicitly requested a return to the shell. Shell hooks
        /// this to swap the page content back to the landing page.
        /// </summary>
        public event EventHandler SessionEnded;

        public ExtenderSessionControl() {
            InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) {
            Keyboard.Focus(this);
            // Pick up any change to EnableMouseInput made in the config UI
            // between sessions without forcing a reconnect. Safe to read
            // synchronously — config is a small file and this is the page
            // becoming visible.
            try {
                var cfg = SoftSledConfigManager.ReadConfig();
                _mouseEnabled = cfg.EnableMouseInput;
                ApplyMouseCursorPolicy();
            } catch { /* config file missing — leave defaults */ }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            // Defensive — if shell ever forgets to call Stop, Unloaded still
            // tears the session down so the FreeRDP worker / FFME pipeline
            // don't leak past the page transition.
            Stop();
        }

        // ------- Keyboard forwarding -----------------------------------
        // Routed through Window-level PreviewKeyDown by the shell when this
        // control is the active page. Shell calls these directly.

        private const uint MAPVK_VK_TO_VSC_EX = 4;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>
        /// Forward a WPF key event to the live RDP transport. Returns true
        /// if the key was consumed (so the shell can mark it handled).
        /// </summary>
        public bool ForwardKey(KeyEventArgs e, bool release) {
            if (!_sessionActive || freeRdpClient == null) return false;

            Key k = (e.Key == Key.System) ? e.SystemKey : e.Key;
            if (k == Key.None) return false;

            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk == 0) return false;

            uint sc = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
            if (sc == 0) return false;

            byte scancode = (byte)(sc & 0xFF);
            bool extended = ((sc >> 8) & 0xFF) == 0xE0;

            freeRdpClient.SendKey(scancode, extended, release);
            return true;
        }

        // ------- Mouse forwarding --------------------------------------
        // Wired on rdpDisplay (not the window) because all coordinate maths
        // is relative to the displayed bitmap. All handlers early-return if
        // !_sessionActive || !_mouseEnabled || bitmap not ready.

        private void AttachMouseHandlers() {
            rdpDisplay.MouseMove += RdpDisplay_MouseMove;
            rdpDisplay.MouseDown += RdpDisplay_MouseDown;
            rdpDisplay.MouseUp += RdpDisplay_MouseUp;
            rdpDisplay.MouseWheel += RdpDisplay_MouseWheel;
            rdpDisplay.MouseLeave += RdpDisplay_MouseLeave;
            rdpDisplay.Focusable = true;
            ApplyMouseCursorPolicy();
        }

        private void DetachMouseHandlers() {
            try { rdpDisplay.MouseMove -= RdpDisplay_MouseMove; } catch { }
            try { rdpDisplay.MouseDown -= RdpDisplay_MouseDown; } catch { }
            try { rdpDisplay.MouseUp -= RdpDisplay_MouseUp; } catch { }
            try { rdpDisplay.MouseWheel -= RdpDisplay_MouseWheel; } catch { }
            try { rdpDisplay.MouseLeave -= RdpDisplay_MouseLeave; } catch { }
            rdpDisplay.Cursor = null;
        }

        // Hide the local cursor over the RDP surface when forwarding is on,
        // so the server-painted remote cursor is the only one the user sees.
        // When mouse forwarding is disabled, restore the default arrow so it
        // is obvious nothing is being forwarded.
        private void ApplyMouseCursorPolicy() {
            rdpDisplay.Cursor = _mouseEnabled ? Cursors.None : Cursors.Arrow;
        }

        // Map a WPF point on rdpDisplay back to RDP framebuffer pixels.
        // Returns false if the bitmap isn't ready or the point is outside
        // the displayed image (letterboxed margins).
        private bool TryMapToRdp(Point wpfPt, out ushort rdpX, out ushort rdpY) {
            rdpX = 0; rdpY = 0;
            var bmp = freeRdpClient?.Bitmap;
            if (bmp == null || rdpDisplay.ActualWidth <= 0 || rdpDisplay.ActualHeight <= 0)
                return false;

            double scale = Math.Min(
                rdpDisplay.ActualWidth / bmp.PixelWidth,
                rdpDisplay.ActualHeight / bmp.PixelHeight);
            if (scale <= 0) return false;

            double offX = (rdpDisplay.ActualWidth - bmp.PixelWidth * scale) / 2.0;
            double offY = (rdpDisplay.ActualHeight - bmp.PixelHeight * scale) / 2.0;

            double rx = (wpfPt.X - offX) / scale;
            double ry = (wpfPt.Y - offY) / scale;
            if (rx < 0 || ry < 0 || rx >= bmp.PixelWidth || ry >= bmp.PixelHeight)
                return false;

            rdpX = (ushort)rx;
            rdpY = (ushort)ry;
            return true;
        }

        // Send a position-only MOVE event, deduped at framebuffer-pixel
        // granularity. Returns false if the position couldn't be mapped.
        private bool SendMouseMove(ushort rdpX, ushort rdpY) {
            if (rdpX == _lastSentRX && rdpY == _lastSentRY) return true;
            bool ok = freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_MOVE, rdpX, rdpY);
            if (ok) { _lastSentRX = rdpX; _lastSentRY = rdpY; }
            return ok;
        }

        private bool MouseGatesOpen() {
            return _sessionActive && _mouseEnabled
                && freeRdpClient != null
                && freeRdpClient.Bitmap != null;
        }

        private void RdpDisplay_MouseMove(object sender, MouseEventArgs e) {
            if (!MouseGatesOpen()) return;
            if (!TryMapToRdp(e.GetPosition(rdpDisplay), out var x, out var y)) return;
            SendMouseMove(x, y);
        }

        private void RdpDisplay_MouseDown(object sender, MouseButtonEventArgs e) {
            if (!MouseGatesOpen()) return;
            if (!TryMapToRdp(e.GetPosition(rdpDisplay), out var x, out var y)) return;
            // Ensure focus is on the session so subsequent keyboard input is gated
            // correctly through ForwardKey (shell-level capture also requires it).
            rdpDisplay.Focus();
            SendMouseMove(x, y);
            switch (e.ChangedButton) {
                case MouseButton.Left:
                    freeRdpClient.SendMouse((ushort)(SoftSledNative.PTR_FLAGS_DOWN | SoftSledNative.PTR_FLAGS_BUTTON1), x, y);
                    _pressedMouseFlags |= SoftSledNative.PTR_FLAGS_BUTTON1;
                    break;
                case MouseButton.Right:
                    freeRdpClient.SendMouse((ushort)(SoftSledNative.PTR_FLAGS_DOWN | SoftSledNative.PTR_FLAGS_BUTTON2), x, y);
                    _pressedMouseFlags |= SoftSledNative.PTR_FLAGS_BUTTON2;
                    break;
                case MouseButton.Middle:
                    freeRdpClient.SendMouse((ushort)(SoftSledNative.PTR_FLAGS_DOWN | SoftSledNative.PTR_FLAGS_BUTTON3), x, y);
                    _pressedMouseFlags |= SoftSledNative.PTR_FLAGS_BUTTON3;
                    break;
                case MouseButton.XButton1:
                    freeRdpClient.SendExtendedMouse((ushort)(SoftSledNative.PTR_XFLAGS_DOWN | SoftSledNative.PTR_XFLAGS_BUTTON1), x, y);
                    _pressedXFlags |= SoftSledNative.PTR_XFLAGS_BUTTON1;
                    break;
                case MouseButton.XButton2:
                    freeRdpClient.SendExtendedMouse((ushort)(SoftSledNative.PTR_XFLAGS_DOWN | SoftSledNative.PTR_XFLAGS_BUTTON2), x, y);
                    _pressedXFlags |= SoftSledNative.PTR_XFLAGS_BUTTON2;
                    break;
            }
        }

        private void RdpDisplay_MouseUp(object sender, MouseButtonEventArgs e) {
            if (!MouseGatesOpen()) return;
            if (!TryMapToRdp(e.GetPosition(rdpDisplay), out var x, out var y)) return;
            SendMouseMove(x, y);
            switch (e.ChangedButton) {
                case MouseButton.Left:
                    freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_BUTTON1, x, y);
                    _pressedMouseFlags &= unchecked((ushort)~SoftSledNative.PTR_FLAGS_BUTTON1);
                    break;
                case MouseButton.Right:
                    freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_BUTTON2, x, y);
                    _pressedMouseFlags &= unchecked((ushort)~SoftSledNative.PTR_FLAGS_BUTTON2);
                    break;
                case MouseButton.Middle:
                    freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_BUTTON3, x, y);
                    _pressedMouseFlags &= unchecked((ushort)~SoftSledNative.PTR_FLAGS_BUTTON3);
                    break;
                case MouseButton.XButton1:
                    freeRdpClient.SendExtendedMouse(SoftSledNative.PTR_XFLAGS_BUTTON1, x, y);
                    _pressedXFlags &= unchecked((ushort)~SoftSledNative.PTR_XFLAGS_BUTTON1);
                    break;
                case MouseButton.XButton2:
                    freeRdpClient.SendExtendedMouse(SoftSledNative.PTR_XFLAGS_BUTTON2, x, y);
                    _pressedXFlags &= unchecked((ushort)~SoftSledNative.PTR_XFLAGS_BUTTON2);
                    break;
            }
        }

        private void RdpDisplay_MouseWheel(object sender, MouseWheelEventArgs e) {
            if (!MouseGatesOpen()) return;
            if (!TryMapToRdp(e.GetPosition(rdpDisplay), out var x, out var y)) return;

            // WPF Delta is signed, ±120 per notch. RDP packs a 9-bit signed
            // magnitude into the low bits of flags, with PTR_FLAGS_WHEEL_NEGATIVE
            // indicating direction. Clamp magnitude to the 9-bit max.
            int delta = e.Delta;
            int mag = Math.Min(Math.Abs(delta), SoftSledNative.PTR_WHEEL_ROTATION_MASK);
            ushort flags = (ushort)(SoftSledNative.PTR_FLAGS_WHEEL | (mag & SoftSledNative.PTR_WHEEL_ROTATION_MASK));
            if (delta < 0) flags |= SoftSledNative.PTR_FLAGS_WHEEL_NEGATIVE;
            freeRdpClient.SendMouse(flags, x, y);
        }

        // When the cursor leaves rdpDisplay (e.g. drag-out-of-window), release
        // any buttons we sent down — otherwise the host sees a stuck-down state.
        private void RdpDisplay_MouseLeave(object sender, MouseEventArgs e) {
            if (freeRdpClient == null) return;
            if ((_pressedMouseFlags & SoftSledNative.PTR_FLAGS_BUTTON1) != 0)
                freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_BUTTON1, (ushort)Math.Max(_lastSentRX, 0), (ushort)Math.Max(_lastSentRY, 0));
            if ((_pressedMouseFlags & SoftSledNative.PTR_FLAGS_BUTTON2) != 0)
                freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_BUTTON2, (ushort)Math.Max(_lastSentRX, 0), (ushort)Math.Max(_lastSentRY, 0));
            if ((_pressedMouseFlags & SoftSledNative.PTR_FLAGS_BUTTON3) != 0)
                freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_BUTTON3, (ushort)Math.Max(_lastSentRX, 0), (ushort)Math.Max(_lastSentRY, 0));
            if ((_pressedXFlags & SoftSledNative.PTR_XFLAGS_BUTTON1) != 0)
                freeRdpClient.SendExtendedMouse(SoftSledNative.PTR_XFLAGS_BUTTON1, (ushort)Math.Max(_lastSentRX, 0), (ushort)Math.Max(_lastSentRY, 0));
            if ((_pressedXFlags & SoftSledNative.PTR_XFLAGS_BUTTON2) != 0)
                freeRdpClient.SendExtendedMouse(SoftSledNative.PTR_XFLAGS_BUTTON2, (ushort)Math.Max(_lastSentRX, 0), (ushort)Math.Max(_lastSentRY, 0));
            _pressedMouseFlags = 0;
            _pressedXFlags = 0;
        }

        // ------- Lifecycle ---------------------------------------------

        /// <summary>
        /// Initialise FFME logging, channel handlers, splash controller,
        /// and kick off the RDP connection. Idempotent — repeated calls
        /// from a stale shell are a no-op.
        /// </summary>
        public void Start() {
            if (_started) return;
            _started = true;

            // Visible feedback while FreeRDP works through the connect /
            // security / channel-bind handshake. Hidden the instant we
            // observe State.Active in FreeRdpClient_StateChanged.
            ShowConnectingOverlay("Waiting to connect to Windows Media Center");

            // Defer the heavy initialisation (FFME, FreeRDP shim, channel
            // handlers, config read, UPnP device) by one dispatcher cycle at
            // Background priority. Background (4) runs *after* Render (7),
            // which guarantees the overlay/spinner have been laid out and
            // painted before we block the UI thread with several hundred ms
            // of synchronous work. Without this you see the landing page
            // freeze for a beat before the spinner appears.
            Dispatcher.BeginInvoke(new Action(StartInternal),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }

        // Real init body — runs one dispatcher tick after Start() to let the
        // spinner paint first. All the heavy lifting lives here.
        private void StartInternal() {
            // Guard against Stop() being called between Start() scheduling
            // this and the dispatcher running it (e.g. the user backed out
            // during the brief gap). _started is the flag Stop() clears.
            if (!_started) return;

            InitialiseLogger();
            EnsureFfmeInitialised();

            // Load the SoftSled Config
            var cfg = SoftSledConfigManager.ReadConfig();
            // Load the Extender Capabilities
            m_capabilities = new ExtenderCapabilities();
            // Create the FreeRDP Client
            freeRdpClient = new FreeRdpClient();
            freeRdpClient.DataReceived += FreeRdpClient_DataReceived;
            freeRdpClient.StateChanged += FreeRdpClient_StateChanged;
            freeRdpClient.FrameReady += FreeRdpClient_FrameReady;
            foreach (var ch in new[] { "McxSess", "devcaps", "avctrl", "splash" })
                freeRdpClient.RegisterChannel(ch);


            // Create Fastpath Audio Player for UI Sounds
            _audioPlayer = new SoftSled.Components.AudioVisual.WmcFastpathAudioPlayer(cfg.LogRdpFastpath ? m_logger : null);
            // Enable Fastpath Audio Dumping
            string audioDumpDir = Environment.GetEnvironmentVariable("SOFTSLED_AUDIO_DUMP");
            if (!string.IsNullOrWhiteSpace(audioDumpDir)) {
                _audioDumper = new SoftSled.Components.AudioVisual.WmcFastpathAudioDumper(
                    m_logger, audioDumpDir);
            }
            // Enable Fastpath Raw Dumping
            string rawDumpDir = Environment.GetEnvironmentVariable("SOFTSLED_FASTPATH_RAW_DUMP");
            if (!string.IsNullOrWhiteSpace(rawDumpDir)) {
                _rawDumper = new SoftSled.Components.AudioVisual.WmcFastpathRawDumper(
                    m_logger, rawDumpDir);
            }
            _overlayDecoder = new SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder(cfg.LogRdpFastpath ? m_logger : null);
            _overlayDecoder.OverlayRegionChanged += OnOverlayRegionChanged;
            _overlayDecoder.ZoomModeChanged += OnZoomModeChanged;

            _fastpathDispatcher = (user, code, data, length) => {
                _rawDumper?.OnFastpath(user, code, data, length);
                _audioPlayer?.OnFastpath(user, code, data, length);
                _audioDumper?.OnFastpath(user, code, data, length);
                _overlayDecoder?.OnFastpath(user, code, data, length);
            };
            freeRdpClient.SetFastpathCallback(_fastpathDispatcher);

            MediaCanvas.SizeChanged += MediaCanvas_SizeChanged;

           

            McxSessHandler = new VirtualChannelMcxSessHandler(cfg.LogMcxSessChannel ? m_logger : null);
            McxSessHandler.VirtualChannelSend += On_VirtualChannelSend;
            DevCapsHandler = new VirtualChannelDevCapsHandler(cfg.LogDevCapsChannel ? m_logger : null, m_capabilities.GetDeviceCapabilities());
            DevCapsHandler.VirtualChannelSend += On_VirtualChannelSend;
            AvCtrlHandler = new VirtualChannelAvCtrlHandler(cfg.LogAvCtrlChannel ? m_logger : null);
            AvCtrlHandler.VirtualChannelSend += On_VirtualChannelSend;
            SplashHandler = new VirtualChannelSplashHandler(m_logger);
            SplashHandler.VirtualChannelSend += On_VirtualChannelSend;

            // Playback controller selection. Phase 1 of the external-
            // sync plan: when SoftSledConfig.UseExternalSyncMode is
            // set, ExternalSyncMediaController takes over (audio-only
            // for now — owns its own libav decoder + NAudio renderer,
            // bypasses FFME entirely for audio). Anything that needs
            // video still uses FFME via the controller swap below.
            // Phase 2/3 will move video into the same controller with
            // FFME used as a video-only decoder driven by NAudio's
            // master clock.
            if (cfg.UseExternalSyncMode) {
                _extSyncController = new SoftSled.Components.AudioVisual.ExternalSync
                    .ExternalSyncMediaController(m_logger, cfg.AudioSyncOffsetMs);
                AvCtrlHandler.MediaController = _extSyncController;
                // Phase 2: hand the FFME MediaElement to the
                // external-sync controller so it can chase NAudio's
                // master clock via SpeedRatio nudges when video is
                // present. The controller subscribes to MediaOpened
                // / MediaClosed itself; until video opens it just
                // sits idle.
                _extSyncController.AttachVideoMediaElement(Media);
                m_logger.LogInfo($"[controller] external-sync mode active " +
                                 $"(audio: libav+NAudio; video: FFME with SpeedRatio nudges; " +
                                 $"audioSyncOffset={cfg.AudioSyncOffsetMs}ms)");
            } else {
                _ffmeController = new SoftSled.Components.AudioVisual.FfmeMediaController(Media, m_logger);
                AvCtrlHandler.MediaController = _ffmeController;
            }

            // Read the splash-audio toggle here — config is also read again
            // later in this method (line ~524) for the mouse + pairing fields,
            // but the SplashController is constructed earlier and needs to
            // know whether the UI sound player should be wired in. Reading
            // the config twice is cheap (small XML file, ~1 ms).
            bool enableSplashAudio = true;
            try {
                enableSplashAudio = SoftSledConfigManager.ReadConfig()?.EnableSplashAudio ?? true;
            } catch { /* config missing — default ON */ }
            _splashController = new SoftSled.Components.Splash.SplashController(
                m_logger, Dispatcher, SplashPayloadBigEndian, SplashHandler.SendBytes,
                enableSplashAudio: enableSplashAudio);
            _splashController.AttachHost(splashHost);
            SplashHandler.AttachController(_splashController);

            // Surface routing for DMCT OpenMedia. In GDI mode this is a
            // single-surface fallback; in RUI mode it ties the video
            // element's position to the splash surface identified by
            // the incoming Surface ID. AvCtrlHandler fires
            // VideoSurfaceRequested → SurfaceRouter.RouteVideoToSurface.
            var renderMode = m_capabilities?.GetRenderMode()
                             ?? SoftSled.Components.Extender.WMCRenderMode.GDI;
            _surfaceRouter = new SoftSled.Components.AudioVisual.SurfaceRouter(
                renderMode, MediaCanvas, Media, _splashController, m_logger);
            AvCtrlHandler.VideoSurfaceRequested += sid => _surfaceRouter.RouteVideoToSurface(sid);
            AvCtrlHandler.VideoPipelineClosed += () => _surfaceRouter.ReleaseSurface();

            _videoOpenComplete = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            AvCtrlHandler.VideoPipelineReady += stream =>
                Dispatcher.BeginInvoke(new Action(async () => {
                    try {
                        bool hasVideo =
                            (stream as SoftSled.Components.AudioVisual.AsfFfmeInputStream)?.HasVideo
                            ?? true;
                        if (hasVideo) {
                            SizeMediaToCanvasFill();
                            Media.Visibility = Visibility.Visible;
                        } else {
                            Media.Visibility = Visibility.Collapsed;
                        }
                        var openSw = System.Diagnostics.Stopwatch.StartNew();
                        await Media.Open(stream);
                        openSw.Stop();
                        m_logger.LogInfo($"[ffme] Media.Open succeeded in {openSw.ElapsedMilliseconds}ms " +
                                         $"({(hasVideo ? "video+audio" : "audio-only")})");
                    } catch (Exception ex) {
                        m_logger.LogError($"[ffme] Media.Open failed: {ex.Message}");
                    } finally {
                        _videoOpenComplete?.TrySetResult(true);
                    }
                }));

            AvCtrlHandler.AudioPipelineReady += stream =>
                Dispatcher.BeginInvoke(new Action(async () => {
                    try {
                        if (_videoOpenComplete != null) {
                            var tcs = _videoOpenComplete.Task;
                            var done = await System.Threading.Tasks.Task.WhenAny(
                                tcs, System.Threading.Tasks.Task.Delay(2000));
                            if (done != tcs) {
                                m_logger.LogInfo("[ffme] audio open: video TCS timed out, opening anyway");
                            }
                        }
                        await MediaAudio.Open(stream);
                        m_logger.LogInfo("[ffme] MediaAudio.Open succeeded (PCM audio pipeline)");
                    } catch (Exception ex) {
                        m_logger.LogError($"[ffme] MediaAudio.Open failed: {ex.Message}");
                    }
                }));

            AvCtrlHandler.VideoPipelineClosed += () =>
                Dispatcher.BeginInvoke(new Action(async () => {
                    try { await Media.Close(); } catch (Exception ex) { m_logger.LogError($"[ffme] Media.Close failed: {ex.Message}"); }
                    try { await MediaAudio.Close(); } catch (Exception ex) { m_logger.LogError($"[ffme] MediaAudio.Close failed: {ex.Message}"); }
                    Media.Visibility = Visibility.Collapsed;
                    _lastOverlay = null;
                    _videoOpenComplete?.TrySetResult(false);
                    _videoOpenComplete = new System.Threading.Tasks.TaskCompletionSource<bool>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                }));

            McxSessHandler.StatusChanged += McxSessHandler_StatusChanged;

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            // Snapshot the mouse-input preference at session start. The
            // EnableMouseInput config field is also re-read in
            // OnLoaded so toggling it in settings between sessions takes
            // effect without an app restart.
            _mouseEnabled = config.EnableMouseInput;
            AttachMouseHandlers();
            if (!config.IsPaired) {
                m_logger.LogInfo("Extender is not paired!");
                HideConnectingOverlay();
                MessageBox.Show("SoftSled is currently not paired with Windows Media Center.");
                _started = false;
                RaiseSessionEnded();
                return;
            }
            m_logger.LogInfo("Extender is paired with " + config.RdpLoginHost);
            // Personalise the banner now that we know who we're calling.
            UpdateConnectingStatus("Waiting to connect to " + config.RdpLoginHost + " ...");

            Connect(config);
        }

        /// <summary>
        /// Tear down everything Start() set up. Safe to call repeatedly
        /// and from the page Unloaded path even if Start() already cleaned
        /// up after a connection failure.
        /// </summary>
        public void Stop() {
            if (!_started) return;
            _started = false;

            // Make sure the spinner storyboard isn't left running once
            // the visual tree is torn down — DispatcherTimer-style leaks
            // here will keep the page alive past navigation.
            HideConnectingOverlay();

            DisconnectRdp();
            try { freeRdpClient?.Dispose(); } catch { }
            freeRdpClient = null;
            try { _audioPlayer?.Dispose(); } catch { }
            try { _rawDumper?.Dispose(); } catch { }
            if (AvCtrlHandler != null) AvCtrlHandler.MediaController = null;
            try { _ffmeController?.Dispose(); } catch { }
            _ffmeController = null;
            try { _extSyncController?.Dispose(); } catch { }
            _extSyncController = null;
            if (_overlayDecoder != null) {
                _overlayDecoder.OverlayRegionChanged -= OnOverlayRegionChanged;
                _overlayDecoder.ZoomModeChanged -= OnZoomModeChanged;
            }
            try { m_device?.Stop(); } catch { }
            m_device = null;
            _audioPlayer = null;
            _audioDumper = null;
            _rawDumper = null;
            _overlayDecoder = null;
            _fastpathDispatcher = null;

            try { _ffmeLogFlushTimer?.Dispose(); } catch { }
            _ffmeLogFlushTimer = null;
            try {
                lock (_ffmeLogGate) {
                    _ffmeLogWriter?.Flush();
                    _ffmeLogWriter?.Dispose();
                }
            } catch { }
            _ffmeLogWriter = null;

            try {
                MediaCanvas.SizeChanged -= MediaCanvas_SizeChanged;
            } catch { }
            DetachMouseHandlers();
            _pressedMouseFlags = 0;
            _pressedXFlags = 0;
            _lastSentRX = -1; _lastSentRY = -1;
        }

        private void MediaCanvas_SizeChanged(object sender, SizeChangedEventArgs sizeEv) {
            if (_lastOverlay != null) ApplyOverlayMapping(_lastOverlay);
        }

        // ---- Connecting overlay ---------------------------------------

        /// <summary>
        /// Show the "waiting to connect" curtain and start the spinner.
        /// Idempotent — repeated calls just refresh the message.
        /// </summary>
        private void ShowConnectingOverlay(string message) {
            if (ConnectingOverlay == null) return;
            ConnectingStatusText.Text = message;
            ConnectingOverlay.Visibility = Visibility.Visible;
            StartSpinner();
        }

        private void UpdateConnectingStatus(string message) {
            if (ConnectingStatusText == null) return;
            if (ConnectingOverlay.Visibility != Visibility.Visible) return;
            ConnectingStatusText.Text = message;
        }

        private void HideConnectingOverlay() {
            if (ConnectingOverlay == null) return;
            if (ConnectingOverlay.Visibility == Visibility.Collapsed) return;
            ConnectingOverlay.Visibility = Visibility.Collapsed;
            StopSpinner();
        }

        private void StartSpinner() {
            if (_spinnerRunning) return;
            if (SpinnerRotate == null) return; // visual tree not realised yet
            var anim = new DoubleAnimation {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1.4),
                RepeatBehavior = RepeatBehavior.Forever
            };
            // Apply the animation straight to the RotateTransform's Angle
            // property. Storyboard targeting works on a FrameworkElement +
            // NameScope, but RotateTransform is a Freezable in a render-
            // transform slot — the storyboard sees it as "not in the tree"
            // and silently no-ops, which is why the original Begin() never
            // produced any motion. BeginAnimation on the Freezable itself
            // sidesteps that whole layer.
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            _spinnerRunning = true;
        }

        private void StopSpinner() {
            if (!_spinnerRunning) return;
            if (SpinnerRotate != null) {
                // Passing null clears the active animation and snaps the
                // property back to its local/dependency value (Angle=0).
                SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
            _spinnerRunning = false;
        }

        private void EnsureFfmeInitialised() {
            if (_ffmeInitialised) return;
            _ffmeInitialised = true;

            string ffmpegDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Tools\\ffmpeg\\x64";
            // FFmpegDirectory is process-wide and must be set BEFORE the
            // first MediaElement loads; safe to set repeatedly to the same
            // value, FFME ignores the second set.
            Unosquare.FFME.Library.FFmpegDirectory = ffmpegDir;

            // Force-load the FFmpeg native DLLs eagerly on a background
            // thread. Without this, FFME does the load lazily inside
            // the first Media.Open() — that's ~30 MB of DLLs
            // (avcodec-58, avformat-58, avutil-56, swscale-5,
            // swresample-3, postproc-55) plus their internal table
            // initialisation, observed at 200-500 ms on first call.
            // Doing it here on the thread pool overlaps with the
            // FreeRDP handshake (which takes seconds), so by the time
            // WMC sends OpenMedia → first Start, FFME's Open call
            // skips the lib-load entirely. LoadFFmpegAsync returns
            // false if already loaded, so a session reset is a free
            // no-op.
            System.Threading.Tasks.Task.Run(() => {
                try {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool loaded = Unosquare.FFME.Library.LoadFFmpegAsync()
                                   .GetAwaiter().GetResult();
                    sw.Stop();
                    m_logger?.LogInfo($"[ffme] preload completed in {sw.ElapsedMilliseconds}ms " +
                                      $"(loaded={loaded}, version={Unosquare.FFME.Library.FFmpegVersionInfo})");
                } catch (Exception ex) {
                    m_logger?.LogError($"[ffme] preload failed: {ex.Message}");
                }
            });

            try {
                string ffmeLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                            "softsled-ffme.log");
                _ffmeLogWriter = new System.IO.StreamWriter(
                    new System.IO.FileStream(ffmeLogPath, System.IO.FileMode.Create,
                                             System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    System.Text.Encoding.UTF8) { AutoFlush = false };
                _ffmeLogWriter.WriteLine($"# FFME internal log, started {DateTime.Now:HH:mm:ss.fff}");
                _ffmeLogWriter.WriteLine($"# FFmpegDirectory={ffmpegDir} " +
                                         $"(exists={System.IO.Directory.Exists(ffmpegDir)})");
                _ffmeLogWriter.Flush();

                _ffmeLogFlushTimer = new System.Threading.Timer(_ => {
                    try {
                        lock (_ffmeLogGate) { _ffmeLogWriter?.Flush(); }
                    } catch { }
                }, null, 2000, 2000);

                Media.MessageLogged += (s, e) => {
                    var mt = e.MessageType;
                    if (mt == Unosquare.FFME.Common.MediaLogMessageType.Trace ||
                        mt == Unosquare.FFME.Common.MediaLogMessageType.Debug) return;
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[FFME/{mt}] {e.AspectName}: {e.Message}");
                        }
                    } catch { }
                };
                Unosquare.FFME.MediaElement.FFmpegMessageLogged += (s, e) => {
                    var mt = e.MessageType;
                    if (mt == Unosquare.FFME.Common.MediaLogMessageType.Trace ||
                        mt == Unosquare.FFME.Common.MediaLogMessageType.Debug ||
                        mt == Unosquare.FFME.Common.MediaLogMessageType.Info) return;
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[libav/{mt}] {e.AspectName}: {e.Message}");
                        }
                    } catch { }
                };
                Media.MediaFailed += (s, e) => {
                    string ex = e.ErrorException?.ToString() ?? "(no exception)";
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[MEDIA_FAILED] {ex}");
                        }
                    } catch { }
                    m_logger.LogError($"[ffme] MediaFailed: {e.ErrorException?.Message ?? "(no message)"}");
                };
                Media.MediaInitializing += (s, e) => {
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     "[MEDIA_INITIALIZING]");
                        }
                    } catch { }
                };
                Media.MediaOpening += (s, e) => {
                    try {
                        lock (_ffmeLogGate) {
                            string streams = string.Join(",", e.Info.Streams.Keys);
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[MEDIA_OPENING] format={e.Info.Format} " +
                                                     $"duration={e.Info.Duration} streams=[{streams}]");
                        }
                    } catch { }
                    // FFME's per-stream clock policy. Two regimes:
                    //
                    //   * IsTimeSyncDisabled=false (preferred — set
                    //     here): FFME's master clock locks to audio
                    //     and aligns video to it for tight lip-sync
                    //     at 1×. The downside is that when the wire
                    //     stops delivering audio (server-side trick
                    //     play), the audio buffer drains and the
                    //     renderer enters SYNC-BUFFER — video freezes.
                    //     AudioSilenceInjector (armed in
                    //     FfmeMediaController.AttachRtspClient) solves
                    //     that by emitting synthetic audio MAUs so the
                    //     buffer never drains.
                    //
                    //   * IsTimeSyncDisabled=true (fallback): FFME
                    //     runs each stream on its own clock. Trick-
                    //     play freezes go away even without the
                    //     injector, but lip-sync at 1× breaks because
                    //     the per-stream first-MAU offset (~1.5 s
                    //     from the server's prior-IDR padding)
                    //     becomes visible as permanent skew.
                    //
                    // Default to sync-on; the silence injector keeps
                    // the buffer fed.
                    try { e.Options.IsTimeSyncDisabled = false; } catch { }
                };
                MediaAudio.MediaOpening += (s, e) => {
                    // Audio-only sessions can't suffer the trick-play
                    // freeze (no video to wait on); leaving sync on
                    // here keeps behaviour symmetric with the main
                    // element so position reporting / seek behave the
                    // same in both modes.
                    try { e.Options.IsTimeSyncDisabled = false; } catch { }
                };
                Media.MediaOpened += (s, e) => {
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     "[MEDIA_OPENED]");
                        }
                    } catch { }
                    m_logger.LogInfo("[ffme] MediaOpened");
                };

                Unosquare.FFME.Library.FFmpegLogLevel = 32;
            } catch (Exception ex) {
                m_logger?.LogError($"[ffme] log capture init failed: {ex.Message}");
            }
        }

        private void OnOverlayRegionChanged(object sender,
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion region) {
            Dispatcher.BeginInvoke(new Action(() => {
                _lastOverlay = region;
                ApplyOverlayMapping(region);
            }));
        }

        private void OnZoomModeChanged(object sender,
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode mode) {
            Dispatcher.BeginInvoke(new Action(() => {
                System.Windows.Media.Stretch stretch;
                switch (mode) {
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.Normal:
                        stretch = System.Windows.Media.Stretch.Uniform; break;
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.StretchSides:
                        stretch = System.Windows.Media.Stretch.Fill; break;
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.StretchTopBottom:
                        stretch = System.Windows.Media.Stretch.UniformToFill; break;
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.Dynamic:
                        stretch = System.Windows.Media.Stretch.Fill; break;
                    default:
                        stretch = System.Windows.Media.Stretch.Uniform; break;
                }
                Media.Stretch = stretch;
                m_logger.LogInfo($"[zoom] WMC mode {mode} → Media.Stretch={stretch}");
            }));
        }

        private void ApplyOverlayMapping(
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion region) {
            if (region == null || MediaCanvas == null) return;
            var bmp = freeRdpClient?.Bitmap;
            if (bmp == null || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return;
            double srcW = bmp.PixelWidth;
            double srcH = bmp.PixelHeight;

            double cellW = MediaCanvas.ActualWidth;
            double cellH = MediaCanvas.ActualHeight;
            if (cellW <= 0 || cellH <= 0) return;

            double scale = Math.Min(cellW / srcW, cellH / srcH);
            double imgW = srcW * scale;
            double imgH = srcH * scale;
            double imgOffX = (cellW - imgW) / 2.0;
            double imgOffY = (cellH - imgH) / 2.0;

            double wpfX = imgOffX + region.X * scale;
            double wpfY = imgOffY + region.Y * scale;
            double wpfW = region.Width * scale;
            double wpfH = region.Height * scale;

            Canvas.SetLeft(Media, wpfX);
            Canvas.SetTop(Media, wpfY);
            Media.Width = wpfW;
            Media.Height = wpfH;

            m_logger.LogDebug($"[overlay] RDP {region} (src {srcW}x{srcH}) → " +
                              $"WPF ({wpfX:F1},{wpfY:F1}) {wpfW:F1}x{wpfH:F1} " +
                              $"in {cellW:F1}x{cellH:F1} cell, scale={scale:F3}");
        }

        private void SizeMediaToCanvasFill() {
            if (MediaCanvas == null) return;
            Canvas.SetLeft(Media, 0);
            Canvas.SetTop(Media, 0);
            Media.Width = double.IsNaN(MediaCanvas.ActualWidth) ? 0 : MediaCanvas.ActualWidth;
            Media.Height = double.IsNaN(MediaCanvas.ActualHeight) ? 0 : MediaCanvas.ActualHeight;
        }

        private void FreeRdpClient_FrameReady(object sender, EventArgs e) {
            rdpDisplay.Source = freeRdpClient.Bitmap;
            m_logger.LogInfo($"RDP framebuffer ready: {freeRdpClient.Bitmap.PixelWidth}x{freeRdpClient.Bitmap.PixelHeight}");
        }

        void InitialiseLogger() {
            m_logger = new TextBoxLogger(loggerTextBox, Window.GetWindow(this));
            m_logger.IsLoggingDebug = true;

            // Apply the persistent EnableLogger toggle from config. The
            // textbox starts Collapsed in XAML so the default ("not shown")
            // requires no change; if config asks for it on, show it now.
            try {
                if (SoftSledConfigManager.ReadConfig().EnableLogger) {
                    SetLoggerVisible(true);
                }
            } catch { /* config read failure → leave hidden */ }
        }

        /// <summary>
        /// Show or hide the on-screen logger textbox. Called by:
        ///   * <see cref="InitialiseLogger"/> at session start (reads config)
        ///   * <see cref="ToggleLogger"/> when the user presses Ctrl+L
        ///   * the Debugging config-page checkbox handler in the shell
        ///
        /// Idempotent and safe to call from any thread (marshals to UI).
        /// </summary>
        public void SetLoggerVisible(bool visible) {
            if (!Dispatcher.CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => SetLoggerVisible(visible)));
                return;
            }
            if (loggerTextBox != null) {
                loggerTextBox.Visibility = visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Flip the logger textbox visibility. Used by the Ctrl+L shortcut
        /// in <see cref="ShellWindow"/> — provides a transient on/off toggle
        /// without touching the persistent <c>EnableLogger</c> config
        /// (so a quick "let me peek at the log" doesn't permanently change
        /// the user's preference).
        /// </summary>
        public void ToggleLogger() {
            if (!Dispatcher.CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(ToggleLogger));
                return;
            }
            if (loggerTextBox == null) return;
            bool visible = loggerTextBox.Visibility == Visibility.Visible;
            SetLoggerVisible(!visible);
        }

        private void FreeRdpClient_DataReceived(object sender, DataReceived e) {
            try {
                if (e.channelName == "McxSess") {
                    McxSessHandler.ProcessData(e.data);
                } else if (e.channelName == "devcaps") {
                    DevCapsHandler.ProcessData(e.data);
                } else if (e.channelName == "avctrl") {
                    AvCtrlHandler.ProcessData(e.data);
                } else if (e.channelName == "splash") {
                    SplashHandler.ProcessData(e.data);
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
            // _sessionActive is no longer driven from the FreeRDP transport
            // state — see McxSessHandler_StatusChanged. The transport going
            // Active happens several seconds before WMC's shell is actually
            // ready to receive input, and gating keyboard forwarding on
            // shell-readiness avoids both pre-shell keystrokes being lost
            // and post-shell-closed keystrokes going into a stale RDP
            // session.
            _transportActive = (e.State == SoftSledNative.State.Active);

            if (e.State == SoftSledNative.State.Disconnected
             || e.State == SoftSledNative.State.Failed) {
                // Transport dropped — keyboard forwarding must stop
                // immediately, even if McxSess never gets to fire its own
                // shellOpen=false (the channel goes away with the socket).
                _sessionActive = false;
                try { _splashController?.Reset(); } catch { }
            }

            Dispatcher.BeginInvoke(new Action(() => {
                if (e.State == SoftSledNative.State.Active) {
                    if (splashHost != null) splashHost.Visibility = Visibility.Visible;
                    // RDP transport is up, but the WMC shell hasn't
                    // necessarily opened yet — keep the curtain in place
                    // and update the message. The McxSess StatusChanged
                    // handler is responsible for actually hiding it once
                    // it observes shellOpen=true.
                    UpdateConnectingStatus("Connected — waiting for Windows Media Center...");
                } else if (e.State == SoftSledNative.State.Disconnected || e.State == SoftSledNative.State.Failed) {
                    if (splashHost != null) splashHost.Visibility = Visibility.Collapsed;
                    HideConnectingOverlay();
                    // Fire the SessionEnded event so the shell pops us back
                    // to the landing page. Defer to next dispatcher tick so
                    // any in-flight cleanup unwinds first.
                    Dispatcher.BeginInvoke(new Action(RaiseSessionEnded));
                } else {
                    // Intermediate transports (Connecting, NLA, Capabilities...).
                    // Keep the user informed instead of just showing a black box.
                    UpdateConnectingStatus($"Connecting to Windows Media Center ({e.State})...");
                }
            }));
        }

        private void RaiseSessionEnded() {
            SessionEnded?.Invoke(this, EventArgs.Empty);
        }

        private void McxSessHandler_StatusChanged(object sender, StatusChangedArgs e) {
            // Source-of-truth for session liveness from WMC's own status
            // stream. shellOpen=true means the WMC shell is up and willing
            // to accept input; shellOpen=false either means startup
            // ("Starting Experience...", statusInt=null) or a host-side
            // shell teardown (statusInt set to a disconnect reason). We
            // flip _sessionActive here so ForwardKey() stops emitting RDP
            // input at the right moment — neither too early (lost during
            // the FreeRDP→WMC shell warmup) nor too late (sent into a
            // stale shell that's already gone away).
            _sessionActive = e.shellOpen;
            if (!e.shellOpen && e.statusInt.HasValue) {
                m_logger?.LogInfo(
                    $"MCXSESS: shell closed (reason={e.statusInt.Value}, " +
                    $"text=\"{e.statusText}\")");
            }

            Dispatcher.BeginInvoke(new Action(() => {
                if (e.shellOpen) {
                    rdpDisplay.Visibility = Visibility.Visible;
                    // WMC shell is now ready to paint — drop the curtain
                    // so the RDP / splash layers underneath become visible.
                    // We do this here rather than on FreeRDP State.Active
                    // because the transport going Active happens several
                    // seconds before WMC actually loads its UI.
                    HideConnectingOverlay();
                } else {
                    rdpDisplay.Visibility = Visibility.Hidden;
                    // Shell closed. Deliberately do NOT re-show the
                    // connecting overlay here — the curtain should only
                    // appear during the initial session start, not after
                    // a WMC shell close. If transport is still up the
                    // user just sees the black/splash background until
                    // they ESC out; if transport drops too the state-
                    // change path will pop us back to landing.
                }
            }));
        }

        private void Connect(SoftSledConfig currConfig) {
            try {
                // Confirm the machine has at least one IPv4 adapter, same
                // safety net the previous FullScreenWindow had.
                var host = Dns.GetHostEntry(Dns.GetHostName());
                if (!host.AddressList.Any(xx => xx.AddressFamily == AddressFamily.InterNetwork)) {
                    throw new Exception("No network adapters with an IPv4 address in the system!");
                }
            } catch (Exception ex) {
                m_logger.LogError($"Network preflight failed: {ex.Message}");
                MessageBox.Show(ex.Message);
                RaiseSessionEnded();
                return;
            }

            if (m_device != null) {
                try { m_device.Stop(); } catch { }
            }

            // m_device is a UPnP broadcaster used both by Pair flow and
            // by the live session for advertising. Keep it running so the
            // host can re-locate us if the connection blips.
            m_device = new ExtenderDevice(m_logger);
            m_device.Start();

            const ushort port = 3390;
            const bool isMcxPort = (port == 3391);

            freeRdpClient.SetDecodeEnabled(!isMcxPort);
            m_logger.LogInfo($"FreeRDP: graphics decoding {(isMcxPort ? "DISABLED (MCX)" : "ENABLED")}");
            // Resolution is picked from the Video sub-page in settings.
            // Falling back to 1920x1200 keeps behaviour identical to the
            // pre-picker default for config files written before the
            // SessionWidth/Height fields existed (XmlSerializer leaves
            // missing int fields at 0).
            uint desktopWidth = currConfig.SessionWidth > 0 ? (uint)currConfig.SessionWidth : 1920u;
            uint desktopHeight = currConfig.SessionHeight > 0 ? (uint)currConfig.SessionHeight : 1200u;
            m_logger.LogInfo($"FreeRDP: requested desktop {desktopWidth}x{desktopHeight}");
            freeRdpClient.SetInitialDesktopSize(desktopWidth, desktopHeight);
            freeRdpClient.Configure(
                currConfig.RdpLoginHost,
                port: port,
                user: currConfig.RdpLoginUserName,
                password: currConfig.RdpLoginPassword,
                rdpOnlySecurity: true,
                ignoreCertificate: true);
            freeRdpClient.Connect();
        }

        private void DisconnectRdp() {
            try { freeRdpClient?.Disconnect(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FreeRDP disconnect error: {ex.Message}"); }
            rdpDisplay.Source = null;
        }
    }
}
