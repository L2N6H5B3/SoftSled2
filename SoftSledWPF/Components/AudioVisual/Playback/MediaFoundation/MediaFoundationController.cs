using SoftSled.Components.AudioVisual;
using SoftSled.Components.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SharpDX.Mathematics.Interop;
using SDX = SharpDX;
using SDXMF = SharpDX.MediaFoundation;
using SDXD11 = SharpDX.Direct3D11;
using SDXD9 = SharpDX.Direct3D9;
using SDXDXGI = SharpDX.DXGI;

namespace SoftSled.Components.AudioVisual.Playback.MediaFoundation {

    /// <summary>
    /// <see cref="IMediaController"/> implementation backed by SharpDX's
    /// MediaEngine wrapper over Media Foundation, in <b>frame-server
    /// mode</b>. Renders into a WPF <see cref="D3DImage"/> via a shared
    /// D3D11/D3D9 texture pair so WPF overlays composite normally above
    /// the video (no HWND airspace problem).
    ///
    /// <para>Pipeline:</para>
    /// <list type="number">
    ///   <item>An <see cref="AsfStreamProducer"/> is wrapped in
    ///   <see cref="MediaFoundationProducerStream"/> (a non-seekable
    ///   <see cref="System.IO.Stream"/>) and handed to SharpDX's
    ///   <c>ByteStream</c> (the managed <c>IMFByteStream</c>).</item>
    ///   <item>MediaEngine is constructed in frame-server mode by NOT
    ///   setting <c>PlaybackHwnd</c>, and is given a
    ///   <see cref="SDXMF.DXGIDeviceManager"/> backed by a D3D11
    ///   hardware device. With a DXGI manager attached MediaEngine
    ///   uses hardware decoders (DXVA) and renders into D3D11 textures
    ///   we provide via <see cref="SDXMF.MediaEngine.TransferVideoFrame"/>.</item>
    ///   <item>On the first frame we allocate a B8G8R8A8 D3D11
    ///   Texture2D with <c>ResourceOptionFlags.Shared</c>, get its
    ///   share handle, and open that handle as a D3D9 surface on our
    ///   <see cref="SDXD9.DeviceEx"/>. Both APIs now point at the
    ///   same GPU memory — TransferVideoFrame on the D3D11 side
    ///   makes the new pixels visible to D3DImage on the D3D9 side.</item>
    ///   <item>A 16 ms <see cref="Timer"/> drives the render tick.
    ///   Each tick (marshalled to the UI thread at Render priority):
    ///   poll <c>OnVideoStreamTick</c>; if <c>S_OK</c>, call
    ///   <c>TransferVideoFrame</c> and <c>D3DImage.AddDirtyRect</c>.</item>
    ///   <item>Audio is handled internally by MediaEngine via the
    ///   default Windows audio renderer; we don't touch it.</item>
    /// </list>
    ///
    /// <para>Why this matters over the previous HWND-rendering mode:
    /// HWND rendering creates an airspace overlay that WPF can't
    /// composite anything above. Splash overlays, log textbox, the
    /// connecting spinner — all would have been hidden behind the
    /// video plane. Frame-server + D3DImage puts the video INSIDE
    /// the WPF visual tree, so everything above it in z-order
    /// blends normally.</para>
    /// </summary>
    internal sealed class MediaFoundationController : IMediaController, IDisposable {

        // ---- WPF render target ----
        private readonly System.Windows.Controls.Image _videoTarget;
        private readonly Dispatcher _dispatcher;
        private readonly Logger _log;
        private D3DImage _d3dImage;
        private bool _backBufferAssigned;

        // ---- MediaFoundation ----
        private SDXMF.MediaEngine _engine;
        private SDXMF.MediaEngineEx _engineEx;
        private SDXMF.MediaEngineClassFactory _factory;
        private SDXMF.MediaEngineAttributes _attrs;
        private SDXMF.DXGIDeviceManager _dxgiManager;
        private SDXMF.ByteStream _byteStream;
        private MediaFoundationProducerStream _producerStream;

