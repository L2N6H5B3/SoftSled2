using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// Standalone benchmark window for the "libav decode + Direct3D image"
    /// playback path. Two modes:
    ///   * Open file — libav decodes a media file, frames are colour-converted
    ///     to BGRA and presented through a D3D9Ex/D3DImage GPU surface. The HUD
    ///     reports decoded fps vs presented fps so you can compare against FFME
    ///     playing the same file.
    ///   * Synthetic 1080p — skips decode entirely; a generated 1080p BGRA
    ///     frame is offered as fast as possible, isolating the present path's
    ///     ceiling from decode cost.
    ///
    /// Opened from the shell via F12. Disposes everything on close. Nothing
    /// here is wired into the live extender session — it's purely a lab.
    /// </summary>
    public partial class VideoFpsLabWindow : Window {

        private readonly Logger _log = new VideoFpsLabLogger();
        private D3DImagePresenter _presenter;
        private LibAvVideoDecoder _decoder;

        private Thread _synthThread;
        private volatile bool _synthStop;
        private long _synthFrames;

        // Live RTSP tap state.
        private LibAvVideoPushDecoder _pushDecoder;
        private PtsFramePacer _pacer;
        // Unpaced live path only: the push decoder now emits packed yuv420p, so
        // when we present straight to the D3D surface (bypassing the pacer, which
        // would otherwise do this) we convert to BGRA here. Called only from the
        // decoder worker thread → single-threaded, matching the converter.
        private Yuv420ToBgra _liveConverter;
        private bool _liveActive;
        private bool _paced;
        private int _liveClockHz = 90000;
        private int _selectedPrerollMs = 250;
        private readonly object _liveGate = new object();

        private DispatcherTimer _hudTimer;
        private long _prevSource;
        private long _prevPresented;
        private System.Diagnostics.Stopwatch _hudClock;
        private string _mode = "idle";
        private int _w, _h;

        // Live-mode arrival instrumentation: measures the wire/MAU delivery
        // cadence BEFORE decode, so we can tell bursty delivery (large gap =
        // waiting on an I-frame) apart from a decoder/present problem.
        private readonly object _arrivalGate = new object();
        private readonly System.Diagnostics.Stopwatch _arrivalClock = new System.Diagnostics.Stopwatch();
        private long _mauArrived;
        private long _prevMauArrived;
        private long _lastArrivalMs = -1;
        private double _maxGapMs;

        public VideoFpsLabWindow() {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
        }

        private void OnSourceInitialized(object sender, EventArgs e) {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            try {
                _presenter = new D3DImagePresenter(Dispatcher, hwnd, _log);
                PreviewImage.Source = _presenter.Image;
            } catch (Exception ex) {
                StatusText.Text = "D3D9Ex device creation failed: " + ex.Message +
                                  "  (GPU may not support a lockable render target — " +
                                  "a D3D11 shared-surface path would be the fallback.)";
                return;
            }

            _hudClock = System.Diagnostics.Stopwatch.StartNew();
            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _hudTimer.Tick += UpdateHud;
            _hudTimer.Start();
        }

        private long SourceFrames {
            get {
                if (_decoder != null) return _decoder.FramesDecoded;
                if (_pushDecoder != null) return _pushDecoder.FramesDecoded;
                return Interlocked.Read(ref _synthFrames);
            }
        }

        private void UpdateHud(object sender, EventArgs e) {
            if (_presenter == null) return;
            long src = SourceFrames;
            long pres = _presenter.FramesPresented;
            double secs = _hudClock.Elapsed.TotalSeconds;
            _hudClock.Restart();
            if (secs <= 0) secs = 1;

            double srcFps  = (src  - _prevSource)    / secs;
            double presFps = (pres - _prevPresented) / secs;
            _prevSource = src;
            _prevPresented = pres;

            string sourceLabel = _mode == "synthetic" ? "offered" : "decoded";
            string hud =
                $"mode      : {_mode}\n" +
                $"resolution: {_w}x{_h}\n" +
                $"{sourceLabel,-9} : {srcFps,7:F1} fps\n" +
                $"presented : {presFps,7:F1} fps";

            if (_mode.StartsWith("live-rtsp")) {
                long mau; double maxGap;
                lock (_arrivalGate) {
                    mau = _mauArrived;
                    maxGap = _maxGapMs;
                    _maxGapMs = 0;
                }
                double arrFps = (mau - _prevMauArrived) / secs;
                _prevMauArrived = mau;
                // Arrival = wire/MAU cadence before decode. A max gap well
                // above the frame period (~40ms @ 25fps) means bursty
                // delivery (e.g. waiting on a large I-frame) — that, not the
                // decoder or present path, is what makes the fps wobble.
                hud =
                    $"mode      : {_mode}\n" +
                    $"resolution: {_w}x{_h}\n" +
                    $"arrival   : {arrFps,7:F1} fps  (max gap {maxGap,5:F0} ms)\n" +
                    $"decoded   : {srcFps,7:F1} fps\n" +
                    $"presented : {presFps,7:F1} fps";

                var pacer = _pacer;
                if (pacer != null) {
                    // Released-frame cadence: if the pacer works, max gap here
                    // collapses to ~the frame period (~40ms) even though
                    // arrival above still shows ~160ms bursts.
                    hud += $"\npreroll   : {pacer.PrerollMs} ms\n" +
                           $"released  : (gap max {pacer.ReadMaxReleaseGapMs(),5:F0} ms, " +
                           $"underflow {pacer.Underflows}, drop {pacer.Dropped})";
                }
            }

            HudText.Text = hud;
        }

        // ----- File mode -----

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e) {
            var dlg = new Microsoft.Win32.OpenFileDialog {
                Title = "Choose a video file to benchmark",
                Filter = "Media files|*.ts;*.mpg;*.mpeg;*.mp4;*.mkv;*.avi;*.wtv;*.dvr-ms;*.m2ts;*.wmv|All files|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;

            StopCurrent();
            _mode = "file";
            _w = _h = 0;
            StatusText.Text = "Decoding: " + dlg.FileName;

            _decoder = new LibAvVideoDecoder(dlg.FileName, _log);
            _decoder.OnFormatReady += (w, h) => Dispatcher.BeginInvoke(new Action(() => {
                _w = w; _h = h;
            }));
            _decoder.OnFrame += (ptr, stride, w, h) => _presenter?.SubmitFrame(ptr, stride, w, h);
            _decoder.Start();
        }

        // ----- Live RTSP tap mode -----

        private void BtnLive_Click(object sender, RoutedEventArgs e) => StartLive(paced: false);
        private void BtnLivePaced_Click(object sender, RoutedEventArgs e) => StartLive(paced: true);

        private void StartLive(bool paced) {
            StopCurrent();
            _mode = paced ? "live-rtsp (paced)" : "live-rtsp (unpaced)";
            _paced = paced;
            _selectedPrerollMs = ReadPrerollFromUi(); // capture on UI thread
            _w = _h = 0;
            StatusText.Text = (paced
                ? "Paced tap — jitter buffer + PTS-scheduled release should hold a steady ~40 ms cadence. "
                : "Unpaced tap — frames presented the instant they arrive (shows raw delivery jitter). ") +
                "Start (or keep) playback in WMC.";
            lock (_arrivalGate) {
                _mauArrived = 0; _prevMauArrived = 0; _lastArrivalMs = -1; _maxGapMs = 0;
                _arrivalClock.Restart();
            }
            _liveActive = true;
            LiveVideoTap.Frame += OnLiveFrame;
        }

        // Fires on the RTSP depacketizer thread for each live video MAU.
        private void OnLiveFrame(byte[] data, uint rtpTs, string wireCodec) {
            if (!_liveActive) return;

            // Record wire arrival cadence (before decode).
            lock (_arrivalGate) {
                long now = _arrivalClock.ElapsedMilliseconds;
                if (_lastArrivalMs >= 0) {
                    double gap = now - _lastArrivalMs;
                    if (gap > _maxGapMs) _maxGapMs = gap;
                }
                _lastArrivalMs = now;
                _mauArrived++;
            }
            var dec = _pushDecoder;
            if (dec == null) {
                lock (_liveGate) {
                    if (!_liveActive) return;
                    if (_pushDecoder == null) {
                        AVCodecID id = MapWireCodec(wireCodec);
                        _liveClockHz = MapClockHz(wireCodec);
                        if (_paced) {
                            _pacer = new PtsFramePacer(
                                (ptr, stride, w, h) => _presenter?.SubmitFrame(ptr, stride, w, h),
                                _selectedPrerollMs, _log);
                        }
                        var d = new LibAvVideoPushDecoder(id, _liveClockHz, _log);
                        d.OnFormatReady += (w, h) => Dispatcher.BeginInvoke(new Action(() => {
                            _w = w; _h = h;
                        }));
                        if (_paced) {
                            var pacer = _pacer;
                            d.OnFrame += (ptr, stride, w, h, ptsMs) => pacer?.Submit(ptr, stride, w, h, ptsMs);
                        } else {
                            _liveConverter = new Yuv420ToBgra(_log);
                            var conv = _liveConverter;
                            d.OnFrame += (ptr, stride, w, h, ptsMs) => {
                                IntPtr bgra = conv.Convert(ptr, w, h);
                                if (bgra != IntPtr.Zero) _presenter?.SubmitFrame(bgra, conv.Stride, w, h);
                            };
                        }
                        try {
                            d.Start();
                            _pushDecoder = d;
                            _log?.LogInfo($"[fpslab] live tap decoder started for wireCodec={wireCodec} " +
                                          $"→ {id} clockHz={_liveClockHz} paced={_paced}");
                        } catch (Exception ex) {
                            _log?.LogError($"[fpslab] live tap decoder start failed: {ex.Message}");
                            return;
                        }
                    }
                    dec = _pushDecoder;
                }
            }
            dec.SubmitPacket(data, rtpTs);
        }

        private static AVCodecID MapWireCodec(string wireCodec) {
            switch ((wireCodec ?? "").ToUpperInvariant()) {
                case "VND.MS.WM-MPV": return AVCodecID.AV_CODEC_ID_MPEG2VIDEO; // decodes MPEG-1 + 2
                case "H264":
                case "X-WMF-PF":      return AVCodecID.AV_CODEC_ID_H264;
                default:              return AVCodecID.AV_CODEC_ID_H264;
            }
        }

        // RTP timestamp clock per wire codec (from the SDP rtpmap convention):
        // vnd.ms.wm-MPV is 90 kHz; x-wmf-pf is a 1 kHz (1 ms) tick.
        private static int MapClockHz(string wireCodec) {
            switch ((wireCodec ?? "").ToUpperInvariant()) {
                case "VND.MS.WM-MPV": return 90000;
                case "X-WMF-PF":      return 1000;
                default:              return 90000;
            }
        }

        // Read the pre-roll dropdown. Called on the UI thread (from StartLive)
        // and cached, since OnLiveFrame runs on the depacketizer thread.
        private int ReadPrerollFromUi() {
            try {
                if (CboPreroll.SelectedItem is System.Windows.Controls.ComboBoxItem item
                    && item.Tag is string s && int.TryParse(s, out int ms) && ms > 0) {
                    return ms;
                }
            } catch { }
            return 250;
        }

        // ----- Synthetic present-only mode -----

        private void BtnSynthetic_Click(object sender, RoutedEventArgs e) {
            StopCurrent();
            _mode = "synthetic";
            _w = 1920; _h = 1080;
            StatusText.Text = "Synthetic 1080p — offering frames as fast as the present path accepts.";

            _synthStop = false;
            _synthThread = new Thread(SyntheticLoop) {
                IsBackground = true,
                Name = "VideoFpsLab-Synthetic",
            };
            _synthThread.Start();
        }

        private unsafe void SyntheticLoop() {
            const int W = 1920, H = 1080, Stride = W * 4;
            byte[] buf = new byte[Stride * H];
            // Prime with a vertical gradient so there's real content to upload.
            for (int y = 0; y < H; y++) {
                byte g = (byte)(y * 255 / H);
                int rowOff = y * Stride;
                for (int x = 0; x < W; x++) {
                    int o = rowOff + x * 4;
                    buf[o] = (byte)(x * 255 / W); // B
                    buf[o + 1] = g;               // G
                    buf[o + 2] = 64;              // R
                    buf[o + 3] = 255;             // A
                }
            }

            int phase = 0;
            fixed (byte* p = buf) {
                IntPtr ptr = (IntPtr)p;
                while (!_synthStop) {
                    // Cheap per-frame mutation: a moving white horizontal bar,
                    // so content changes every frame without re-filling 8 MB.
                    phase = (phase + 4) % H;
                    int barOff = phase * Stride;
                    for (int x = 0; x < Stride; x++) buf[barOff + x] = 255;

                    _presenter?.SubmitFrame(ptr, Stride, W, H);
                    Interlocked.Increment(ref _synthFrames);
                    Thread.Sleep(0); // yield; present is coalesced so this offers freely
                }
            }
        }

        // ----- Lifecycle -----

        private void BtnStop_Click(object sender, RoutedEventArgs e) {
            StopCurrent();
            _mode = "idle";
            StatusText.Text = "Stopped.";
        }

        private void StopCurrent() {
            // Live tap: detach first so no more MAUs arrive, then dispose the
            // decoder (stops producing frames), then the pacer.
            _liveActive = false;
            LiveVideoTap.Frame -= OnLiveFrame;
            lock (_liveGate) {
                try { _pushDecoder?.Dispose(); } catch { }
                _pushDecoder = null;
                try { _pacer?.Dispose(); } catch { }
                _pacer = null;
                try { _liveConverter?.Dispose(); } catch { }
                _liveConverter = null;
            }
            _paced = false;

            _synthStop = true;
            try { _synthThread?.Join(2000); } catch { }
            _synthThread = null;
            Interlocked.Exchange(ref _synthFrames, 0);

            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
        }

        private void OnClosed(object sender, EventArgs e) {
            _hudTimer?.Stop();
            StopCurrent();
            try { _presenter?.Dispose(); } catch { }
            _presenter = null;
        }
    }

    /// <summary>Minimal logger writing to the debug output + a temp file so the
    /// lab can run without the shell's logging surface.</summary>
    internal sealed class VideoFpsLabLogger : Logger {
        protected override void OnLogInfo(string message)
            => System.Diagnostics.Debug.WriteLine("[fpslab] INFO  " + message);
        protected override void OnLogDebug(string message)
            => System.Diagnostics.Debug.WriteLine("[fpslab] DEBUG " + message);
        protected override void OnLogError(string message)
            => System.Diagnostics.Debug.WriteLine("[fpslab] ERROR " + message);
    }
}
