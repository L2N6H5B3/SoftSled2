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
        // Held alive only while a consumer has registered one. Native code
        // would reach into a collected delegate thunk and crash without this.
        private SoftSledNative.FastpathCallback _fastpathCb;

        // Framebuffer state. The native pointer in _fbInfo.Pixels is owned by
        // FreeRDP and remains valid only between PostConnect and Disconnect.
        private readonly Dispatcher _uiDispatcher;
        private SoftSledNative.FramebufferInfo _fbInfo;
        private WriteableBitmap _bitmap;

        public event EventHandler<DataReceived> DataReceived;
        public event EventHandler<StateChangedEventArgs> StateChanged;

        /// <summary>
        /// WPF-friendly software framebuffer for RDP video. Set when the
        /// session reaches ACTIVE; null otherwise. Bind via code-behind
        /// (e.g. <c>image.Source = client.Bitmap</c>) on the FrameReady or
        /// StateChanged-Active event.
        /// </summary>
        public WriteableBitmap Bitmap => _bitmap;

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

            _stateCb   = OnNativeStateChanged;
            _channelCb = OnNativeChannelData;
            _paintCb   = OnNativePaint;
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
                SoftSledNative.softsled_set_paint_callback(_handle, _paintCb, IntPtr.Zero);
                FrameReady?.Invoke(this, EventArgs.Empty);
            };
            // Synchronous: we want the bitmap allocated before any paint
            // callback can race in. softsled_set_paint_callback is the gate.
            _uiDispatcher.Invoke(init);
        }

        // Paint callback fires on the FreeRDP worker thread. Marshal a
        // dirty-rect blit onto the UI dispatcher at Render priority. We
        // re-read the cached fbInfo each time — pointer is stable for the
        // lifetime of the connection.
        private void OnNativePaint(IntPtr user, int x, int y, int w, int h) {
            if (w <= 0 || h <= 0) return;
            // Snapshot the struct so a concurrent disconnect that clears
            // _fbInfo can't null the pointer mid-marshal. We deliberately do
            // NOT touch _bitmap here — its PixelWidth/PixelHeight are
            // DependencyProperty getters with thread affinity and would
            // throw "calling thread cannot access this object" off the UI
            // thread. The framebuffer struct fields are equivalent and safe.
            SoftSledNative.FramebufferInfo fb = _fbInfo;
            if (fb.Pixels == IntPtr.Zero) return;

            // Clip rect into the framebuffer bounds (defence against odd server data).
            int bw = (int)fb.Width, bh = (int)fb.Height;
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > bw) w = bw - x;
            if (y + h > bh) h = bh - y;
            if (w <= 0 || h <= 0) return;

            int xx = x, yy = y, ww = w, hh = h;
            try {
                _uiDispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
                    var b = _bitmap;
                    var f = _fbInfo;
                    if (b == null || f.Pixels == IntPtr.Zero) return;
                    var rect = new Int32Rect(xx, yy, ww, hh);
                    b.Lock();
                    b.WritePixels(rect, f.Pixels,
                                  (int)(f.Stride * f.Height),
                                  (int)f.Stride, xx, yy);
                    b.AddDirtyRect(rect);
                    b.Unlock();
                }));
            } catch {
                /* dispatcher shutting down — drop the frame */
            }
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
                    // Clear paint cb first so no further frame dispatches are
                    // queued onto the (possibly-shutting-down) UI dispatcher.
                    SoftSledNative.softsled_set_paint_callback(_handle, null, IntPtr.Zero);
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
