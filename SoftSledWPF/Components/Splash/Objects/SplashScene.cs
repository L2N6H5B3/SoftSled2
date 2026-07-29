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

        // The SplashRenderHost reference is no longer wired here —
        // background paint is owned by the layered Rectangle outside
        // splashHost (see ExtenderSessionControl.xaml). This class
        // just records the colour as state and lets the controller's
        // background-color sink push it to the actual paint surface.
        private readonly SplashRenderHost _host;

        public Color BackgroundColor { get; private set; } = Colors.Transparent;
        public uint  ContentVisualHandle { get; set; } = 0;

        public SplashWindow(uint handle, string className, SplashRenderHost host)
            : base(handle, SplashClassKind.Window, className) {
            _host = host;
        }

        public void SetBackgroundColor(Color c) {
            // Record state only. The wire dispatch in
            // SplashController.DispatchWindow forwards Window_SetBackgroundColor
            // (msgid=0) to the background-color sink directly — keeping
            // the actual paint side-effect out of SplashWindow lets us
            // composite background ↓ video ↓ scene-graph cleanly in the
            // outer Grid.
            BackgroundColor = c;
        }

        public override void OnDestroyed() {
            // Window destruction doesn't reset the host background — the
            // HostWindow background sticks until a subsequent
            // HostWindow_SetBackgroundColor explicitly overrides it.
            // (The previous "reset to transparent" behaviour was a
            // side-effect of painting through SplashRenderHost; the
            // layered model leaves this to whoever owns the sink.)
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
        // Default Size to (1,1,1) — the original behaviour. The earlier
        // experimental change to (0,0,0) was paired with default
        // clip-to-bounds; both were reverted. (1,1,1) means visuals that
        // never receive an explicit SetSize still have a unit logical
        // size, which is harmless for the bit-5-only clip path that
        // requires both an opt-in bit and a non-zero size.
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

        // Handle of the gradient currently providing this visual's
        // OpacityMask, or 0 if no gradient is applied. Tracked so the
        // controller can rebuild the WPF brush when the gradient's
        // Offset or ColorMask changes (e.g. an Animation[GradientOffset]
        // sweep, the WMC selection-highlight pattern) without having
        // to re-execute the whole SetContent path. Cleared by
        // SetContentFromRenderBuilder when no gradient is pending.
        public uint AppliedGradientHandle;

        // MS-RRSP2 Visual_SetLayer (spec §2.2.4.6.6). Layer is a z-order
        // override applied amongst the visual's siblings: lower Layer
        // paints first (appears behind), higher Layer paints later
        // (appears in front). WMC ships this 582+ times per session
        // mostly with values 0 or 1 (back/front toggles) and a long tail
        // up to ~99 for stacking depth. Sibling order from
        // Visual_ChangeParent's nOrder is the default; SetLayer
        // overrides it. Applied by re-ordering this visual's index in
        // its parent's DrawingVisual.Children via ApplyLayer().
        public uint Layer;

        // Cached per-visual transforms — reused across ApplyTransform
        // calls instead of allocating fresh ones each time. WPF Transform
        // objects are mutable while unfrozen; we just update properties
        // in-place so the WPF compositor picks up the change without
        // garbage churn. SetPosition / SetSize / SetScale / SetRotation
        // each call ApplyTransform, often many times per frame during
        // animation — for a typical WMC scene that's thousands of
        // TransformGroup + ScaleTransform + RotateTransform + TranslateTransform
        // allocations per frame avoided.
        private TranslateTransform _translate;
        private ScaleTransform     _scale;
        private RotateTransform    _rotate;
        private TransformGroup     _group;

        public SplashVisual(uint handle, string className, SplashRenderHost host)
            : base(handle, SplashClassKind.Visual, className) {
            _host = host;
            _translate = new TranslateTransform(0, 0);
            DrawingVisual.Transform = _translate;
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
                // Insert into BOTH lists at the SAME index so they stay
                // in lockstep. Previous code Add()'d to newParent.Children
                // (always appending) but Insert()'d into siblings at a
                // computed index — for order=Bottom/Before/Behind the two
                // lists end up referring to different visuals at the same
                // index. Anything that later cross-correlates the lists
                // (Visual_SetLayer / ApplyLayer, the VTREE diagnostic
                // dump) trips on the divergence with "Specified Visual
                // is already a child of another Visual" or "index out
                // of range" errors.
                if (index < 0 || index >= siblings.Count) {
                    newParent.Children.Add(this);
                    siblings.Add(DrawingVisual);
                } else {
                    newParent.Children.Insert(index, this);
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
            //
            // Perf path: the common case (~90% of WMC visuals) is
            // translate-only — no scale, no rotation. Hot path mutates a
            // single TranslateTransform in place to avoid TransformGroup
            // + child allocations on every SetPosition tick. The full
            // case (scale and/or rotation) falls through to a TransformGroup
            // that's also cached and mutated in place.
            bool wantsScale = ScaleX != 1f || ScaleY != 1f;
            bool wantsRotation = RotAngleDeg != 0f && Math.Abs(RotAxisZ) > 0.0001f;

            if (!wantsScale && !wantsRotation) {
                // Translate-only fast path. Reuse the cached TranslateTransform.
                _translate.X = PosX;
                _translate.Y = PosY;
                if (DrawingVisual.Transform != _translate) {
                    DrawingVisual.Transform = _translate;
                }
                return;
            }

            // Full path: scale and/or rotation. Reuse a cached
            // TransformGroup populated with whichever children are needed.
            double cx = SizeX * CenterScaleX + CenterOffsetX;
            double cy = SizeY * CenterScaleY + CenterOffsetY;

            if (_group == null) _group = new TransformGroup();
            // We rebuild the group's child list when the SHAPE of the
            // transform (scale on/off, rotation on/off) changes; otherwise
            // we just mutate each cached transform's properties.
            int desiredChildCount = (wantsScale ? 1 : 0) + (wantsRotation ? 1 : 0) + 1; // +1 for translate
            bool shapeChanged = _group.Children.Count != desiredChildCount;

            if (wantsScale) {
                if (_scale == null) { _scale = new ScaleTransform(); shapeChanged = true; }
                _scale.ScaleX  = ScaleX;
                _scale.ScaleY  = ScaleY;
                _scale.CenterX = cx;
                _scale.CenterY = cy;
            }
            if (wantsRotation) {
                if (_rotate == null) { _rotate = new RotateTransform(); shapeChanged = true; }
                _rotate.Angle   = RotAngleDeg;
                _rotate.CenterX = cx;
                _rotate.CenterY = cy;
            }
            _translate.X = PosX;
            _translate.Y = PosY;

            if (shapeChanged) {
                _group.Children.Clear();
                if (wantsScale)    _group.Children.Add(_scale);
                if (wantsRotation) _group.Children.Add(_rotate);
                _group.Children.Add(_translate);
            }

            if (DrawingVisual.Transform != _group) {
                DrawingVisual.Transform = _group;
            }
        }

        public void ApplyAlpha() {
            // WPF DrawingVisual has no separate Visibility flag, so
            // opacity multiplexes Visible, AlphaByte and the
            // DataBits-driven hide rule.
            DrawingVisual.Opacity = ComputeEffectiveOpacity();
        }

        /// <summary>
        /// Re-position this visual amongst its parent's children to honour
        /// the current <see cref="Layer"/> value. Spec §2.2.4.6.6 — lower
        /// Layer = back, higher = front. WPF VisualCollection paints
        /// index 0 first (behind) and index Count-1 last (in front), so
        /// we sort the parent's children by Layer ascending and re-insert
        /// at the position matching this visual's rank.
        ///
        /// <para>Stability: visuals with the same Layer keep their
        /// existing relative order (the order set by Visual_ChangeParent's
        /// nOrder is the default, and SetLayer with ties leaves it
        /// alone).</para>
        ///
        /// <para>Cost: O(n) where n is the parent's child count, which is
        /// usually small (one menu strip's worth of tiles). Acceptable
        /// for the ~582 SetLayer fires per session we observe.</para>
        ///
        /// <para>No-op if this visual is unparented (still on the host
        /// root); the host root has no notion of Layer-based z-order.</para>
        /// </summary>
        public void ApplyLayer() {
            if (Parent == null) return;
            var sibList   = Parent.Children;
            var sibVisual = Parent.DrawingVisual.Children;
            int n = sibList.Count;
            if (n <= 1) return;

            // Use the WPF VisualCollection as the source of truth for
            // the current index — that's the collection ApplyLayer will
            // physically mutate, so any lockstep drift between the two
            // lists must be reconciled here.
            int currentIdx = sibVisual.IndexOf(this.DrawingVisual);
            if (currentIdx < 0) return;

            // Sanity: if the SplashVisual bookkeeping list disagrees about
            // where this visual sits, the two lists got out of lockstep
            // (historically this happened in ChangeParent with
            // order=Bottom/Before/Behind). Bail out — re-sorting would
            // misalign the lists further. ChangeParent now keeps them in
            // lockstep so this should never fire post-fix.
            int currentIdxList = sibList.IndexOf(this);
            if (currentIdxList != currentIdx) return;

            // Compute this visual's target index by counting how many
            // siblings have a strictly-lower Layer (those paint before us)
            // plus a stable tie-breaker for siblings with the same Layer
            // that already sit before us in the current order.
            int targetIdx = 0;
            for (int i = 0; i < n; i++) {
                if (i == currentIdx) continue;
                var s = sibList[i];
                if (s.Layer < Layer || (s.Layer == Layer && i < currentIdx)) {
                    targetIdx++;
                }
            }
            if (targetIdx == currentIdx) return;

            // Move both lists in lockstep. Remove-then-insert is the only
            // mutation pattern VisualCollection supports for re-ordering.
            sibList.RemoveAt(currentIdx);
            sibVisual.RemoveAt(currentIdx);
            sibList.Insert(targetIdx, this);
            sibVisual.Insert(targetIdx, this.DrawingVisual);
        }

        /// <summary>
        /// Update <see cref="DrawingVisual.Clip"/> to a rectangle matching
        /// the visual's current Size — but only for visuals WMC explicitly
        /// flagged as "clip my children" via DataBits bit 5 (0x20).
        ///
        /// <para>Background: MS-RRSP2 has no explicit SetClip message; the
        /// spec (§2.2.4.6.2) just calls the data-bits "user-defined".
        /// Empirically, only a few rare bit patterns set bit 5 (0x30, 0x60,
        /// 0x70, ...), and those correlate with cases where a hard clip
        /// is wanted. Most clipping in WMC is delegated to its
        /// gradient soft-fade mechanism (§2.2.4.15) rather than hard
        /// rectangle clip.</para>
        ///
        /// <para>Previous attempts to clip more aggressively — every sized
        /// visual, or every visual with a giant child — broke intentional
        /// cross-bound overflow (page background fills, home-row icons
        /// positioned past the viewport). The correct mechanism for hiding
        /// items past container bounds is the gradient OpacityMask path,
        /// not a hard clip.</para>
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

        /// <summary>
        /// No-op placeholder kept so the SplashController dispatch sites
        /// (Visual_SetSize, Visual_ChangeParent) compile. Earlier passes
        /// experimented with a "viewport clip" heuristic (clip when a
        /// child's Size dramatically exceeds the parent's Size); it caused
        /// regressions on the home-row icons and Recorded TV columns
        /// without fixing popup row escape, so it was reverted. The real
        /// containment mechanism is the wire-supplied gradient OpacityMask.
        /// </summary>
        public void EvaluateViewportClip() {
            // Intentionally empty — see method-level comment.
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
        /// <summary>
        /// True when WMC cached this subtree off via ChangeDataBits with a
        /// mask covering BOTH bits 0 and 2 (0x05) and a value clearing
        /// them. Wire evidence (run 091349): page-instance containers get
        /// `value=0x05 mask=0x05` on activation and exactly one
        /// `value=0x00 mask=0x05` when the page is navigated away —
        /// WITH NO SetVisible/SetAlpha — and the (atlas-era, page-caching)
        /// WMC expects the renderer to stop showing the subtree. Only 26
        /// such deactivations per session, each on a distinct page/instance
        /// container; focus-only changes (mask=0x01) do NOT match, so the
        /// old over-broad bit-pattern hide rules' false positives
        /// (recorded-TV thumbnails etc.) are structurally excluded.
        /// Cleared again if WMC reactivates the cached page
        /// (value=0x05 mask=0x05).
        /// </summary>
        public bool DataBitsDeactivated { get; private set; }

        public double ComputeEffectiveOpacity() {
            if (!Visible) return 0.0;
            // Page-cache hide rule — see DataBitsDeactivated. Opacity on
            // the container hides the whole cached subtree in WPF.
            if (DataBitsDeactivated) return 0.0;
            // The tint colour's alpha channel (Visual_SetColor high byte)
            // multiplies with the explicit SetAlpha byte. Default Color is
            // opaque White so untinted visuals are unaffected.
            return (AlphaByte / 255.0) * (Color.A / 255.0);
        }

        /// <summary>Update the user-data bits with a value/mask pair
        /// (matching Visual_ChangeDataBits semantics) and re-apply the
        /// effective opacity + the bounds clip (bit 5 controls the clip).</summary>
        public void ApplyDataBits(uint value, uint mask) {
            DataBits = (DataBits & ~mask) | (value & mask);
            // Page-cache activation/deactivation: only a change whose mask
            // covers BOTH bits 0 and 2 counts (see DataBitsDeactivated).
            if ((mask & 0x05u) == 0x05u) {
                DataBitsDeactivated = (DataBits & 0x05u) == 0;
            }
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
        // The third op parameter is the visual's tint Color (spec
        // §2.2.4.6.4 Visual_SetColor) — White = untinted fast path.
        private List<Action<DrawingContext, Size, Color>> _cachedOps;

        // True when at least one cached op resolves its destination from
        // the visual's Size at paint time (a stretch-to-visual op). ONLY
        // such content changes when Size changes; fixed-rect / fractional
        // content renders byte-for-byte identically regardless of Size, so
        // a Size animation on a visual whose content is size-independent
        // can skip the (expensive) per-frame RenderOpen re-rasterization
        // entirely. Set from the RenderBuilder at SetContent time.
        public bool ContentDependsOnSize { get; private set; }

        // Size at which _cachedOps were last painted. Lets RepaintContent
        // coalesce redundant re-rasterizations when an animation ticks the
        // Size to the same value twice (WMC often over-sends). NaN until
        // the first paint.
        private double _lastPaintW = double.NaN, _lastPaintH = double.NaN;

        public void SetContentFromRenderBuilder(SplashRenderBuilder rb,
                                                System.Collections.Generic.IReadOnlyList<SplashGradient> pendingGradients = null) {
            ContentRenderBuilderHandle = rb?.Handle ?? 0;
            // Snapshot the RB's ops so RepaintContent can run later with
            // a new Size after the RB has been Clear()'d / re-filled.
            _cachedOps = rb?.SnapshotOps();
            // Capture whether any op depends on the visual's Size BEFORE
            // rb.Clear() (below) resets the flag on the shared builder.
            ContentDependsOnSize = rb?.HasStretchToVisualOp ?? false;
            // Force the coalescing guard to repaint on the next size-driven
            // call — content just changed, so the last-painted size is stale.
            _lastPaintW = double.NaN; _lastPaintH = double.NaN;
            RepaintContent();
            // Apply any gradients that were queued onto this RB via
            // Gradient_Draw (spec §2.2.4.15) as the visual's OpacityMask.
            // NEXT-BOUND model: WMC ships the gradient after the
            // content-bearing SetContent and before this (sibling)
            // SetContent. Empirically this binding model produces
            // correct results for the home-row tile-window masks and
            // most strip-edge fades; raised popup row containers
            // (Synopsis/Actions/Other Showings) are a known mis-clip
            // and are tracked separately.
            if (pendingGradients != null && pendingGradients.Count > 0) {
                var last = pendingGradients[pendingGradients.Count - 1];
                // AppliedGradientHandle tracks the LAST gradient so the
                // Gradient_SetOffset / offset-animation rebuild path
                // (RebuildMasksForGradient) can still find this visual.
                // Combined-mask visuals are viewport edge-clips, which WMC
                // does not offset-animate, so tracking only the last is
                // sufficient in practice.
                AppliedGradientHandle = last?.Handle ?? 0;
                // Combine ALL queued gradients (multiplicative) rather than
                // applying only the last: WMC ships a scroll viewport's clip
                // as a left+right (or top+bottom) edge-fade PAIR on one RB,
                // and WPF allows one OpacityMask per visual. Single-gradient
                // binds take the original BuildOpacityMask path unchanged.
                DrawingVisual.OpacityMask = SplashGradient.BuildCombinedMask(pendingGradients, SizeX, SizeY);
            } else {
                // No queued gradients = no mask. Clearing is important so
                // a previously masked visual that gets re-bound without
                // gradients doesn't retain the stale mask.
                AppliedGradientHandle = 0;
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
            // The spec (2.2.4.6.13) just says "transfers", which is
            // consistent with move-and-clear; the wire empirically confirms
            // it. Reviewed all 8 published spec revisions (2013–2017) and
            // the wording is unchanged across the entire history.
            rb?.Clear();
        }

        /// <summary>
        /// Re-paint the cached RenderBuilder ops onto this visual using
        /// the current Size. No-op if no content has ever been bound.
        /// </summary>
        /// <summary>
        /// Re-paint only if the content actually tracks Size and the Size
        /// has changed since the last paint. Called from the Size-animation
        /// tick, this skips the per-frame RenderOpen for the common case
        /// (size-independent content) and coalesces duplicate sizes —
        /// turning a Size sweep over fixed-rect content from N re-rasters
        /// into zero. Content whose destination stretches to the visual
        /// still repaints exactly when the size moves.
        /// </summary>
        public void RepaintContentIfSizeAffected() {
            if (!ContentDependsOnSize) return;
            double w = SizeX > 0 ? SizeX : 0, h = SizeY > 0 ? SizeY : 0;
            if (w == _lastPaintW && h == _lastPaintH) return;
            RepaintContent();
        }

        public void RepaintContent() {
            if (_cachedOps == null) return;
            var bounds = new Size(SizeX > 0 ? SizeX : 0, SizeY > 0 ? SizeY : 0);
            _lastPaintW = bounds.Width; _lastPaintH = bounds.Height;
            // Tint (Visual_SetColor, spec §2.2.4.6.4): the RGB channels
            // modulate the painted content (vector op colors multiply;
            // images route through a cached multiplied bitmap). The ALPHA
            // channel is NOT applied here — it folds into the visual's
            // effective opacity (ComputeEffectiveOpacity) so it composes
            // with SetAlpha the way a compositing renderer would.
            var tint = Color.FromRgb(Color.R, Color.G, Color.B);
            using (var dc = DrawingVisual.RenderOpen()) {
                for (int i = 0; i < _cachedOps.Count; i++) {
                    _cachedOps[i](dc, bounds, tint);
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

        // Op signature: (DrawingContext, visualBounds, tint). The tint is
        // the consuming visual's Color RGB (spec §2.2.4.6.4) supplied at
        // paint time by SplashVisual.RepaintContent — White = untinted.
        private readonly List<Action<DrawingContext, Size, Color>> _ops = new List<Action<DrawingContext, Size, Color>>();

        /// <summary>
        /// True once any op added to this builder resolves its destination
        /// from the consuming visual's Size at paint time (the WMC
        /// <c>dst=(0,0,-1,-1)</c> stretch-to-visual sentinel). The visual
        /// captures this at SetContent time as
        /// <see cref="SplashVisual.ContentDependsOnSize"/> so a Size
        /// animation can skip re-rasterizing size-independent content.
        /// Reset by <see cref="Clear"/> alongside the ops.
        /// </summary>
        public bool HasStretchToVisualOp { get; private set; }

        /// <summary>
        /// Gradient handles queued via <c>Gradient_Draw / Gradient_Push rb=this</c>.
        /// Drained by the next <c>Visual_SetContent</c> that consumes this RB
        /// and applied as that visual's OpacityMask (NEXT-BOUND model).
        ///
        /// NEXT-BOUND works correctly for home-row tile-window masks and
        /// the EPG right-side bounding gradient — both cases where the
        /// "next" visual is the container whose subtree needs masking.
        /// Raised popup row containers (Synopsis/Actions/Other Showings)
        /// are a known mis-target: the gradient is sized for the visual
        /// that came BEFORE Gradient_Draw, so the next-bound empty
        /// sibling gets a mask that has no effect, leaving the popup row
        /// items un-clipped. Tracked separately; a naive "apply to both"
        /// workaround broke the home strip and EPG so was reverted.
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
            HasStretchToVisualOp = false;
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

        // ---- Shared frozen-brush / frozen-pen cache ----
        // WMC reuses the same handful of fill/outline colors across
        // thousands of draw ops per scene (white text, off-white panel
        // chrome, transparent black scrim, etc.). Caching frozen brushes
        // keyed by ARGB cuts down on per-op allocations and gives WPF's
        // resource dedup a much better shot at sharing the underlying
        // composition resources too.
        //
        // Keyed by packed ARGB (uint) so we don't pay for Color.GetHashCode
        // every lookup. Pen cache is keyed by (ARGB, thickness*256) packed
        // into a long; thickness is rounded to 1/256 because WMC passes
        // wire-float thicknesses that are virtually always exact integers
        // or simple fractions in practice.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SolidColorBrush> s_brushCache
            = new System.Collections.Concurrent.ConcurrentDictionary<uint, SolidColorBrush>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Pen> s_penCache
            = new System.Collections.Concurrent.ConcurrentDictionary<long, Pen>();

        private static SolidColorBrush GetBrush(Color color) {
            uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            if (s_brushCache.TryGetValue(key, out var b)) return b;
            var fresh = new SolidColorBrush(color);
            fresh.Freeze();
            // Best-effort cache add; the ConcurrentDictionary handles the
            // race if two threads create the same key in parallel and one
            // loses — both candidates are equivalent.
            s_brushCache.TryAdd(key, fresh);
            return fresh;
        }

        private static Pen GetPen(Color color, double thickness) {
            uint argbKey = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            // Quantise thickness to 1/256 (0.00390625) to cap cache size.
            int thickKey = (int)Math.Round(thickness * 256.0);
            long key = ((long)argbKey << 32) | (uint)thickKey;
            if (s_penCache.TryGetValue(key, out var p)) return p;
            var fresh = new Pen(GetBrush(color), thickness);
            fresh.Freeze();
            s_penCache.TryAdd(key, fresh);
            return fresh;
        }

        // ---- Tint support (Visual_SetColor, spec §2.2.4.6.4) ----
        // The tint RGB modulates painted content per-channel. White is the
        // identity and short-circuits everywhere, so untinted visuals (the
        // 100% case in current captures — zero SetColor traffic observed)
        // pay only a three-byte comparison per op.

        private static bool IsWhite(Color tint) => tint.R == 255 && tint.G == 255 && tint.B == 255;

        /// <summary>Per-channel multiply of a draw colour by the visual tint.</summary>
        private static Color Modulate(Color c, Color tint) {
            if (IsWhite(tint)) return c;
            return Color.FromArgb(c.A,
                (byte)(c.R * tint.R / 255),
                (byte)(c.G * tint.G / 255),
                (byte)(c.B * tint.B / 255));
        }

        // Tinted-bitmap cache: keyed by source bitmap (weak — dies with the
        // bitmap) then by tint RGB. Tint changes are rare (driven by
        // SetColor / Color animations), so per-(bitmap,tint) one-off pixel
        // passes are acceptable; steady-state repaints hit the cache.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Dictionary<uint, BitmapSource>>
            s_tintCache = new System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Dictionary<uint, BitmapSource>>();

        // Upper bound on distinct tints cached per source bitmap. A Color
        // animation drives a DIFFERENT tint every frame, so an uncapped
        // cache would insert (and hold, via the ConditionalWeakTable, for
        // the life of the source) a full-image copy per animation frame —
        // both a per-frame allocation storm and an unbounded leak over a
        // sweep. Real static usage needs only a handful of distinct tints
        // (focus/selected/dimmed states), so a small cap keeps the steady
        // state fully cached while a live animation simply computes-and-
        // discards past the cap instead of accumulating garbage.
        private const int TintCachePerSourceCap = 8;

        /// <summary>Return <paramref name="src"/> multiplied by the tint RGB
        /// (cached), or the source itself for a White tint.</summary>
        private static BitmapSource TintedIfNeeded(BitmapSource src, Color tint) {
            if (src == null || IsWhite(tint)) return src;
            uint key = ((uint)tint.R << 16) | ((uint)tint.G << 8) | tint.B;
            var perSrc = s_tintCache.GetOrCreateValue(src);
            bool cacheFull;
            lock (perSrc) {
                if (perSrc.TryGetValue(key, out var cached)) return cached;
                cacheFull = perSrc.Count >= TintCachePerSourceCap;
            }
            try {
                BitmapSource bgra = src.Format == PixelFormats.Bgra32
                    ? src
                    : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                int stride = w * 4;
                byte[] px = new byte[h * stride];
                bgra.CopyPixels(px, stride, 0);
                for (int i = 0; i < px.Length; i += 4) {
                    px[i]     = (byte)(px[i]     * tint.B / 255);
                    px[i + 1] = (byte)(px[i + 1] * tint.G / 255);
                    px[i + 2] = (byte)(px[i + 2] * tint.R / 255);
                }
                var tinted = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
                tinted.Freeze();
                // Only cache when under the per-source cap. Past it (i.e.
                // mid color-animation) we return the freshly-tinted bitmap
                // without storing it — no accumulation, no leak.
                if (!cacheFull) {
                    lock (perSrc) {
                        if (perSrc.Count < TintCachePerSourceCap) perSrc[key] = tinted;
                    }
                }
                return tinted;
            } catch {
                return src; // any conversion hiccup → draw untinted rather than nothing
            }
        }

        public void AddSolid(Rect rect, Color color, bool stretchToVisual = false) {
            if (stretchToVisual) HasStretchToVisualOp = true;
            if (stretchToVisual) {
                _ops.Add((dc, b, tint) => {
                    if (b.Width > 0 && b.Height > 0)
                        dc.DrawRectangle(GetBrush(Modulate(color, tint)), null, new Rect(0, 0, b.Width, b.Height));
                });
            } else {
                _ops.Add((dc, _, tint) => dc.DrawRectangle(GetBrush(Modulate(color, tint)), null, rect));
            }
        }

        public void AddOutline(Rect rect, Color color, double thickness, bool stretchToVisual = false) {
            if (stretchToVisual) HasStretchToVisualOp = true;
            if (stretchToVisual) {
                _ops.Add((dc, b, tint) => {
                    if (b.Width > 0 && b.Height > 0)
                        dc.DrawRectangle(null, GetPen(Modulate(color, tint), thickness), new Rect(0, 0, b.Width, b.Height));
                });
            } else {
                _ops.Add((dc, _, tint) => dc.DrawRectangle(null, GetPen(Modulate(color, tint), thickness), rect));
            }
        }

        public void AddLine(Point a, Point b, Color color, double thickness) {
            _ops.Add((dc, _, tint) => dc.DrawLine(GetPen(Modulate(color, tint), thickness), a, b));
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
            if (stretchToVisual) HasStretchToVisualOp = true;
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
            _ops.Add((dc, visualBounds, tint) => {
                Rect dst = stretchToVisual && visualBounds.Width > 0 && visualBounds.Height > 0
                    ? new Rect(0, 0, visualBounds.Width, visualBounds.Height)
                    : dstRect;
                dc.DrawImage(TintedIfNeeded(toDraw, tint), dst);
            });
        }

        /// <summary>
        /// Append a pool-composition image op (SurfacePool_Draw,
        /// spec §2.2.4.12.1). The surface's sub-image is placed at a
        /// FRACTIONAL rectangle of the final destination — computed by the
        /// controller from the surface's pool-placement rect relative to
        /// the draw's pool-space source rect — so the op works whether the
        /// destination is explicit or the (0,0,-1,-1) stretch-to-visual
        /// sentinel resolved at paint time.
        /// </summary>
        public void AddImageMapped(BitmapSource src, Rect srcSubRect, Rect dstFraction,
                                   Rect explicitDst, bool stretchToVisual) {
            if (src == null) return;
            if (stretchToVisual) HasStretchToVisualOp = true;
            BitmapSource cropped = TryCrop(src,
                (int)srcSubRect.X, (int)srcSubRect.Y,
                (int)srcSubRect.Width, (int)srcSubRect.Height);
            if (cropped == null) return;
            _ops.Add((dc, visualBounds, tint) => {
                Rect d = stretchToVisual && visualBounds.Width > 0 && visualBounds.Height > 0
                    ? new Rect(0, 0, visualBounds.Width, visualBounds.Height)
                    : explicitDst;
                if (d.Width <= 0 || d.Height <= 0) return;
                var target = new Rect(
                    d.X + dstFraction.X * d.Width,
                    d.Y + dstFraction.Y * d.Height,
                    dstFraction.Width  * d.Width,
                    dstFraction.Height * d.Height);
                if (target.Width <= 0 || target.Height <= 0) return;
                dc.DrawImage(TintedIfNeeded(cropped, tint), target);
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
            if (stretchToVisual) HasStretchToVisualOp = true;
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

            _ops.Add((dc, visualBounds, tint) => {
                Rect dst = stretchToVisual && visualBounds.Width > 0 && visualBounds.Height > 0
                    ? new Rect(0, 0, visualBounds.Width, visualBounds.Height)
                    : dstRect;
                double dw = dst.Width, dh = dst.Height;
                if (dw <= 0 || dh <= 0) return;
                // Tint each slice through the shared cache (no-op for White).
                BitmapSource tl2 = TintedIfNeeded(tl, tint), t2 = TintedIfNeeded(t, tint),
                             tr2 = TintedIfNeeded(tr, tint), l2 = TintedIfNeeded(l, tint),
                             c2  = TintedIfNeeded(c, tint),  r2 = TintedIfNeeded(r, tint),
                             bl2 = TintedIfNeeded(bl, tint), b2 = TintedIfNeeded(b, tint),
                             br2 = TintedIfNeeded(br, tint);

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

                if (tl2 != null && leftW    > 0 && topH    > 0) dc.DrawImage(tl2, new Rect(xL, yT, leftW,    topH));
                if (t2  != null && centerDW > 0 && topH    > 0) dc.DrawImage(t2,  new Rect(xC, yT, centerDW, topH));
                if (tr2 != null && rightW   > 0 && topH    > 0) dc.DrawImage(tr2, new Rect(xR, yT, rightW,   topH));
                if (l2  != null && leftW    > 0 && centerDH> 0) dc.DrawImage(l2,  new Rect(xL, yM, leftW,    centerDH));
                if (c2  != null && centerDW > 0 && centerDH> 0) dc.DrawImage(c2,  new Rect(xC, yM, centerDW, centerDH));
                if (r2  != null && rightW   > 0 && centerDH> 0) dc.DrawImage(r2,  new Rect(xR, yM, rightW,   centerDH));
                if (bl2 != null && leftW    > 0 && bottomH > 0) dc.DrawImage(bl2, new Rect(xL, yB, leftW,    bottomH));
                if (b2  != null && centerDW > 0 && bottomH > 0) dc.DrawImage(b2,  new Rect(xC, yB, centerDW, bottomH));
                if (br2 != null && rightW   > 0 && bottomH > 0) dc.DrawImage(br2, new Rect(xR, yB, rightW,   bottomH));
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
            foreach (var op in _ops) op(dc, visualBounds, Colors.White);
        }

        /// <summary>
        /// Return a copy of the current op list. Used by SplashVisual at
        /// SetContent time to cache the ops independently of the RB
        /// (which is about to be Clear()'d).
        /// </summary>
        public List<Action<DrawingContext, Size, Color>> SnapshotOps() {
            return new List<Action<DrawingContext, Size, Color>>(_ops);
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

        /// <summary>Owner callback (objcb) + context (ctxcb) registered via
        /// DataBuffer_RegisterOwner (spec §2.2.4.1.1). When the buffer's
        /// usage completes, the controller sends
        /// LocalDataBufferCallback_OnComplete (§2.2.5.9) addressed to this
        /// pair so the server can reclaim its copy. Zero = no owner
        /// registered (complete-and-release still drops our copy).</summary>
        public uint OwnerObj { get; set; }
        public uint OwnerCtx { get; set; }

        public SplashDataBuffer(uint handle, byte[] bytes)
            : base(handle, SplashClassKind.DataBuffer, "DataBuffer") {
            Bytes = bytes ?? new byte[0];
        }
    }

    // -------- SoundBuffer --------

    /// <summary>
    /// MS-RRSP2 SoundBuffer (spec §2.2.4.19). Holds the raw bytes of a
    /// loaded sound asset — typically a WAV file or raw PCM payload that
    /// was previously deposited by the server as a DataBuffer and is now
    /// being claimed by the audio subsystem.
    ///
    /// <para>Wire lifecycle:</para>
    /// <list type="bullet">
    ///   <item><c>XAudSoundDevice_CreateSoundBuffer</c> registers an empty
    ///         SoundBuffer with the given handle.</item>
    ///   <item><c>SoundBuffer_LoadSoundData(dataBuffer u32)</c> references a
    ///         DataBuffer whose bytes become this SoundBuffer's
    ///         <see cref="Bytes"/>.</item>
    /// </list>
    ///
    /// <para>Many <see cref="SplashSound"/> instances can share one
    /// SoundBuffer — WMC's MCE shell loads ~3 distinct UI sound assets and
    /// instantiates dozens of Sound objects that reference them for
    /// concurrent / repeated playback.</para>
    /// </summary>
    internal sealed class SplashSoundBuffer : SplashGenericObject {
        /// <summary>Handle of the DataBuffer that supplied the bytes
        /// (set when <c>SoundBuffer_LoadSoundData</c> fires).
        /// 0 until the load completes.</summary>
        public uint DataBufferHandle { get; set; }

        // ---- SoundHeader (spec §2.2.6.11), parsed from
        // XAudSoundDevice_CreateSoundBuffer. Drives the playback format
        // instead of the historic hardcoded 44.1 kHz/16-bit/stereo
        // assumption. Zero = header absent/implausible → player falls
        // back to its defaults.
        public int FormatTag     { get; set; }
        public int Channels      { get; set; }
        public int SampleRate    { get; set; }
        public int BitsPerSample { get; set; }

        /// <summary>Owner callback (objcb/ctxcb) from CreateSoundBuffer.
        /// After LoadSoundData succeeds the controller sends
        /// LocalSoundBufferCallback_OnSoundBufferReady (§2.2.5.2) to this
        /// pair. Zero = none registered.</summary>
        public uint OwnerObj { get; set; }
        public uint OwnerCtx { get; set; }

        /// <summary>The actual sound bytes — typically a complete WAV file
        /// (RIFF/WAVE header + PCM data) for splash UI sounds, but the
        /// player accepts raw PCM as a fallback. Empty until LoadSoundData
        /// runs.</summary>
        public byte[] Bytes { get; set; } = System.Array.Empty<byte>();

        public SplashSoundBuffer(uint handle, string className)
            : base(handle, SplashClassKind.SoundBuffer, className) { }
    }

    // -------- Sound --------

    /// <summary>
    /// MS-RRSP2 Sound (spec §2.2.4.20). A playable instance bound to a
    /// <see cref="SplashSoundBuffer"/>. <c>Sound_Play</c> triggers playback
    /// of the buffer's bytes; <c>Sound_Stop</c> halts. WMC's MCE shell uses
    /// these for navigation clicks / focus chimes / error feedback.
    /// </summary>
    internal sealed class SplashSound : SplashGenericObject {
        /// <summary>Handle of the SoundBuffer that supplies this sound's
        /// playable bytes. Set at construction by
        /// <c>XAudSoundDevice_CreateSound</c>.</summary>
        public uint SoundBufferHandle { get; }

        public SplashSound(uint handle, uint soundBufferHandle, string className)
            : base(handle, SplashClassKind.Sound, className) {
            SoundBufferHandle = soundBufferHandle;
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

        // ---- Atlas placement (live since fAllowDynamicPool=1) ----
        // Surface_RemapLocation (spec §2.2.4.11.4): this surface's
        // rectangle INSIDE its pool's shared sheet, in pool pixels.
        // Needed to resolve pool-space coordinates (SurfacePool_Draw
        // source rects) back to member surfaces. NOTE: for rotated
        // storage the rect's dims are the ROTATED (swapped) dims.
        public int  PoolRectX { get; set; }
        public int  PoolRectY { get; set; }
        public int  PoolRectW { get; set; }
        public int  PoolRectH { get; set; }
        public bool HasPoolRect { get; set; }

        // Surface_SetRotation (spec §2.2.4.11.7): content stored rotated
        // 90° inside the pool for tighter packing. Our per-surface
        // bitmaps keep the LOGICAL orientation (the Rasterizer upload
        // arrives unrotated), so this flag only matters when resolving
        // pool-space coordinates.
        public bool StoredRotated { get; set; }

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
        /// Resolve every stop's position into an absolute pixel coordinate
        /// against the given target visual size. Mirrors the math in
        /// <see cref="BuildOpacityMask"/> so the diagnostic logger can
        /// report the same effective coordinates the brush would use.
        ///
        /// <para>Returns the minimum and maximum effective-pixel coords
        /// across all stops along this gradient's orientation axis. Used by
        /// the controller's gradient-binding-fitness logger to evaluate
        /// which candidate visual (last-bound, last-bound parent, next-bound)
        /// has a Size that best matches the gradient's coordinate range.</para>
        /// </summary>
        public void GetEffectiveSpan(double visualWidth, double visualHeight,
                                      out double minPx, out double maxPx,
                                      out double axisLen) {
            axisLen = Direction == Orientation.Horizontal ? visualWidth : visualHeight;
            minPx   = 0; maxPx = 0;
            if (Stops.Count == 0) return;
            bool first = true;
            foreach (var s in Stops) {
                double px;
                switch (s.Relative) {
                    case 1:  px = axisLen + s.Position; break;
                    case 4:
                    case 0:
                    default: px = s.Position; break;
                }
                px += Offset;
                if (first) { minPx = maxPx = px; first = false; }
                else {
                    if (px < minPx) minPx = px;
                    if (px > maxPx) maxPx = px;
                }
            }
        }

        /// <summary>
        /// Returns 0..1 score for how well this gradient's coordinate span
        /// covers a target visual of the given size. 1.0 means the
        /// gradient's [min, max] effective pixel range maps cleanly onto
        /// the visual's [0, axisLen] (any overflow at the edges is
        /// acceptable because edge fades commonly extend a few px past
        /// the bounds). Lower scores mean a mismatch — typically the
        /// gradient's span massively overshoots the visual (visual too
        /// small) or sits entirely outside it (wrong visual entirely).
        /// </summary>
        public double FitScoreForVisual(double visualWidth, double visualHeight) {
            GetEffectiveSpan(visualWidth, visualHeight,
                             out double minPx, out double maxPx, out double axisLen);
            if (axisLen <= 0) return 0.0;
            double span = maxPx - minPx;
            if (span <= 0) return 0.0;
            // Compute the gradient's overlap with the visual's [0, axisLen].
            double overlapLo = Math.Max(0,        minPx);
            double overlapHi = Math.Min(axisLen,  maxPx);
            double overlap   = Math.Max(0, overlapHi - overlapLo);
            // Two ratios: (a) fraction of the GRADIENT'S coord range that
            // falls inside the visual (1.0 = no waste, 0.0 = entirely
            // outside); (b) fraction of the VISUAL'S axis that the gradient
            // touches. A good match scores high on both.
            double gradInsideRatio = overlap / span;
            double visualCoverage  = overlap / axisLen;
            return Math.Min(gradInsideRatio, visualCoverage);
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

            // Skip-rule for TRUE U-shape gradients (val=1 outside on BOTH
            // sides, val=0 inside). Wire pattern from WMC's action panels
            // (e.g. delete / play buttons):
            //   val=1 at x=-20  (outside, before min edge)
            //   val=0 at x=20   (inside)
            //   val=0 at x=W-20 (inside)
            //   val=1 at x=W+20 (outside, past max edge)
            // The two val=1 stops on either side mean the gradient is
            // shaped as a U with both anchors outside — there's no way
            // to apply it as an OpacityMask without erasing the visual's
            // middle. Real WMC clearly renders these buttons fully visible,
            // so the gradient must function as some other effect (perhaps
            // a fill that's only drawn at the val=1 edges).
            //
            // CRITICAL DISTINCTION from one-sided edge fades: many
            // legitimate gradients (e.g. the settings-page title fade-in:
            // val=1 at y=-94, val=0 at y=6) also have val=0 inside and
            // val=1 outside, but only on ONE side — the other side has
            // either no stop or val=0 well within bounds. Those produce
            // a meaningful soft fade that we DO want to apply. The
            // earlier heuristic ("all inside stops are val=0") was too
            // broad and dropped both cases identically — the title page
            // ended up rendered as a flat-coloured rectangle instead of
            // the wire-intended top-fade.
            //
            // Refined predicate: skip iff there is val>0.01 BOTH before
            // the min edge AND past the max edge — the unambiguous U
            // signature. One-sided fades (val=1 only on one side) fall
            // through and are applied normally.
            bool hasInsideStop = false;
            bool allInsideStopsZero = true;
            bool hasOutsideMinVal1 = false; // a val>0 stop at pixel < -0.5
            bool hasOutsideMaxVal1 = false; // a val>0 stop at pixel > axisLen+0.5
            foreach (var c in coords) {
                if (c.Pixel >= -0.5 && c.Pixel <= axisLen + 0.5) {
                    hasInsideStop = true;
                    if (c.Value > 0.01f) allInsideStopsZero = false;
                } else {
                    if (c.Value > 0.01f) {
                        if (c.Pixel < -0.5)            hasOutsideMinVal1 = true;
                        else /* c.Pixel > axisLen+0.5 */ hasOutsideMaxVal1 = true;
                    }
                }
            }
            if (hasInsideStop && allInsideStopsZero
                && hasOutsideMinVal1 && hasOutsideMaxVal1) {
                return null; // skip — true U-shape, no useful mask
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

        /// <summary>
        /// Resolve a stop to an absolute pixel coordinate along the axis
        /// (mirrors <see cref="BuildOpacityMask"/>'s per-stop switch,
        /// including <see cref="Offset"/>).
        /// </summary>
        private double ResolveStopPx(Stop s, double axisLen) {
            double px;
            switch (s.Relative) {
                case 1:  px = axisLen + s.Position; break;   // from max corner
                case 4:  // global — best-effort local-min
                case 0:
                default: px = s.Position; break;             // from min corner
            }
            return px + Offset;
        }

        /// <summary>
        /// Interpolated alpha (0..1) this gradient would apply at absolute
        /// pixel <paramref name="px"/>, with Pad spread (clamped to the
        /// first/last stop value outside the stop range) — matching the
        /// <see cref="GradientSpreadMethod.Pad"/> brushes we build. Used by
        /// <see cref="BuildCombinedMask"/> to multiply several gradients'
        /// masks together.
        /// </summary>
        private double AlphaAt(double px, double axisLen) {
            int n = Stops.Count;
            if (n == 0) return 1.0; // no stops = fully opaque (identity)
            var arr = new (double Px, double Val)[n];
            for (int i = 0; i < n; i++)
                arr[i] = (ResolveStopPx(Stops[i], axisLen), Stops[i].Value);
            System.Array.Sort(arr, (a, b) => a.Px.CompareTo(b.Px));
            if (px <= arr[0].Px)     return Clamp01(arr[0].Val);
            if (px >= arr[n - 1].Px) return Clamp01(arr[n - 1].Val);
            for (int i = 0; i < n - 1; i++) {
                if (px >= arr[i].Px && px <= arr[i + 1].Px) {
                    double span = arr[i + 1].Px - arr[i].Px;
                    double t = span > 1e-9 ? (px - arr[i].Px) / span : 0.0;
                    return Clamp01(arr[i].Val + (arr[i + 1].Val - arr[i].Val) * t);
                }
            }
            return Clamp01(arr[n - 1].Val);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>
        /// Build a single OpacityMask that is the MULTIPLICATIVE combination
        /// of several gradients' masks against one visual size. WMC ships a
        /// scroll viewport's edge clip as a PAIR of gradients drawn onto the
        /// same RenderBuilder — a fade-in from the leading edge plus a
        /// fade-out at the trailing edge — but WPF allows only one
        /// OpacityMask per visual. Applying only the last (the prior
        /// behaviour) clipped just one edge, so content scrolled off the
        /// other edge stayed visible (the recorded-TV popup tab strip
        /// bleeding past the popup's left edge).
        ///
        /// <para>Same-orientation gradients are merged into one
        /// LinearGradientBrush whose alpha at each sampled position is the
        /// product of every gradient's alpha there (the correct meaning of
        /// stacking opacity masks). For the both-edges case this yields
        /// stops 0@0, 1@lead, 1@(len-trail), 0@len — transparent at both
        /// ends, opaque in the middle. Mixed-orientation sets can't be a
        /// single linear brush, so they fall back to the last gradient's
        /// mask (unchanged from before). A single gradient also takes the
        /// original path, so all one-mask cases (home-row window masks, EPG
        /// bound, title fades) are byte-for-byte unchanged.</para>
        /// </summary>
        public static Brush BuildCombinedMask(
                System.Collections.Generic.IReadOnlyList<SplashGradient> grads,
                double w, double h) {
            if (grads == null || grads.Count == 0) return null;
            if (grads.Count == 1) return grads[0].BuildOpacityMask(w, h);
            if (w <= 0 || h <= 0) return null;

            var dir = grads[0].Direction;
            for (int i = 1; i < grads.Count; i++)
                if (grads[i].Direction != dir)
                    return grads[grads.Count - 1].BuildOpacityMask(w, h); // mixed → last

            double axisLen = dir == Orientation.Horizontal ? w : h;

            // Union of every gradient's stop positions (plus the axis ends),
            // so the piecewise-linear product is sampled wherever any input
            // has a knee.
            var posSet = new System.Collections.Generic.SortedSet<double>();
            posSet.Add(0); posSet.Add(axisLen);
            foreach (var g in grads)
                foreach (var s in g.Stops)
                    posSet.Add(g.ResolveStopPx(s, axisLen));

            var pts = new System.Collections.Generic.List<(double Px, double A)>(posSet.Count);
            foreach (var px in posSet) {
                double a = 1.0;
                foreach (var g in grads) a *= g.AlphaAt(px, axisLen);
                pts.Add((px, a));
            }

            double minPx = pts[0].Px, maxPx = pts[pts.Count - 1].Px;
            if (Math.Abs(maxPx - minPx) < 0.001) {
                byte a0 = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(pts[0].A * 255)));
                var solid = new SolidColorBrush(Color.FromArgb(a0, 0xFF, 0xFF, 0xFF));
                solid.Freeze();
                return solid;
            }

            var sc = new GradientStopCollection();
            foreach (var p in pts) {
                double t = (p.Px - minPx) / (maxPx - minPx);
                byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(p.A * 255)));
                sc.Add(new GradientStop(Color.FromArgb(a, 0xFF, 0xFF, 0xFF), t));
            }
            sc.Freeze();

            Point start, end;
            if (dir == Orientation.Horizontal) {
                start = new Point(minPx, h * 0.5); end = new Point(maxPx, h * 0.5);
            } else {
                start = new Point(w * 0.5, minPx); end = new Point(w * 0.5, maxPx);
            }
            var brush = new LinearGradientBrush(sc, start, end) {
                MappingMode  = BrushMappingMode.Absolute,
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
