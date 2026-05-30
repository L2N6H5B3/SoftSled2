using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// WPF host element for the MS-RRSP2 scene graph. Owns a single root
    /// <see cref="DrawingVisual"/>. The root visual hosts orphan splash
    /// visuals (those without an explicit MS-RRSP2 parent); parented
    /// visuals attach via <see cref="DrawingVisual.Children"/> on their
    /// parent's drawing visual instead.
    ///
    /// <para><b>Z-order in <c>ExtenderSessionControl.xaml</c>:</b>
    /// the host is placed ABOVE both <c>splashBackgroundFill</c> and
    /// <c>MediaCanvas</c> (FFME video plane) so the splash UI composites
    /// over the video, matching the Xbox / WMC hardware "video plane
    /// below UI plane" model. Background painting was deliberately moved
    /// OUT of this class into the dedicated <c>splashBackgroundFill</c>
    /// <see cref="System.Windows.Shapes.Rectangle"/> so the FFME video
    /// element naturally sits between background and scene graph — no
    /// "transparent background during video" hack needed.</para>
    ///
    /// <para><c>Background = null</c> on the host itself means WPF
    /// doesn't paint anything where the scene graph hasn't drawn, so
    /// the video plane below shows through automatically wherever
    /// splash content isn't covering it.</para>
    /// </summary>
    public sealed class SplashRenderHost : FrameworkElement {

        private readonly DrawingVisual _rootVisual = new DrawingVisual();
        private readonly VisualCollection _children;
        private readonly HashSet<DrawingVisual> _attachedToRoot = new HashSet<DrawingVisual>();

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

            // ---- Full-screen rendering performance hints (task #171) ----
            //
            // RenderOptions cascade through the visual tree, so applying these
            // to the root visual once covers every descendant DrawingVisual the
            // scene contains. The splash canvas is composed at WMC's logical
            // 1280×720 and we apply a ScaleTransform on _rootVisual that
            // stretches to the host cell size — so on a 1080p host the scale
            // factor is 1.5×, on 1440p it's 1.875×, on 4K it's 3×. Without
            // these hints WPF's default is high-quality bilinear/trilinear
            // bitmap scaling and full geometry anti-aliasing, both of which
            // get noticeably expensive per-pixel as the output area grows.
            //
            // BitmapScalingMode.LowQuality = bilinear (no mipmap chain).
            // Faster than the default HighQuality (~tricubic) and produces
            // acceptable results for UI chrome — the splash surfaces are
            // pre-rendered glyph atlases and 9-slice chrome where pixel-
            // perfect rescaling isn't a meaningful improvement.
            //
            // EdgeMode.Aliased disables the per-pixel coverage anti-aliasing
            // that WPF runs on geometry edges. We mostly draw axis-aligned
            // rectangles + bitmap quads where AA gives nothing visible, and
            // the cost is per-pixel at the output resolution.
            //
            // CachingHint.Cache = "you may cache rendered output for static
            // sub-trees." Lets WPF use intermediate bitmap caches where it
            // detects a static subtree, particularly useful for the chrome
            // surfaces that don't change between animation frames.
            //
            // Together these knobs typically halve the per-frame compositor
            // cost at 1080p+ resolutions and noticeably reduce CPU when the
            // window is maximised on a high-DPI display.
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                _rootVisual, System.Windows.Media.BitmapScalingMode.LowQuality);
            System.Windows.Media.RenderOptions.SetEdgeMode(
                _rootVisual, System.Windows.Media.EdgeMode.Aliased);
            System.Windows.Media.RenderOptions.SetCachingHint(
                _rootVisual, System.Windows.Media.CachingHint.Cache);

            // Log the WPF rendering tier on first construction so we can
            // detect software-rendering fallback (Tier 0). Tier 1 = partial
            // hardware acceleration (DX7 / shader 1.x), Tier 2 = full hardware
            // acceleration (DX9+, shader 2.0+). On any modern desktop or VM
            // with a sane GPU driver we expect Tier 2. Tier 0 means WPF is
            // rendering everything on the CPU — that produces exactly the
            // "slow at full-screen" symptom we're tracking — and the user
            // would need to fix their display driver / RDP-session settings
            // / virtualisation host before any in-renderer perf work can
            // matter.
            int tier = System.Windows.Media.RenderCapability.Tier >> 16;
            System.Diagnostics.Debug.WriteLine(
                $"[Splash] WPF render tier = {tier} ({(tier >= 2 ? "full HW accel" : tier == 1 ? "partial HW accel" : "SOFTWARE")})");
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
        }

        /// <summary>
        /// Replace the scene-graph root with a single visual (used by
        /// <c>HostWindow_SetRoot</c>). Detaches every other top-level
        /// visual.
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
            // Re-fit the scene to the new host size.
            ApplySceneScale();
        }
    }
}
