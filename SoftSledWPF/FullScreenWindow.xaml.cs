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
using SoftSledWPF.Components.Utility;

namespace SoftSledWPF {
    /// <summary>
    /// Interaction logic for FullScreenWindow.xaml
    /// </summary>
    public partial class FullScreenWindow : Window {

        // Private members
        private Logger m_logger;
        private ExtenderDevice m_device;
        private ExtenderCapabilities m_capabilities;
        private bool isConnecting = false;

        // In-process FreeRDP transport (softsled-rdp.dll + freerdp3.dll under
        // native/x64/). Replaces the mstscax + RDPVCManager.dll + named-pipe
        // bridge that earlier revisions used. Provides both virtual-channel
        // I/O and a software framebuffer surfaced via WriteableBitmap.
        private FreeRdpClient freeRdpClient;

        private VirtualChannelAvCtrlHandler AvCtrlHandler;
        private VirtualChannelDevCapsHandler DevCapsHandler;
        private VirtualChannelMcxSessHandler McxSessHandler;

        // Testing SPLASH
        private VirtualChannelSplashHandler SplashHandler;

        // MS-RRSP2 (Xbox-360-style UI) renderer. Owns the framing
        // reassembler, payload dispatcher, object registry, and the WPF
        // DrawingVisual tree exposed via splashHost. Constructed in
        // FullScreenWindow_Loaded so we can hand it the WPF dispatcher and the
        // already-instantiated splashHost element from XAML.
        //
        // Endianness defaults to BE because the live DevCaps advertises
        // BIG=True (see VirtualChannelDevCapsHandler.cs:24). If BIG flips
        // we can flip this flag and the reader follows.
        private SoftSled.Components.Splash.SplashController _splashController;
        private const bool SplashPayloadBigEndian = true;

        // WMC's MCX-specific 0x0D fast-path audio. Player is always-on and
        // produces live output via NAudio. Dumper is opt-in via the
        // SOFTSLED_AUDIO_DUMP env var (writes per-sound PCM .wav files for
        // RE / debugging). Held in fields so the bound delegates stay alive
        // for as long as native code holds the function pointer.
        //
        // _rawDumper is a separate opt-in (SOFTSLED_FASTPATH_RAW_DUMP=<dir>)
        // that captures EVERY fast-path update — every code, every length —
        // for reverse-engineering. Used to investigate non-audio 0x0D and
        // any other WMC-specific fast-path types that appear when devcaps
        // like SUP (RDP super blt) are enabled. Records get a length-
        // prefixed binary log + a human-readable per-message summary.
        private SoftSled.Components.AudioVisual.WmcFastpathAudioPlayer _audioPlayer;
        private SoftSled.Components.AudioVisual.WmcFastpathAudioDumper _audioDumper;
        private SoftSled.Components.AudioVisual.WmcFastpathRawDumper _rawDumper;
        private SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder _overlayDecoder;
        private SoftSledNative.FastpathCallback _fastpathDispatcher;

        // Last overlay-region received from WMC. Kept so we can re-apply
        // the mapping if the WPF window resizes (since the same RDP-source
        // rectangle now lands at different WPF coords).
        private SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion _lastOverlay;

        // FFME → IMediaController adapter. Owned by FullScreenWindow; lifetime
        // matches the WPF window. Disposing unhooks our event listeners on
        // the MediaElement (the element itself is owned by the visual tree).
        private SoftSled.Components.AudioVisual.FfmeMediaController _ffmeController;

        // Serialization point between Media.Open (video) and
        // MediaAudio.Open (PCM audio) — FFME's command queue rejects a
        // second Open while a first is still pending. The audio handler
        // awaits this TCS before calling MediaAudio.Open. Reset on
        // pipeline close so a subsequent session starts clean.
        private System.Threading.Tasks.TaskCompletionSource<bool> _videoOpenComplete;

