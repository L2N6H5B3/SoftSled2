using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoftSled.Components.AudioVisual.Utilities {

    /// <summary>
    /// Software video presenter — the fallback for when 3D acceleration is
    /// disabled (or the Direct3D9Ex device can't be created). Backs the video
    /// plane with a plain WPF <see cref="WriteableBitmap"/> (Bgra32). Decoded
    /// BGRA frames are copied into the bitmap's back-buffer and WPF composites
    /// it on the software/render layer — no GPU D3D surface, no SharpDX.
    ///
    /// <para>Drop-in peer of <see cref="D3DImagePresenter"/>: identical
    /// staging-buffer + coalesced-UI-present model, same <see cref="SubmitFrame"/>
    /// contract. The one difference the interface exposes is
    /// <see cref="ImageChanged"/>: a <c>WriteableBitmap</c> cannot resize in
    /// place, so a resolution change allocates a fresh bitmap and raises the
    /// event, letting the session re-bind <c>VideoImage.Source</c>.</para>
    ///
    /// <para>Threading: constructed on the WPF UI thread.
    /// <see cref="SubmitFrame"/> is callable from any thread — it copies into a
    /// staging buffer and coalesces a single UI-thread present, mirroring
    /// <see cref="D3DImagePresenter"/> and the RDP paint coalescer. The
    /// <see cref="WriteableBitmap"/> itself is only ever created and touched on
    /// the UI thread (inside <see cref="DoPresent"/>/<see cref="DoBlank"/>).</para>
    /// </summary>
    internal sealed class WriteableBitmapPresenter : IVideoPresenter {

        private readonly Dispatcher _dispatcher;
        private readonly Logger _log;

        // The live bitmap. Created/replaced on the UI thread only; read as
        // ImageSource from the UI thread when binding the plane.
        private WriteableBitmap _bitmap;
        public ImageSource Image => _bitmap;

        public event Action ImageChanged;

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

        private long _submittedCount;
        public long FramesSubmitted => System.Threading.Interlocked.Read(ref _submittedCount);

        // Present-rate diagnostic (see D3DImagePresenter for the rationale).
        private int _presentStatTick;
        private long _presentStatSubmitted;
        private long _presentStatPresented;

        public WriteableBitmapPresenter(Dispatcher dispatcher, Logger log) {
            _dispatcher = dispatcher;
            _log = log;
            _log?.LogInfo("[wbimg] software presenter created (WriteableBitmap, no 3D acceleration)");
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
            _log?.LogInfo($"[wbimg] present: submitted={dSub} presented={dPres} " +
                          $"coalesced={dSub - dPres} → {presFps:F1}fps on screen (window={elapsed}ms)");
        }

        private void DoPresent() {
            if (_disposed) return;
            try {
                int w, h, stride;
                lock (_gate) {
                    _presentPending = false;
                    if (!_frameDirty || _staging == null) return;
                    w = _w; h = _h; stride = _stride;
                }

                bool created = false;
                if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h) {
                    // WriteableBitmap can't resize in place — allocate a fresh one
                    // and let the session re-bind VideoImage.Source via ImageChanged.
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    created = true;
                    _log?.LogInfo($"[wbimg] bitmap created {w}x{h}");
                }
                if (created) {
                    try { ImageChanged?.Invoke(); }
                    catch (Exception ex) { _log?.LogError($"[wbimg] ImageChanged handler threw: {ex.Message}"); }
                }

                _bitmap.Lock();
                try {
                    int pitch = _bitmap.BackBufferStride;
                    IntPtr back = _bitmap.BackBuffer;
                    lock (_gate) {
                        if (!_frameDirty || _staging == null) return; // blanked/raced away
                        if (pitch == stride) {
                            Marshal.Copy(_staging, 0, back, stride * h);
                        } else {
                            for (int y = 0; y < h; y++) {
                                Marshal.Copy(_staging, y * stride,
                                             IntPtr.Add(back, y * pitch), stride);
                            }
                        }
                        _frameDirty = false;
                    }
                    _bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
                    System.Threading.Interlocked.Increment(ref _framesPresented);
                } finally {
                    _bitmap.Unlock();
                }
            } catch (Exception ex) {
                _log?.LogError($"[wbimg] present failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Paint the bitmap opaque black, discarding the last decoded frame.
        /// Called when media closes so a stale frame doesn't linger behind the
        /// WMC menu (the presenter is reused across media). Safe from any
        /// thread; marshals to the UI thread. No-op if no bitmap exists yet.
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
            if (_disposed || _bitmap == null) return;
            try {
                // Drop any pending/queued frame so a late DoPresent can't repaint
                // the old image after we've blanked.
                lock (_gate) { _frameDirty = false; _staging = null; _w = _h = _stride = 0; }

                _bitmap.Lock();
                try {
                    int pitch = _bitmap.BackBufferStride;
                    IntPtr back = _bitmap.BackBuffer;
                    // Bgra32 opaque black: B=G=R=0, A=0xFF (4th byte of each pixel).
                    // Build one row (full pitch) and copy it to every scanline.
                    byte[] row = new byte[pitch];
                    for (int i = 3; i < pitch; i += 4) row[i] = 0xFF;
                    for (int y = 0; y < _bitmap.PixelHeight; y++) {
                        Marshal.Copy(row, 0, IntPtr.Add(back, y * pitch), pitch);
                    }
                    _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
                } finally {
                    _bitmap.Unlock();
                }
                _log?.LogInfo("[wbimg] bitmap blanked to black (media closed)");
            } catch (Exception ex) {
                _log?.LogError($"[wbimg] blank failed: {ex.Message}");
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            // WriteableBitmap is managed and thread-affine; just drop the
            // reference (the session clears VideoImage.Source separately).
            _bitmap = null;
            lock (_gate) { _staging = null; }
            _log?.LogInfo("[wbimg] disposed");
        }
    }
}