        // ---- D3D11 (MediaEngine output) ----
        private SDXD11.Device _d3d11Device;
        private SDXD11.Texture2D _d3d11Tex;
        private int _texW, _texH;

        // ---- D3D9 (D3DImage backing) ----
        private SDXD9.Direct3DEx _d3d9Ex;
        private SDXD9.DeviceEx _d3d9DeviceEx;
        private SDXD9.Texture _d3d9Tex;
        private SDXD9.Surface _d3d9Surface;

        // ---- Render tick ----
        // 16 ms = 60 Hz target. The tick is the only thing that drives
        // visible updates in frame-server mode; OnVideoStreamTick
        // returns S_FALSE when no new frame is ready, in which case we
        // just leave the previous frame visible.
        private const int TickPeriodMs = 16;
        private Timer _tickTimer;
        private int _tickPending;
        private readonly Action _doTickAction;

        // ---- Diagnostics ----
        private long _framesPresented;
        private long _framesTransferred;
        private long _windowPresented;
        private long _windowStartMs;
        private readonly System.Diagnostics.Stopwatch _statsClock = System.Diagnostics.Stopwatch.StartNew();

        // ---- State ----
        private volatile bool _isOpen;
        private long _cachedDurationTicks;
        private bool _disposed;

        // ---- Pending state for early callers ----
        private long _pendingSeekMs = -1;
        private double _pendingRate = double.NaN;

        private TaskCompletionSource<bool> _openTcs =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private SoftSled.Components.RTSP.RTSPClient _rtsp;
        private long _lastBandwidthBps = -1;
        private bool _lastOptimisedPreroll;
        private double _lastRequestedRate = 1.0;

        public MediaFoundationController(System.Windows.Controls.Image videoTarget, Logger log) {
            _videoTarget = videoTarget ?? throw new ArgumentNullException(nameof(videoTarget));
            _dispatcher = _videoTarget.Dispatcher;
            _log = log;
            _doTickAction = DoTick;

            // Media Foundation needs to be initialised before any MF
            // object can be created. Idempotent.
            SDXMF.MediaManager.Startup();

            // All D3D9/D3D11/D3DImage construction must happen on the
            // UI thread (D3DImage WPF requirement). MediaEngine itself
            // is thread-safe but we keep the device construction
            // co-located for simpler teardown.
            _dispatcher.BeginInvoke(new Action(InitOnUiThread));
        }