        public FullScreenWindow() {
            InitializeComponent();
            this.Loaded += FullScreenWindow_Loaded;
            this.Closed += FullScreenWindow_Closed;

            // Tunnel-phase keyboard hooks at the Window level so we catch
            // arrow/Enter/Escape regardless of which control technically has
            // focus. While ACTIVE we forward to FreeRDP and mark Handled to
            // suppress WPF's own use of the keys.
            this.PreviewKeyDown += FullScreenWindow_PreviewKeyDown;
            this.PreviewKeyUp += FullScreenWindow_PreviewKeyUp;

            #region FFME Instantiation ########################################

            // The process runs x64 (Prefer32Bit=false above is required by the
            // FreeRDP shim, see csproj comment) so x86 DLLs throw at load.
            // FFME 4.4.350 expects FFmpeg 4.x shared builds (avcodec-58.dll,
            // avformat-58.dll, avutil-56.dll, swscale-5.dll, swresample-3.dll,
            // avfilter-7.dll, avdevice-58.dll). Recommended source:
            //   https://www.gyan.dev/ffmpeg/builds/   (pick "shared" 4.x)
            string ffmpegDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\Tools\\ffmpeg\\x64";
            Unosquare.FFME.Library.FFmpegDirectory = ffmpegDir;

            // Capture FFME's internal log stream + libav log stream to a file
            // so MEDIA_OPEN failures (codec not found, probe failed, DLL load
            // error, etc.) are visible after the fact. Lives in
            // %TEMP%\softsled-ffme.log.
            //
            // Performance is critical here: FFME emits ~60 Trace-level
            // "Engine.Rendering: A BLK ..." messages PER SECOND during normal
            // playback. Previous version did AutoFlush=true + lock + sync
            // disk write per line, which queued ~60 disk-flush operations
            // per second onto FFME's rendering thread — that thread blocks
            // on disk I/O, contention spreads through every other thread
            // touching the WPF dispatcher, and the whole app slows down.
            //
            // Two changes:
            //   * Filter out Trace-level (and Debug for FFME's own log)
            //     since they're per-frame chatter that doesn't help debug
            //     anything outside of FFME engine internals.
            //   * AutoFlush=false. StreamWriter's internal buffer absorbs
            //     ~4KB before hitting disk; a background timer flushes
            //     every 2s, and Dispose flushes on shutdown.
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

                // Periodic flush so we don't lose anything on crash/abort.
                _ffmeLogFlushTimer = new System.Threading.Timer(_ => {
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter?.Flush();
                        }
                    } catch { /* ignore */ }
                }, null, 2000, 2000);

