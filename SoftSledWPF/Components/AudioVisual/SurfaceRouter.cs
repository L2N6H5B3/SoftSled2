using SoftSled.Components.Diagnostics;
using SoftSled.Components.Extender;
using SoftSled.Components.Splash;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Routes the primary video element to the on-screen rectangle of
    /// the splash surface identified by the DMCT OpenMedia Surface ID.
    ///
    /// Two modes per <see cref="ExtenderCapabilities.GetRenderMode"/>:
    /// <list type="bullet">
    ///   <item><description><b>GDI</b> — the RDP framebuffer is the only
    ///   surface. Any incoming Surface ID is ignored (with a warning
    ///   for non-zero values) and the video element is sized to fill
    ///   the host Canvas.</description></item>
    ///   <item><description><b>RUI</b> — the Splash MS-RRSP2 channel
    ///   manages multiple surfaces. The incoming Surface ID matches a
    ///   <c>SurfacePool_CreateSurface.idNewSurface</c> previously sent
    ///   over Splash; we look it up in <see cref="SplashController"/>
    ///   and position the video element at the matching screen
    ///   rectangle. Subsequent rect changes (host resize, surface
    ///   re-bind) update the element via the controller's
    ///   <c>SurfaceScreenRectChanged</c> event.</description></item>
    /// </list>
    ///
    /// Thread model: all public methods marshal to the canvas's WPF
    /// dispatcher.
    /// </summary>
    internal sealed class SurfaceRouter {

        private readonly WMCRenderMode _mode;
        private readonly Canvas _canvas;
        private readonly FrameworkElement _primary;   // Media element behind RDP
        private readonly SplashController _splash;    // may be null in GDI mode
        private readonly Logger _logger;
        private readonly Dispatcher _dispatcher;

        private int _currentSurfaceId = -1;
        private bool _subscribed;

        public SurfaceRouter(WMCRenderMode mode,
                             Canvas canvas,
                             FrameworkElement primary,
                             SplashController splashController,
                             Logger logger) {
            _mode    = mode;
            _canvas  = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _splash  = splashController;
            _logger  = logger;
            _dispatcher = canvas.Dispatcher;
        }

        /// <summary>
        /// Called from <see cref="VirtualChannel.VirtualChannelAvCtrlHandler.VideoSurfaceRequested"/>
        /// when DMCT OpenMedia arrives. Sets up positioning + subscriptions.
        /// </summary>
        public void RouteVideoToSurface(int surfaceId) {
            if (!_dispatcher.CheckAccess()) {
                _dispatcher.BeginInvoke(new Action(() => RouteVideoToSurface(surfaceId)));
                return;
            }

            _currentSurfaceId = surfaceId;

            switch (_mode) {
                case WMCRenderMode.GDI:
                    if (surfaceId != 0) {
                        _logger?.LogInfo($"[surface-router] GDI mode received non-zero surface " +
                                         $"ID {surfaceId} — ignoring, using full canvas.");
                    }
                    SizeToCanvas();
                    return;

                case WMCRenderMode.RUI:
                    if (_splash == null) {
                        _logger?.LogError("[surface-router] RUI mode requested but no SplashController " +
                                          "is wired — falling back to full canvas.");
                        SizeToCanvas();
                        return;
                    }
                    if (!_splash.TryGetSurfaceScreenRect((uint)surfaceId, out var rect) ||
                        rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) {
                        _logger?.LogInfo($"[surface-router] Surface {surfaceId} not yet in registry " +
                                         $"(or zero-sized) — falling back to full canvas. Will re-snap " +
                                         $"on next SurfaceScreenRectChanged.");
                        SizeToCanvas();
                    } else {
                        ApplyRect(rect);
                    }
                    EnsureSubscribed();
                    return;
            }
        }

        /// <summary>
        /// Called from DMCT CloseMedia / Stop. Drops the surface
        /// subscription so subsequent splash resizes don't keep
        /// re-positioning a no-longer-visible video element.
        /// </summary>
        public void ReleaseSurface() {
            if (!_dispatcher.CheckAccess()) {
                _dispatcher.BeginInvoke(new Action(ReleaseSurface));
                return;
            }
            _currentSurfaceId = -1;
            if (_subscribed && _splash != null) {
                _splash.SurfaceScreenRectChanged -= OnSplashSurfaceRectChanged;
                _subscribed = false;
            }
        }

        // ----- internals -------------------------------------------------

        private void EnsureSubscribed() {
            if (_subscribed || _splash == null) return;
            _splash.SurfaceScreenRectChanged += OnSplashSurfaceRectChanged;
            _subscribed = true;
        }

        private void OnSplashSurfaceRectChanged(uint surfaceId, Rect rect) {
            // We only care about updates for the surface we're currently
            // bound to. Subscription is global because v1 broadcasts
            // host-resize to every surface, so we filter here.
            if (_currentSurfaceId < 0) return;
            if ((uint)_currentSurfaceId != surfaceId) return;
            if (!_dispatcher.CheckAccess()) {
                _dispatcher.BeginInvoke(new Action(() => ApplyRect(rect)));
            } else {
                ApplyRect(rect);
            }
        }

        private void SizeToCanvas() {
            // Match the existing SizeMediaToCanvasFill semantics in
            // ExtenderSessionControl — element fills the canvas top-
            // left to bottom-right.
            if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return;
            Canvas.SetLeft(_primary, 0);
            Canvas.SetTop(_primary, 0);
            _primary.Width  = _canvas.ActualWidth;
            _primary.Height = _canvas.ActualHeight;
        }

        private void ApplyRect(Rect rect) {
            if (rect.Width <= 0 || rect.Height <= 0) {
                SizeToCanvas();
                return;
            }
            Canvas.SetLeft(_primary, rect.X);
            Canvas.SetTop(_primary, rect.Y);
            _primary.Width  = rect.Width;
            _primary.Height = rect.Height;
        }
    }
}
