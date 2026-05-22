using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftSled.Components.Splash.Objects {

    /// <summary>
    /// WPF-backed implementations of the small MS-RRSP2 class subset we
    /// support in slice 1: Visual, RenderBuilder, Window, Device, Context,
    /// plus a generic placeholder for everything else.
    ///
    /// Slice 1 goals:
    /// <list type="bullet">
    ///   <item><description>Track <c>Broker_CreateObject</c> /
    ///   <c>Broker_DestroyObject</c> so the registry never leaks.</description></item>
    ///   <item><description>Maintain a parent/child visual tree so
    ///   <c>Visual_ChangeParent</c> attaches a child to its parent's
    ///   <see cref="DrawingVisual"/> rather than to the host root.</description></item>
    ///   <item><description>Render <c>Window_SetBackgroundColor</c> as a
    ///   full-host fill — that's the "first pixel" milestone.</description></item>
    ///   <item><description>Render <c>Device_DrawSolid/Outline/Line</c>
    ///   primitives via a <c>DrawingContext</c> opened on the *current*
    ///   <see cref="SplashRenderBuilder"/>, then flushed onto its target
    ///   visual when <c>Visual_SetContent(renderBuilderHandle)</c> arrives.</description></item>
    /// </list>
    ///
    /// All splash-object types live in this file because the per-class
    /// state is small and lumping them keeps the diff scannable. They can
    /// be split later as more messages land.
    /// </summary>

    // -------- Generic placeholder --------

    internal class SplashGenericObject : ISplashObject {
        public uint            Handle    { get; }
        public SplashClassKind Kind      { get; }
        public string          ClassName { get; }

        public SplashGenericObject(uint handle, SplashClassKind kind, string className) {
            Handle    = handle;
            Kind      = kind;
            ClassName = className ?? string.Empty;
        }

        public virtual void OnDestroyed() { /* no resources to free */ }
    }

    // -------- Context --------

    internal sealed class SplashContext : SplashGenericObject {
        public SplashContext(uint handle, string className)
            : base(handle, SplashClassKind.Context, className) { }
    }

    // -------- Window --------

    internal sealed class SplashWindow : SplashGenericObject {

        private readonly SplashRenderHost _host;

        public Color BackgroundColor { get; private set; } = Colors.Transparent;
        public uint  ContentVisualHandle { get; set; } = 0;

        public SplashWindow(uint handle, string className, SplashRenderHost host)
            : base(handle, SplashClassKind.Window, className) {
            _host = host;
        }

        public void SetBackgroundColor(Color c) {
            BackgroundColor = c;
            _host?.SetBackground(c);
        }

        public override void OnDestroyed() {
            // Clear the host background when the window goes away.
            _host?.SetBackground(Colors.Transparent);
        }
    }

    // -------- Visual --------

    internal sealed class SplashVisual : SplashGenericObject {

        private readonly SplashRenderHost _host;

        public DrawingVisual           DrawingVisual { get; } = new DrawingVisual();
        public SplashVisual             Parent       { get; private set; }
        public readonly List<SplashVisual> Children = new List<SplashVisual>();

        // User-defined bits set via Visual_ChangeDataBits (spec
        // §2.2.4.6.2). The MS-RRSP2 spec calls these "user-defined" but
        // empirically WMC uses specific bits to drive rendering state.
        //
        // Reverse-engineered semantics from live wire traces:
        //   bit 6 (0x40) = "this visual is a row container in a
        //                   home-screen-style vertical row stack"
        //   bit 4 (0x10) = "currently selected/active" — for row
        //                   containers this means show the full tile
        //                   strip; for non-row visuals it appears on
        //                   most rendered visuals (close to a "render
        //                   normally" flag).
        //   bit 0 (0x01) = "focused" — paired with bit 4 on the active
        //                   row in selected state.
        //   bit 2 (0x04) = additional "row participation" marker.
        //
        // The asymmetry that matters: in a home-row stack, each row's
        // inner container has bit 6 set. The CURRENT row's inner
        // container additionally has bit 4 set. Non-current rows have
        // bit 6 only. WMC's renderer hides any row container without
        // bit 4 — that's what keeps non-selected rows' tile strips off-
        // screen while their preview titles remain (titles live in a
        // different subtree without bit 6).
        public uint DataBits;

        // Property cache — kept so we can re-apply the transform after any
        // single field changes without losing the others.
        public float PosX, PosY, PosZ;
        public float SizeX = 1, SizeY = 1, SizeZ = 1;
        public float ScaleX = 1, ScaleY = 1, ScaleZ = 1;
        // Center point per MS-RRSP2 spec §2.2.4.6.8 / §2.2.4.6.9:
        // effective pivot = Size * CenterScale + CenterOffset. WMC's MCE
        // shell sets CenterScale=(0.5, 0.5) on most tiles so scale &
        // rotation pivot around the visual's center; storing only
        // CenterX/Y (as the old code did) loses the per-size dependency.
        public float CenterScaleX, CenterScaleY, CenterScaleZ;
        public float CenterOffsetX, CenterOffsetY, CenterOffsetZ;
        public float RotAxisX, RotAxisY, RotAxisZ = 1; // default Z-axis
        public float RotAngleDeg;
        public byte  AlphaByte = 255;
        public Color Color = Colors.White;
        public bool  Visible = true;
        public uint  ContentRenderBuilderHandle = 0;

        public SplashVisual(uint handle, string className, SplashRenderHost host)
            : base(handle, SplashClassKind.Visual, className) {
            _host = host;
            DrawingVisual.Transform = new TranslateTransform(0, 0);
            DrawingVisual.Opacity   = 1.0;
        }

        /// <summary>
        /// MS-RRSP2 <c>Visual_ChangeParent</c> z-order placement
        /// (spec 2.2.4.6.3 nOrder enum).
        /// </summary>
        public enum ChildOrder : int {
            Any    = 0,
            Before = 1,
            Behind = 2,
            Top    = 3,   // Top of parent's children list — appears IN FRONT
            Bottom = 4,   // Bottom of parent's children list — appears BEHIND
        }

        public void ChangeParent(SplashVisual newParent, ChildOrder order = ChildOrder.Any,
                                 SplashVisual sibling = null) {
            // Detach from old parent (or host root if currently rooted).
            // _host may be null if the controller hasn't called AttachHost
            // yet — defensive ?. so a stray Broker_CreateObject before
            // wiring doesn't NRE the whole channel.
            if (Parent != null) {
                Parent.Children.Remove(this);
                Parent.DrawingVisual.Children.Remove(DrawingVisual);
            } else {
                _host?.DetachFromRoot(DrawingVisual);
            }

            // Attach to new parent or host root, honouring the z-order
            // hint. WPF VisualCollection paints index[0] first (so it's
            // BEHIND siblings in z) and index[last] last (so it's IN
            // FRONT). MS-RRSP2 "Bottom" means behind in z and must map
            // to index 0 — the slice-4 default of Add()-to-end made
            // every visual front-most and caused later-arriving siblings
            // (e.g. the background) to cover earlier-positioned ones
            // (e.g. the WMC logo).
            Parent = newParent;
            var siblings = newParent != null ? newParent.DrawingVisual.Children : null;
            int index = ResolveInsertIndex(siblings, order, sibling);
            if (newParent != null) {
                newParent.Children.Add(this);
                if (index < 0 || index >= siblings.Count) {
                    siblings.Add(DrawingVisual);
                } else {
                    siblings.Insert(index, DrawingVisual);
                }
            } else {
                _host?.AttachToRoot(DrawingVisual);
            }
        }

        private static int ResolveInsertIndex(VisualCollection siblings, ChildOrder order, SplashVisual sibling) {
            if (siblings == null) return -1;
            int sibIdx = -1;
            if (sibling != null) sibIdx = siblings.IndexOf(sibling.DrawingVisual);
            switch (order) {
                case ChildOrder.Top:    return siblings.Count;   // paint last = front
                case ChildOrder.Bottom: return 0;                // paint first = back
                case ChildOrder.Before:
                    // Spec: "Before the specified sibling." Interpreting
                    // as "appears in front of sibling in z" → insert
                    // AFTER sibling in render order so it paints later.
                    return sibIdx >= 0 ? sibIdx + 1 : siblings.Count;
                case ChildOrder.Behind:
                    // Spec: "Behind the specified sibling." Insert at
                    // sibling's index so we paint first and appear behind.
                    return sibIdx >= 0 ? sibIdx : 0;
                case ChildOrder.Any:
                default:
                    return siblings.Count; // default to top, same as before
            }
        }

        public void ApplyTransform() {
            // 2D subset: position translate + (uniform-ish) scale +
            // Z-axis rotation. 3D rotations on other axes are ignored.
            //
            // Pivot point per spec §2.2.4.6.8 / §2.2.4.6.9:
            //   center = Size * CenterScale + CenterOffset
            // Computed inside ApplyTransform (not at the SetCenterPoint*
            // dispatch) because Size frequently arrives AFTER the center
            // setters on the wire — using a precomputed value would
            // anchor pivots at (0,0) for the lifetime of every visual.
            double cx = SizeX * CenterScaleX + CenterOffsetX;
            double cy = SizeY * CenterScaleY + CenterOffsetY;
            var group = new TransformGroup();
            if (ScaleX != 1 || ScaleY != 1) {
                group.Children.Add(new ScaleTransform(ScaleX, ScaleY, cx, cy));
            }
            if (RotAngleDeg != 0 && Math.Abs(RotAxisZ) > 0.0001f) {
                group.Children.Add(new RotateTransform(RotAngleDeg, cx, cy));
            }
            group.Children.Add(new TranslateTransform(PosX, PosY));
            DrawingVisual.Transform = group;
        }

        public void ApplyAlpha() {
            // WPF DrawingVisual has no separate Visibility flag, so
            // opacity multiplexes Visible, AlphaByte and the
            // DataBits-driven hide rule.
            DrawingVisual.Opacity = ComputeEffectiveOpacity();
        }

        /// <summary>
        /// Update <see cref="DrawingVisual.Clip"/> to a rectangle matching
        /// the visual's current Size — but only for visuals WMC explicitly
        /// flagged as "clip my children".
        ///
        /// Background: MS-RRSP2 has no explicit SetClip message; the spec
        /// (§2.2.4.6.2) just calls the data-bits "user-defined". A first
        /// pass clipped *every* visual to its bounds, which fixed selection
        /// glows leaking past menu rows but broke drop shadows, overflow
        /// images and selection boxes elsewhere — those are intentionally
        /// drawn past their parent's edge and WMC's renderer does not
        /// clip them.
        ///
        /// Empirical hypothesis: bit 5 (0x20) marks "container clips its
        /// children". Bit-pattern catalogue from the live wire:
        ///   0x30, 0x31, 0x60, 0x64, 0x66, 0x70, 0x74, 0x75, 0x76, 0x77
        /// — i.e. only the rarer patterns set bit 5. The common
        /// non-clipping wrappers (0x44/0x54/0x55/0x57/0x40) all leave
        /// bit 5 clear, which matches the "shadows still visible"
        /// requirement.
        /// </summary>
        public void ApplyBoundsClip() {
            bool wantsClip = (DataBits & 0x20u) != 0;
            if (!wantsClip) {
                if (DrawingVisual.Clip != null) DrawingVisual.Clip = null;
                return;
            }
            if (SizeX > 0 && SizeY > 0) {
                var clipRect = new System.Windows.Rect(0, 0, SizeX, SizeY);
                // Avoid allocating a fresh RectangleGeometry every frame
                // during a Size animation — only reassign when the rect
                // actually changed.
                if (DrawingVisual.Clip is RectangleGeometry rg && rg.Rect == clipRect) return;
                var geom = new RectangleGeometry(clipRect);
                geom.Freeze();
                DrawingVisual.Clip = geom;
            } else if (DrawingVisual.Clip != null) {
                DrawingVisual.Clip = null;
            }
        }

        public void ApplyVisibility() {
            DrawingVisual.Opacity = ComputeEffectiveOpacity();
        }

        /// <summary>
        /// Re-compute the effective WPF opacity from all hide-related
        /// state: <see cref="Visible"/> and <see cref="AlphaByte"/>.
        ///
        /// Previously this method also applied three reverse-engineered
        /// "hide" rules driven by <see cref="DataBits"/> bit patterns
        /// (0x44 and 0x57) to suppress non-focused tiles in the home-
        /// screen row strips. Those rules over-fit: bit patterns 0x44
        /// and 0x57 appear on many screens beyond the home strip (e.g.
        /// the recorded-TV thumbnail grid), so applying the predicate
        /// universally hid most of those grids' thumbnails. The home-
        /// screen "all rows visible at once" cosmetic issue is the
        /// lesser of two evils until we redesign the home strip
        /// rendering around an explicit focus-window mask instead of
        /// a bit-pattern visibility heuristic.
        /// </summary>
        public double ComputeEffectiveOpacity() {
            if (!Visible) return 0.0;
            return AlphaByte / 255.0;
        }

        /// <summary>Update the user-data bits with a value/mask pair
        /// (matching Visual_ChangeDataBits semantics) and re-apply the
        /// effective opacity + the bounds clip (bit 5 controls the clip).</summary>
        public void ApplyDataBits(uint value, uint mask) {
            DataBits = (DataBits & ~mask) | (value & mask);
            DrawingVisual.Opacity = ComputeEffectiveOpacity();
            // Bit 5 (0x20) governs whether the visual clips its children
            // to its own bounds. If the mask touched bit 5 — or if the
            // bit is on and a previous Size set deferred the clip — the
            // ApplyBoundsClip call ensures the clip state matches the
            // post-update bit value.
            if ((mask & 0x20u) != 0) ApplyBoundsClip();
        }

        // Snapshot of the most-recent SetContent's ops. Cached so that
        // when Size changes (e.g. via Animation[Size]) we can re-paint
        // the same content at the new size — important for stretch-to-
        // visual surface draws (Surface_Draw with dst=(0,0,-1,-1)).
        private List<Action<DrawingContext, Size>> _cachedOps;

        public void SetContentFromRenderBuilder(SplashRenderBuilder rb,
                                                System.Collections.Generic.IReadOnlyList<SplashGradient> pendingGradients = null) {
            ContentRenderBuilderHandle = rb?.Handle ?? 0;
            // Snapshot the RB's ops so RepaintContent can run later with
            // a new Size after the RB has been Clear()'d / re-filled.
            _cachedOps = rb?.SnapshotOps();
            RepaintContent();
            // Apply any gradients that were queued onto this RB via
            // Gradient_Draw (spec §2.2.4.15) as the visual's OpacityMask.
            // NEXT-BOUND model: WMC ships the gradient after the
            // content-bearing SetContent and before this (sibling)
            // SetContent. Empirically this binding model produces
            // correct results for the home-row tile-window masks and
            // most strip-edge fades; some smaller action panels still
            // get mis-clipped and are tracked separately.
            if (pendingGradients != null && pendingGradients.Count > 0) {
                var last = pendingGradients[pendingGradients.Count - 1];
                var mask = last?.BuildOpacityMask(SizeX, SizeY);
                DrawingVisual.OpacityMask = mask;
            } else {
                // No queued gradients = no mask. Clearing is important so
                // a previously masked visual that gets re-bound without
                // gradients doesn't retain the stale mask.
                DrawingVisual.OpacityMask = null;
            }
            // Empirical: the WMC server reuses the same RenderBuilder
            // handle for many sequential Visual_SetContent bindings and
            // never sends RenderBuilder_Clear between them. The wire
            // pattern is repeated (Surface_Draw → Visual_SetContent), so
            // each pair clearly intends one surface-op per visual. Treat
            // Visual_SetContent as implicitly consuming the RB's ops —
            // otherwise the second visual would draw the first surface
            // too, the third would draw both, etc. (cumulative bug).
            //
            // The spec (2.2.4.6.13) says "copies" not "moves", but the
            // observed semantics on WMC strongly imply move-and-clear.
            rb?.Clear();
        }

        /// <summary>
        /// Re-paint the cached RenderBuilder ops onto this visual using
        /// the current Size. No-op if no content has ever been bound.
        /// </summary>
        public void RepaintContent() {
            if (_cachedOps == null) return;
            var bounds = new Size(SizeX > 0 ? SizeX : 0, SizeY > 0 ? SizeY : 0);
            using (var dc = DrawingVisual.RenderOpen()) {
                for (int i = 0; i < _cachedOps.Count; i++) {
                    _cachedOps[i](dc, bounds);
                }
            }
        }

        public override void OnDestroyed() {
            // Detach so the parent doesn't keep a reference and WPF can
            // collect the underlying DrawingVisual.
            //
            // Previously this called ChangeParent(null) (which re-roots
            // to the host) THEN DetachFromRoot — a redundant
            // re-root/un-root dance. Just remove from current parent or
            // host directly.
            if (Parent != null) {
                Parent.Children.Remove(this);
                Parent.DrawingVisual.Children.Remove(DrawingVisual);
                Parent = null;
            } else {
                _host?.DetachFromRoot(DrawingVisual);
            }
            // CRITICAL: also detach surviving children from BOTH our
            // SplashVisual children list AND our DrawingVisual.Children.
            // If we leave them in DrawingVisual.Children, they keep a
            // WPF parent pointer to our (now-destroyed) DrawingVisual,
            // and any later Visual_ChangeParent on a surviving child
            // throws "Specified Visual is already a child of another
            // Visual" — visibly: old screen elements stay on-screen
            // because the server can't re-parent them out of our dead
            // subtree.
            foreach (var child in Children) {
                child.Parent = null;
            }
            DrawingVisual.Children.Clear();
            Children.Clear();
        }
    }

    // -------- RenderBuilder --------

    /// <summary>
    /// Accumulates draw commands sent through <c>Device</c> /
    /// <c>Surface_Draw</c> while this RenderBuilder is being populated.
    /// Painted into a target <see cref="SplashVisual"/>'s
    /// <see cref="DrawingVisual"/> on <c>Visual_SetContent</c>.
    ///
    /// Ops accept the target Visual's bounding rectangle so that
    /// <c>Surface_Draw</c> with the WMC <c>dst=(0,0,-1,-1)</c> sentinel
    /// (which means "stretch to fill the visual") can resolve its
    /// destination at paint time, after the visual has been chosen.
    /// </summary>
    internal sealed class SplashRenderBuilder : SplashGenericObject {

        private readonly List<Action<DrawingContext, Size>> _ops = new List<Action<DrawingContext, Size>>();

        /// <summary>
        /// Gradient handles queued via <c>Gradient_Draw rb=this</c>. Kept for
        /// diagnostic logging only — the controller now applies gradients
        /// immediately to <see cref="LastBoundVisualHandle"/> at Gradient_Draw
        /// time, rather than waiting for the next SetContent. Wire evidence
        /// (every gradient-attached SetContent has ops=0) showed that the
        /// "next bind" is always a sibling/recycle visual, not the intended
        /// content target.
        /// </summary>
        public readonly List<uint> PendingGradients = new List<uint>();

        /// <summary>
        /// Handle of the visual most recently bound to this RB via
        /// <c>Visual_SetContent</c>. <c>Gradient_Draw</c> uses this to
        /// route the gradient onto the actual content-bearing visual
        /// (the one that just baked the RB's ops) rather than onto the
        /// empty sibling bind that follows.
        /// </summary>
        public uint LastBoundVisualHandle;

        public SplashRenderBuilder(uint handle, string className)
            : base(handle, SplashClassKind.RenderBuilder, className) { }

        public int OpCount => _ops.Count;

        public void Clear() {
            _ops.Clear();
            // Pending gradients are SetContent-scoped, not Clear-scoped:
            // WMC sometimes calls Gradient_Draw between Surface_Draw ops
            // and the SetContent that consumes them. Clearing on every
            // RenderBuilder reuse cycle would drop gradients prematurely.
            // PendingGradients is only emptied by ConsumePendingGradients
            // (called from SplashVisual.SetContentFromRenderBuilder).
        }

        public uint[] ConsumePendingGradients() {
            if (PendingGradients.Count == 0) return System.Array.Empty<uint>();
            var arr = PendingGradients.ToArray();
            PendingGradients.Clear();
            return arr;
        }

        public void AddSolid(Rect rect, Color color, bool stretchToVisual = false) {
            var brush = new SolidColorBrush(color); brush.Freeze();
            if (stretchToVisual) {
                _ops.Add((dc, b) => {
                    if (b.Width > 0 && b.Height > 0)
                        dc.DrawRectangle(brush, null, new Rect(0, 0, b.Width, b.Height));
                });
            } else {
                _ops.Add((dc, _) => dc.DrawRectangle(brush, null, rect));
            }
        }

        public void AddOutline(Rect rect, Color color, double thickness, bool stretchToVisual = false) {
            var pen = new Pen(new SolidColorBrush(color), thickness); pen.Freeze();
            if (stretchToVisual) {
                _ops.Add((dc, b) => {
                    if (b.Width > 0 && b.Height > 0)
                        dc.DrawRectangle(null, pen, new Rect(0, 0, b.Width, b.Height));
                });
            } else {
                _ops.Add((dc, _) => dc.DrawRectangle(null, pen, rect));
            }
        }

        public void AddLine(Point a, Point b, Color color, double thickness) {
            var pen = new Pen(new SolidColorBrush(color), thickness); pen.Freeze();
            _ops.Add((dc, _) => dc.DrawLine(pen, a, b));
        }

        /// <summary>
        /// Append a textured-rectangle op (Surface_Draw / SurfacePool_Draw).
        /// If <paramref name="stretchToVisual"/> is true the destination
        /// rectangle is resolved at paint time to the Visual's bounding
        /// box (the WMC <c>(0, 0, -1, -1)</c> "fill bounds" sentinel).
        /// We crop to the source rectangle first via
        /// <see cref="CroppedBitmap"/> so partial-atlas blits work.
        /// </summary>
        public void AddImage(BitmapSource src, Rect srcRect, Rect dstRect, bool stretchToVisual) {
            if (src == null) return;
            BitmapSource toDraw;
            if (srcRect.X == 0 && srcRect.Y == 0
                && (int)srcRect.Width  == src.PixelWidth
                && (int)srcRect.Height == src.PixelHeight) {
                toDraw = src;
            } else {
                try {
                    var ix = (int)System.Math.Max(0, srcRect.X);
                    var iy = (int)System.Math.Max(0, srcRect.Y);
                    var iw = (int)System.Math.Min(src.PixelWidth  - ix, srcRect.Width);
                    var ih = (int)System.Math.Min(src.PixelHeight - iy, srcRect.Height);
                    if (iw <= 0 || ih <= 0) return;
                    toDraw = new CroppedBitmap(src, new Int32Rect(ix, iy, iw, ih));
                    if (toDraw.CanFreeze) toDraw.Freeze();
                } catch {
                    toDraw = src; // fall back to full bitmap on any CroppedBitmap error
                }
            }
            _ops.Add((dc, visualBounds) => {
                Rect dst = stretchToVisual && visualBounds.Width > 0 && visualBounds.Height > 0
                    ? new Rect(0, 0, visualBounds.Width, visualBounds.Height)
                    : dstRect;
                dc.DrawImage(toDraw, dst);
            });
        }

        /// <summary>
        /// Append a 9-slice textured-rectangle op (<c>Surface_DrawGrid</c>,
        /// spec §2.2.4.11.1). The source bitmap is divided into a 3×3 grid
        /// using the four "division" parameters as PIXEL OFFSETS from
        /// their respective edges:
        /// <list type="bullet">
        ///   <item><paramref name="gridX1"/>: width of the left edge column</item>
        ///   <item><paramref name="gridX2"/>: width of the right edge column</item>
        ///   <item><paramref name="gridY1"/>: height of the top edge row</item>
        ///   <item><paramref name="gridY2"/>: height of the bottom edge row</item>
        /// </list>
        /// When rendered, the four corners keep their original pixel
        /// size, the four edges stretch only along their non-corner axis,
        /// and the centre stretches both ways to fill the destination.
        /// This is how WMC ships its focus-chrome / button-frame /
        /// drop-shadow assets — the soft shadow under a focus selector
        /// is embedded in the bottom edge band of the chrome image,
        /// so a uniform stretch (the previous stub behaviour) compresses
        /// the shadow and shifts it away from the bottom border, opening
        /// a visible gap.
        /// </summary>
        public void AddNineSliceImage(BitmapSource src,
                                      float gridX1, float gridX2,
                                      float gridY1, float gridY2,
                                      Rect dstRect, bool stretchToVisual) {
            if (src == null) return;
            int srcW = src.PixelWidth;
            int srcH = src.PixelHeight;
            // Clamp grid values into [0, srcDim) and ensure the two
            // edge widths don't overlap.
            int x1 = (int)System.Math.Max(0, System.Math.Min(srcW, gridX1));
            int x2 = (int)System.Math.Max(0, System.Math.Min(srcW - x1, gridX2));
            int y1 = (int)System.Math.Max(0, System.Math.Min(srcH, gridY1));
            int y2 = (int)System.Math.Max(0, System.Math.Min(srcH - y1, gridY2));
            int centerW = srcW - x1 - x2;
            int centerH = srcH - y1 - y2;
            if (centerW < 0 || centerH < 0) {
                // Degenerate — corners overlap. Fall back to a uniform
                // stretch so something still renders.
                AddImage(src, new Rect(0, 0, srcW, srcH), dstRect, stretchToVisual);
                return;
            }

            // Pre-crop each non-empty source region once and freeze it,
            // so the per-frame paint op is just nine cheap DrawImage
            // calls with no per-frame allocation.
            BitmapSource tl = TryCrop(src, 0,         0,           x1,      y1);
            BitmapSource t  = TryCrop(src, x1,        0,           centerW, y1);
            BitmapSource tr = TryCrop(src, srcW - x2, 0,           x2,      y1);
            BitmapSource l  = TryCrop(src, 0,         y1,          x1,      centerH);
            BitmapSource c  = TryCrop(src, x1,        y1,          centerW, centerH);
            BitmapSource r  = TryCrop(src, srcW - x2, y1,          x2,      centerH);
            BitmapSource bl = TryCrop(src, 0,         srcH - y2,   x1,      y2);
            BitmapSource b  = TryCrop(src, x1,        srcH - y2,   centerW, y2);
            BitmapSource br = TryCrop(src, srcW - x2, srcH - y2,   x2,      y2);

            _ops.Add((dc, visualBounds) => {
                Rect dst = stretchToVisual && visualBounds.Width > 0 && visualBounds.Height > 0
                    ? new Rect(0, 0, visualBounds.Width, visualBounds.Height)
                    : dstRect;
                double dw = dst.Width, dh = dst.Height;
                if (dw <= 0 || dh <= 0) return;

                // Corners stay at original pixel size, but if the dest
                // is too small to fit both, shrink them proportionally
                // so the rendering degrades gracefully instead of
                // overlapping or going negative.
                double leftW   = System.Math.Min(x1, dw);
                double rightW  = System.Math.Min(x2, System.Math.Max(0, dw - leftW));
                double centerDW = System.Math.Max(0, dw - leftW - rightW);
                double topH    = System.Math.Min(y1, dh);
                double bottomH = System.Math.Min(y2, System.Math.Max(0, dh - topH));
                double centerDH = System.Math.Max(0, dh - topH - bottomH);

                double xL = dst.X;
                double xC = dst.X + leftW;
                double xR = dst.X + leftW + centerDW;
                double yT = dst.Y;
                double yM = dst.Y + topH;
                double yB = dst.Y + topH + centerDH;

                if (tl != null && leftW    > 0 && topH    > 0) dc.DrawImage(tl, new Rect(xL, yT, leftW,    topH));
                if (t  != null && centerDW > 0 && topH    > 0) dc.DrawImage(t,  new Rect(xC, yT, centerDW, topH));
                if (tr != null && rightW   > 0 && topH    > 0) dc.DrawImage(tr, new Rect(xR, yT, rightW,   topH));
                if (l  != null && leftW    > 0 && centerDH> 0) dc.DrawImage(l,  new Rect(xL, yM, leftW,    centerDH));
                if (c  != null && centerDW > 0 && centerDH> 0) dc.DrawImage(c,  new Rect(xC, yM, centerDW, centerDH));
                if (r  != null && rightW   > 0 && centerDH> 0) dc.DrawImage(r,  new Rect(xR, yM, rightW,   centerDH));
                if (bl != null && leftW    > 0 && bottomH > 0) dc.DrawImage(bl, new Rect(xL, yB, leftW,    bottomH));
                if (b  != null && centerDW > 0 && bottomH > 0) dc.DrawImage(b,  new Rect(xC, yB, centerDW, bottomH));
                if (br != null && rightW   > 0 && bottomH > 0) dc.DrawImage(br, new Rect(xR, yB, rightW,   bottomH));
            });
        }

        private static BitmapSource TryCrop(BitmapSource src, int x, int y, int w, int h) {
            if (w <= 0 || h <= 0) return null;
            if (x < 0 || y < 0 || x + w > src.PixelWidth || y + h > src.PixelHeight) return null;
            try {
                var cropped = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
                if (cropped.CanFreeze) cropped.Freeze();
                return cropped;
            } catch {
                return null;
            }
        }

        public void PaintInto(DrawingContext dc, Size visualBounds) {
            foreach (var op in _ops) op(dc, visualBounds);
        }

        /// <summary>
        /// Return a copy of the current op list. Used by SplashVisual at
        /// SetContent time to cache the ops independently of the RB
        /// (which is about to be Clear()'d).
        /// </summary>
        public List<Action<DrawingContext, Size>> SnapshotOps() {
            return new List<Action<DrawingContext, Size>>(_ops);
        }
    }

    // -------- Device --------

    /// <summary>
    /// MS-RRSP2 Device class. In a real server this represents the actual
    /// rendering pipeline; for slice 1 we treat it as a passive object —
    /// Device messages target a <c>idObjectSubject</c> that is actually a
    /// RenderBuilder handle (the spec describes RenderBuilder messages
    /// being routed via Device in some flows). We resolve the target at
    /// dispatch time, so this class itself stays empty.
    /// </summary>
    internal sealed class SplashDevice : SplashGenericObject {
        public SplashDevice(uint handle, string className)
            : base(handle, SplashClassKind.Device, className) { }
    }

    // -------- Animation --------

    /// <summary>
    /// MS-RRSP2 Animation — a property animation that tweens a Visual's
    /// position / size / scale / rotation / colour / alpha over time.
    /// Created by AnimationManager_Build*Animation, referenced by a
    /// target visual handle.
    ///
    /// Slice 5 minimum: register the object so subsequent Animation
    /// methods (AddCallback / SetStopCommand / AddKeyframe / Play / Stop /
    /// SetKeyframeCount / SetKeyframeTime / etc.) dispatch correctly
    /// rather than getting auto-promoted to Visual by mistake.
    /// Actual playback (driving the target visual's property at frame
    /// rate) is a follow-up.
    /// </summary>
    internal sealed class SplashAnimation : SplashGenericObject {
        /// <summary>Kind of property this animation tweens (Position / Size / etc.).</summary>
        public AnimationKind Kind { get; }
        /// <summary>Target visual handle (the visual whose property changes).</summary>
        public uint TargetVisual { get; set; }
        /// <summary>Number of keyframes declared via SetKeyframeCount.</summary>
        public int KeyframeCount { get; set; }
        /// <summary>Stop command (per Animation_SetStopCommand).</summary>
        public int StopCommand { get; set; }
        /// <summary>True after Animation_Play, false after Animation_Stop.</summary>
        public bool Playing { get; set; }
        /// <summary>Number of times to repeat. 1 = play once, -1 = infinite.</summary>
        public int RepeatCount { get; set; } = 1;
        /// <summary>Whether the animation auto-stops after RepeatCount loops complete.</summary>
        public bool AutoStop { get; set; } = true;
        /// <summary>Handle of the animation to auto-play when this one completes
        /// (per Animation_AddCompletionLink, spec §17.1). Zero means none.</summary>
        public uint NextOnComplete { get; set; }

        /// <summary>
        /// Callback object handle (_objcb) and context (_ctxcb) registered
        /// via Animation_AddCallback (spec §17.23). On animation completion
        /// the client MUST send LocalAnimationCallback_OnComplete (spec §2.2.5.1)
        /// addressed to <see cref="CallbackCtx"/> with subject =
        /// <see cref="CallbackObj"/>. Without this, the WMC server-side state
        /// machine never advances past "waiting for fade-in to complete" so
        /// e.g. row titles never become visible, selection-highlight follow-ups
        /// never fire, and old-screen destroy hooks never run.
        /// </summary>
        public uint CallbackObj { get; set; }
        public uint CallbackCtx { get; set; }

        /// <summary>
        /// True after we've fired LocalAnimationCallback_OnComplete for
        /// the current Play cycle. Prevents firing twice when an
        /// auto-stop is followed by an explicit Animation_Stop on the
        /// same animation handle. Reset on the next Play.
        /// </summary>
        public bool OnCompleteFired { get; set; }

        /// <summary>Per-keyframe data. Indexed 0..KeyframeCount-1.</summary>
        public AnimationKeyframe[] Keyframes;

        /// <summary>
        /// Indices into <see cref="Keyframes"/> sorted by <see cref="AnimationKeyframe.TimeSec"/>
        /// ascending. Rebuilt at Play time. WMC frequently ships an
        /// out-of-order "start" keyframe at the END of the index array
        /// with t=0.0 — without sorting, our tick loop would mis-treat
        /// that as the animation's last frame, snap to its (usually 0)
        /// value, and stop immediately. Row titles, fade-in tiles, etc.
        /// stay invisible until the wrap-around hammer.
        /// </summary>
        public int[] SortedIndices;

        // Runtime playback state
        public double StartTimeMs;
        public int CompletedRepeats;

        public SplashAnimation(uint handle, AnimationKind kind, uint targetVisual)
            : base(handle, SplashClassKind.Animation, "Animation") {
            Kind = kind;
            TargetVisual = targetVisual;
        }

        /// <summary>Ensure the keyframes array exists and is at least <paramref name="idx"/>+1 long.</summary>
        public AnimationKeyframe[] EnsureKeyframeAt(int idx) {
            if (idx < 0) return Keyframes;
            int neededLen = idx + 1;
            if (Keyframes == null) Keyframes = new AnimationKeyframe[Math.Max(neededLen, 4)];
            else if (Keyframes.Length < neededLen) {
                var grown = new AnimationKeyframe[Math.Max(neededLen, Keyframes.Length * 2)];
                Array.Copy(Keyframes, grown, Keyframes.Length);
                Keyframes = grown;
            }
            return Keyframes;
        }

        public override void OnDestroyed() {
            Playing = false;
            Keyframes = null;
        }
    }

    /// <summary>
    /// Per-keyframe data. Each keyframe carries a time + a typed value
    /// (only one of vec3 / argb / fl / rot is meaningful depending on
    /// the parent animation's Kind) + an easing definition that
    /// describes the interpolation curve FROM this keyframe to the next.
    /// </summary>
    internal struct AnimationKeyframe {
        public float          TimeSec;     // wall-time at which this keyframe occurs
        public AnimationEasing Easing;     // curve type leaving this keyframe
        public float          EaseP1, EaseP2; // weight/handle or bezier handles
        // Value (only one used per animation Kind)
        public float          VecX, VecY, VecZ;        // Position/Size/Scale/Rotation axis
        public float          RotAngleDeg;             // Rotation angle (paired with VecXYZ axis)
        public uint           ArgbValue;               // Color animations
        public float          FloatValue;              // Alpha / DynamicFloat
        /// <summary>
        /// True if this keyframe was declared via Animation_SetDynamicXxx
        /// (msgid 0x0A/0x0D/0x0E/0x11/0x13). The value is sampled from
        /// the target visual at Animation_Play time, then optionally
        /// multiplied or added per the wire's <c>fMultiply</c> flag.
        /// </summary>
        public bool           IsDynamic;
        /// <summary>
        /// <c>fMultiply</c> field from the Dynamic setter. Spec says:
        /// "Indicates whether the values can be multiplied or added".
        /// Empirically: 0 = additive (sampled value + nothing extra),
        /// non-zero = multiplicative (sampled value * fMultiply). We
        /// preserve the raw u32 so the playback engine can decide.
        /// </summary>
        public uint           DynamicMultiply;
    }

    internal enum AnimationEasing {
        Linear = 0,
        EaseOut,
        EaseIn,
        Bezier,
        Cosine,
        Sine,
        SCurve,
        Logarithmic,
        Exponential,
    }

    internal enum AnimationKind {
        Unknown,
        GradientColorMask,
        GradientOffset,
        Rotation,
        Size,
        Scale,
        Position,
        Color,
        Alpha,
    }

    // -------- DataBuffer --------

    /// <summary>
    /// Holds raw bytes pushed by the server on a <c>BufferKind.DataBuffer</c>
    /// (i.e. a <c>BufferInfo</c> with <c>idBuffer != 0</c>, per spec
    /// section 2.2.3.1). MS-RRSP2 implicitly creates a DataBuffer
    /// instance keyed by that <c>idBuffer</c>; subsequent messages —
    /// notably <c>Rasterizer_LoadRawImage</c> — reference it by handle
    /// to fetch the raw pixel bytes.
    /// </summary>
    internal sealed class SplashDataBuffer : SplashGenericObject {
        public byte[] Bytes { get; }
        public SplashDataBuffer(uint handle, byte[] bytes)
            : base(handle, SplashClassKind.DataBuffer, "DataBuffer") {
            Bytes = bytes ?? new byte[0];
        }
    }

    // -------- SurfacePool --------

    /// <summary>
    /// MS-RRSP2 SurfacePool — a managed memory pool that owns one or more
    /// Surfaces. In WPF land we don't share backing memory between
    /// surfaces (each one gets its own <see cref="WriteableBitmap"/>), so
    /// the pool is mostly metadata: the size + pixel-format options
    /// declared via <c>SurfacePool_Allocate</c>, plus a list of child
    /// Surface handles for cleanup on <c>SurfacePool_Free</c>.
    /// </summary>
    internal sealed class SplashSurfacePool : SplashGenericObject {
        public int   AllocatedWidth  { get; set; }
        public int   AllocatedHeight { get; set; }
        public uint  Format          { get; set; } // matches MS-RRSP2 nOptions code
        public Color EmptyColor      { get; set; } = Colors.Transparent;
        public int   Priority        { get; set; }

        public readonly List<uint> SurfaceHandles = new List<uint>();

        public SplashSurfacePool(uint handle, string className)
            : base(handle, SplashClassKind.SurfacePool, className) { }
    }

    // -------- Surface --------

    /// <summary>
    /// MS-RRSP2 Surface — a rectangular region within a SurfacePool that
    /// holds rasterised pixel content. We back it with a
    /// <see cref="WriteableBitmap"/> lazily created on the first
    /// <c>Rasterizer_LoadRawImage</c> hit, sized to the image's actual
    /// pixel dimensions. <c>Surface_Draw</c> then references the bitmap
    /// when appending a textured-rect op to a RenderBuilder.
    /// </summary>
    internal sealed class SplashSurface : SplashGenericObject {
        public uint            PoolHandle    { get; set; }
        public WriteableBitmap Bitmap        { get; set; }
        public int             Width         { get; set; }
        public int             Height        { get; set; }
        public int             Stride        { get; set; }
        public uint            Format        { get; set; }
        public bool            ContentValid  { get; set; }

        public SplashSurface(uint handle, string className)
            : base(handle, SplashClassKind.Surface, className) { }

        public override void OnDestroyed() {
            Bitmap = null; // let WPF release the unmanaged backing store
        }
    }

    // -------- Gradient --------

    /// <summary>
    /// MS-RRSP2 Gradient (spec §2.2.4.15). A gradient is a 1-D ramp of
    /// (value, position) stops applied as a soft-fade alpha mask. The
    /// only built-in clipping mechanism the spec mentions ("soft fade
    /// clipping" — §7242, §8903).
    ///
    /// Lifecycle on the wire:
    /// <list type="bullet">
    ///   <item><c>Gradient_Clear</c> wipes the stop list.</item>
    ///   <item><c>Gradient_AddValue</c> appends a <c>(val, pos, relative)</c>
    ///         triple. <c>relative</c> picks the reference frame for
    ///         <c>pos</c>: 0 = visual's logical-rect min, 1 = max,
    ///         2/3 = mesh extents, 4 = global.</item>
    ///   <item><c>Gradient_SetOrientation</c> sets horizontal/vertical.</item>
    ///   <item><c>Gradient_SetOffset</c> / <c>Gradient_SetColorMask</c> tweak
    ///         the ramp.</item>
    ///   <item><c>Gradient_Draw rb=R</c> queues this gradient onto a
    ///         RenderBuilder; the next <c>Visual_SetContent</c> that
    ///         consumes R picks the queued gradient(s) up as its
    ///         <c>OpacityMask</c>.</item>
    /// </list>
    /// </summary>
    internal sealed class SplashGradient : SplashGenericObject {
        public enum Orientation { Horizontal = 0, Vertical = 1 }
        public struct Stop { public float Value, Position; public int Relative; }

        public Orientation Direction = Orientation.Horizontal;
        public float       Offset    = 0f;
        public uint        ColorMask = 0xFFFFFFFFu; // ARGB; default opaque white
        public readonly List<Stop> Stops = new List<Stop>();

        public SplashGradient(uint handle, string className)
            : base(handle, SplashClassKind.Gradient, className) { }

        public void Clear() => Stops.Clear();
        public void AddStop(float val, float pos, int rel) {
            Stops.Add(new Stop { Value = val, Position = pos, Relative = rel });
        }

        /// <summary>
        /// Build a WPF <see cref="System.Windows.Media.Brush"/> suitable for
        /// use as a <c>Visual.OpacityMask</c>. The brush is fit into the
        /// visual's local bounds: stops with <c>relative=0</c> are pixel
        /// offsets from the min corner; <c>relative=1</c> are offsets from
        /// the max corner (typically negative); <c>relative=4</c> (global)
        /// is treated like <c>relative=0</c> for now — we don't have a
        /// global→local transform handy. The brush uses
        /// <c>MappingMode.Absolute</c> so positions are interpreted as
        /// pixels in the visual's local space.
        /// </summary>
        public Brush BuildOpacityMask(double visualWidth, double visualHeight) {
            if (Stops.Count == 0) return null;
            if (visualWidth <= 0 || visualHeight <= 0) return null;

            // Map each stop to an absolute pixel coordinate along the
            // chosen axis. We sort by that coordinate so WPF's gradient
            // is monotone (LinearGradientBrush requires offsets in [0,1]
            // but we'll renormalize after computing the absolute range).
            double axisLen = Direction == Orientation.Horizontal ? visualWidth : visualHeight;
            var coords = new (double Pixel, float Value)[Stops.Count];
            for (int i = 0; i < Stops.Count; i++) {
                var s = Stops[i];
                double px;
                switch (s.Relative) {
                    case 1:  px = axisLen + s.Position; break;   // from max corner
                    case 4:  // global — best-effort treat as local-min
                    case 0:
                    default: px = s.Position; break;             // from min corner
                }
                coords[i] = (px + Offset, s.Value);
            }
            // Sort by pixel position so the gradient is monotone.
            System.Array.Sort(coords, (a, b) => a.Pixel.CompareTo(b.Pixel));

            // Skip-rule for "overlay-effect" gradients (no clipping):
            // every stop whose pixel position falls INSIDE the visual's
            // own bounds is val=0. Wire pattern from WMC's action panels
            // (e.g. delete / play buttons): val=1 at x=-20, val=0 at x=20,
            // val=0 at x=W-20, val=1 at x=W+20 — a U-shape with the
            // value-1 anchors *outside* the visual. The two stops inside
            // the visual (x=20 and x=W-20) are both 0, so the gradient
            // is effectively defining an "all hidden inside" mask that,
            // applied as an OpacityMask, makes the visual's middle
            // disappear — but real WMC clearly renders these buttons
            // fully visible, so the gradient must NOT be functioning as
            // a clip mask here. Treating "all inside-stops are 0" as
            // "no clip" preserves real strip-mask gradients (like the
            // home-screen tile window which has val=1 stops inside the
            // visual) and lets these decorative U-shape gradients pass
            // through without hiding the content.
            bool hasInsideStop = false;
            bool allInsideStopsZero = true;
            foreach (var c in coords) {
                if (c.Pixel >= -0.5 && c.Pixel <= axisLen + 0.5) {
                    hasInsideStop = true;
                    if (c.Value > 0.01f) { allInsideStopsZero = false; break; }
                }
            }
            if (hasInsideStop && allInsideStopsZero) {
                return null; // skip — no clip mask, content stays fully visible
            }

            // Collapse the absolute range to a [0,1] gradient parameter,
            // then build StartPoint/EndPoint at the absolute pixel coords
            // (Absolute mapping mode). If all stops coincide, the brush
            // would be degenerate — fall back to a flat mask at that value.
            double minPx = coords[0].Pixel;
            double maxPx = coords[coords.Length - 1].Pixel;
            if (Math.Abs(maxPx - minPx) < 0.001) {
                byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(coords[0].Value * 255)));
                var solid = new SolidColorBrush(Color.FromArgb(a, 0xFF, 0xFF, 0xFF));
                solid.Freeze();
                return solid;
            }

            var stopCollection = new GradientStopCollection();
            foreach (var c in coords) {
                double t = (c.Pixel - minPx) / (maxPx - minPx);
                byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(c.Value * 255)));
                stopCollection.Add(new GradientStop(Color.FromArgb(a, 0xFF, 0xFF, 0xFF), t));
            }
            stopCollection.Freeze();

            Point start, end;
            if (Direction == Orientation.Horizontal) {
                start = new Point(minPx, visualHeight * 0.5);
                end   = new Point(maxPx, visualHeight * 0.5);
            } else {
                start = new Point(visualWidth * 0.5, minPx);
                end   = new Point(visualWidth * 0.5, maxPx);
            }
            var brush = new LinearGradientBrush(stopCollection, start, end) {
                MappingMode = BrushMappingMode.Absolute,
                // Hold the end values past the explicit stops so a fade
                // from 0..1 over a small range stays at 1 across the rest
                // of the visual (otherwise WPF tiles by default).
                SpreadMethod = GradientSpreadMethod.Pad,
            };
            brush.Freeze();
            return brush;
        }
    }

    // -------- WaitCursor --------

    /// <summary>
    /// MS-RRSP2 WaitCursor (spec §2.2.4.8). Tracks the visuals that
    /// compose the spinner plus the animation handles to play on
    /// show / hide. The MCE shell creates one early, hides its visuals
    /// by default, then sends <c>WaitCursor_Show</c> when a navigation
    /// blocks long enough to warrant the spinner and
    /// <c>WaitCursor_Hide</c> when the destination page is ready.
    ///
    /// We hold raw handle arrays rather than resolved object refs —
    /// the visuals/animations are created in the same batch and may not
    /// be in the registry yet when <c>SetVisuals</c> / <c>Set*Animations</c>
    /// arrive. Resolution happens lazily in <c>Show</c> / <c>Hide</c>.
    /// </summary>
    internal sealed class SplashWaitCursor : SplashGenericObject {
        public uint[] Visuals          = System.Array.Empty<uint>();
        public uint[] ShowAnimations   = System.Array.Empty<uint>();
        public uint[] HideAnimations   = System.Array.Empty<uint>();

        public SplashWaitCursor(uint handle, string className)
            : base(handle, SplashClassKind.WaitCursor, className) { }
    }
}
