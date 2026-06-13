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
        // GDI-mode only: returns the on-screen rect (canvas coords) the RDP
        // framebuffer actually occupies (Uniform letterbox-fit). The video
        // plane is constrained to this so it never spills into the black bars
        // / beyond the WMC desktop. Null return → framebuffer not ready yet,
        // fall back to full canvas. Null provider → behave as before.
        private readonly Func<Rect?> _gdiDisplayRectProvider;

        private int _currentSurfaceId = -1;
        private bool _subscribed;
        // Raised when the video should sit ABOVE the UI (true) vs below it
        // (false). The video plane normally composites BELOW the splash UI so
        // fullscreen UI (seek bar, etc.) overlays the video. But for a PiP
        // sub-rect, WMC paints an (opaque) placeholder in the splash layer that
        // would cover the video — so for PiP the video plane is raised above
        // the UI, clipped to its rect, leaving the rest of the UI untouched.
        // Tracks last state to fire only on change.
        public event Action<bool> VideoAboveUiChanged;
        private bool? _videoAboveUi;
        private void RaiseVideoAboveUi(bool above) {
            if (_videoAboveUi == above) return;
            _videoAboveUi = above;
            try { VideoAboveUiChanged?.Invoke(above); }
            catch (Exception ex) { _logger?.LogError($"[surface-router] VideoAboveUiChanged threw: {ex.Message}"); }
        }
        // Latest PiP rectangle reported by SplashController.
        // Empty = no PiP, route video to the full host bounds.
        // Populated = strict PIP-CAND match (see SplashController's
        // PIP-LOCK logic); routes the video element to the PiP
        // rectangle until either the PiP visual is destroyed, video
        // closes, or the host re-resolves a different surface rect.
        private Rect _currentPipRect = Rect.Empty;

        public SurfaceRouter(WMCRenderMode mode,
                             Canvas canvas,
                             FrameworkElement primary,
                             SplashController splashController,
                             Logger logger,
                             Func<Rect?> gdiDisplayRectProvider = null) {
            _mode    = mode;
            _canvas  = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _splash  = splashController;
            _logger  = logger;
            _dispatcher = canvas.Dispatcher;
            _gdiDisplayRectProvider = gdiDisplayRectProvider;
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
                    } else {
                        _logger?.LogInfo("[surface-router] GDI mode — sizing video element to full canvas " +
                                         "(WMC will reposition via fastpath overlay updates)");
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
                    bool ok = _splash.TryGetSurfaceScreenRect((uint)surfaceId, out var rect,
                                                              out uint splashHandle, out bool viaUid);
                    string resolutionTag = viaUid
                        ? $"DMCT uid={surfaceId} → splash handle=0x{splashHandle:X8}"
                        : $"splash handle=0x{splashHandle:X8} (direct)";
                    if (!ok || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) {
                        _logger?.LogInfo($"[surface-router] RUI mode — {resolutionTag} NOT yet in registry " +
                                         $"(or zero-sized) — falling back to full canvas. Will re-snap " +
                                         $"on next SurfaceScreenRectChanged.");
                        SizeToCanvas();
                    } else {
                        _logger?.LogInfo($"[surface-router] RUI mode — {resolutionTag} resolved to " +
                                         $"rect=({rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0}) " +
                                         $"— positioning video element");
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
            _currentPipRect = Rect.Empty;
            RaiseVideoAboveUi(false);   // media closed → restore below-UI default
            if (_subscribed && _splash != null) {
                _splash.SurfaceScreenRectChanged   -= OnSplashSurfaceRectChanged;
                _splash.VideoPipCandidateChanged   -= OnSplashPipCandidateChanged;
                _subscribed = false;
            }
        }

        // ----- internals -------------------------------------------------

        private void EnsureSubscribed() {
            if (_subscribed || _splash == null) return;
            _splash.SurfaceScreenRectChanged   += OnSplashSurfaceRectChanged;
            // PiP routing follows the deterministic PIP-CORNER-BIND in
            // SplashController (the recorded-TV now-playing window), tracked
            // per-frame so the video follows the box through animations.
            _splash.VideoPipCandidateChanged   += OnSplashPipCandidateChanged;
            _subscribed = true;
        }

        /// <summary>
        /// Receives PiP destination hints from SplashController. WMC does
        /// NOT emit a spec-level positioning message for the video plane
        /// (no <c>VideoPool_Draw</c> in any captured corpus); the
        /// controller infers the destination heuristically from
        /// gradient-only Visuals with PiP-shaped geometry that appear
        /// while a video instance is active. An empty rect means "video
        /// closed / PiP placeholder destroyed — revert to full canvas".
        /// </summary>
        private void OnSplashPipCandidateChanged(uint visualHandle, Rect rect) {
            if (!_dispatcher.CheckAccess()) {
                _dispatcher.BeginInvoke(new Action(() => OnSplashPipCandidateChanged(visualHandle, rect)));
                return;
            }
            // Ignore PiP hints unless we're actively routing a surface —
            // the AvCtrl handler has set _currentSurfaceId on OpenMedia
            // and cleared it on CloseMedia, so this gate keeps us from
            // racing a stale event into a "no current video" state.
            if (_currentSurfaceId < 0) {
                return;
            }
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) {
                _currentPipRect = Rect.Empty;
                _logger?.LogInfo($"[surface-router] PiP unlock — reverting video element to full canvas " +
                                 $"(previous lock vis=0x{visualHandle:X8})");
                SizeToCanvas();
            } else {
                _currentPipRect = rect;
                _logger?.LogInfo($"[surface-router] PiP lock vis=0x{visualHandle:X8} → routing video to " +
                                 $"rect=({rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0})");
                ApplyRect(rect);
            }
        }

        private void OnSplashSurfaceRectChanged(uint surfaceId, Rect rect) {
            // No per-ID filtering here.
            //
            // SplashController.RaiseSurfaceScreenRectChanged broadcasts
            // splash *object handles* (e.g. 0x06000049), but
            // _currentSurfaceId stores the DMCT *uid* (e.g. 101 — the
            // DynamicSurfaceFactory uid that DMCT OpenMedia carries as
            // its SurfaceID). A direct `_currentSurfaceId == surfaceId`
            // check would never match because the two are different
            // numbering spaces. Until v1 needs per-surface broadcasting,
            // the cheapest correct behaviour is: any rect-changed event
            // means "host or surface state has shifted, re-fetch the
            // rect for my current uid through the same path RouteVideoToSurface
            // uses." TryGetSurfaceScreenRect already knows how to bridge
            // uid → splash handle → screen rect.
            if (_currentSurfaceId < 0) return;
            if (!_dispatcher.CheckAccess()) {
                _dispatcher.BeginInvoke(new Action(() => ReResolveAndApply()));
            } else {
                ReResolveAndApply();
            }
        }

        /// <summary>
        /// Re-resolve the current surfaceId to its screen rect and apply
        /// it. Used both by the rect-changed event and by external
        /// "re-check the position" pokes. No-op when no surface is
        /// currently routed.
        /// </summary>
        private void ReResolveAndApply() {
            if (_currentSurfaceId < 0) return;
            switch (_mode) {
                case WMCRenderMode.GDI:
                    SizeToCanvas();
                    return;
                case WMCRenderMode.RUI:
                    // PiP lock wins over the surface rect: when WMC has
                    // pinned the video to a small rectangle inside its
                    // chrome (browsing-while-playing), host-size events
                    // should keep that pin, not yank the video back to
                    // fullscreen.
                    if (!_currentPipRect.IsEmpty && _currentPipRect.Width > 0 && _currentPipRect.Height > 0) {
                        _logger?.LogDebug($"[surface-router] re-resolve honoured PiP lock → rect=({_currentPipRect.X:F0},{_currentPipRect.Y:F0} {_currentPipRect.Width:F0}x{_currentPipRect.Height:F0})");
                        ApplyRect(_currentPipRect);
                        return;
                    }
                    if (_splash == null) {
                        SizeToCanvas();
                        return;
                    }
                    bool ok = _splash.TryGetSurfaceScreenRect((uint)_currentSurfaceId, out var rect,
                                                              out uint splashHandle, out bool viaUid);
                    if (ok && !rect.IsEmpty && rect.Width > 0 && rect.Height > 0) {
                        string tag = viaUid
                            ? $"uid={_currentSurfaceId} → splash handle=0x{splashHandle:X8}"
                            : $"splash handle=0x{splashHandle:X8}";
                        _logger?.LogDebug($"[surface-router] re-resolve {tag} → rect=({rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0})");
                        ApplyRect(rect);
                    } else {
                        SizeToCanvas();
                    }
                    return;
            }
        }

        private void SizeToCanvas() {
            // Match the existing SizeMediaToCanvasFill semantics in
            // ExtenderSessionControl — element fills the canvas top-
            // left to bottom-right.
            if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) {
                _logger?.LogInfo("[surface-router] SizeToCanvas skipped — canvas has zero size (layout not run yet)");
                return;
            }

            // GDI mode: constrain the video plane to the RDP display's
            // letterboxed rect (the rdpDisplay Image is Stretch="Uniform" over
            // the same cell). Without this the plane fills the whole window and
            // spills into the black bars beyond the WMC desktop area.
            if (_mode == WMCRenderMode.GDI && _gdiDisplayRectProvider != null) {
                var r = _gdiDisplayRectProvider();
                if (r.HasValue && r.Value.Width > 0 && r.Value.Height > 0) {
                    ApplyRect(r.Value);
                    _logger?.LogDebug($"[surface-router] SizeToCanvas (GDI) → constrained to RDP display rect " +
                                      $"({r.Value.X:F0},{r.Value.Y:F0} {r.Value.Width:F0}x{r.Value.Height:F0})");
                    return;
                }
            }

            Canvas.SetLeft(_primary, 0);
            Canvas.SetTop(_primary, 0);
            _primary.Width  = _canvas.ActualWidth;
            _primary.Height = _canvas.ActualHeight;
            _logger?.LogDebug($"[surface-router] SizeToCanvas → video element at (0,0) {_canvas.ActualWidth:F0}x{_canvas.ActualHeight:F0}");
            RaiseVideoAboveUi(false);   // fullscreen → below the UI (UI overlays)
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
            _logger?.LogDebug($"[surface-router] ApplyRect → video element at ({rect.X:F0},{rect.Y:F0}) {rect.Width:F0}x{rect.Height:F0}");

            // A genuine PiP box is small in BOTH dimensions (letterbox bars
            // shrink only one). Raise the video above the UI for PiP so the
            // splash placeholder doesn't cover it; keep it below otherwise.
            bool isPip = _canvas.ActualWidth > 0 && _canvas.ActualHeight > 0
                         && rect.Width  < _canvas.ActualWidth  * 0.70
                         && rect.Height < _canvas.ActualHeight * 0.70;
            RaiseVideoAboveUi(isPip);
        }
    }
}
