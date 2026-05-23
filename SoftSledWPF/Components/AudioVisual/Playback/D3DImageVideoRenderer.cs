using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// GPU-backed video renderer using <see cref="D3DImage"/>. Replaces the
    /// <c>WriteableBitmap</c>-based <see cref="WpfVideoRenderer"/> path
    /// because that path was bottle-necked by WPF compositor cycles
    /// (observed ~10 Hz tick rate under combined RDP + splash + video
    /// load, with ~25 fps source frames being dropped 60% of the time as
    /// "late" because multiple were due in the same tick).
    ///
    /// Why D3DImage helps even with CPU upload:
    ///
    /// <para><c>WriteableBitmap</c> is a software-backed bitmap; on every
    /// composition cycle the WPF compositor uploads the changed pixels to
    /// the GPU. At 1080p that's an 8 MB upload per cycle, which compounds
    /// with other compositor work (rdpDisplay framebuffer updates, splash
    /// overlay invalidations) and chokes the tick rate.</para>
    ///
    /// <para><c>D3DImage</c> wraps a Direct3D 9 surface that already lives
    /// on the GPU. The WPF compositor doesn't upload anything — it just
    /// references the surface during composition. Per-tick work drops
    /// from "8 MB CPU→GPU upload + composite" to "reference texture +
    /// composite", which typically lets the compositor recover its full
    /// 60 Hz cadence.</para>
    ///
    /// <para>Phase 2 (DXVA hardware decode) would further eliminate the
    /// per-frame CPU upload by having libav decode directly into a
    /// GPU-resident D3D11 texture, shared as a D3D9 surface for
    /// D3DImage. That's a follow-up; this Phase 1 path still copies
    /// decoded BGRA bytes from the decoder into the D3D9 surface on the
    /// UI thread per frame, but the upload goes to a D3D9 surface (one
    /// memcpy via LockRect/UnlockRect) instead of through WriteableBitmap's
    /// back-buffer (two copies + a compositor-side upload).</para>
    ///
    /// Lifecycle:
    /// <list type="bullet">
    ///   <item>Constructor: initialises an offscreen D3D9Ex device and an
    ///   empty <see cref="D3DImage"/> assigned to the target Image
    ///   control. The actual surface is allocated lazily on the first
    ///   frame so we know the source dimensions.</item>
    ///   <item><see cref="EnqueueFrame"/>: producer hand-off from the
    ///   decoder thread. Thread-safe bounded queue, drops oldest on
    ///   overflow.</item>
    ///   <item>WPF <c>CompositionTarget.Rendering</c> tick (UI thread):
    ///   picks the latest due frame from the queue, locks the D3D9
    ///   surface, memcpys the BGRA bytes in, unlocks, and tells
    ///   D3DImage which rect changed.</item>
    ///   <item><see cref="Dispose"/>: releases the D3D9 device + surface,
    ///   drops the D3DImage off the target.</item>
    /// </list>
    /// </summary>
    internal sealed class D3DImageVideoRenderer : IDisposable {

        private readonly Image _target;
        private readonly PlaybackClock _clock;
        private readonly Logger _log;
        private readonly ConcurrentQueue<VideoFrameSample> _queue = new ConcurrentQueue<VideoFrameSample>();
        private const int MaxQueuedFrames = 60;     // ~1 s @ 60 fps headroom

        // D3D9 + D3DImage state. All UI-thread-owned after construction.
        private IntPtr _d3d9Ex;
        private IntPtr _device;
        private IntPtr _surface;
        private int _surfaceW;
        private int _surfaceH;
        private D3DImage _d3dImage;
        private bool _backBufferAssigned;

        // Diagnostics — matches WpfVideoRenderer for direct comparison.
        private long _framesPresented;
        private long _framesDroppedLate;
        private long _framesDroppedOverflow;
        private long _fpsWindowStartMs;
        private long _fpsWindowPresented;
        private long _fpsWindowDroppedLate;
        private long _fpsWindowDroppedOverflow;
        private readonly System.Diagnostics.Stopwatch _fpsSw = System.Diagnostics.Stopwatch.StartNew();

        private bool _disposed;
        private EventHandler _renderingHandler;

        public D3DImageVideoRenderer(Image target, PlaybackClock clock, Logger log) {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log;

            // All D3D9 + D3DImage initialisation has to happen on the UI
            // thread (WPF requirement for D3DImage; D3D9 device works
            // anywhere but we keep it consistent for simpler teardown).
            _target.Dispatcher.BeginInvoke(new Action(InitOnUiThread));
        }

        private void InitOnUiThread() {
            if (_disposed) return;
            try {
                int hr = D3D9Interop.Direct3DCreate9Ex(D3D9Interop.D3D_SDK_VERSION, out _d3d9Ex);
                if (D3D9Interop.Failed(hr) || _d3d9Ex == IntPtr.Zero) {
                    _log?.LogError($"[d3d-video] Direct3DCreate9Ex failed: 0x{hr:X8}");
                    return;
                }

                // Minimal offscreen device. BackBuffer dimensions are
                // ignored (we never present a swap chain), but a sane
                // format is required. hDeviceWindow uses a hidden message
                // window handle — we get one via GetDesktopWindow which
                // is a safe sentinel (the device is never windowed).
                var pp = new D3D9Interop.D3DPRESENT_PARAMETERS {
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    BackBufferFormat = D3D9Interop.D3DFMT_X8R8G8B8,
                    BackBufferCount = 1,
                    MultiSampleType = D3D9Interop.D3DMULTISAMPLE_NONE,
                    MultiSampleQuality = 0,
                    SwapEffect = 1,          // D3DSWAPEFFECT_DISCARD
                    hDeviceWindow = GetDesktopWindow(),
                    Windowed = 1,
                    EnableAutoDepthStencil = 0,
                    AutoDepthStencilFormat = 0,
                    Flags = 0,
                    FullScreen_RefreshRateInHz = 0,
                    PresentationInterval = 0,
                };

                uint behavior = D3D9Interop.D3DCREATE_HARDWARE_VERTEXPROCESSING
                              | D3D9Interop.D3DCREATE_MULTITHREADED
                              | D3D9Interop.D3DCREATE_FPU_PRESERVE;

                hr = D3D9Interop.CreateDeviceEx(_d3d9Ex, D3D9Interop.D3DADAPTER_DEFAULT,
                    D3D9Interop.D3DDEVTYPE_HAL, GetDesktopWindow(), behavior, ref pp, out _device);
                if (D3D9Interop.Failed(hr) || _device == IntPtr.Zero) {
                    // Fall back to software vertex processing — some
                    // virtualised environments lack a hardware VP path.
                    behavior = D3D9Interop.D3DCREATE_SOFTWARE_VERTEXPROCESSING
                             | D3D9Interop.D3DCREATE_MULTITHREADED
                             | D3D9Interop.D3DCREATE_FPU_PRESERVE;
                    hr = D3D9Interop.CreateDeviceEx(_d3d9Ex, D3D9Interop.D3DADAPTER_DEFAULT,
                        D3D9Interop.D3DDEVTYPE_HAL, GetDesktopWindow(), behavior, ref pp, out _device);
                }
                if (D3D9Interop.Failed(hr) || _device == IntPtr.Zero) {
                    _log?.LogError($"[d3d-video] CreateDeviceEx failed: 0x{hr:X8}");
                    return;
                }

                _d3dImage = new D3DImage();
                _target.Source = _d3dImage;
                _target.Visibility = Visibility.Visible;

                _renderingHandler = OnRenderingTick;
                CompositionTarget.Rendering += _renderingHandler;

                _log?.LogInfo($"[d3d-video] D3D9Ex device + D3DImage ready");
            } catch (Exception ex) {
                _log?.LogError($"[d3d-video] init failed: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        /// <summary>Enqueue a decoded frame for presentation. Safe from
        /// any thread.</summary>
        public void EnqueueFrame(VideoFrameSample sample) {
            if (_disposed || sample == null) return;
            if (_queue.Count >= MaxQueuedFrames) {
                if (_queue.TryDequeue(out var dropped)) {
                    dropped?.Release();
                    Interlocked.Increment(ref _fpsWindowDroppedOverflow);
                    long n = Interlocked.Increment(ref _framesDroppedOverflow);
                    if ((n & 0x1F) == 1) {
                        _log?.LogError($"[d3d-video] queue overflow, dropped oldest (total {n})");
                    }
                }
            }
            _queue.Enqueue(sample);
        }

        // ---- UI thread ----

        private void OnRenderingTick(object sender, EventArgs e) {
            if (_disposed || _device == IntPtr.Zero || _d3dImage == null) return;
            long now = _clock.CurrentMediaTimeMs;

            // Drain due frames; keep only the most recent (same liveness
            // policy as WpfVideoRenderer).
            VideoFrameSample dueFrame = null;
            while (_queue.TryPeek(out var head) && head.PresentationMs <= now) {
                if (!_queue.TryDequeue(out head)) break;
                if (dueFrame != null) {
                    dueFrame.Release();
                    Interlocked.Increment(ref _framesDroppedLate);
                    Interlocked.Increment(ref _fpsWindowDroppedLate);
                }
                dueFrame = head;
            }
            if (dueFrame == null) return;

            try {
                Present(dueFrame);
                Interlocked.Increment(ref _framesPresented);
                Interlocked.Increment(ref _fpsWindowPresented);
            } finally {
                dueFrame.Release();
            }

            MaybeLogFps();
        }

        private void Present(VideoFrameSample frame) {
            // (Re)allocate the D3D9 surface on first frame or resolution
            // change. Surface uses D3DFMT_X8R8G8B8 which is BGRA-laid-out
            // in memory little-endian (matches the BGRA32 the decoder
            // produces via sws_scale's AV_PIX_FMT_BGRA target).
            if (_surface == IntPtr.Zero || _surfaceW != frame.Width || _surfaceH != frame.Height) {
                ReleaseSurface();
                int hr = D3D9Interop.CreateRenderTarget(_device,
                    (uint)frame.Width, (uint)frame.Height,
                    D3D9Interop.D3DFMT_X8R8G8B8,
                    D3D9Interop.D3DMULTISAMPLE_NONE, 0,
                    lockable: true, out _surface);
                if (D3D9Interop.Failed(hr) || _surface == IntPtr.Zero) {
                    _log?.LogError($"[d3d-video] CreateRenderTarget {frame.Width}x{frame.Height} failed: 0x{hr:X8}");
                    return;
                }
                _surfaceW = frame.Width;
                _surfaceH = frame.Height;
                _backBufferAssigned = false;
                _log?.LogInfo($"[d3d-video] D3D9 surface allocated: {frame.Width}x{frame.Height}");
            }

            // Copy decoded BGRA bytes into the D3D9 surface. Single
            // memcpy at the surface stride (which may differ from
            // frame.Stride — D3D9 surfaces are often padded for
            // alignment, hence the per-row copy).
            int hr2 = D3D9Interop.LockRect(_surface, out var locked, D3D9Interop.D3DLOCK_DISCARD);
            if (D3D9Interop.Failed(hr2)) {
                _log?.LogError($"[d3d-video] LockRect failed: 0x{hr2:X8}");
                return;
            }
            try {
                int rowBytes = frame.Width * 4;
                if (locked.Pitch == frame.Stride) {
                    // Single contiguous copy — fastest case (common for
                    // power-of-two widths and most modern GPUs).
                    Marshal.Copy(frame.Bgra32, 0, locked.pBits, frame.Stride * frame.Height);
                } else {
                    // Per-row copy when surface stride differs (D3D9 may
                    // pad rows for alignment). Negligible overhead vs
                    // contiguous copy at 1080p.
                    for (int y = 0; y < frame.Height; y++) {
                        IntPtr dstRow = IntPtr.Add(locked.pBits, y * locked.Pitch);
                        Marshal.Copy(frame.Bgra32, y * frame.Stride, dstRow, rowBytes);
                    }
                }
            } finally {
                D3D9Interop.UnlockRect(_surface);
            }

            // Tell D3DImage which rectangle of the surface changed. Bind
            // the surface as backbuffer the first time.
            _d3dImage.Lock();
            try {
                if (!_backBufferAssigned) {
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface);
                    _backBufferAssigned = true;
                }
                _d3dImage.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
            } finally {
                _d3dImage.Unlock();
            }
        }

        private void MaybeLogFps() {
            long wallMs = _fpsSw.ElapsedMilliseconds;
            long sinceStart = wallMs - _fpsWindowStartMs;
            if (sinceStart < 1000) return;

            long presented = Interlocked.Exchange(ref _fpsWindowPresented, 0);
            long droppedLate = Interlocked.Exchange(ref _fpsWindowDroppedLate, 0);
            long droppedOverflow = Interlocked.Exchange(ref _fpsWindowDroppedOverflow, 0);
            _fpsWindowStartMs = wallMs;

            double fps = presented * 1000.0 / sinceStart;
            _log?.LogInfo($"[d3d-video] fps={fps:F1} " +
                          $"presented={presented} droppedLate={droppedLate} " +
                          $"droppedOverflow={droppedOverflow} window={sinceStart}ms " +
                          $"queueDepth={_queue.Count}");
        }

        // ---- Teardown ----

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try {
                _target.Dispatcher.Invoke(() => {
                    if (_renderingHandler != null) {
                        CompositionTarget.Rendering -= _renderingHandler;
                        _renderingHandler = null;
                    }
                    if (_d3dImage != null) {
                        try {
                            _d3dImage.Lock();
                            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                            _d3dImage.Unlock();
                        } catch { }
                        _d3dImage = null;
                    }
                    _target.Source = null;
                });
            } catch { }
            while (_queue.TryDequeue(out var leftover)) leftover?.Release();
            BufferPool.Clear();
            ReleaseSurface();
            if (_device != IntPtr.Zero) {
                D3D9Interop.Release(_device);
                _device = IntPtr.Zero;
            }
            if (_d3d9Ex != IntPtr.Zero) {
                D3D9Interop.Release(_d3d9Ex);
                _d3d9Ex = IntPtr.Zero;
            }
            _log?.LogInfo($"[d3d-video] disposed: presented={_framesPresented} " +
                          $"droppedLate={_framesDroppedLate} droppedOverflow={_framesDroppedOverflow}");
        }

        private void ReleaseSurface() {
            if (_surface != IntPtr.Zero) {
                D3D9Interop.Release(_surface);
                _surface = IntPtr.Zero;
                _backBufferAssigned = false;
            }
        }
    }
}
