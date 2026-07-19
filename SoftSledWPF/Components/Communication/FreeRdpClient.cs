using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoftSled.Components.Communication {

    /// <summary>
    /// Args for the connection-state change event.
    /// </summary>
    public class StateChangedEventArgs : EventArgs {
        public SoftSledNative.State State { get; }
        public int Detail { get; }
        public StateChangedEventArgs(SoftSledNative.State s, int d) { State = s; Detail = d; }
    }

    /// <summary>
    /// In-process replacement for <c>RDPVCInterface</c> — fronts FreeRDP via the
    /// softsled-rdp.dll shim. Same <see cref="DataReceived"/> event signature
    /// and same <see cref="SendOnVirtualChannel"/> API, so existing channel
    /// handlers (<c>VirtualChannelMcxSessHandler</c>, <c>VirtualChannelDevCapsHandler</c>,
    /// etc.) plug in unchanged. Replaces the named-pipe transport that
    /// previously bridged to RDPVCManager.dll inside mstscax.
    /// </summary>
    public class FreeRdpClient : IDisposable {

        private IntPtr _handle;

        // Delegates MUST be held in fields so the GC doesn't collect them while
        // native code holds the function pointer. This is the canonical bug for
        // any P/Invoke wrapper that takes callbacks.
        private readonly SoftSledNative.StateCallback _stateCb;
        private readonly SoftSledNative.ChannelCallback _channelCb;
        private readonly SoftSledNative.PaintCallback _paintCb;
        private readonly SoftSledNative.PaintRectsCallback _paintRectsCb;
        // Held alive only while a consumer has registered one. Native code
        // would reach into a collected delegate thunk and crash without this.
        private SoftSledNative.FastpathCallback _fastpathCb;

        // Framebuffer state. The native pointer in _fbInfo.Pixels is owned by
        // FreeRDP and remains valid only between PostConnect and Disconnect.
        private readonly Dispatcher _uiDispatcher;
        private SoftSledNative.FramebufferInfo _fbInfo;
        private WriteableBitmap _bitmap;

        // Paint coalescer: worker-thread paint callbacks accumulate the frame's
        // dirty rects into _pendingRects under _paintLock. A single BeginInvoke
        // drains them on the UI thread, which blits each rect separately. Keeping
        // the rects discrete (rather than unioning them into one bounding box)
        // matters because a WMC frame often touches a few far-apart regions
        // (clock, menu, focus highlight) whose bounding box can be the whole
        // screen — blitting the box would CPU-copy and GPU-resample a full frame
        // of unchanged pixels. Multiple frames that arrive while the UI thread is
        // busy simply keep appending, so the queue depth stays capped at one
        // dispatch. If a burst pushes the rect count past _maxPendingRects we
        // collapse to a single bounding box (bounded worst case). The two lists
        // are swapped on drain so the worker never blocks on the UI blit and no
        // per-frame list allocation occurs. _flushAction is cached to avoid
        // per-paint closure allocation.
        private struct DirtyRect {
            public int X, Y, W, H;
            public DirtyRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        }
        private const int _maxPendingRects = 64;
        private readonly object _paintLock = new object();
        private bool _paintDispatchPending;
        private bool _pendingCoalesced;   // true once we've collapsed to slot 0
        private System.Collections.Generic.List<DirtyRect> _pendingRects =
            new System.Collections.Generic.List<DirtyRect>(_maxPendingRects);
        private System.Collections.Generic.List<DirtyRect> _drainRects =
            new System.Collections.Generic.List<DirtyRect>(_maxPendingRects);
        private readonly Action _flushAction;

        public event EventHandler<DataReceived> DataReceived;
        public event EventHandler<StateChangedEventArgs> StateChanged;

        /// <summary>
        /// WPF-friendly software framebuffer for RDP video. Set when the
        /// session reaches ACTIVE; null otherwise. Bind via code-behind
        /// (e.g. <c>image.Source = client.Bitmap</c>) on the FrameReady or
        /// StateChanged-Active event.
        /// </summary>
        public WriteableBitmap Bitmap => _bitmap;

        /// <summary>Read the alpha byte of the dead-centre framebuffer pixel
        /// (BGRA32; alpha = 4th byte). Used by Media Playback Mode to tell whether
        /// WMC UI covers the centre (alpha≠0) or the video is showing through it
        /// (alpha==0). Returns false if the framebuffer isn't ready. Best-effort:
        /// the caller only invokes this during an ACTIVE session, so _fbInfo.Pixels
        /// is valid; a torn read of one pixel is harmless for this heuristic.</summary>
        public bool TryGetCenterPixelAlpha(out byte alpha) {
            alpha = 0;
            SoftSledNative.FramebufferInfo fb = _fbInfo;   // struct snapshot
            if (fb.Pixels == IntPtr.Zero || fb.Width == 0 || fb.Height == 0 || fb.Stride == 0)
                return false;
            try {
                long x = fb.Width / 2, y = fb.Height / 2;
                long off = y * (long)fb.Stride + x * 4 + 3;   // +3 = alpha in BGRA
                alpha = System.Runtime.InteropServices.Marshal.ReadByte(fb.Pixels, (int)off);
                return true;
            } catch { return false; }
        }

        /// <summary>Raised once on the UI thread when the framebuffer is ready (ACTIVE + bitmap allocated).</summary>
        public event EventHandler FrameReady;

        public FreeRdpClient() {
            _handle = SoftSledNative.softsled_client_new();
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("softsled_client_new returned null");

            // Capture the dispatcher of whatever thread constructed us. In
            // SoftSledWPF that's the UI thread (MainWindow.xaml.cs). Native
            // paint callbacks fire on the FreeRDP worker thread; we marshal
            // back here to touch the WriteableBitmap.
            _uiDispatcher = Application.Current?.Dispatcher
                            ?? Dispatcher.CurrentDispatcher;

            _stateCb      = OnNativeStateChanged;
            _channelCb    = OnNativeChannelData;
            _paintCb      = OnNativePaint;
            _paintRectsCb = OnNativePaintRects;
            _flushAction  = FlushPendingPaint;
            SoftSledNative.softsled_set_state_callback  (_handle, _stateCb,   IntPtr.Zero);
            SoftSledNative.softsled_set_channel_callback(_handle, _channelCb, IntPtr.Zero);
        }

        /// <summary>Toggle FreeRDP's client-side graphics decoder. Default ON. Disable for MCX
        /// where unhandled drawing orders would otherwise tear the session down.</summary>
        public void SetDecodeEnabled(bool enabled) {
            Check(nameof(SoftSledNative.softsled_set_decode_enabled),
                  SoftSledNative.softsled_set_decode_enabled(_handle, enabled ? 1 : 0));
        }

        /// <summary>Suggest the initial desktop size to negotiate. 0,0 = use FreeRDP defaults.</summary>
        public void SetInitialDesktopSize(uint width, uint height) {
            Check(nameof(SoftSledNative.softsled_set_initial_desktop_size),
                  SoftSledNative.softsled_set_initial_desktop_size(_handle, width, height));
        }

        /// <summary>Apply server, credentials, security, and cert-validation settings.</summary>
        public void Configure(string host, ushort port, string user, string password,
                              bool rdpOnlySecurity = true, bool ignoreCertificate = true) {
            Check(nameof(SoftSledNative.softsled_set_server),
                  SoftSledNative.softsled_set_server(_handle, host, port));
            Check(nameof(SoftSledNative.softsled_set_credentials),
                  SoftSledNative.softsled_set_credentials(_handle, user, password));
            if (rdpOnlySecurity)
                Check(nameof(SoftSledNative.softsled_set_security_rdp_only),
                      SoftSledNative.softsled_set_security_rdp_only(_handle));
            if (ignoreCertificate)
                Check(nameof(SoftSledNative.softsled_set_cert_ignore),
                      SoftSledNative.softsled_set_cert_ignore(_handle));
        }

        /// <summary>Register a static virtual channel by name. Up to 31 channels.</summary>
        public void RegisterChannel(string name) {
            Check(nameof(SoftSledNative.softsled_register_channel),
                  SoftSledNative.softsled_register_channel(_handle, name));
        }

        /// <summary>Begin connecting in a background thread. Non-blocking.</summary>
        public void Connect() {
            Check(nameof(SoftSledNative.softsled_connect),
                  SoftSledNative.softsled_connect(_handle));
        }

        /// <summary>Request graceful disconnect. Non-blocking.</summary>
        public void Disconnect() {
            // Tear-down ordering matters: stop new paints from being dispatched,
            // drain anything already queued on the UI dispatcher (so no Render-
            // priority callback runs after FreeRDP frees the framebuffer), then
            // tell the native side to disconnect.
            try {
                SoftSledNative.softsled_set_paint_callback(_handle, null, IntPtr.Zero);
                SoftSledNative.softsled_set_paint_rects_callback(_handle, null, IntPtr.Zero);
                SoftSledNative.softsled_set_fastpath_callback(_handle, null, IntPtr.Zero);
                if (_uiDispatcher != null && !_uiDispatcher.HasShutdownStarted)
                    _uiDispatcher.Invoke(() => { /* drain */ });
            } catch { /* keep going — must not skip the native disconnect */ }
            Check(nameof(SoftSledNative.softsled_disconnect),
                  SoftSledNative.softsled_disconnect(_handle));
            _fastpathCb = null;
        }

        /// <summary>Send a single RDP keyboard scan-code event. Safe to call from any
        /// thread once the session is ACTIVE. <paramref name="scancode"/> is a Set-1
        /// scan-code (low byte). Set <paramref name="extended"/> for keys that send
        /// the 0xE0 prefix (arrows, navigation cluster, RCtrl/RAlt, numpad div/enter).
        /// Set <paramref name="release"/> for key-up; default is key-down.</summary>
        public bool SendKey(byte scancode, bool extended = false, bool release = false) {
            ushort flags = 0;
            if (extended) flags |= SoftSledNative.KBD_FLAGS_EXTENDED;
            if (release)  flags |= SoftSledNative.KBD_FLAGS_RELEASE;
            return SoftSledNative.softsled_send_keyboard_event(_handle, flags, scancode)
                   == (int)SoftSledNative.Result.Ok;
        }

        public bool SendKeyDown(byte scancode, bool extended = false) =>
            SendKey(scancode, extended, release: false);

        public bool SendKeyUp(byte scancode, bool extended = false) =>
            SendKey(scancode, extended, release: true);

        /// <summary>Send a single RDP mouse event. <paramref name="x"/>/<paramref name="y"/>
        /// are framebuffer-pixel coordinates. <paramref name="flags"/> is a bitmask of
        /// <c>SoftSledNative.PTR_FLAGS_*</c> (MOVE / DOWN / BUTTON1-3 / WHEEL /
        /// WHEEL_NEGATIVE plus low-9-bit wheel magnitude). The call returns
        /// immediately — the shim's async input thread handles the actual FreeRDP
        /// send, so this never blocks the WPF UI thread on RDP transport latency.</summary>
        public bool SendMouse(ushort flags, ushort x, ushort y) {
            if (_handle == IntPtr.Zero) return false;
            return SoftSledNative.softsled_send_mouse_event(_handle, flags, x, y)
                   == (int)SoftSledNative.Result.Ok;
        }

        /// <summary>Send an extended mouse event (X1/X2 side buttons).
        /// <paramref name="flags"/> uses the <c>PTR_XFLAGS_*</c> set, not <c>PTR_FLAGS_*</c>.</summary>
        public bool SendExtendedMouse(ushort flags, ushort x, ushort y) {
            if (_handle == IntPtr.Zero) return false;
            return SoftSledNative.softsled_send_extended_mouse_event(_handle, flags, x, y)
                   == (int)SoftSledNative.Result.Ok;
        }

        /// <summary>
        /// Register a callback for fast-path updates of unknown types (e.g. WMC's
        /// MCX-specific 0x0D audio payload). Pass <c>null</c> to unregister.
        /// The callback fires on the FreeRDP worker thread; the bytes pointer is
        /// only valid for the duration of the call. May be called any time.
        /// </summary>
        public void SetFastpathCallback(SoftSledNative.FastpathCallback cb) {
            _fastpathCb = cb; // hold strong ref so GC doesn't collect the thunk
            SoftSledNative.softsled_set_fastpath_callback(_handle, cb, IntPtr.Zero);
        }

        /// <summary>Drop-in replacement for <c>RDPVCInterface.SendOnVirtualChannel</c>.</summary>
        public bool SendOnVirtualChannel(string channelName, byte[] data) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int rc = SoftSledNative.softsled_send_channel_data(
                _handle, channelName, data, (UIntPtr)data.Length);
            return rc == (int)SoftSledNative.Result.Ok;
        }

        // ---- native callback thunks ----

        private void OnNativeStateChanged(IntPtr user, SoftSledNative.State state, int detail) {
            try {
                if (state == SoftSledNative.State.Active)
                    InitFramebufferOnUiThread();
                StateChanged?.Invoke(this, new StateChangedEventArgs(state, detail));
            } catch {
                /* swallow — never let a managed exception cross back into FreeRDP */
            }
        }

        // Called on FreeRDP worker thread when ACTIVE fires. Hops to the UI
        // thread to allocate the bitmap, then registers the paint callback so
        // EndPaint dispatches start flowing into the bitmap.
        private void InitFramebufferOnUiThread() {
            if (_uiDispatcher == null) return;

            Action init = () => {
                int rc = SoftSledNative.softsled_get_framebuffer_info(_handle, out _fbInfo);
                if (rc != (int)SoftSledNative.Result.Ok || _fbInfo.Pixels == IntPtr.Zero) {
                    // decoding disabled or framebuffer not yet ready — stay headless.
                    return;
                }
                _bitmap = new WriteableBitmap(
                    (int)_fbInfo.Width, (int)_fbInfo.Height,
                    96, 96, PixelFormats.Bgra32, null);
                // Prime the bitmap with the current framebuffer contents.
                _bitmap.Lock();
                _bitmap.WritePixels(
                    new Int32Rect(0, 0, (int)_fbInfo.Width, (int)_fbInfo.Height),
                    _fbInfo.Pixels,
                    (int)(_fbInfo.Stride * _fbInfo.Height),
                    (int)_fbInfo.Stride);
                _bitmap.Unlock();

                // Now safe to start receiving per-frame dirty-rect callbacks.
                // Prefer the multi-rect path — once GFX/H.264 is on, frames carry
                // many small surface-tile rects whose union over-blits a lot of
                // unchanged pixels. The shim treats the rects callback as taking
                // precedence over the single-rect one when both are set, so we
                // wire both: rects for normal operation, single-rect as a safety
                // net if the rects path ever gets unregistered.
                SoftSledNative.softsled_set_paint_callback      (_handle, _paintCb,      IntPtr.Zero);
                SoftSledNative.softsled_set_paint_rects_callback(_handle, _paintRectsCb, IntPtr.Zero);
                FrameReady?.Invoke(this, EventArgs.Empty);
            };
            // Synchronous: we want the bitmap allocated before any paint
            // callback can race in. softsled_set_paint_callback is the gate.
            _uiDispatcher.Invoke(init);
        }

        // Single-rect paint callback — only fires if the shim couldn't route
        // through the multi-rect path (e.g. allocation failure). Both paths
        // funnel into the same coalescer.
        private void OnNativePaint(IntPtr user, int x, int y, int w, int h) {
            QueueRect(x, y, w, h);
        }

        // Multi-rect paint callback. <paramref name="rects"/> points at shim-owned
        // memory holding <paramref name="count"/> SoftSledNative.Rect structs;
        // valid only for the duration of the call. We read each one and queue it
        // as a discrete dirty rect (QueueRect keeps them separate for the blit).
        private void OnNativePaintRects(IntPtr user, IntPtr rects, uint count) {
            if (rects == IntPtr.Zero || count == 0) return;
            int sz = Marshal.SizeOf(typeof(SoftSledNative.Rect));
            for (uint i = 0; i < count; i++) {
                var r = (SoftSledNative.Rect)Marshal.PtrToStructure(
                    new IntPtr(rects.ToInt64() + i * sz), typeof(SoftSledNative.Rect));
                QueueRect(r.X, r.Y, r.W, r.H);
            }
        }

        // Worker-thread side of the coalescer. Appends the (clipped) rect to the
        // pending list; if no dispatch is currently in flight, posts a single
        // BeginInvoke at Render priority. The dispatcher closure (FlushPendingPaint)
        // drains the list, then the next worker frame is free to re-post. Past
        // _maxPendingRects the list is collapsed to a single bounding box so a
        // pathological burst can't grow the list (or the per-rect blit loop)
        // without bound.
        private void QueueRect(int x, int y, int w, int h) {
            if (w <= 0 || h <= 0) return;
            SoftSledNative.FramebufferInfo fb = _fbInfo;
            if (fb.Pixels == IntPtr.Zero) return;

            // Clip into framebuffer bounds.
            int bw = (int)fb.Width, bh = (int)fb.Height;
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > bw) w = bw - x;
            if (y + h > bh) h = bh - y;
            if (w <= 0 || h <= 0) return;

            bool needDispatch;
            lock (_paintLock) {
                if (_pendingCoalesced) {
                    // Already collapsed — keep unioning into the single box (slot 0).
                    UnionIntoPending(0, x, y, w, h);
                } else if (_pendingRects.Count >= _maxPendingRects) {
                    // Too many discrete rects — collapse the backlog to one box.
                    CollapsePendingToBoundingBox();
                    UnionIntoPending(0, x, y, w, h);
                    _pendingCoalesced = true;
                } else {
                    _pendingRects.Add(new DirtyRect(x, y, w, h));
                }
                needDispatch = !_paintDispatchPending;
                if (needDispatch) _paintDispatchPending = true;
            }

            if (needDispatch) {
                try {
                    // Reuse cached _flushAction — no per-frame closure allocation.
                    _uiDispatcher.BeginInvoke(DispatcherPriority.Render, _flushAction);
                } catch {
                    /* dispatcher shutting down — clear pending so we don't spin */
                    lock (_paintLock) {
                        _paintDispatchPending = false;
                        _pendingRects.Clear();
                        _pendingCoalesced = false;
                    }
                }
            }
        }

        // Union (x,y,w,h) into the pending rect at index i. Caller holds _paintLock.
        private void UnionIntoPending(int i, int x, int y, int w, int h) {
            DirtyRect r = _pendingRects[i];
            int x2 = Math.Max(r.X + r.W, x + w);
            int y2 = Math.Max(r.Y + r.H, y + h);
            r.X = Math.Min(r.X, x);
            r.Y = Math.Min(r.Y, y);
            r.W = x2 - r.X;
            r.H = y2 - r.Y;
            _pendingRects[i] = r;
        }

        // Collapse every pending rect into a single bounding box at slot 0.
        // Caller holds _paintLock. No-op when the list is empty.
        private void CollapsePendingToBoundingBox() {
            if (_pendingRects.Count == 0) return;
            DirtyRect acc = _pendingRects[0];
            for (int i = 1; i < _pendingRects.Count; i++) {
                DirtyRect r = _pendingRects[i];
                int x2 = Math.Max(acc.X + acc.W, r.X + r.W);
                int y2 = Math.Max(acc.Y + acc.H, r.Y + r.H);
                acc.X = Math.Min(acc.X, r.X);
                acc.Y = Math.Min(acc.Y, r.Y);
                acc.W = x2 - acc.X;
                acc.H = y2 - acc.Y;
            }
            _pendingRects.Clear();
            _pendingRects.Add(acc);
        }

        // UI-thread drain. Swaps the pending list out for an empty one (under
        // lock) so the worker can keep accumulating the next frame without
        // blocking on the blit, then blits every dirty rect inside a single
        // Lock/Unlock. Each WritePixels copies only that rect's pixels and each
        // AddDirtyRect marks only that region, so both the CPU copy and WPF's
        // GPU upload stay proportional to what actually changed — not to the
        // bounding box of scattered updates.
        private void FlushPendingPaint() {
            System.Collections.Generic.List<DirtyRect> rects;
            lock (_paintLock) {
                _paintDispatchPending = false;
                if (_pendingRects.Count == 0) { _pendingCoalesced = false; return; }
                // Swap: hand the accumulated list to this drain, give the worker
                // the (already-empty) other list to fill.
                var tmp = _drainRects;
                _drainRects = _pendingRects;
                _pendingRects = tmp;
                _pendingRects.Clear();
                _pendingCoalesced = false;
                rects = _drainRects;
            }

            var b = _bitmap;
            var f = _fbInfo;
            if (b == null || f.Pixels == IntPtr.Zero) { rects.Clear(); return; }

            // sourceBufferSize must span the entire region addressed by
            // sourceRect within sourceBuffer. We pass the whole framebuffer
            // base pointer (f.Pixels) and a non-zero sourceRect, so WPF reads
            // (ry + rh) rows × stride from f.Pixels — meaning the buffer size
            // we declare must cover at least the full framebuffer, not just
            // the dirty rows. Anything smaller throws "Buffer not large
            // enough to copy memory".
            int stride = (int)f.Stride;
            int bufSize = stride * (int)f.Height;
            int fbW = (int)f.Width, fbH = (int)f.Height;
            b.Lock();
            try {
                for (int i = 0; i < rects.Count; i++) {
                    DirtyRect r = rects[i];
                    // Rects were clipped at queue time; re-validate against the
                    // current framebuffer as cheap insurance and skip anything
                    // degenerate (belt-and-braces — fb size is stable between
                    // connect and disconnect).
                    if (r.W <= 0 || r.H <= 0) continue;
                    if (r.X < 0 || r.Y < 0 || r.X + r.W > fbW || r.Y + r.H > fbH) continue;
                    var rect = new Int32Rect(r.X, r.Y, r.W, r.H);
                    b.WritePixels(rect, f.Pixels, bufSize, stride, r.X, r.Y);
                    b.AddDirtyRect(rect);
                }
            } finally {
                b.Unlock();
            }
            rects.Clear();
        }

        private void OnNativeChannelData(IntPtr user, IntPtr namePtr, IntPtr dataPtr, UIntPtr length) {
            try {
                string name = Marshal.PtrToStringAnsi(namePtr) ?? "";
                int len = checked((int)(uint)length);
                byte[] copy = new byte[len];
                if (len > 0)
                    Marshal.Copy(dataPtr, copy, 0, len);
                DataReceived?.Invoke(this, new DataReceived(name, copy));
            } catch {
                /* same — exceptions can't safely cross the native boundary */
            }
        }

        private static void Check(string name, int rc) {
            if (rc != (int)SoftSledNative.Result.Ok)
                throw new InvalidOperationException($"{name} returned {rc}");
        }

        public void Dispose() {
            if (_handle != IntPtr.Zero) {
                try {
                    // Clear paint cbs first so no further frame dispatches are
                    // queued onto the (possibly-shutting-down) UI dispatcher.
                    SoftSledNative.softsled_set_paint_callback(_handle, null, IntPtr.Zero);
                    SoftSledNative.softsled_set_paint_rects_callback(_handle, null, IntPtr.Zero);
                    SoftSledNative.softsled_set_fastpath_callback(_handle, null, IntPtr.Zero);
                } catch { }
                try { SoftSledNative.softsled_disconnect(_handle); } catch { }
                // Brief pause so the worker thread fires its final state callback
                // before we yank the delegate out from under it.
                Thread.Sleep(200);
                SoftSledNative.softsled_client_free(_handle);
                _handle = IntPtr.Zero;
            }
            _bitmap = null;
            _fbInfo = default(SoftSledNative.FramebufferInfo);
            _fastpathCb = null;
            GC.SuppressFinalize(this);
        }
    }
}