        private void InitOnUiThread() {
            if (_disposed) return;
            try {
                // ----- D3D11 device (where MediaEngine will decode + render frames) -----
                // BgraSupport: D3DImage requires B8G8R8A8_UNorm
                // (matches D3DFMT_X8R8G8B8 on the D3D9 side).
                // VideoSupport: enables DXVA hardware decode paths.
                _d3d11Device = new SDXD11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    SDXD11.DeviceCreationFlags.BgraSupport | SDXD11.DeviceCreationFlags.VideoSupport);

                // Multithread-protect the immediate context. Required
                // because MediaEngine's worker threads will access the
                // D3D11 device for decode + frame transfer while our
                // render thread reads it for D3DImage updates. SharpDX
                // calls the ID3D11Multithread interface 'Multithread'.
                using (var mt = _d3d11Device.ImmediateContext.QueryInterface<SDXD11.Multithread>()) {
                    mt.SetMultithreadProtected(true);
                }

                // ----- DXGI device manager (the bridge from MediaEngine to D3D11) -----
                _dxgiManager = new SDXMF.DXGIDeviceManager();
                _dxgiManager.ResetDevice(_d3d11Device);

                // ----- D3D9Ex device (for the D3DImage side of the shared surface) -----
                _d3d9Ex = new SDXD9.Direct3DEx();
                var pp = new SDXD9.PresentParameters {
                    Windowed = true,
                    SwapEffect = SDXD9.SwapEffect.Discard,
                    DeviceWindowHandle = GetDesktopWindow(),
                    PresentationInterval = SDXD9.PresentInterval.Default,
                    BackBufferFormat = SDXD9.Format.X8R8G8B8,
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                };
                _d3d9DeviceEx = new SDXD9.DeviceEx(_d3d9Ex, 0,
                    SDXD9.DeviceType.Hardware, GetDesktopWindow(),
                    SDXD9.CreateFlags.HardwareVertexProcessing
                    | SDXD9.CreateFlags.Multithreaded
                    | SDXD9.CreateFlags.FpuPreserve,
                    pp);

                // ----- MediaEngine attributes -----
                _factory = new SDXMF.MediaEngineClassFactory();
                _attrs = new SDXMF.MediaEngineAttributes();
                // DxgiManager binds MediaEngine to our D3D11 device so
                // it uses hardware decoders and we can hand it our
                // own D3D11 textures via TransferVideoFrame.
                _attrs.Set(SDXMF.MediaEngineAttributeKeys.DxgiManager, _dxgiManager);
                // VideoOutputFormat picks the pixel format
                // TransferVideoFrame will write. B8G8R8A8_UNorm
                // matches D3DFMT_X8R8G8B8 on the D3D9 side bit-for-bit
                // (little-endian BGRA32) so the shared texture is
                // valid to both APIs without any conversion.
                _attrs.Set(SDXMF.MediaEngineAttributeKeys.VideoOutputFormat,
                    (int)SDXDXGI.Format.B8G8R8A8_UNorm);

                _engine = new SDXMF.MediaEngine(_factory, _attrs,
                    SDXMF.MediaEngineCreateFlags.None,
                    OnMediaEngineEvent);

                // QueryInterface to the Ex variant for
                // SetSourceFromByteStream.
                _engineEx = _engine.QueryInterface<SDXMF.MediaEngineEx>();

                // ----- D3DImage on the target Image element -----
                _d3dImage = new D3DImage();
                _videoTarget.Source = _d3dImage;
                _videoTarget.Visibility = Visibility.Visible;

                // ----- Start the render tick -----
                _tickTimer = new Timer(OnTimerTick, null, TickPeriodMs, TickPeriodMs);

                _log?.LogInfo("[mf-mc] frame-server mode ready " +
                              "(D3D11 + DXGIDeviceManager + D3D9Ex/D3DImage)");
            } catch (Exception ex) {
                _log?.LogError($"[mf-mc] InitOnUiThread failed: {ex}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        // ============================================================
        //  Source binding
        // ============================================================

        public void OpenProducer(AsfStreamProducer producer, string mimeHint) {
            if (_disposed) return;
            if (producer == null) throw new ArgumentNullException(nameof(producer));
            if (_byteStream != null) {
                _log?.LogError("[mf-mc] OpenProducer called twice — refusing second open");
                return;
            }
            try {
                _producerStream = new MediaFoundationProducerStream(producer, _log);
                _byteStream = new SDXMF.ByteStream(_producerStream);

                string url = string.IsNullOrEmpty(mimeHint)
                    ? "softsled://stream"
                    : mimeHint;
                _engineEx.SetSourceFromByteStream(_byteStream, url);
                _log?.LogInfo($"[mf-mc] SetSourceFromByteStream(url={url}) issued");
            } catch (Exception ex) {
                _log?.LogError($"[mf-mc] OpenProducer failed: {ex}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        // ============================================================
        //  MediaEngine event callback (runs on MF worker thread)
        // ============================================================

        private void OnMediaEngineEvent(SDXMF.MediaEngineEvent eventId,
                                        long param1, int param2) {
            try {
                switch (eventId) {
                    case SDXMF.MediaEngineEvent.LoadedMetadata: {
                        double durSec = 0;
                        try { durSec = _engine.Duration; } catch { }
                        long ticks = double.IsNaN(durSec) || durSec <= 0
                            ? 0L
                            : (long)(durSec * TimeSpan.TicksPerSecond);
                        Interlocked.Exchange(ref _cachedDurationTicks, ticks);

                        // Try to read native video size now; if we can't
                        // (some sources don't expose it until first
                        // frame), the tick handler will retry later.
                        TryReadNativeVideoSize();
                        _log?.LogInfo($"[mf-mc] LoadedMetadata — duration={(ticks > 0 ? new TimeSpan(ticks).ToString() : "live/unknown")}, " +
                                      $"nativeSize={_texW}x{_texH}");
                        break;
                    }
                    case SDXMF.MediaEngineEvent.CanPlay:
                    case SDXMF.MediaEngineEvent.CanPlayThrough: {
                        if (!_isOpen) {
                            _isOpen = true;
                            _log?.LogInfo($"[mf-mc] {eventId} → IsOpen=true");
                            try { _openTcs.TrySetResult(true); } catch { }
                            try { BufferingEnded?.Invoke(); } catch { }
                        }
                        break;
                    }
                    case SDXMF.MediaEngineEvent.Ended: {
                        _log?.LogInfo("[mf-mc] Ended");
                        try { MediaEnded?.Invoke(); } catch { }
                        break;
                    }
                    case SDXMF.MediaEngineEvent.Error: {
                        var err = (SDXMF.MediaEngineErr)param1;
                        var msg = $"MediaEngine error code={err} hresult=0x{param2:X8}";
                        _log?.LogError("[mf-mc] " + msg);
                        try { MediaFailed?.Invoke(new Exception(msg)); } catch { }
                        break;
                    }
                    default:
                        _log?.LogDebug($"[mf-mc] event {eventId} param1={param1} param2={param2}");
                        break;
                }
            } catch (Exception ex) {
                _log?.LogError($"[mf-mc] OnMediaEngineEvent threw: {ex.Message}");
            }
        }

        private void TryReadNativeVideoSize() {
            try {
                int w, h;
                _engineEx.GetNativeVideoSize(out w, out h);
                if (w > 0 && h > 0) {
                    _texW = w;
                    _texH = h;
                }
            } catch { /* not ready yet — retry on next tick */ }
        }

        // ============================================================
        //  Render tick — drives TransferVideoFrame into the shared
        //  texture and asks D3DImage to refresh.
        // ============================================================

        private void OnTimerTick(object state) {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _tickPending, 1) != 0) return;
            try {
                _dispatcher.BeginInvoke(_doTickAction, DispatcherPriority.Render);
            } catch {
                Interlocked.Exchange(ref _tickPending, 0);
            }
        }

        private void DoTick() {
            try {
                if (_disposed || _engine == null) return;

                // First tick after metadata load: pull native size if
                // we didn't get it from LoadedMetadata.
                if (_texW == 0 || _texH == 0) {
                    TryReadNativeVideoSize();
                    if (_texW == 0 || _texH == 0) return;  // still not ready
                }

                // Lazy-allocate the shared texture pair on first valid size.
                if (_d3d11Tex == null) {
                    if (!TryAllocateSharedTexture(_texW, _texH)) return;
                }

                // Ask MediaEngine if a new frame is available.
                // SharpDX 4.2's wrapper coerces the HRESULT to a
                // RawBool: true = S_OK (new frame), false = S_FALSE
                // (no new frame yet).
                long pts;
                bool newFrame = _engine.OnVideoStreamTick(out pts);
                if (!newFrame) return;

                // Transfer the new frame into our shared D3D11 texture.
                var dstRect = new RawRectangle(0, 0, _texW, _texH);
                _engine.TransferVideoFrame(_d3d11Tex, null, dstRect, null);
                Interlocked.Increment(ref _framesTransferred);

                // D3D11→D3D9 is the same GPU memory via the shared
                // handle; D3DImage just needs to be told the back
                // buffer changed.
                _d3dImage.Lock();
                try {
                    if (!_backBufferAssigned) {
                        _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9,
                                                _d3d9Surface.NativePointer);
                        _backBufferAssigned = true;
                        _log?.LogInfo($"[mf-mc] D3DImage backbuffer attached: " +
                                      $"IsFrontBufferAvailable={_d3dImage.IsFrontBufferAvailable}");
                    }
                    _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _texW, _texH));
                } finally {
                    _d3dImage.Unlock();
                }

                Interlocked.Increment(ref _framesPresented);
                Interlocked.Increment(ref _windowPresented);
                MaybeLogFps();
            } catch (Exception ex) {
                _log?.LogError($"[mf-mc] DoTick threw: {ex.Message}");
            } finally {
                Interlocked.Exchange(ref _tickPending, 0);
            }
        }