                // FFME's own messages. Skip Trace and Debug — those are
                // per-frame rendering chatter that swamps the log AND
                // slows the rendering thread via lock contention.
                Media.MessageLogged += (s, e) => {
                    var mt = e.MessageType;
                    if (mt == Unosquare.FFME.Common.MediaLogMessageType.Trace ||
                        mt == Unosquare.FFME.Common.MediaLogMessageType.Debug) return;
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[FFME/{mt}] {e.AspectName}: {e.Message}");
                        }
                    } catch { /* ignore */ }
                };
                // libav internal messages (decoder errors, codec mismatches).
                // Static event on MediaElement, not an instance event. Same
                // filter: only Warning / Error matter for our purposes.
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
                    } catch { /* ignore */ }
                };
                Media.MediaFailed += (s, e) => {
                    string ex = e.ErrorException?.ToString() ?? "(no exception)";
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[MEDIA_FAILED] {ex}");
                        }
                    } catch { /* ignore */ }
                    m_logger.LogError($"[ffme] MediaFailed: {e.ErrorException?.Message ?? "(no message)"}");
                };
                Media.MediaInitializing += (s, e) => {
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     "[MEDIA_INITIALIZING]");
                        }
                    } catch { /* ignore */ }
                };
                Media.MediaOpening += (s, e) => {
                    // ForcedInputFormat is set pre-probe via IMediaInputStream.
                    // OnInitializing; here we get post-probe info instead.
                    try {
                        lock (_ffmeLogGate) {
                            string streams = string.Join(",", e.Info.Streams.Keys);
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     $"[MEDIA_OPENING] format={e.Info.Format} " +
                                                     $"duration={e.Info.Duration} streams=[{streams}]");
                        }
                    } catch { /* ignore */ }
                };
                Media.MediaOpened += (s, e) => {
                    try {
                        lock (_ffmeLogGate) {
                            _ffmeLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} " +
                                                     "[MEDIA_OPENED]");
                        }
                    } catch { /* ignore */ }
                    m_logger.LogInfo("[ffme] MediaOpened");
                };

                // libav log levels: 16=FATAL, 24=ERROR, 32=WARNING, 40=INFO,
                // 48=VERBOSE, 56=DEBUG. Previously set to 48 (verbose)
                // which spammed the file at ~30+ lines/second per stream
                // and pinned FFME's decode thread on disk I/O via the
                // synchronous write path. 32 (WARNING) keeps the genuinely
                // useful diagnostics (codec failures, packet corruption,
                // demuxer errors) without the per-frame chatter.
                Unosquare.FFME.Library.FFmpegLogLevel = 32;

                //m_logger.LogInfo($"[ffme] log capture started: {ffmeLogPath}");
            } catch (Exception ex) {
                m_logger.LogError($"[ffme] log capture init failed: {ex.Message}");
            }

            #endregion ########################################################
        }

        private System.IO.StreamWriter _ffmeLogWriter;
        private System.Threading.Timer _ffmeLogFlushTimer;
        private readonly object _ffmeLogGate = new object();

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

        private void FullScreenWindow_PreviewKeyDown(object sender, KeyEventArgs e) =>
            ForwardKey(e, release: false);

        private void FullScreenWindow_PreviewKeyUp(object sender, KeyEventArgs e) =>
            ForwardKey(e, release: true);

        private void FullScreenWindow_Loaded(object sender, RoutedEventArgs e) {
            InitialiseLogger();

            // Get Extender Capabilities
            m_capabilities = new ExtenderCapabilities();

            // Create the FreeRDP client. DataReceived/StateChanged signatures
            // match the legacy RDPVCInterface, so the existing channel
            // handlers plug in unchanged. FrameReady fires once the GDI
            // framebuffer is allocated and the WriteableBitmap is ready.
            freeRdpClient = new FreeRdpClient();
            freeRdpClient.DataReceived += FreeRdpClient_DataReceived;
            freeRdpClient.StateChanged += FreeRdpClient_StateChanged;
            freeRdpClient.FrameReady += FreeRdpClient_FrameReady;
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
            string rawDumpDir = Environment.GetEnvironmentVariable("SOFTSLED_FASTPATH_RAW_DUMP");
            if (!string.IsNullOrWhiteSpace(rawDumpDir)) {
                _rawDumper = new SoftSled.Components.AudioVisual.WmcFastpathRawDumper(
                    m_logger, rawDumpDir);
            }
            // WMC overlay-region decoder — handles 0x0D message type 3,
            // which carries the video destination rectangle in RDP source
            // coords. Fires on the FreeRDP worker thread; we marshal to
            // the WPF dispatcher to mutate the MediaCanvas / Media element.
            _overlayDecoder = new SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder(m_logger);
            _overlayDecoder.OverlayRegionChanged += OnOverlayRegionChanged;
            _overlayDecoder.ZoomModeChanged += OnZoomModeChanged;

            _fastpathDispatcher = (user, code, data, length) => {
                // Raw dumper FIRST so its capture is unaffected by any work
                // the audio handlers may do (they shouldn't — both are
                // read-only — but ordering guarantees the capture is whole).
                _rawDumper?.OnFastpath(user, code, data, length);
                _audioPlayer?.OnFastpath(user, code, data, length);
                _audioDumper?.OnFastpath(user, code, data, length);
                _overlayDecoder?.OnFastpath(user, code, data, length);
            };
            freeRdpClient.SetFastpathCallback(_fastpathDispatcher);

            // Re-apply the mapping when the WPF window changes size, so
            // the same RDP-source rectangle continues to land in the right
            // place. Cheap — ApplyOverlayMapping just sets four properties.
            MediaCanvas.SizeChanged += (s, sizeEv) => {
                if (_lastOverlay != null) ApplyOverlayMapping(_lastOverlay);
            };

            // Create VirtualChannel Handlers
            McxSessHandler = new VirtualChannelMcxSessHandler(m_logger);
            McxSessHandler.VirtualChannelSend += On_VirtualChannelSend;
            DevCapsHandler = new VirtualChannelDevCapsHandler(m_logger, m_capabilities.GetDeviceCapabilities());
            DevCapsHandler.VirtualChannelSend += On_VirtualChannelSend;
            AvCtrlHandler = new VirtualChannelAvCtrlHandler(m_logger);
            AvCtrlHandler.VirtualChannelSend += On_VirtualChannelSend;
            //// If the Render Mode is Remote UI
            //if (rendermode == WMCRenderMode.RUI) {
                SplashHandler = new VirtualChannelSplashHandler(m_logger);
                SplashHandler.VirtualChannelSend += On_VirtualChannelSend;
            //}

            // Adapt FFME to the IMediaController contract so AvCtrl's
            // GetPosition / GetDuration handlers can return real playback
            // state and Play/Pause/Stop drive the FFME element. The
            // controller also forwards FFME's BufferingEnded / MediaEnded /
            // MediaFailed back to AvCtrl, which translates them into
            // outbound DMCT OnMediaEvent messages to WMC.
            _ffmeController = new SoftSled.Components.AudioVisual.FfmeMediaController(Media, m_logger);
            AvCtrlHandler.MediaController = _ffmeController;

            // MS-RRSP2 renderer wiring. The host is already in the visual
            // tree (declared in FullScreenWindow.xaml above rdpDisplay); the
            // controller holds it and pushes parsed scene-graph state
            // through it on the UI thread.
            //
            // The send callback lets the controller emit
            // Context_ForwardMessage callbacks (and any future outbound
            // messages) back to the WMC server via the splash VC.
            // Without this the server only sends an initial boot batch
            // (26 class defs + a couple of CreateObjects) and then waits
            // forever for our ACK — the dashed "Message Callback" arrow
            // in the MS-RRSP2 spec sequence diagram.
            _splashController = new SoftSled.Components.Splash.SplashController(
                m_logger, Dispatcher, SplashPayloadBigEndian, SplashHandler.SendBytes);
            _splashController.AttachHost(splashHost);
            SplashHandler.AttachController(_splashController);

            // MPEG-ES video pipeline: when RTSPClient finds a wm-MPV PT in
            // the SDP it constructs an FFME IMediaInputStream and raises
            // VideoPipelineReady (via AvCtrlHandler). Marshal to the WPF
            // dispatcher before calling Media.Open — FFME's Open is async
            // and must run on the dispatcher.
            // Signals video-element Open is complete so the audio element
            // can begin its own Open. Two MediaElement instances share
            // libavformat global state during init — kicking off two
            // Opens in parallel triggers FFME's "Open command is pending
            // completion" rejection. Serialize them by awaiting this TCS
            // in the audio pipeline handler.
            _videoOpenComplete = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            AvCtrlHandler.VideoPipelineReady += stream =>
                Dispatcher.BeginInvoke(new Action(async () => {
                    try {
                        // Audio-only streams (e.g. MP3 music files via the
                        // mp3-ffme pipeline) leave the FFME element hidden
                        // — FFME still runs its audio decoder/renderer, but
                        // there's no visual content to surface. Video and
                        // mixed streams size to the canvas as before, then
                        // wait for the first overlay-region message to
                        // snap to WMC's destination rectangle.
                        bool hasVideo =
                            (stream as SoftSled.Components.AudioVisual.AsfFfmeInputStream)?.HasVideo
                            ?? true;
                        if (hasVideo) {
                            SizeMediaToCanvasFill();
                            Media.Visibility = Visibility.Visible;
                        } else {
                            Media.Visibility = Visibility.Collapsed;
                        }
                        await Media.Open(stream);
                        m_logger.LogInfo($"[ffme] Media.Open succeeded " +
                                         $"({(hasVideo ? "video+audio" : "audio-only")})");
                    } catch (Exception ex) {
                        m_logger.LogError($"[ffme] Media.Open failed: {ex.Message}");
                    } finally {
                        _videoOpenComplete?.TrySetResult(true);
                    }
                }));

            // Separate FFME pipeline for raw-PCM audio (X-WMF-PF
            // audio/vnd.wave). Fires in parallel with the video
            // pipeline when the recording uses the modern PCM+H264
            // profile. MediaAudio stays Collapsed — FFME's audio
            // renderer runs without needing any visual surface.
            //
            // The Open is serialized BEHIND the video element's Open
            // (via _videoOpenComplete) so FFME's command queue doesn't
            // reject ours mid-init. If video never opens (audio-only
            // session, no video pipeline ready) we fall through after
            // a short timeout and open audio anyway.
            AvCtrlHandler.AudioPipelineReady += stream =>
                Dispatcher.BeginInvoke(new Action(async () => {
                    try {
                        // Either video.Open finishes (TCS set) or we
                        // time out after a couple of seconds — whichever
                        // first. Audio-only sessions have no video TCS to
                        // wait on, so the timeout lets them proceed.
                        if (_videoOpenComplete != null) {
                            var tcs = _videoOpenComplete.Task;
                            var done = await System.Threading.Tasks.Task.WhenAny(
                                tcs, System.Threading.Tasks.Task.Delay(2000));
                            if (done != tcs) {
                                m_logger.LogInfo("[ffme] audio open: video TCS timed out, opening anyway");
                            }
                        }
                        // MediaAudio stays Visible with a 1x1 off-canvas
                        // footprint per XAML — don't toggle Visibility
                        // here (Collapsed starves FFME's audio decoder).
                        await MediaAudio.Open(stream);
                        m_logger.LogInfo("[ffme] MediaAudio.Open succeeded (PCM audio pipeline)");
                    } catch (Exception ex) {
                        m_logger.LogError($"[ffme] MediaAudio.Open failed: {ex.Message}");
                    }
                }));

            AvCtrlHandler.VideoPipelineClosed += () =>
                Dispatcher.BeginInvoke(new Action(async () => {
                    try {
                        await Media.Close();
                    } catch (Exception ex) {
                        m_logger.LogError($"[ffme] Media.Close failed: {ex.Message}");
                    }
                    try {
                        await MediaAudio.Close();
                    } catch (Exception ex) {
                        m_logger.LogError($"[ffme] MediaAudio.Close failed: {ex.Message}");
                    }
                    Media.Visibility = Visibility.Collapsed;
                    // MediaAudio stays Visible (1x1 off-canvas) — see
                    // XAML comment. Don't Collapse it on close.
                    _lastOverlay = null;
                    // Reset the open-serialization TCS so the next session
                    // gates correctly. Setting RunContinuationsAsync so any
                    // late audio handler awaiting the OLD TCS gets a
                    // synchronous "complete" (no hang).
                    _videoOpenComplete?.TrySetResult(false);
                    _videoOpenComplete = new System.Threading.Tasks.TaskCompletionSource<bool>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                }));

            McxSessHandler.StatusChanged += McxSessHandler_StatusChanged;

            SoftSledConfig config = SoftSledConfigManager.ReadConfig();
            if (!config.IsPaired) {
                m_logger.LogInfo("Extender is not paired!");
            } else {
                m_logger.LogInfo("Extender is paired with " + config.RdpLoginHost);
            }

            Connect();
        }

        /// <summary>
        /// Fires off the FreeRDP worker thread when WMC sends a type-3
        /// overlay-region message. Marshal to dispatcher and apply.
        /// </summary>
        private void OnOverlayRegionChanged(object sender,
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion region) {
            Dispatcher.BeginInvoke(new Action(() => {
                _lastOverlay = region;
                ApplyOverlayMapping(region);
            }));
        }

        /// <summary>
        /// Fires off the FreeRDP worker thread when WMC sends a type-4
        /// zoom-mode message (Normal / StretchSides / StretchTopBottom /
        /// Dynamic). Translate to FFME's <see cref="System.Windows.Media.Stretch"/>
        /// on the dispatcher thread.
        /// </summary>
        private void OnZoomModeChanged(object sender,
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode mode) {
            Dispatcher.BeginInvoke(new Action(() => {
                System.Windows.Media.Stretch stretch;
                switch (mode) {
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.Normal:
                        // Preserve source aspect; allow letterbox/pillarbox
                        // bars to appear naturally inside the destination
                        // rectangle.
                        stretch = System.Windows.Media.Stretch.Uniform;
                        break;
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.StretchSides:
                        // Distort to fill — removes pillarbox bars on the
                        // sides (4:3 source in a 16:9 destination, etc.).
                        // Aspect ratio is not preserved.
                        stretch = System.Windows.Media.Stretch.Fill;
                        break;
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.StretchTopBottom:
                        // Zoom-in preserving aspect; crops to fill. Removes
                        // letterbox bars baked into the source content
                        // (e.g. 2.35:1 movie inside a 16:9 raster).
                        stretch = System.Windows.Media.Stretch.UniformToFill;
                        break;
                    case SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.WmcZoomMode.Dynamic:
                        // Non-linear zoom — WPF can't natively reproduce it.
                        // Fill is the closest "fill the rectangle" approximation
                        // without distortion-free crop. A custom HLSL shader
                        // could mimic the centre-preserving stretch later.
                        stretch = System.Windows.Media.Stretch.Fill;
                        break;
                    default:
                        stretch = System.Windows.Media.Stretch.Uniform;
                        break;
                }
                Media.Stretch = stretch;
                m_logger.LogInfo($"[zoom] WMC mode {mode} → Media.Stretch={stretch}");
            }));
        }

        /// <summary>
        /// Map WMC's RDP-source-space rectangle into MediaCanvas coords,
        /// accounting for the <c>rdpDisplay</c> Image's <c>Stretch="Uniform"</c>
        /// letterboxing. The RDP bitmap renders centred inside its grid
        /// cell at the largest scale that fits; we compute that same scale
        /// here so the FFME element lands exactly behind WMC's α=0 region.
        /// </summary>
        private void ApplyOverlayMapping(
            SoftSled.Components.AudioVisual.WmcFastpathOverlayRegionDecoder.OverlayRegion region) {
            if (region == null || MediaCanvas == null) return;

            // RDP source dimensions = live WriteableBitmap pixel size. We
            // don't hardcode 1024×768 because the session may renegotiate.
            var bmp = freeRdpClient?.Bitmap;
            if (bmp == null || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) {
                // Bitmap not ready yet — keep the bootstrap fill and try
                // again on the next region update or canvas resize.
                return;
            }
            double srcW = bmp.PixelWidth;
            double srcH = bmp.PixelHeight;

            // MediaCanvas's render size = the area the RDP image renders in
            // (we made it share the same grid cell and margin in XAML).
            double cellW = MediaCanvas.ActualWidth;
            double cellH = MediaCanvas.ActualHeight;
            if (cellW <= 0 || cellH <= 0) return;  // not measured yet

            // Stretch=Uniform: the image is scaled to fit the cell while
            // preserving aspect; whichever axis is tighter wins.
            double scale = Math.Min(cellW / srcW, cellH / srcH);
            double imgW = srcW * scale;
            double imgH = srcH * scale;
            double imgOffX = (cellW - imgW) / 2.0;
            double imgOffY = (cellH - imgH) / 2.0;

            // Final WPF coords inside the MediaCanvas.
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

        /// <summary>
        /// Bootstrap behaviour: until the first type-3 message arrives, the
        /// FFME element fills the canvas so video appears immediately. The
        /// first <see cref="OnOverlayRegionChanged"/> will resize it.
        /// </summary>
        private void SizeMediaToCanvasFill() {
            if (MediaCanvas == null) return;
            Canvas.SetLeft(Media, 0);
            Canvas.SetTop(Media, 0);
            Media.Width = double.IsNaN(MediaCanvas.ActualWidth) ? 0 : MediaCanvas.ActualWidth;
            Media.Height = double.IsNaN(MediaCanvas.ActualHeight) ? 0 : MediaCanvas.ActualHeight;
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

        private void FreeRdpClient_DataReceived(object sender, DataReceived e) {
            try {
                //var res = rdpClient.GetVirtualChannelOptions("McxSess");
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
            // Gate keyboard forwarding on session liveness. Set on the
            // worker thread so KeyDown handlers see it without round-tripping
            // through the dispatcher.
            _sessionActive = (e.State == SoftSledNative.State.Active);

            // MS-RRSP2 handshake direction note:
            // The spec (sec 2.2.1) says the *client* (us) sends
            // RemoteClientInformation first. Empirically, when running
            // with DSPA BIG=True the WMC server sends an unsolicited
            // kickoff byte on the splash VC anyway and the previous
            // SoftSled code triggered the send on first inbound byte.
            // Sending it proactively on State.Active *does* fire but
            // the server never replies — most likely because the
            // FreeRDP VC isn't actually plumbed end-to-end the moment
            // State == Active. So we keep the reactive trigger as the
            // primary path (see VirtualChannelSplashHandler.ProcessData)
            // and leave StartHandshake() available for opt-in / future
            // BIG=False work, but don't call it here.
            //
            // On Disconnected/Failed we reset the controller so a fresh
            // session starts from a clean state graph (no leaked visuals).
            if (e.State == SoftSledNative.State.Disconnected
             || e.State == SoftSledNative.State.Failed) {
                try { _splashController?.Reset(); } catch { }
            }

            // Mirror the mstscax button behaviour so the UI reflects the
            // FreeRDP-driven session state.
            Dispatcher.BeginInvoke(new Action(() => {
                if (e.State == SoftSledNative.State.Active) {
                    // Make the splash overlay visible once the session is
                    // up. Renderer paints nothing until messages arrive, so
                    // there's no visual overhead — but we want it visible
                    // by the time the first SetBackgroundColor lands.
                    if (splashHost != null) splashHost.Visibility = Visibility.Visible;
                } else if (e.State == SoftSledNative.State.Disconnected || e.State == SoftSledNative.State.Failed) {
                    if (splashHost != null) splashHost.Visibility = Visibility.Collapsed;
                }
            }));
        }

        private void McxSessHandler_StatusChanged(object sender, StatusChangedArgs e) {
            Dispatcher.BeginInvoke(new Action(() => {
                if (e.shellOpen) {
                    rdpDisplay.Visibility = Visibility.Visible;
                } else {
                    rdpDisplay.Visibility = Visibility.Hidden;
                }
            }));
        }


        private void Connect() {

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
            const bool isMcxPort = (port == 3391);

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
            freeRdpClient.SetInitialDesktopSize(1920, 1200);
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
            string pin = m_device.GetPairingCode();

            MessageBox.Show($"SoftSled is broadcasting! Use the key {pin} to pair the device");
        }

        // --- Cleanup ---

        private void FullScreenWindow_Closed(object sender, EventArgs e) {
            DisconnectRdp();
            try {
                freeRdpClient?.Dispose();
            } catch { /* ignored */ }
            freeRdpClient = null;
            try { _audioPlayer?.Dispose(); } catch { }
            try { _rawDumper?.Dispose(); } catch { }
            // Unhook FFME event handlers before the visual tree tears down.
            if (AvCtrlHandler != null) AvCtrlHandler.MediaController = null;
            try { _ffmeController?.Dispose(); } catch { }
            _ffmeController = null;
            if (_overlayDecoder != null) {
                _overlayDecoder.OverlayRegionChanged -= OnOverlayRegionChanged;
                _overlayDecoder.ZoomModeChanged -= OnZoomModeChanged;
            }
            _audioPlayer = null;
            _audioDumper = null;
            _rawDumper = null;
            _overlayDecoder = null;
            _fastpathDispatcher = null;

            // Stop the periodic FFME log flush, then drain the writer
            // buffer to disk so the last second or two of log lines aren't
            // lost on exit.
            try { _ffmeLogFlushTimer?.Dispose(); } catch { }
            _ffmeLogFlushTimer = null;
            try {
                lock (_ffmeLogGate) {
                    _ffmeLogWriter?.Flush();
                    _ffmeLogWriter?.Dispose();
                }
            } catch { }
            _ffmeLogWriter = null;
        }

        private void btnPair_Click(object sender, RoutedEventArgs e) {
        }
    }
}
