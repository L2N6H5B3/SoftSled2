using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// WPF host element for the MS-RRSP2 scene graph. Owns a single root
    /// <see cref="DrawingVisual"/> plus a background fill primitive that
    /// covers the entire host. The root visual hosts orphan splash
    /// visuals (those without an explicit MS-RRSP2 parent); parented
    /// visuals attach via <see cref="DrawingVisual.Children"/> on their
    /// parent's drawing visual instead.
    ///
    /// Placed *above* <c>rdpDisplay</c> in <c>MainWindow.xaml</c> so the
    /// splash UI composites on top of the RDP framebuffer. With WPF's
    /// default opaque background we'd hide the RDP layer entirely;
    /// keeping <c>Background = null</c> lets RDP show through wherever
    /// MS-RRSP2 has drawn nothing.
    /// </summary>
    public sealed class SplashRenderHost : FrameworkElement {

        private readonly DrawingVisual _backgroundVisual = new DrawingVisual();
        private readonly DrawingVisual _rootVisual       = new DrawingVisual();
        private readonly VisualCollection _children;
        private readonly HashSet<DrawingVisual> _attachedToRoot = new HashSet<DrawingVisual>();

        // Cache the most-recently-requested background colour so we can
        // repaint it after a layout pass — HostWindow_SetBackgroundColor
        // typically arrives milliseconds after we flip Visibility to
        // Visible, before WPF has re-measured the cell, so ActualWidth
        // is still 0 and a one-shot paint would draw a 0×0 rectangle.
        private Color? _pendingBackground;

        // Cached frozen brush for the current background colour, recreated
        // only when the colour itself changes. Avoids allocating a fresh
        // SolidColorBrush on every layout pass (OnRenderSizeChanged hits
        // RenderBackground for every WMC reconnect, splash visibility flip,
        // and live host resize).
        private SolidColorBrush _backgroundBrush;

        // Logical canvas size (TV resolution the WMC shell composes for).
        // The scene-root visual's Size dictates this; SplashController
        // sets it via SetLogicalCanvasSize. The _rootVisual is scaled by
        // ActualWidth/LogicalWidth × ActualHeight/LogicalHeight so the
        // shell fills whatever cell we're rendered into instead of
        // stamping a 1280×720 box in the top-left corner. Default 1280×720
        // matches the most common WMC composition target so a missed
        // SetLogicalCanvasSize call still produces something reasonable.
        private double _logicalCanvasWidth  = 1280;
        private double _logicalCanvasHeight = 720;

        public SplashRenderHost() {
            _children = new VisualCollection(this) {
                _backgroundVisual,
                _rootVisual,
            };
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            // Default Stretch alignment fills the parent cell, but
            // FrameworkElement's default MeasureOverride returns (0,0)
            // — explicitly request the available space so the host gets
            // arranged at the parent-cell size rather than collapsing to
            // a zero-pixel box.
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment   = VerticalAlignment.Stretch;
            // ClipToBounds is essential for the MCE home screen: WMC
            // positions vertically-stacked rows at Y=560, 840, 1120, ...,
            // far below the 1280×720 canvas, expecting the off-screen
            // ones to be clipped away. Without clipping, all rows render
            // on top of each other and the home screen looks like every
            // category is selected simultaneously.
            ClipToBounds = true;
        }

        /// <summary>Repaint the background visual with the given color.</summary>
        public void SetBackground(Color c) {
            if (_pendingBackground != c) {
                _pendingBackground = c;
                // Colour changed — drop the cached brush so RenderBackground
                // rebuilds it. The previous brush is frozen, so no need to
                // detach it explicitly; WPF GC reclaims it once nothing
                // references it.
                _backgroundBrush = null;
            }
            RenderBackground();
        }

        /// <summary>
        /// Update the logical canvas size that the splash UI was composed
        /// for (e.g. 1280×720). The root scene visual will be scaled
        /// uniformly so this canvas maps onto the host's actual size.
        /// </summary>
        public void SetLogicalCanvasSize(double width, double height) {
            if (width <= 0 || height <= 0) return;
            if (_logicalCanvasWidth == width && _logicalCanvasHeight == height) return;
            _logicalCanvasWidth  = width;
            _logicalCanvasHeight = height;
            ApplySceneScale();
        }

        /// <summary>
        /// Apply a ScaleTransform to the root visual so the logical
        /// canvas fills the host. Uses non-uniform scale (width and
        /// height computed independently) — matches what WPF does for
        /// <c>Stretch=Fill</c> on an Image. Anything aspect-ratio-
        /// sensitive should be re-thought if the host cell is far from
        /// the canvas aspect.
        /// </summary>
        private void ApplySceneScale() {
            double aw = ActualWidth, ah = ActualHeight;
            if (aw <= 0 || ah <= 0 || _logicalCanvasWidth <= 0 || _logicalCanvasHeight <= 0) {
                _rootVisual.Transform = Transform.Identity;
                return;
            }
            double sx = aw / _logicalCanvasWidth;
            double sy = ah / _logicalCanvasHeight;
            // Guard against degenerate scales — WMC has been observed to
            // briefly report 0×0 sizes during reconnect.
            if (sx <= 0 || sy <= 0 || double.IsInfinity(sx) || double.IsInfinity(sy)) {
                _rootVisual.Transform = Transform.Identity;
                return;
            }
            _rootVisual.Transform = new ScaleTransform(sx, sy);
        }

        private void RenderBackground() {
            using (var dc = _backgroundVisual.RenderOpen()) {
                if (!_pendingBackground.HasValue) return;
                Color c = _pendingBackground.Value;
                if (c.A == 0) return; // fully transparent — leave empty
                double w = ActualWidth, h = ActualHeight;
                if (w <= 0 || h <= 0) {
                    // Layout pass hasn't run yet. Bail — OnRenderSizeChanged
                    // will retry once a real size arrives.
                    return;
                }
                // Reuse the cached brush when only the host size changed
                // (the colour itself is invalidated by SetBackground).
                if (_backgroundBrush == null) {
                    _backgroundBrush = new SolidColorBrush(c);
                    _backgroundBrush.Freeze();
                }
                dc.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, w, h));
            }
        }

        protected override Size MeasureOverride(Size availableSize) {
            // Stretch to whatever the parent grid cell offers. Without
            // this, FrameworkElement's default returns (0,0) and the
            // host never actually receives non-zero ActualWidth/Height.
            // Use a finite fallback for cases where Available is
            // PositiveInfinity (e.g. inside a ScrollViewer).
            double w = double.IsInfinity(availableSize.Width)  ? 0 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            return new Size(w, h);
        }

        public void AttachToRoot(DrawingVisual visual) {
            if (_attachedToRoot.Add(visual)) {
                _rootVisual.Children.Add(visual);
            }
        }

        public void DetachFromRoot(DrawingVisual visual) {
            if (_attachedToRoot.Remove(visual)) {
                _rootVisual.Children.Remove(visual);
            }
        }

        public void ClearAll() {
            _rootVisual.Children.Clear();
            _attachedToRoot.Clear();
            _pendingBackground = null;
            using (var dc = _backgroundVisual.RenderOpen()) { /* empty */ }
        }

        /// <summary>
        /// Replace the scene-graph root with a single visual (used by
        /// <c>HostWindow_SetRoot</c>). Detaches every other top-level
        /// visual but preserves the background fill.
        /// </summary>
        public void SetSceneRoot(DrawingVisual root) {
            _rootVisual.Children.Clear();
            _attachedToRoot.Clear();
            if (root != null) {
                _rootVisual.Children.Add(root);
                _attachedToRoot.Add(root);
            }
        }

        // -------- Visual-host plumbing (required for custom DrawingVisual hosts) --------

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {
            base.OnRenderSizeChanged(sizeInfo);
            // Re-stretch the cached background to the new size. If
            // SetBackground was called before layout produced a real
            // ActualWidth/Height (common: HostWindow_SetBackgroundColor
            // arrives milliseconds after Visibility flips to Visible),
            // we deferred the paint then — replay it now.
            RenderBackground();
            // Re-fit the scene to the new host size.
            ApplySceneScale();
        }
    }
}
