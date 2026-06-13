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
        // Concrete handle to the per-session FileLogger so Stop() can
        // Dispose it (which flushes the close-marker line). null when
        // file logging is disabled in config or failed to initialise.
        private FileLogger _fileLogger;
        // Aliased logger for the A/V playback log group ([ffme], [engine],
        // [controller], [external-sync], [zoom], [decoder], [mf]). Set to
        // either m_logger or null in StartInternal based on
        // SoftSledConfig.LogAvPlayback. All A/V-related log call sites in
        // this file use this instead of m_logger directly so the toggle
        // takes effect without per-site conditionals — flip it OFF when
        // isolating splash-channel diagnostics from playback noise.
        private Logger _avLogger;
        // Aliased logger for the RDP-fastpath log group, specifically the
        // [overlay] dispatcher-side lines emitted from OnOverlayRegionChanged
        // / ApplyOverlayMapping. Gated by SoftSledConfig.LogRdpFastpath so
        // it shares its toggle with the raw [fp-overlay] decode lines that
        // WmcFastpathOverlayRegionDecoder emits — flipping the one checkbox
        // turns the whole overlay diagnostic stack on or off. The toggle is
        // separate from _avLogger because fastpath overlay updates can fire
        // many times per second and shouldn't pollute the default-ON A/V
        // playback log group.
        private Logger _fastpathLogger;
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
        // Sole playback controller: audio decoded via libav + rendered through
        // NAudio (the master clock); video decoded via libav and presented on a
        // GPU surface (D3DImage), paced to the audio clock. _videoPresenter owns
        // the D3D9Ex device + D3DImage; it's bound to VideoImage.Source.
        private SoftSled.Components.AudioVisual.ExternalSync.ExternalSyncMediaController _extSyncController;
        private SoftSled.Components.AudioVisual.VideoFpsLab.D3DImagePresenter _videoPresenter;
        // Render mode (GDI vs RUI), resolved once at session start. GDI mode
        // letterboxes the RDP framebuffer (rdpDisplay, Stretch=Uniform) inside
        // the window, so the video plane must be constrained to that same
        // letterboxed rect rather than the full MediaCanvas.
        private SoftSled.Components.Extender.WMCRenderMode _renderMode =
            SoftSled.Components.Extender.WMCRenderMode.GDI;
        // Current video zoom (WPF Stretch) requested by WMC's zoom mode. Held
        // so it can be re-asserted whenever the video plane is (re)sized and
        // applied to new media. Defaults to Uniform (preserve aspect) — the
        // correct neutral state before WMC sends an explicit zoom mode.
        private System.Windows.Media.Stretch _currentZoomStretch =
            System.Windows.Media.Stretch.Uniform;

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
        // Wired on MouseInputLayer — a transparent Border that sits above
        // BOTH rdpDisplay and splashHost — so mouse events reach these
        // handlers regardless of which layer is currently the visible UI
        // (splash-only navigation pages, mixed splash + RDP, RDP-only).
        // Previously these were attached to rdpDisplay, which silently
        // dropped events whenever rdpDisplay was Hidden (a Hidden element
        // doesn't hit-test in WPF). All coordinate math still resolves
        // against rdpDisplay's layout slot — both elements occupy the
        // same Grid cell, so e.GetPosition(rdpDisplay) returns the same
        // value it would have when the handlers lived on rdpDisplay
        // itself, and TryMapToRdp keeps working unchanged.

        private void AttachMouseHandlers() {
            MouseInputLayer.MouseMove  += RdpDisplay_MouseMove;
            MouseInputLayer.MouseDown  += RdpDisplay_MouseDown;
            MouseInputLayer.MouseUp    += RdpDisplay_MouseUp;
            MouseInputLayer.MouseWheel += RdpDisplay_MouseWheel;
            MouseInputLayer.MouseLeave += RdpDisplay_MouseLeave;
            MouseInputLayer.Focusable   = true;
            ApplyMouseCursorPolicy();
        }

        private void DetachMouseHandlers() {
            try { MouseInputLayer.MouseMove  -= RdpDisplay_MouseMove;  } catch { }
            try { MouseInputLayer.MouseDown  -= RdpDisplay_MouseDown;  } catch { }
            try { MouseInputLayer.MouseUp    -= RdpDisplay_MouseUp;    } catch { }
            try { MouseInputLayer.MouseWheel -= RdpDisplay_MouseWheel; } catch { }
            try { MouseInputLayer.MouseLeave -= RdpDisplay_MouseLeave; } catch { }
            MouseInputLayer.Cursor = null;
        }

        // Hide the local cursor when forwarding is on, so the server-painted
        // remote cursor is the only one the user sees. When mouse forwarding
        // is disabled, restore the default arrow so it is obvious nothing is
        // being forwarded. Applied to the input layer so it covers the entire
        // session region (including the splash overlay area).
        private void ApplyMouseCursorPolicy() {
            MouseInputLayer.Cursor = _mouseEnabled ? Cursors.None : Cursors.Arrow;
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
            // If the Mouse hasn't moved position since last send, return with no action
            if (rdpX == _lastSentRX && rdpY == _lastSentRY) return true;
            // Send Mouse Move Action
            bool ok = freeRdpClient.SendMouse(SoftSledNative.PTR_FLAGS_MOVE, rdpX, rdpY);
            // If Last Mouse Move Action was Acknowledged, save position
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
            // Focus the input layer (always visible) rather than rdpDisplay
            // (which may be Hidden during a splash-only navigation page).
            MouseInputLayer.Focus();
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
            // Apply the env-var-driven diagnostic toggles to the process
            // environment so existing call sites (RtspWireDumper,
            // SplashRawDumper, WmcFastpathAudioPlayer, etc.) that read
            // SOFTSLED_* vars don't need to know about config. Must
            // happen BEFORE any of those consumers are constructed.
            ApplyAdvancedDumpConfig(cfg);
            // Wire the A/V playback log toggle — all [ffme]/[engine]/etc.
            // call sites use _avLogger so flipping LogAvPlayback OFF in
            // the Debugging page silences the entire playback log group
            // without needing per-site guards.
            _avLogger = cfg.LogAvPlayback ? m_logger : null;
            // Wire the RDP-fastpath log toggle (shares its checkbox with
            // the raw [fp-overlay] decoder logs). The [overlay] dispatcher-
            // side lines from OnOverlayRegionChanged / ApplyOverlayMapping
            // log through this instead of _avLogger so they don't spam the
            // default-ON A/V group.
            _fastpathLogger = cfg.LogRdpFastpath ? m_logger : null;
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

            // Playback controller: audio is decoded via libav and rendered
            // through NAudio (the master clock); video is decoded via libav and
            // presented on a GPU surface (D3DImage), paced to the audio clock.
            // The GPU presenter needs a window handle, so it's created in
            // OnLoaded and attached then (see AttachVideoPresenterWhenReady).
            _extSyncController = new SoftSled.Components.AudioVisual.ExternalSync
                .ExternalSyncMediaController(_avLogger, cfg.AudioSyncOffsetMs, cfg.VideoJitterBufferMs);
            AvCtrlHandler.MediaController = _extSyncController;

            // Create the GPU video presenter (D3D9Ex device + D3DImage) and
            // bind it to the VideoImage plane. Needs a window handle, which is
            // available now (Start runs after the page is shown).
            try {
                var win = System.Windows.Window.GetWindow(this);
                IntPtr hwnd = win != null
                    ? new System.Windows.Interop.WindowInteropHelper(win).Handle
                    : IntPtr.Zero;
                _videoPresenter = new SoftSled.Components.AudioVisual.VideoFpsLab
                    .D3DImagePresenter(Dispatcher, hwnd, _avLogger);
                VideoImage.Source = _videoPresenter.Image;
                _extSyncController.AttachVideoPresenter(_videoPresenter);
            } catch (Exception ex) {
                _avLogger?.LogError($"[video] D3DImage presenter init failed: {ex.Message}");
            }

            _avLogger?.LogInfo($"[controller] playback controller active " +
                               $"(audio: libav+NAudio master; video: libav + D3DImage, audio-paced; " +
                               $"audioSyncOffset={cfg.AudioSyncOffsetMs}ms)");

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
            // Push HostWindow / Window background-colour updates into the
            // dedicated Rectangle that sits at the bottom of the Grid
            // (see ExtenderSessionControl.xaml). This keeps the FFME
            // video plane (MediaCanvas) and the splash scene-graph
            // (splashHost) cleanly layered above the background fill,
            // so video composites correctly without needing the splash
            // to suppress its own background.
            _splashController.SetBackgroundColorSink(c => {
                if (Dispatcher.CheckAccess()) {
                    splashBackgroundFill.Fill = new SolidColorBrush(c);
                } else {
                    Dispatcher.BeginInvoke(new Action(() => {
                        splashBackgroundFill.Fill = new SolidColorBrush(c);
                    }));
                }
            });
            SplashHandler.AttachController(_splashController);

            // Surface routing for DMCT OpenMedia. In GDI mode this is a
            // single-surface fallback; in RUI mode it ties the video
            // element's position to the splash surface identified by
            // the incoming Surface ID. AvCtrlHandler fires
            // VideoSurfaceRequested → SurfaceRouter.RouteVideoToSurface.
            var renderMode = m_capabilities?.GetRenderMode()
                             ?? SoftSled.Components.Extender.WMCRenderMode.GDI;
            _renderMode = renderMode;
            _surfaceRouter = new SoftSled.Components.AudioVisual.SurfaceRouter(
                renderMode, MediaCanvas, VideoImage, _splashController, m_logger,
                gdiDisplayRectProvider: ComputeGdiVideoRect);
            AvCtrlHandler.VideoSurfaceRequested += sid => _surfaceRouter.RouteVideoToSurface(sid);
            AvCtrlHandler.VideoPipelineClosed += () => _surfaceRouter.ReleaseSurface();
            // PiP z-order: the video plane normally sits BELOW the splash UI so
            // fullscreen chrome (seek bar) overlays it. For a PiP sub-rect WMC
            // paints an opaque placeholder in the splash layer that would cover
            // the video, so raise the plane ABOVE the UI for PiP only. VideoImage
            // is clipped to its rect (MediaCanvas ClipToBounds=True), so only the
            // small PiP box is over the UI — the rest stays untouched.
            _surfaceRouter.VideoAboveUiChanged += OnVideoAboveUiChanged;
            // The video plane never needs hit-testing — input goes to
            // MouseInputLayer. Disable it so a raised PiP box can't swallow
            // mouse events meant for the UI beneath it.
            MediaCanvas.IsHitTestVisible = false;

            // Video now flows through the controller's libav decoder +
            // D3DImage presenter (via RTSPClient.SetExternalVideoConsumer), so
            // there is no FFME element to open/close here. Make the video plane
            // visible + sized; the D3DImage shows nothing until frames arrive.
            Dispatcher.BeginInvoke(new Action(() => {
                SizeMediaToCanvasFill();
                VideoImage.Visibility = Visibility.Visible;
            }));
            AvCtrlHandler.VideoPipelineClosed += () =>
                Dispatcher.BeginInvoke(new Action(() => { _lastOverlay = null; }));
            // Blank the video plane to opaque black on media close so the last
            // decoded frame doesn't linger behind the WMC menu (the controller
            // is reused across media, so the D3DImage keeps its last backbuffer
            // until the next media's frames overwrite it). The next OpenMedia's
            // frames replace the black automatically.
            AvCtrlHandler.VideoPipelineClosed += () =>
                Dispatcher.BeginInvoke(new Action(() => {
                    try { _videoPresenter?.Blank(); } catch { }
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
            try { _extSyncController?.Dispose(); } catch { }
            _extSyncController = null;
            // Dispose the GPU presenter after the controller (which stops
            // feeding it frames). Owned here, not by the controller.
            try { VideoImage.Source = null; } catch { }
            try { _videoPresenter?.Dispose(); } catch { }
            _videoPresenter = null;
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

            // Session-end marker — written through m_logger so it also
            // hits the textbox. Reaches the app log if file logging is on.
            try { m_logger?.LogInfo("[session] stop"); } catch { }

            // Dispose the file logger ONLY if we own it (we created a
            // per-session FileLogger because App.AppLog was null at
            // session start). When App.AppLog is the file sink we leave
            // disposal to App.OnExit so subsequent sessions in the same
            // app run keep writing to the same file.
            try { _fileLogger?.Dispose(); } catch { }
            _fileLogger = null;
        }

        private void MediaCanvas_SizeChanged(object sender, SizeChangedEventArgs sizeEv) {
            if (_lastOverlay != null) {
                // WMC is actively driving the video position via fastpath
                // overlay updates — re-map the last one into the new canvas.
                ApplyOverlayMapping(_lastOverlay);
            } else {
                // No fastpath positioning yet — re-fit the video plane. In GDI
                // mode this re-letterboxes it to the RDP display rect so it
                // tracks the window resize without exceeding the RDP bounds.
                SizeMediaToCanvasFill();
            }
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

            // Point FFmpeg.AutoGen at the bundled native DLLs. This used to be
            // FFME's job (Library.FFmpegDirectory + LoadFFmpeg). With FFME
            // removed, the libav decoders (audio + video) resolve the DLLs via
            // ffmpeg.RootPath, which must be set process-wide before the first
            // ffmpeg call. Safe to set repeatedly.
            try {
                string ffmpegDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                                   + "\\Tools\\ffmpeg\\x64";
                FFmpeg.AutoGen.ffmpeg.RootPath = ffmpegDir;
                _avLogger?.LogInfo($"[ffmpeg] RootPath set to {ffmpegDir} " +
                                   $"(exists={System.IO.Directory.Exists(ffmpegDir)})");
            } catch (Exception ex) {
                _avLogger?.LogError($"[ffmpeg] RootPath init failed: {ex.Message}");
            }
        }

        private void OnOverlayRegionChanged(object sender,
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion region) {
            // INFO log under the A/V playback toggle so the user can tell
            // — without enabling the noisier raw fastpath decode log —
            // whether WMC is sending fastpath video-position updates AT
            // ALL (which is how WMC drives PiP positioning in GDI mode,
            // and might or might not still drive it in RUI mode).
            //
            // Rate-limit: only log when the region actually CHANGED so a
            // mid-video stream of identical "stay full-screen" updates
            // doesn't flood the log. _lastOverlay is the dispatcher-thread
            // snapshot updated below.
            bool changed = _lastOverlay == null
                           || _lastOverlay.X      != region.X
                           || _lastOverlay.Y      != region.Y
                           || _lastOverlay.Width  != region.Width
                           || _lastOverlay.Height != region.Height;
            if (changed) {
                _fastpathLogger?.LogInfo($"[overlay] fastpath video-region update " +
                                         $"{region} " +
                                         $"(srcSpace = RDP framebuffer pixels; this is the WMC PiP-position signal)");
            }
            Dispatcher.BeginInvoke(new Action(() => {
                _lastOverlay = region;
                ApplyOverlayMapping(region);
            }));
        }

        // Swap the video plane's z-order relative to the splash UI. PiP →
        // above (so WMC's placeholder gradient doesn't cover the video);
        // fullscreen → below (so the UI overlays the video as normal).
        // MediaCanvas defaults to ZIndex 0 (below splashHost via document
        // order); raising it to 1 puts it above. Clipping keeps it to the
        // PiP rect.
        private void OnVideoAboveUiChanged(bool above) {
            if (!Dispatcher.CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => OnVideoAboveUiChanged(above)));
                return;
            }
            System.Windows.Controls.Panel.SetZIndex(MediaCanvas, above ? 1 : 0);
            _avLogger?.LogInfo($"[video] PiP z-order: video plane {(above ? "ABOVE" : "below")} the UI");
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
                _currentZoomStretch = stretch;
                VideoImage.Stretch = stretch;
                _avLogger?.LogInfo($"[zoom] WMC mode {mode} → VideoImage.Stretch={stretch}");
            }));
        }

        private void ApplyOverlayMapping(
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion region) {
            if (region == null || MediaCanvas == null) return;
            var bmp = freeRdpClient?.Bitmap;
            if (bmp == null || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) {
                // ApplyOverlayMapping needs the RDP framebuffer size as
                // its coordinate space — without a bitmap there's no way
                // to scale region pixels into WPF coords. Log so the
                // user can tell the difference between "overlay update
                // arrived but no bitmap" vs "no overlay updates at all."
                _fastpathLogger?.LogInfo($"[overlay] update {region} arrived but no RDP framebuffer — skipped");
                return;
            }
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

            Canvas.SetLeft(VideoImage, wpfX);
            Canvas.SetTop(VideoImage, wpfY);
            VideoImage.Width = wpfW;
            VideoImage.Height = wpfH;
            // Re-assert the current zoom stretch — sizing the box must not
            // silently leave the plane in a stale stretch state.
            VideoImage.Stretch = _currentZoomStretch;

            // Promoted from Debug to Info (under _avLogger) so the user
            // can see — at a glance, without enabling raw fastpath log —
            // when fastpath overlay updates actually reposition the FFME
            // element. This is the consequence of the [overlay] fastpath
            // video-region update log a couple of methods above.
            _fastpathLogger?.LogInfo($"[overlay] APPLIED RDP {region} (src {srcW:F0}x{srcH:F0}) → " +
                                     $"FFME at WPF ({wpfX:F1},{wpfY:F1}) {wpfW:F1}x{wpfH:F1} " +
                                     $"in {cellW:F1}x{cellH:F1} cell, scale={scale:F3}");
        }

        private void SizeMediaToCanvasFill() {
            if (MediaCanvas == null) return;

            // GDI mode: the RDP framebuffer (rdpDisplay, Stretch=Uniform) is
            // letterboxed inside the window when the window aspect differs from
            // the RDP session aspect. The video plane must be constrained to
            // that same displayed rect, otherwise it spills into the black bars
            // and beyond the WMC desktop area. Compute the Uniform-fit rect of
            // the framebuffer within MediaCanvas and size the plane to it.
            if (_renderMode == SoftSled.Components.Extender.WMCRenderMode.GDI) {
                var r = ComputeGdiVideoRect();
                if (r.HasValue) {
                    ApplyVideoRect(r.Value);
                    return;
                }
                // No framebuffer / canvas yet — fall through to full-canvas
                // fill; the next MediaCanvas/FrameReady event re-runs this with
                // the framebuffer available and constrains it properly.
            }

            Canvas.SetLeft(VideoImage, 0);
            Canvas.SetTop(VideoImage, 0);
            VideoImage.Width = double.IsNaN(MediaCanvas.ActualWidth) ? 0 : MediaCanvas.ActualWidth;
            VideoImage.Height = double.IsNaN(MediaCanvas.ActualHeight) ? 0 : MediaCanvas.ActualHeight;
            VideoImage.Stretch = _currentZoomStretch;
        }

        /// <summary>
        /// Compute the on-screen rectangle (in MediaCanvas coordinates) that the
        /// RDP framebuffer actually occupies — i.e. the Uniform letterbox-fit of
        /// the framebuffer pixels inside MediaCanvas. This is the bound the GDI
        /// video plane must not exceed (rdpDisplay uses Stretch="Uniform" over
        /// the same Grid cell). Returns null if the framebuffer or canvas size
        /// isn't known yet.
        /// </summary>
        private Rect? ComputeGdiVideoRect() {
            var bmp = freeRdpClient?.Bitmap;
            if (bmp == null || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return null;
            if (MediaCanvas == null) return null;
            double cellW = MediaCanvas.ActualWidth;
            double cellH = MediaCanvas.ActualHeight;
            if (cellW <= 0 || cellH <= 0) return null;

            double scale = Math.Min(cellW / bmp.PixelWidth, cellH / bmp.PixelHeight);
            double w = bmp.PixelWidth * scale;
            double h = bmp.PixelHeight * scale;
            return new Rect((cellW - w) / 2.0, (cellH - h) / 2.0, w, h);
        }

        /// <summary>Position + size VideoImage to a MediaCanvas-space rect and
        /// re-assert the current zoom stretch.</summary>
        private void ApplyVideoRect(Rect r) {
            Canvas.SetLeft(VideoImage, r.X);
            Canvas.SetTop(VideoImage, r.Y);
            VideoImage.Width = r.Width;
            VideoImage.Height = r.Height;
            VideoImage.Stretch = _currentZoomStretch;
        }

        private void FreeRdpClient_FrameReady(object sender, EventArgs e) {
            bool firstFrame = rdpDisplay.Source == null;
            rdpDisplay.Source = freeRdpClient.Bitmap;
            m_logger.LogInfo($"RDP framebuffer ready: {freeRdpClient.Bitmap.PixelWidth}x{freeRdpClient.Bitmap.PixelHeight}");
            // The GDI video letterbox rect depends on the framebuffer size,
            // which isn't known until the first frame. Re-fit once it arrives
            // (only when WMC isn't already positioning via fastpath overlay).
            if (firstFrame && _renderMode == SoftSled.Components.Extender.WMCRenderMode.GDI
                && _lastOverlay == null) {
                SizeMediaToCanvasFill();
            }
        }

        void InitialiseLogger() {
            // Build the underlying loggers. The textbox is the in-session
            // overlay (Ctrl+L to show); the file sink is the post-mortem
            // capture that survives a crash. Composing them through
            // CompositeLogger keeps every existing m_logger.Log* call
            // site unchanged while broadcasting to both sinks transparently.
            var textboxLogger = new TextBoxLogger(loggerTextBox, Window.GetWindow(this));

            // PREFER the app-lifetime file logger created in App.OnStartup.
            // It captures global exception handlers and any pre-session
            // diagnostics. The session and the app share the same file so
            // a crash that ends the session/app leaves one coherent
            // timeline on disk.
            Logger fileSink = SoftSledWPF.App.AppLog;
            bool fileSinkIsAppOwned = (fileSink != null);

            // Fallback: if App.OnStartup couldn't init a file logger but
            // the config now says LogToFile=true (e.g. the user toggled
            // the checkbox after app launch), spin up a per-session file
            // here. We own its Dispose in Stop() in that case.
            if (fileSink == null) {
                try {
                    var cfg = SoftSledConfigManager.ReadConfig();
                    if (cfg.LogToFile) {
                        string logFilePath = ResolveLogFilePath(cfg.LogFileDirectory);
                        _fileLogger = new FileLogger(logFilePath);
                        fileSink = _fileLogger;
                    }
                } catch (Exception ex) {
                    // Log-init failure must not block session start.
                    System.Diagnostics.Debug.WriteLine("[FileLogger init] " + ex);
                }
            }

            m_logger = fileSink != null
                ? (Logger)new CompositeLogger(textboxLogger, fileSink)
                : textboxLogger;
            m_logger.IsLoggingDebug = true;

            // Session-start marker — useful both in the textbox and in
            // the file timeline (e.g. when the app log contains multiple
            // sessions from a long-running process).
            if (fileSinkIsAppOwned) {
                m_logger.LogInfo("[session] start — file sink: app log (shared across sessions)");
            } else if (_fileLogger != null) {
                m_logger.LogInfo($"[session] start — file sink: per-session ({_fileLogger.Path})");
            } else {
                m_logger.LogInfo("[session] start — file sink: none (LogToFile is off)");
            }

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
        /// Build the absolute path to the session log file. The directory
        /// is taken from <paramref name="configuredDir"/> when non-empty,
        /// otherwise defaults to <c>%LocalAppData%/SoftSled/Logs</c>. The
        /// filename is per-session and timestamped so concurrent runs
        /// don't clobber each other and so the user can correlate a
        /// crash by its timestamp.
        /// </summary>
        private static string ResolveLogFilePath(string configuredDir) {
            string dir = configuredDir;
            if (string.IsNullOrWhiteSpace(dir)) {
                string localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                dir = System.IO.Path.Combine(localAppData, "SoftSled", "Logs");
            }
            string fileName = $"softsled-{DateTime.Now:yyyyMMdd-HHmmss}-pid{System.Diagnostics.Process.GetCurrentProcess().Id}.log";
            return System.IO.Path.Combine(dir, fileName);
        }

        /// <summary>
        /// Returns the directory the file logger is writing to (or would
        /// write to per current config), without actually opening a file.
        /// Used by the "Open log folder" button on the Debugging page.
        /// </summary>
        internal static string GetLogDirectoryForConfig() {
            try {
                var cfg = SoftSledConfigManager.ReadConfig();
                return System.IO.Path.GetDirectoryName(ResolveLogFilePath(cfg.LogFileDirectory));
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Root directory for diagnostic dumps — splash raw bytes,
        /// fastpath payloads, audio PCM. Surfaced by the Debugging
        /// page's "Open dumps folder" button. Honours
        /// <see cref="SoftSledConfig.DumpsDirectory"/>; falls back to
        /// the platform default when empty.
        /// </summary>
        internal static string GetDumpsRootDirectory() {
            try {
                string dir = null;
                try { dir = SoftSledConfigManager.ReadConfig()?.DumpsDirectory; } catch { }
                if (string.IsNullOrWhiteSpace(dir)) {
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SoftSled", "Dumps");
                }
                return dir;
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Translate the diagnostic-toggle config fields into process-
        /// scope environment variables that the existing SOFTSLED_*
        /// consumers (RtspWireDumper, SplashRawDumper,
        /// WmcFastpathAudioPlayer, RTSPClient audio routing) already
        /// read. The config values always WIN over any shell-set env
        /// vars — a ticked checkbox is the most explicit signal of
        /// intent we can get. An unticked box clears the corresponding
        /// env var so subsequent toggles take effect on the next
        /// session.
        /// </summary>
        private static void ApplyAdvancedDumpConfig(SoftSledConfig cfg) {
            string dumpsRoot = GetDumpsRootDirectory();
            string splashDir   = cfg.EnableSplashRawDump   && dumpsRoot != null
                                 ? System.IO.Path.Combine(dumpsRoot, "splash")   : null;
            string fastpathDir = cfg.EnableFastpathRawDump && dumpsRoot != null
                                 ? System.IO.Path.Combine(dumpsRoot, "fastpath") : null;
            string audioDir    = cfg.EnableAudioDump       && dumpsRoot != null
                                 ? System.IO.Path.Combine(dumpsRoot, "audio")    : null;
            // RDPGFX dump shares the SAME enable flag as fastpath — both
            // are RDP-layer diagnostics for the PiP-positioning hunt and
            // there's no use case for one without the other right now.
            // The native rdpgfx_main.c dumper (in our patched
            // freerdp-client3.dll) creates rdpgfx-raw.log inside this
            // directory; it needs the dir to exist before the env var
            // is read at session start. Ensure_directory below.
            string rdpgfxDir   = cfg.EnableFastpathRawDump && dumpsRoot != null
                                 ? System.IO.Path.Combine(dumpsRoot, "rdpgfx")   : null;
            if (rdpgfxDir != null) {
                try { System.IO.Directory.CreateDirectory(rdpgfxDir); } catch { }
            }

            try { Environment.SetEnvironmentVariable("SOFTSLED_SPLASH_RAW_DUMP",   splashDir); }   catch { }
            try { Environment.SetEnvironmentVariable("SOFTSLED_FASTPATH_RAW_DUMP", fastpathDir); } catch { }
            try { Environment.SetEnvironmentVariable("SOFTSLED_RDPGFX_RAW_DUMP",   rdpgfxDir); }   catch { }
            try { Environment.SetEnvironmentVariable("SOFTSLED_AUDIO_DUMP",        audioDir); }    catch { }
            try { Environment.SetEnvironmentVariable("SOFTSLED_RTSP_WIRE_DUMP",
                cfg.EnableRtspWireDump   ? "1" : null); } catch { }
            try { Environment.SetEnvironmentVariable("SOFTSLED_AUDIO_TRACE",
                cfg.EnableAudioTrace     ? "1" : null); } catch { }
            try { Environment.SetEnvironmentVariable("SOFTSLED_AUDIO_VIA_NAUDIO",
                cfg.EnableAudioViaNAudio ? "1" : null); } catch { }
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

        /// <summary>Live A/V sync nudge (from a session hotkey). Positive delta
        /// advances video to reduce video-lags-audio; negative delays it. Applies
        /// to the running pacer immediately and persists the new trim to config so
        /// it carries to the next session. No-op if no controller is active.</summary>
        public void NudgeAvSync(int deltaMs) {
            var ctrl = _extSyncController;
            if (ctrl == null) return;
            int trim = ctrl.NudgeAudioSyncTrim(deltaMs);
            // Persist so the dialled-in value survives reconnect.
            try {
                var cfg = SoftSledConfigManager.ReadConfig();
                if (cfg != null) { cfg.AudioSyncOffsetMs = trim; SoftSledConfigManager.WriteConfig(cfg); }
            } catch (Exception ex) {
                m_logger?.LogError($"[av-sync] persist trim failed: {ex.Message}");
            }
            // Surface the value on the logger overlay so it can be tuned by eye.
            m_logger?.LogInfo($"[av-sync] trim = {(trim >= 0 ? "+" : "")}{trim} ms " +
                              $"(video {(trim >= 0 ? "earlier" : "later")})");
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

            //// Additional Code to attempt to start the Splash Handshake
            //if (e.channelName == "devcaps" && e.additional == "BIG") {
            //    SplashHandler.StartHandshake();
            //}
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

            // In RUI mode the splash channel is the only UI source — the
            // WMC server pushes the entire shell over MS-RRSP2 onto
            // splashHost, and rdpDisplay just shows whatever fallback
            // framebuffer the host happens to paint (typically a
            // 1280×720 letterbox of the player chrome). When DMCT
            // OpenMedia kicks off video playback, that fallback
            // framebuffer ends up pillarboxed in the centre of our
            // client area, painting opaque over the FFME video element
            // sitting underneath and leaving only ~25-pixel "bars"
            // visible on each side. Solution: never make rdpDisplay
            // Visible in RUI mode — the splash IS the UI and the FFME
            // element (positioned by SurfaceRouter) is the video.
            var renderMode = m_capabilities?.GetRenderMode()
                             ?? SoftSled.Components.Extender.WMCRenderMode.GDI;
            bool isRui = renderMode == SoftSled.Components.Extender.WMCRenderMode.RUI;
            Dispatcher.BeginInvoke(new Action(() => {
                if (e.shellOpen) {
                    if (!isRui) {
                        rdpDisplay.Visibility = Visibility.Visible;
                    } else {
                        // Be defensive — if anything previously
                        // flipped rdpDisplay to Visible (e.g. an early
                        // status arrived under GDI assumption before
                        // capabilities were established), force it
                        // back to Hidden so it can't cover the splash
                        // composition or the FFME video plane.
                        rdpDisplay.Visibility = Visibility.Hidden;
                    }
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
