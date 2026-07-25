using SharpDX.Direct3D9;
using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// GPU video presenter for the Video FPS Lab. Backs a WPF
    /// <see cref="System.Windows.Interop.D3DImage"/> with a Direct3D9Ex
    /// lockable render-target surface (via SharpDX). Decoded BGRA frames are
    /// uploaded into the D3D9 surface and WPF composites that surface on the
    /// GPU — no per-frame WPF <c>WriteableBitmap</c> upload, and scaling
    /// happens on the GPU. This is the "Direct3D image" path we want to
    /// FPS-test against FFME.
    ///
    /// <para>Threading: constructed on the WPF UI thread (owns the
    /// <see cref="D3DImage"/>). <see cref="SubmitFrame"/> is callable from any
    /// thread — it copies into a staging buffer and coalesces a single
    /// UI-thread present, mirroring the RDP paint coalescer.</para>
    /// </summary>
    internal sealed class D3DImagePresenter : IDisposable {

        private readonly Dispatcher _dispatcher;
        private readonly Logger _log;

        private Direct3DEx _d3d;
        private DeviceEx _device;
        private Surface _surface;

        public D3DImage Image { get; }

        // Staging buffer (BGRA, stride = width*4). Filled by SubmitFrame,
        // drained by DoPresent. Guarded by _gate.
        private readonly object _gate = new object();
        private byte[] _staging;
        private int _w, _h, _stride;
        private bool _frameDirty;
        private bool _presentPending;
        private bool _disposed;

        private long _framesPresented;
        public long FramesPresented => System.Threading.Interlocked.Read(ref _framesPresented);

        /// <summary>Frames handed to <see cref="SubmitFrame"/> (the pacer's
        /// release count). <c>FramesSubmitted - FramesPresented</c> is the
        /// present-layer coalescing backlog — how far on-screen video lags the
        /// frames the pacer already released. Diagnostic (av-timing).</summary>
        public long FramesSubmitted => System.Threading.Interlocked.Read(ref _submittedCount);

        // Present-rate diagnostic: SubmitFrame is called at the pacer's release
        // rate (~25/s); DoPresent runs on the UI thread and COALESCES (a frame
        // submitted while a present is still pending is skipped). If the UI/render
        // thread can't keep up, presented << submitted → video judders/slows even
        // though decode+pacer look perfect. Logged once/sec from SubmitFrame so it
        // reports regardless of how far the present layer falls behind.
        private long _submittedCount;
        private int _presentStatTick;
        private long _presentStatSubmitted;
        private long _presentStatPresented;

        public D3DImagePresenter(Dispatcher dispatcher, IntPtr hwnd, Logger log) {
            _dispatcher = dispatcher;
            _log = log;
            Image = new D3DImage();

            try {
                _d3d = new Direct3DEx();
                var pp = new PresentParameters {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    DeviceWindowHandle = hwnd,
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    BackBufferFormat = Format.A8R8G8B8,
                    PresentationInterval = PresentInterval.Immediate,
                };
                _device = new DeviceEx(_d3d, 0, DeviceType.Hardware, hwnd,
                    CreateFlags.HardwareVertexProcessing
                    | CreateFlags.Multithreaded
                    | CreateFlags.FpuPreserve,
                    pp);
                _log?.LogInfo("[d3dimg] Direct3D9Ex device created");
            } catch (Exception ex) {
                _log?.LogError($"[d3dimg] device creation failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>Upload one BGRA frame. <paramref name="src"/> points at
        /// <paramref name="srcStride"/>×<paramref name="h"/> bytes of BGRA.
        /// Safe from any thread.</summary>
        public void SubmitFrame(IntPtr src, int srcStride, int w, int h) {
            if (_disposed || src == IntPtr.Zero || w <= 0 || h <= 0) return;

            System.Threading.Interlocked.Increment(ref _submittedCount);
            MaybePresentStats();

            bool schedule;
            lock (_gate) {
                int stride = w * 4;
                if (_staging == null || _w != w || _h != h) {
                    _staging = new byte[stride * h];
                    _w = w; _h = h; _stride = stride;
                }
                // Copy src → staging (row-aware in case srcStride is padded).
                if (srcStride == stride) {
                    Marshal.Copy(src, _staging, 0, stride * h);
                } else {
                    for (int y = 0; y < h; y++) {
                        Marshal.Copy(IntPtr.Add(src, y * srcStride),
                                     _staging, y * stride, stride);
                    }
                }
                _frameDirty = true;
                schedule = !_presentPending;
                if (schedule) _presentPending = true;
            }

            if (schedule) {
                try {
                    _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DoPresent));
                } catch {
                    lock (_gate) { _presentPending = false; }
                }
            }
        }

        private void MaybePresentStats() {
            int now = System.Environment.TickCount;
            if (_presentStatTick == 0) {
                _presentStatTick = now;
                _presentStatSubmitted = System.Threading.Interlocked.Read(ref _submittedCount);
                _presentStatPresented = System.Threading.Interlocked.Read(ref _framesPresented);
                return;
            }
            int elapsed = unchecked(now - _presentStatTick);
            if (elapsed < 1000) return;
            _presentStatTick = now;
            long sub = System.Threading.Interlocked.Read(ref _submittedCount);
            long pres = System.Threading.Interlocked.Read(ref _framesPresented);
            long dSub = sub - _presentStatSubmitted;
            long dPres = pres - _presentStatPresented;
            _presentStatSubmitted = sub;
            _presentStatPresented = pres;
            double presFps = dPres * 1000.0 / elapsed;
            _log?.LogInfo($"[d3dimg] present: submitted={dSub} presented={dPres} " +
                          $"coalesced={dSub - dPres} → {presFps:F1}fps on screen (window={elapsed}ms)");
        }

        private void DoPresent() {
            if (_disposed) return;
            try {
                // Ensure the surface matches the current frame size.
                int w, h, stride;
                lock (_gate) {
                    _presentPending = false;
                    if (!_frameDirty || _staging == null) return;
                    w = _w; h = _h; stride = _stride;
                }

                if (_surface == null || _surfaceW != w || _surfaceH != h) {
                    RecreateSurface(w, h);
                    if (_surface == null) return;
                }

                // WPF D3DImage contract: the back-buffer surface may be modified
                // ONLY while the D3DImage is locked (Lock…AddDirtyRect…Unlock).
                // Writing it outside that bracket (as this did previously) races
                // the compositor's read of the surface and can tear, so the
                // surface copy AND the dirty-rect are now issued inside one lock.
                if (!Image.IsFrontBufferAvailable) return; // can't present; retry on next submit (_frameDirty stays set)
                Image.Lock();
                try {
                    var dr = _surface.LockRectangle(LockFlags.None);
                    try {
                        lock (_gate) {
                            if (!_frameDirty || _staging == null) return; // blanked/raced away
                            if (dr.Pitch == stride) {
                                Marshal.Copy(_staging, 0, dr.DataPointer, stride * h);
                            } else {
                                for (int y = 0; y < h; y++) {
                                    Marshal.Copy(_staging, y * stride,
                                                 IntPtr.Add(dr.DataPointer, y * dr.Pitch),
                                                 stride);
                                }
                            }
                            _frameDirty = false;
                        }
                    } finally {
                        _surface.UnlockRectangle();
                    }
                    Image.AddDirtyRect(new Int32Rect(0, 0, w, h));
                    System.Threading.Interlocked.Increment(ref _framesPresented);
                } finally {
                    Image.Unlock();
                }
            } catch (Exception ex) {
                _log?.LogError($"[d3dimg] present failed: {ex.Message}");
            }
        }

        private int _surfaceW, _surfaceH;

        private void RecreateSurface(int w, int h) {
            try {
                Image.Lock();
                try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
                finally { Image.Unlock(); }

                _surface?.Dispose();
                _surface = Surface.CreateRenderTarget(
                    _device, w, h, Format.A8R8G8B8,
                    MultisampleType.None, 0, /*lockable*/ true);
                _surfaceW = w; _surfaceH = h;

                Image.Lock();
                try {
                    Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.NativePointer);
                } finally {
                    Image.Unlock();
                }
                _log?.LogInfo($"[d3dimg] render-target surface created {w}x{h}");
            } catch (Exception ex) {
                _log?.LogError($"[d3dimg] surface creation failed ({w}x{h}): {ex.Message}");
                _surface = null;
            }
        }

        /// <summary>
        /// Paint the presenter surface opaque black, discarding the last
        /// decoded frame. Called when media closes so a stale frame doesn't
        /// linger behind the WMC menu (the presenter is reused across media —
        /// it isn't disposed until the whole session ends). Safe from any
        /// thread; marshals to the UI thread. No-op if no surface exists yet
        /// (nothing has been presented), or after Dispose.
        /// </summary>
        public void Blank() {
            if (_disposed) return;
            if (_dispatcher.CheckAccess()) DoBlank();
            else {
                try { _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DoBlank)); }
                catch { /* dispatcher shutting down — nothing to blank */ }
            }
        }

        private void DoBlank() {
            if (_disposed || _surface == null) return;
            try {
                // Drop any pending/queued frame so a late DoPresent can't repaint
                // the old image after we've blanked.
                lock (_gate) { _frameDirty = false; _staging = null; _w = _h = _stride = 0; }

                // Same D3DImage contract as DoPresent: write the surface only
                // inside Image.Lock…Unlock so the compositor never reads a
                // half-cleared surface.
                if (!Image.IsFrontBufferAvailable) return;
                Image.Lock();
                try {
                    var dr = _surface.LockRectangle(LockFlags.None);
                    try {
                        // A8R8G8B8 opaque black: B=G=R=0, A=0xFF. Alpha is the 4th
                        // byte of each BGRA pixel. Build one row (full pitch) and
                        // copy it to every scanline.
                        int pitch = dr.Pitch;
                        byte[] row = new byte[pitch];
                        for (int i = 3; i < pitch; i += 4) row[i] = 0xFF;
                        for (int y = 0; y < _surfaceH; y++) {
                            Marshal.Copy(row, 0, IntPtr.Add(dr.DataPointer, y * pitch), pitch);
                        }
                    } finally {
                        _surface.UnlockRectangle();
                    }
                    Image.AddDirtyRect(new Int32Rect(0, 0, _surfaceW, _surfaceH));
                } finally {
                    Image.Unlock();
                }
                _log?.LogInfo("[d3dimg] surface blanked to black (media closed)");
            } catch (Exception ex) {
                _log?.LogError($"[d3dimg] blank failed: {ex.Message}");
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try {
                if (Image.Dispatcher.CheckAccess()) ClearBackBuffer();
                else Image.Dispatcher.Invoke(ClearBackBuffer);
            } catch { }
            try { _surface?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            try { _d3d?.Dispose(); } catch { }
            _surface = null; _device = null; _d3d = null;
            _log?.LogInfo("[d3dimg] disposed");
        }

        private void ClearBackBuffer() {
            Image.Lock();
            try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero); }
            finally { Image.Unlock(); }
        }
    }
}