        private bool TryAllocateSharedTexture(int w, int h) {
            try {
                // D3D11 side: shared B8G8R8A8 texture, render-target
                // capable so MediaEngine can write to it.
                var desc = new SDXD11.Texture2DDescription {
                    Width = w,
                    Height = h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = SDXDXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new SDXDXGI.SampleDescription(1, 0),
                    Usage = SDXD11.ResourceUsage.Default,
                    BindFlags = SDXD11.BindFlags.RenderTarget | SDXD11.BindFlags.ShaderResource,
                    CpuAccessFlags = SDXD11.CpuAccessFlags.None,
                    // Shared (NT-handle-style not required for legacy
                    // share — D3D9 only understands the old handle
                    // kind, which is what ResourceOptionFlags.Shared
                    // produces).
                    OptionFlags = SDXD11.ResourceOptionFlags.Shared,
                };
                _d3d11Tex = new SDXD11.Texture2D(_d3d11Device, desc);

                // Pull the share handle out of the texture's DXGI side.
                IntPtr shareHandle;
                using (var dxgiResource = _d3d11Tex.QueryInterface<SDXDXGI.Resource>()) {
                    shareHandle = dxgiResource.SharedHandle;
                }
                if (shareHandle == IntPtr.Zero) {
                    _log?.LogError("[mf-mc] D3D11 texture shared handle is null — D3DImage will fall back to software");
                    return false;
                }

                // D3D9 side: open the same shared resource. The Texture
                // constructor with the trailing `ref IntPtr` argument
                // OPENS an existing shared surface using the supplied
                // handle (it's both in + out — in: existing handle to
                // open; out: handle of the new resource, ignored in
                // this case).
                _d3d9Tex = new SDXD9.Texture(_d3d9DeviceEx, w, h, 1,
                    SDXD9.Usage.RenderTarget, SDXD9.Format.A8R8G8B8,
                    SDXD9.Pool.Default, ref shareHandle);
                _d3d9Surface = _d3d9Tex.GetSurfaceLevel(0);

                _backBufferAssigned = false;
                _log?.LogInfo($"[mf-mc] shared texture pair allocated: {w}x{h} " +
                              $"(shareHandle=0x{shareHandle.ToInt64():X})");
                return true;
            } catch (Exception ex) {
                _log?.LogError($"[mf-mc] TryAllocateSharedTexture failed: {ex.Message}");
                return false;
            }
        }

        private void MaybeLogFps() {
            long now = _statsClock.ElapsedMilliseconds;
            long start = Interlocked.Read(ref _windowStartMs);
            long elapsed = now - start;
            if (elapsed < 1000) return;
            if (Interlocked.CompareExchange(ref _windowStartMs, now, start) != start) return;
            long n = Interlocked.Exchange(ref _windowPresented, 0);
            double fps = n * 1000.0 / elapsed;
            _log?.LogInfo($"[mf-mc] fps={fps:F1} (presented={n} in {elapsed}ms, " +
                          $"transferred-total={_framesTransferred})");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        // ============================================================
        //  IMediaController surface
        // ============================================================

        public TimeSpan Position {
            get {
                if (_disposed || _engine == null) return TimeSpan.Zero;
                try {
                    double t = _engine.CurrentTime;
                    if (double.IsNaN(t) || t < 0) return TimeSpan.Zero;
                    return TimeSpan.FromSeconds(t);
                } catch { return TimeSpan.Zero; }
            }
        }

        public TimeSpan? Duration {
            get {
                long ticks = Interlocked.Read(ref _cachedDurationTicks);
                return ticks > 0 ? new TimeSpan(ticks) : (TimeSpan?)null;
            }
        }

        public bool IsOpen => !_disposed && _isOpen;

        public Task WaitUntilOpenAsync(int timeoutMs) {
            if (_disposed) return Task.CompletedTask;
            if (_isOpen) return Task.CompletedTask;
            var tcs = _openTcs;
            if (timeoutMs < 0) return tcs.Task;
            if (timeoutMs == 0) return Task.CompletedTask;
            return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }

        public Task PlayAsync() {
            if (_disposed || _engine == null) return Task.CompletedTask;
            try { _engine.Play(); }
            catch (Exception ex) { _log?.LogError($"[mf-mc] Play failed: {ex.Message}"); }
            return Task.CompletedTask;
        }

        public Task PauseAsync() {
            if (_disposed || _engine == null) return Task.CompletedTask;
            try { _engine.Pause(); }
            catch (Exception ex) { _log?.LogError($"[mf-mc] Pause failed: {ex.Message}"); }
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position) {
            if (_disposed || _engine == null) return Task.CompletedTask;
            if (!_isOpen) {
                Interlocked.Exchange(ref _pendingSeekMs, (long)position.TotalMilliseconds);
                _log?.LogInfo($"[mf-mc] SeekAsync({position}) deferred — engine not open yet");
                return Task.CompletedTask;
            }
            try { _engine.CurrentTime = position.TotalSeconds; }
            catch (Exception ex) { _log?.LogError($"[mf-mc] Seek failed: {ex.Message}"); }
            try { _rtsp?.Play(startMs: (long)position.TotalMilliseconds, rate: _lastRequestedRate); }
            catch (Exception ex) { _log?.LogError($"[mf-mc] RTSP seek failed: {ex.Message}"); }
            return Task.CompletedTask;
        }

        public Task SetRateAsync(double rate) {
            if (_disposed) return Task.CompletedTask;
            _lastRequestedRate = rate;
            bool clientSideEligible = rate >= 0.5 && rate <= 2.0;
            if (!_isOpen) {
                _pendingRate = rate;
                _log?.LogInfo($"[mf-mc] SetRateAsync({rate}) deferred — engine not open yet");
                return Task.CompletedTask;
            }
            if (clientSideEligible) {
                try { _engine.PlaybackRate = rate; }
                catch (Exception ex) { _log?.LogError($"[mf-mc] PlaybackRate={rate} failed: {ex.Message}"); }
                try { _rtsp?.SetRate(1.0); }
                catch (Exception ex) { _log?.LogError($"[mf-mc] RTSP SetRate(1.0) failed: {ex.Message}"); }
            } else {
                try { _engine.PlaybackRate = 1.0; }
                catch (Exception ex) { _log?.LogError($"[mf-mc] PlaybackRate=1.0 failed: {ex.Message}"); }
                try { _rtsp?.SetRate(rate); }
                catch (Exception ex) { _log?.LogError($"[mf-mc] RTSP SetRate({rate}) failed: {ex.Message}"); }
            }
            return Task.CompletedTask;
        }

        public void SetAvailableBandwidth(long bitsPerSecond) {
            try { _rtsp?.SetBufferInfo(bitsPerSecond, _lastOptimisedPreroll); }
            catch (Exception ex) {
                _log?.LogError($"[mf-mc] SetBufferInfo (bandwidth) failed: {ex.Message}");
            }
            _lastBandwidthBps = bitsPerSecond;
        }

        public void SetOptimisedPreroll(bool optimised) {
            try { _rtsp?.SetBufferInfo(_lastBandwidthBps, optimised); }
            catch (Exception ex) {
                _log?.LogError($"[mf-mc] SetBufferInfo (preroll) failed: {ex.Message}");
            }
            _lastOptimisedPreroll = optimised;
        }

        public void AttachRtspClient(SoftSled.Components.RTSP.RTSPClient client) {
            if (ReferenceEquals(_rtsp, client)) return;
            if (_rtsp != null) {
                _rtsp.Disconnected      -= OnRtspDisconnected;
                _rtsp.PtsError          -= OnRtspPtsError;
                _rtsp.UnrecoverableSkew -= OnRtspUnrecoverableSkew;
            }
            _rtsp = client;
            if (_rtsp != null) {
                _rtsp.Disconnected      += OnRtspDisconnected;
                _rtsp.PtsError          += OnRtspPtsError;
                _rtsp.UnrecoverableSkew += OnRtspUnrecoverableSkew;
                try { _rtsp.EnableAudioSilenceInjection(_log); }
                catch (Exception ex) {
                    _log?.LogError($"[mf-mc] EnableAudioSilenceInjection threw: {ex.Message}");
                }
            }
        }

        public event Action BufferingEnded;
        public event Action MediaEnded;
        public event Action<Exception> MediaFailed;
        public event Action<Exception> RtspDisconnected;
        public event Action<PtsErrorInfo> PtsError;
        public event Action<SkewInfo> UnrecoverableSkew;

        private void OnRtspDisconnected(Exception ex) { try { RtspDisconnected?.Invoke(ex); } catch { } }
        private void OnRtspPtsError(PtsErrorInfo info) { try { PtsError?.Invoke(info); } catch { } }
        private void OnRtspUnrecoverableSkew(SkewInfo info) { try { UnrecoverableSkew?.Invoke(info); } catch { } }

        // ============================================================
        //  Teardown
        // ============================================================

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _tickTimer?.Dispose(); } catch { }
            _tickTimer = null;
            AttachRtspClient(null);
            try { _engine?.Shutdown(); } catch { }
            try {
                _dispatcher.Invoke(() => {
                    try { _videoTarget.Source = null; } catch { }
                });
            } catch { }
            // Release in inverse order of creation. D3D9 surface, then
            // D3D9 texture, then D3D11 texture, then devices, then MF.
            try { _d3d9Surface?.Dispose(); } catch { }
            _d3d9Surface = null;
            try { _d3d9Tex?.Dispose(); } catch { }
            _d3d9Tex = null;
            try { _d3d11Tex?.Dispose(); } catch { }
            _d3d11Tex = null;
            try { _engineEx?.Dispose(); } catch { }
            _engineEx = null;
            try { _engine?.Dispose(); } catch { }
            _engine = null;
            try { _byteStream?.Dispose(); } catch { }
            _byteStream = null;
            try { _attrs?.Dispose(); } catch { }
            _attrs = null;
            try { _factory?.Dispose(); } catch { }
            _factory = null;
            try { _dxgiManager?.Dispose(); } catch { }
            _dxgiManager = null;
            try { _d3d9DeviceEx?.Dispose(); } catch { }
            _d3d9DeviceEx = null;
            try { _d3d9Ex?.Dispose(); } catch { }
            _d3d9Ex = null;
            try { _d3d11Device?.Dispose(); } catch { }
            _d3d11Device = null;
            try { _producerStream?.Dispose(); } catch { }
            _producerStream = null;
            _log?.LogInfo($"[mf-mc] disposed (frames presented={_framesPresented}, " +
                          $"transferred={_framesTransferred})");
        }
    }
}
