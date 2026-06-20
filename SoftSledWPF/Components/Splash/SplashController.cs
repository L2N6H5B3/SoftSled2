using SoftSled.Components.Diagnostics;
using SoftSled.Components.Splash.Objects;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// Top-level orchestrator for the MS-RRSP2 splash channel. Owns:
    /// <list type="bullet">
    ///   <item><description>A <see cref="SplashWireReassembler"/> that turns
    ///   raw VC chunks into discrete framed buffers.</description></item>
    ///   <item><description>A <see cref="SplashObjectRegistry"/> tracking
    ///   classes and object instances.</description></item>
    ///   <item><description>The payload dispatcher that turns parsed
    ///   message headers into scene-graph mutations on the UI thread.</description></item>
    /// </list>
    ///
    /// Thread model: <see cref="PushVcData"/> is callable from the FreeRDP
    /// worker thread; the reassembler runs synchronously on that thread,
    /// but every <c>BufferReady</c> event is marshalled onto the WPF
    /// dispatcher before mutating the registry or the render host.
    /// </summary>
    internal sealed class SplashController {

        private readonly Logger              _logger;
        private readonly Dispatcher          _uiDispatcher;
        private readonly SplashWireReassembler _wire = new SplashWireReassembler();
        private readonly SplashObjectRegistry  _registry = new SplashObjectRegistry();
        private readonly SplashRawDumper       _dumper; // may be null
        private readonly Action<byte[]>        _sendBytes; // may be null in unit tests

        private SplashRenderHost _host;
        // Optional sink that receives HostWindow_SetBackgroundColor pushes
        // (MS-RRSP2 §2.2.4.23.2). Wired by ExtenderSessionControl to a
        // Rectangle that sits at the very bottom of the WPF Grid — see
        // the layer comment in ExtenderSessionControl.xaml for the
        // motivation. Null in tests / headless contexts.
        private Action<System.Windows.Media.Color> _backgroundColorSink;
        private bool             _payloadBigEndian;  // dictated by DSPA BIG cap
        private uint             _windowHandle;      // most recently created Window (best-effort)
        private uint             _sceneRootHandle;   // visual handle declared by HostWindow_SetRoot

        // -------- Animation playback state --------
        // Set of currently-playing Animations. The tick handler (wired
        // to CompositionTarget.Rendering on the WPF dispatcher) walks
        // this set every frame, interpolates each animation's value
        // along its keyframe sequence, and applies it to the target
        // visual's property.
        private readonly System.Collections.Generic.HashSet<Objects.SplashAnimation> _playingAnimations
            = new System.Collections.Generic.HashSet<Objects.SplashAnimation>();
        private readonly System.Diagnostics.Stopwatch _animClock = new System.Diagnostics.Stopwatch();
        private bool _animTickHooked;

        // ---- Batch-synchronised animation start-time (task #170+) ----
        // Without this, every Animation_Play inside a single message batch
        // captured its own _animClock.Elapsed value the moment the message
        // arrived — and because each message dispatch takes wall-clock
        // microseconds-to-milliseconds, the StartTimeMs values for anims
        // that the wire intended to run in lock-step drifted by a few ms.
        // Visible symptom: on a home-screen-after-Recorded-TV transition,
        // the fade-out of the previous row's label was a few ms ahead of
        // the slide-in covering it, so a descender like the "y" in "movie
        // library" was briefly visible past the new row's mask.
        //
        // Fix: every StartAnimation that fires while we're inside the
        // outermost DispatchBatch shares ONE timestamp, latched at the
        // first such call within the batch. Plays initiated outside a
        // batch (e.g. chained from an OnComplete handler on the anim
        // tick) still read the clock fresh — that's correct, those
        // aren't part of any wire batch.
        private int    _batchDepth;
        private bool   _batchAnimStartLatched;
        private double _batchAnimStartMs;

        // Context IDs assigned in the handshake. idContextApp is the
        // server-side app context (typically 1); idContextRender is the
        // client-side context we were assigned (typically 2). Used as
        // src/dst when emitting Context_ForwardMessage callbacks back to
        // the server.
        private uint _idContextApp;
        private uint _idContextRender;

        public SplashRenderHost RenderHost => _host;

        // ============================================================
        //  Surface-rect lookup for the playback Surface Router
        //  (Layer 6 of the media playback plan)
        // ------------------------------------------------------------
        //  MS-DMCT OpenMedia's Surface ID is the same uint handle that
        //  the splash channel assigns via SurfacePool_CreateSurface
        //  (MS-RRSP2 §2.2.4.12.2 — the idNewSurface u32). So when
        //  AvCtrlHandler hands us a Surface ID, we can look it up in
        //  the object registry to confirm the surface exists, and then
        //  position the video element at the surface's screen-space
        //  rectangle.
        //
        //  v1 simplification: full positional traversal (walking from
        //  SplashSurface up through every SplashVisual that draws it,
        //  applying transforms) is non-trivial. For the typical case
        //  WMC creates a single main video surface that fills the
        //  splash host, so v1 returns the SplashRenderHost's full
        //  client-area rect as long as the surface exists. The
        //  refined per-surface coordinates can be a follow-up if/when
        //  WMC exercises a sub-region surface that we'd otherwise
        //  miss.
        // ============================================================

        /// <summary>
        /// Look up the on-screen rectangle for a video / dynamic surface
        /// identifier.
        ///
        /// <para>Accepts EITHER form of identifier:</para>
        /// <list type="bullet">
        ///   <item><description><b>DynamicSurfaceFactory uid</b> (preferred,
        ///   used by MS-DMCT §2.2.1.1.1 OpenMedia SurfaceID — typically
        ///   small ints like 101). Resolves through
        ///   <see cref="_dynamicSurfaceByUid"/> to the splash Surface
        ///   handle that arrived alongside the uid on the wire.</description></item>
        ///   <item><description><b>Splash object handle</b> directly
        ///   (legacy fall-through — caller hands us the registry handle
        ///   straight). Useful for tests or any future caller that already
        ///   walked the registry.</description></item>
        /// </list>
        ///
        /// <para>Returns the host's full client rectangle for any
        /// recognised surface (v1 assumption: video is always full-host).
        /// Returns <c>false</c> when neither form matches.</para>
        /// </summary>
        public bool TryGetSurfaceScreenRect(uint surfaceId, out Rect rect) {
            return TryGetSurfaceScreenRect(surfaceId, out rect, out _, out _);
        }

        /// <summary>
        /// Detailed overload that also reports which splash handle (if
        /// any) the caller's input was resolved to, plus whether the
        /// resolution went through the DMCT-uid path. Used by
        /// <see cref="AudioVisual.SurfaceRouter"/> to log an unambiguous
        /// uid→splash-handle trace.
        /// </summary>
        public bool TryGetSurfaceScreenRect(uint surfaceId, out Rect rect,
                                            out uint resolvedSplashHandle, out bool resolvedViaUid) {
            rect = Rect.Empty;
            resolvedSplashHandle = 0;
            resolvedViaUid = false;
            if (_host == null) return false;

            // First try as a DynamicSurfaceFactory uid. The DMCT
            // OpenMedia SurfaceID field per §2.2.1.1.1 is *this* uid,
            // not a splash object handle — the surface lookup needs to
            // bridge from one numbering space to the other before any
            // registry check.
            if (surfaceId <= int.MaxValue
                && _dynamicSurfaceByUid.TryGetValue((int)surfaceId, out var dyn)) {
                surfaceId = dyn.SurfaceHandle;  // hop to the splash handle
                resolvedViaUid = true;
            }
            resolvedSplashHandle = surfaceId;

            if (!_registry.TryGetObject(surfaceId, out var obj)) {
                return false;
            }
            if (!(obj is SoftSled.Components.Splash.Objects.SplashSurface)) {
                // Handle exists but isn't a surface — ignore.
                return false;
            }
            // v1: full host rect. Computed on the dispatcher thread
            // since RenderHost is a WPF FrameworkElement.
            if (_uiDispatcher.CheckAccess()) {
                rect = new Rect(0, 0, _host.ActualWidth, _host.ActualHeight);
            } else {
                Rect captured = Rect.Empty;
                _uiDispatcher.Invoke(new Action(() => {
                    captured = new Rect(0, 0, _host.ActualWidth, _host.ActualHeight);
                }));
                rect = captured;
            }
            return true;
        }

        /// <summary>
        /// Fires when the screen rectangle for some surface ID changes
        /// (host resized, surface re-bound, etc.). The Surface Router
        /// subscribes and re-aligns the video element.
        ///
        /// v1: fires only when the host size changes (since we map all
        /// surfaces to the host's bounds). Per-surface positional change
        /// fanout is a follow-up.
        /// </summary>
        public event Action<uint /*surfaceId*/, Rect /*newRect*/> SurfaceScreenRectChanged;

        /// <summary>
        /// Fires when a likely PiP (picture-in-picture) destination
        /// rectangle is identified during active video playback. Carries
        /// the Visual handle that's hosting the PiP placeholder and its
        /// absolute screen rect; empty rect means "no PiP, video should
        /// revert to full canvas" (e.g. video closed, PiP placeholder
        /// destroyed, or the WMC shell jumped back to fullscreen).
        ///
        /// Why a separate event from SurfaceScreenRectChanged: SSRC keys
        /// on a real DMCT/DSF surface handle and broadcasts the host's
        /// full bounds. PiP routing is a HEURISTIC — WMC doesn't emit
        /// VideoPool_Draw on this corpus (confirmed: spec §2.2.4.13.1
        /// messages never appear), so the destination has to be sniffed
        /// from gradient-only Visuals with PiP-shaped geometry. Keeping
        /// the two events apart means the existing surface-resolution
        /// path stays clean.
        /// </summary>
        public event Action<uint /*visualHandle*/, Rect /*absoluteRect*/> VideoPipCandidateChanged;

        // The Visual we're currently treating as the PiP target. Set
        // when a strict PIP-CAND match fires the event; cleared when
        // the visual is destroyed, when video playback closes, or when
        // a better candidate replaces it.
        private uint   _currentPipVisualHandle;
        private double _currentPipFit;
        private Rect   _currentPipRect;

        // ============================================================
        //  Video-bound Visual tracking (PIP-BINDING-driven, definitive)
        // ------------------------------------------------------------
        //  Captured at the moment a Visual_SetContent binds a Visual to
        //  a RenderBuilder that was previously primed by VideoPool_Draw
        //  (the [PIP-BINDING] event). The bound Visual IS the on-screen
        //  rectangle where the video pool composites — every subsequent
        //  geometry-mutating message (Visual_SetSize, _SetPosition,
        //  _SetScale, _SetRotation, _SetCenterPoint*, _ChangeParent) on
        //  this Visual OR any of its ancestors changes where the video
        //  appears.
        //
        //  This is the deterministic alternative to the gradient/aspect
        //  heuristic that gives false positives on Home-screen carousels.
        //  When the heuristic and the binding agree the binding wins
        //  silently; when they disagree we log loudly so the asymmetry
        //  is visible in capture diffs.
        //
        //  Cleared when:
        //   * the bound Visual is destroyed (Broker_DestroyObject)
        //   * the bound video instance closes (DSF CloseInstance with
        //     activeVideoCount → 0)
        //   * a new [PIP-BINDING] fires on a different Visual handle
        // ============================================================
        private uint _videoBoundVisualHandle;
        private uint _videoBoundPoolHandle;     // diagnostic — which pool primed the RB
        private int  _videoBoundUid;            // diagnostic — which DSF uid this came from
        private Rect _videoBoundLastRect;       // change-detection so unchanged rects don't spam
        // Per-frame rect polling. WMC moves/resizes the PiP box via
        // AnimationManager position/size animations, which update the visual's
        // transform WITHOUT sending discrete SetPosition/SetSize messages — so
        // RefreshVideoRectIfRelevant alone misses the motion and the video gets
        // stranded at its bind-time rect. While bound we poll the bound visual's
        // rendered rect on CompositionTarget.Rendering and re-route on change so
        // the video follows the box through animations and layout shifts.
        private bool _videoRectPollHooked;
        private EventHandler _videoRectPollHandler;

        // ============================================================
        //  PiP-candidate lifespan tracking
        // ------------------------------------------------------------
        //  Pure diagnostic: every Visual the strict-criteria heuristic
        //  fingerprints as a possible PiP placeholder is recorded here
        //  with its birth timestamp. When Broker_DestroyObject reaps a
        //  tracked handle, we log how long it lived. Short-lived
        //  candidates (≤ a few seconds) are selector rings — they get
        //  destroyed and re-created on every navigation. A real PiP
        //  placeholder should live the WHOLE playback session.
        //
        //  The intent is to discover the distinguishing signal between
        //  the selector ring (currently the only thing that matches our
        //  heuristic) and the real PiP placeholder (which is presumably
        //  longer-lived but we haven't fingerprinted yet).
        // ============================================================
        private sealed class PipCandidateBirth {
            public uint Handle;
            public Rect Rect;
            public bool IsVert;
            public double LbFit;
            public double Aspect;
            public System.DateTime BornUtc;
        }
        private readonly System.Collections.Generic.Dictionary<uint, PipCandidateBirth> _pipCandidateBirths
            = new System.Collections.Generic.Dictionary<uint, PipCandidateBirth>();

        private void RecordPipCandidateBirth(uint handle, Rect rect, bool isVert, double lbFit, double aspect) {
            // Replace prior record on re-fire (the Visual got a new
            // SetContent — most recent fingerprint wins).
            _pipCandidateBirths[handle] = new PipCandidateBirth {
                Handle = handle, Rect = rect, IsVert = isVert,
                LbFit = lbFit, Aspect = aspect,
                BornUtc = System.DateTime.UtcNow,
            };
        }

        private void ReportPipCandidateDeath(uint handle) {
            if (!_pipCandidateBirths.TryGetValue(handle, out var birth)) return;
            _pipCandidateBirths.Remove(handle);
            double lifespanMs = (System.DateTime.UtcNow - birth.BornUtc).TotalMilliseconds;
            string lifespanTag = lifespanMs < 3000   ? "SHORT-LIVED (selector-like)" :
                                 lifespanMs < 10000  ? "MEDIUM-LIVED" :
                                                       "LONG-LIVED (PIP-like)";
            _dumper?.OnEvent($"    [PIP-CAND-DEATH] vis=0x{handle:X8} lifespan={lifespanMs:F0}ms {lifespanTag} " +
                             $"rect=({birth.Rect.X:F0},{birth.Rect.Y:F0} {birth.Rect.Width:F0}x{birth.Rect.Height:F0}) " +
                             $"grad={(birth.IsVert ? "V" : "H")} aspect={birth.Aspect:F2}");
            // Long-lived candidate destruction is the high-signal event —
            // promote to the app log so we don't have to dig through the
            // splash dump to find it.
            if (lifespanMs >= 10000) {
                _logger?.LogInfo($"[splash] PIP-CAND-DEATH long-lived vis=0x{handle:X8} " +
                                 $"lifespan={lifespanMs/1000.0:F1}s " +
                                 $"rect=({birth.Rect.X:F0},{birth.Rect.Y:F0} {birth.Rect.Width:F0}x{birth.Rect.Height:F0}) " +
                                 $"— this lifetime pattern is consistent with a real PiP placeholder");
            }
        }

        /// <summary>
        /// True if <paramref name="v"/> is the currently-bound video Visual
        /// or any of its ancestors (i.e. any geometry change on it moves
        /// the video on screen). Walks the parent chain bounded to depth
        /// 64.  Out-params let the caller include the chain in a diagnostic
        /// log line.
        /// </summary>
        private bool IsVideoBoundOrAncestor(SplashVisual v, out int hopsToVideo, out uint videoVisualHandleResolved) {
            hopsToVideo = -1;
            videoVisualHandleResolved = 0;
            if (v == null || _videoBoundVisualHandle == 0) return false;
            int depth = 0;
            for (var n = v; n != null && depth < 64; n = n.Parent, depth++) {
                if (n.Handle == _videoBoundVisualHandle) {
                    hopsToVideo = depth;
                    videoVisualHandleResolved = n.Handle;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Compute the on-screen rectangle of the video-bound Visual using
        /// WPF's actual transform composition (TransformToAncestor against
        /// the SplashRenderHost). Folds in EVERY parent-chain transform —
        /// translates, scales, rotations — which is what
        /// <see cref="TryComputeVisualAbsoluteRect"/> can't do because it
        /// only sums PosX/PosY. Returns false if the Visual isn't yet
        /// rooted in the visual tree, or is sized to zero.
        /// </summary>
        private bool TryComputeVisualRenderedRect(SplashVisual v, out Rect rect) {
            rect = Rect.Empty;
            if (v == null || _host == null) return false;
            Rect captured = Rect.Empty;
            bool ok = false;
            Action work = () => {
                try {
                    if (v.SizeX <= 0 || v.SizeY <= 0) return;
                    // TransformToAncestor throws InvalidOperationException
                    // if the descendant isn't in the host's tree yet.
                    var t = v.DrawingVisual.TransformToAncestor(_host);
                    captured = t.TransformBounds(new Rect(0, 0, v.SizeX, v.SizeY));
                    ok = true;
                } catch { /* not rooted yet — leave ok=false */ }
            };
            if (_uiDispatcher.CheckAccess()) work();
            else { try { _uiDispatcher.Invoke(work); } catch { } }
            if (ok) rect = captured;
            return ok;
        }

        /// <summary>
        /// Called by Visual msgid handlers after any geometry-mutating
        /// change. If the touched Visual is the video-bound Visual itself
        /// or any of its ancestors, recomputes the absolute rect and (if
        /// it actually changed) emits a [VIDEO-RECT] diagnostic line +
        /// re-raises <see cref="VideoPipCandidateChanged"/> so the
        /// SurfaceRouter follows along.
        ///
        /// <para>The "trigger" string identifies which msgid caused the
        /// refresh — invaluable when reading capture logs to see exactly
        /// which wire message moved the video.</para>
        /// </summary>
        private void RefreshVideoRectIfRelevant(SplashVisual touched, string trigger) {
            if (_videoBoundVisualHandle == 0 || touched == null) return;
            if (!IsVideoBoundOrAncestor(touched, out int hops, out _)) return;

            // Locate the actual bound Visual (touched may be an ancestor).
            if (!_registry.TryGetObject(_videoBoundVisualHandle, out var obj)
                || !(obj is SplashVisual bound)) return;

            if (!TryComputeVisualRenderedRect(bound, out var newRect)) {
                // Bound visual not yet rooted, or zero-sized. Don't
                // clear — wait for a future tick to re-evaluate.
                _dumper?.OnEvent($"    [VIDEO-RECT] {trigger} touched=0x{touched.Handle:X8} hops={hops} bound=0x{_videoBoundVisualHandle:X8} (unrooted/zero — skipped)");
                return;
            }

            // Tolerance: <1 px change is noise from float rounding through
            // composed transforms. Only fire when a real layout change.
            const double EPS = 1.0;
            bool changed = Math.Abs(newRect.X      - _videoBoundLastRect.X) >= EPS
                        || Math.Abs(newRect.Y      - _videoBoundLastRect.Y) >= EPS
                        || Math.Abs(newRect.Width  - _videoBoundLastRect.Width)  >= EPS
                        || Math.Abs(newRect.Height - _videoBoundLastRect.Height) >= EPS;

            if (!changed) {
                // Still emit a quiet log line so we can see WHICH msgids
                // touched the chain without firing a rect change — useful
                // for finding e.g. SetAlpha or SetColor messages that the
                // server sends to the video Visual that we might want to
                // react to (transparency = "video hidden during menu").
                _dumper?.OnEvent($"    [VIDEO-TOUCH] {trigger} touched=0x{touched.Handle:X8} hops={hops} bound=0x{_videoBoundVisualHandle:X8} rect-unchanged ({newRect.X:F0},{newRect.Y:F0} {newRect.Width:F0}x{newRect.Height:F0})");
                return;
            }

            _dumper?.OnEvent($"    [VIDEO-RECT] {trigger} touched=0x{touched.Handle:X8} hops={hops} bound=0x{_videoBoundVisualHandle:X8} " +
                             $"prev=({_videoBoundLastRect.X:F0},{_videoBoundLastRect.Y:F0} {_videoBoundLastRect.Width:F0}x{_videoBoundLastRect.Height:F0}) " +
                             $"now=({newRect.X:F0},{newRect.Y:F0} {newRect.Width:F0}x{newRect.Height:F0})");
            _logger?.LogInfo($"[splash] VIDEO-RECT moved via {trigger} on 0x{touched.Handle:X8} -> " +
                             $"({newRect.X:F0},{newRect.Y:F0} {newRect.Width:F0}x{newRect.Height:F0}) " +
                             $"(bound vis=0x{_videoBoundVisualHandle:X8} uid={_videoBoundUid} pool=0x{_videoBoundPoolHandle:X8})");

            _videoBoundLastRect = newRect;

            // Route through the same event the gradient-heuristic uses —
            // SurfaceRouter is already subscribed. Bound-driven updates
            // supersede the heuristic-driven lock so we also nudge the
            // _currentPip* fields to match.
            _currentPipVisualHandle = _videoBoundVisualHandle;
            _currentPipRect         = newRect;
            RaiseVideoPipCandidateChanged(_videoBoundVisualHandle, newRect);
        }

        /// <summary>
        /// Capture a new video-bound Visual when [PIP-BINDING] fires.
        /// Idempotent — calling with the same handle is a no-op. A new
        /// handle replaces the previous binding (and broadcasts the new
        /// rect via the normal refresh path).
        /// </summary>
        private void SetVideoBoundVisual(SplashVisual v, uint poolHandle, int uid) {
            if (v == null) return;
            if (_videoBoundVisualHandle == v.Handle) {
                // Same Visual rebinding (WMC re-sends VideoPool_Draw +
                // Visual_SetContent every frame). Just refresh in case the
                // rect drifted.
                _videoBoundPoolHandle = poolHandle;
                _videoBoundUid        = uid;
                RefreshVideoRectIfRelevant(v, "[PIP-BINDING rebind]");
                return;
            }
            _dumper?.OnEvent($"    [VIDEO-BOUND] vis=0x{v.Handle:X8} replaces 0x{_videoBoundVisualHandle:X8} pool=0x{poolHandle:X8} uid={uid}");
            _logger?.LogInfo($"[splash] VIDEO-BOUND captured vis=0x{v.Handle:X8} (uid={uid} pool=0x{poolHandle:X8}) — tracking geometry changes");
            _videoBoundVisualHandle = v.Handle;
            _videoBoundPoolHandle   = poolHandle;
            _videoBoundUid          = uid;
            _videoBoundLastRect     = Rect.Empty;  // force a change on first refresh
            RefreshVideoRectIfRelevant(v, "[PIP-BINDING new]");
            StartVideoRectPolling();   // follow animated moves/resizes of the box
        }

        /// <summary>
        /// Drop the video-bound Visual tracking. Called on destroy and on
        /// video-instance close. Idempotent.
        /// </summary>
        private void ClearVideoBoundVisual(string reason) {
            if (_videoBoundVisualHandle == 0) return;
            _dumper?.OnEvent($"    [VIDEO-UNBOUND] vis=0x{_videoBoundVisualHandle:X8} reason={reason}");
            _logger?.LogInfo($"[splash] VIDEO-UNBOUND vis=0x{_videoBoundVisualHandle:X8} ({reason}) — video routing reverts to full canvas");
            StopVideoRectPolling();
            _videoBoundVisualHandle = 0;
            _videoBoundPoolHandle   = 0;
            _videoBoundUid          = 0;
            _videoBoundLastRect     = Rect.Empty;
            // Route an empty rect so SurfaceRouter reverts to the full
            // canvas / host bounds.
            RaiseVideoPipCandidateChanged(0, Rect.Empty);
        }

        // ---- Per-frame PiP rect polling (follows animated box moves) ----
        private void StartVideoRectPolling() {
            Action hook = () => {
                if (_videoRectPollHooked) return;
                _videoRectPollHandler = (s, e) => PollVideoRectTick();
                System.Windows.Media.CompositionTarget.Rendering += _videoRectPollHandler;
                _videoRectPollHooked = true;
            };
            if (_uiDispatcher.CheckAccess()) hook();
            else { try { _uiDispatcher.BeginInvoke(hook); } catch { } }
        }

        private void StopVideoRectPolling() {
            Action unhook = () => {
                if (!_videoRectPollHooked) return;
                if (_videoRectPollHandler != null)
                    System.Windows.Media.CompositionTarget.Rendering -= _videoRectPollHandler;
                _videoRectPollHandler = null;
                _videoRectPollHooked = false;
            };
            if (_uiDispatcher.CheckAccess()) unhook();
            else { try { _uiDispatcher.BeginInvoke(unhook); } catch { } }
        }

        // Runs on the UI thread (CompositionTarget.Rendering, ~per frame).
        // Re-resolves the bound visual's rendered rect — which reflects the
        // CURRENT animated transform — and re-routes when it changes, so the
        // video tracks the PiP box smoothly through WMC's move/resize
        // animations that send no discrete SetPosition/SetSize.
        private void PollVideoRectTick() {
            if (_videoBoundVisualHandle == 0) return;
            if (!_registry.TryGetObject(_videoBoundVisualHandle, out var obj) || !(obj is SplashVisual bound)) return;
            if (!TryComputeVisualRenderedRect(bound, out var r)) return;
            const double EPS = 1.0;
            if (Math.Abs(r.X - _videoBoundLastRect.X) < EPS
                && Math.Abs(r.Y - _videoBoundLastRect.Y) < EPS
                && Math.Abs(r.Width  - _videoBoundLastRect.Width)  < EPS
                && Math.Abs(r.Height - _videoBoundLastRect.Height) < EPS) return;
            _videoBoundLastRect     = r;
            _currentPipVisualHandle = _videoBoundVisualHandle;
            _currentPipRect         = r;
            RaiseVideoPipCandidateChanged(_videoBoundVisualHandle, r);
        }

        private void RaiseVideoPipCandidateChanged(uint visualHandle, Rect rect) {
            var h = VideoPipCandidateChanged;
            if (h == null) return;
            try { h(visualHandle, rect); }
            catch (Exception ex) {
                _logger?.LogError($"[splash] VideoPipCandidateChanged subscriber threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Walk a Visual's parent chain and accumulate position offsets
        /// to produce an absolute screen rectangle. Ignores scale and
        /// rotation transforms for v1 simplicity — captured PiP
        /// placeholders sit at 1:1 transforms; if WMC ever introduces a
        /// scaled PiP container we'll need to fold ScaleX/ScaleY in.
        /// Returns false if the visual is null or unrooted.
        /// </summary>
        private bool TryComputeVisualAbsoluteRect(SplashVisual v, out Rect rect) {
            rect = Rect.Empty;
            if (v == null) return false;
            double absX = 0, absY = 0;
            int depth = 0;
            for (var n = v; n != null && depth < 64; n = n.Parent, depth++) {
                absX += n.PosX;
                absY += n.PosY;
            }
            if (v.SizeX <= 0 || v.SizeY <= 0) return false;
            rect = new Rect(absX, absY, v.SizeX, v.SizeY);
            return true;
        }

        /// <summary>
        /// Called by the host's size-changed handler (wired up in
        /// AttachHost) so we can notify subscribers that "the surface
        /// rectangle is now X" without them having to poll.
        /// </summary>
        private void RaiseSurfaceScreenRectChanged() {
            var handler = SurfaceScreenRectChanged;
            if (handler == null) return;
            // Snapshot once and broadcast the same rect for every
            // surface handle currently registered. Subscribers know to
            // filter by the surface ID they care about.
            Rect host = _host == null
                        ? Rect.Empty
                        : new Rect(0, 0, _host.ActualWidth, _host.ActualHeight);
            foreach (var kv in EnumerateSurfaceIds()) {
                try { handler(kv, host); }
                catch (Exception ex) {
                    _logger?.LogError($"[splash] SurfaceScreenRectChanged subscriber threw: {ex.Message}");
                }
            }
        }

        private System.Collections.Generic.IEnumerable<uint> EnumerateSurfaceIds() {
            // The registry doesn't currently expose an enumerator;
            // best-effort: walk the internal dict via reflection-free
            // accessor would need a new method. For now provide a small
            // helper inside SplashObjectRegistry that returns the IDs.
            return _registry.EnumerateSurfaceHandles();
        }

        // Splash-channel UI sound player. Lazily constructed on the first
        // SoundBuffer / Sound message so the NAudio output device isn't
        // initialised in sessions that never trigger splash audio. Null
        // when EnableSplashAudio is false — Sound_Play messages then
        // still acknowledge but produce no audio.
        private SplashSoundPlayer _soundPlayer;
        private readonly bool _enableSplashAudio;

        // ============================================================
        //  DynamicSurfaceFactory uid → splash-handle registry
        // ------------------------------------------------------------
        //  MS-RRSP2 §2.2.4.18 — DynamicSurfaceFactory.CreateVideoInstance
        //  (msgid=1) and .CreateSurfaceInstance (msgid=2) carry a
        //  nUniqueID i32 ("uid") that the host application uses as a
        //  stable token for "which dynamic surface" in its higher-layer
        //  protocols. MS-DMCT §2.2.1.1.1 OpenMedia's SurfaceID field is
        //  exactly this uid — *not* a splash object handle.
        //
        //  So DMCT OpenMedia(surfaceId=101) asks us to play to the
        //  splash Surface whose dynamic-factory uid is 101 (i.e. the
        //  surface handle that came in alongside uid=101 on the wire).
        //  TryGetSurfaceScreenRect can use this map to translate.
        //
        //  Also tracked: the count of currently-live VIDEO instances
        //  (msgid=1 specifically — not msgid=2 plain Surface
        //  instances). While that count is >0, the splash host needs
        //  to suppress its opaque background so the externally-fed
        //  video surface (FFME element behind splashHost) can show
        //  through. See SplashRenderHost.SetVideoActive.
        // ============================================================
        private sealed class DynamicSurfaceEntry {
            public uint SurfaceHandle;
            public uint PoolHandle;
            public bool IsVideo;
        }
        private readonly System.Collections.Generic.Dictionary<int, DynamicSurfaceEntry>
            _dynamicSurfaceByUid = new System.Collections.Generic.Dictionary<int, DynamicSurfaceEntry>();
        private int _activeVideoInstanceCount;

        // ---------------- Video-family handle tracking ----------------
        //
        // The WMC server allocates its video surface, pool, and the
        // associated Visual subtree in a distinct handle namespace
        // (different high byte from the regular UI scene graph). E.g.
        // in one captured session: pool=0x0600005B, surface=0x0800007C,
        // video subtree Visuals 0x0A00004D / 0x0600005A / 0x0700007D /
        // 0x07000091 / 0x0700009B — all under high bytes 0x06/0x07/0x08/0x0A.
        // The regular shell UI sits in 0x01/0x02/0x03/0x04/0x05.
        //
        // We can't see how WMC associates "video surface here" with the
        // Visual subtree because that message (if any) is being silently
        // swallowed by an "unhandled msgid" branch — none of the spec
        // msgids on Surface/SurfacePool/VideoPool carry a target-rect
        // payload. By tracking which high bytes belong to the active
        // video family, every "unhandled" dispatch can self-tag the
        // log line `[VFAM]` and emit the raw body hex so the missing
        // message becomes greppable.
        //
        // Population rules:
        //  * XeDevice_CreateVideoPool — pool's high byte added.
        //  * DynamicSurfaceFactory_CreateVideoInstance — surface + pool
        //    high bytes added.
        //  * Visual_ChangeParent where the parent is in the family —
        //    the child's high byte is added too (so the entire video
        //    subtree gets covered, even when WMC reuses a namespace
        //    we hadn't seen yet).
        //
        // Cleared on CloseInstance (only when activeVideoCount hits 0)
        // so a fresh session starts clean.
        private readonly System.Collections.Generic.HashSet<byte>
            _videoFamilyHighBytes = new System.Collections.Generic.HashSet<byte>();

        /// <summary>
        /// Tracks RenderBuilders the WMC server primed via
        /// <c>VideoPool_Draw</c> (MS-RRSP2 §2.2.4.13.1, msgid=0). When a
        /// later <c>Visual_SetContent</c> binds one of these RBs to a
        /// Visual, that Visual IS the on-screen target of the video pool
        /// — the PiP destination we've been hunting for. The Visual's
        /// pos+size at the time of binding give us the absolute rectangle.
        /// </summary>
        private sealed class VideoDrawBinding {
            public uint  PoolHandle;
            public int   VideoUid;
            public float DstX, DstY, DstW, DstH;
        }
        private readonly System.Collections.Generic.Dictionary<uint, VideoDrawBinding>
            _videoDrawByRb = new System.Collections.Generic.Dictionary<uint, VideoDrawBinding>();

        private bool IsVideoFamilyHandle(uint handle) {
            if (_videoFamilyHighBytes.Count == 0) return false;
            return _videoFamilyHighBytes.Contains((byte)(handle >> 24));
        }

        /// <summary>
        /// Try to add a handle's high byte to the video family. Returns
        /// true if this is a NEW high byte (caller should log so we can
        /// trace the family's growth in the splash log).
        /// </summary>
        private bool TryAddVideoFamilyHighByte(uint handle) {
            byte hi = (byte)(handle >> 24);
            return _videoFamilyHighBytes.Add(hi);
        }

        /// <summary>
        /// Hex-dump the unread bytes from <paramref name="rdr"/> if the
        /// subject is in the video family — appended to <paramref name="prefix"/>
        /// with a [VFAM] marker. Used by every dispatcher's "unhandled"
        /// default branch so the body of any message we silently drop
        /// is captured for analysis.
        /// </summary>
        private void MaybeDumpVfamHex(uint subj, string prefix, SplashPayloadReader rdr) {
            if (_dumper == null) return;
            if (!IsVideoFamilyHandle(subj)) return;
            _dumper.OnEvent($"    [VFAM] subj=0x{subj:X8} body={rdr.PeekRemainingHex(128)}");
        }

        public SplashController(Logger logger, Dispatcher uiDispatcher, bool payloadBigEndian,
                                Action<byte[]> sendBytes, bool enableSplashAudio = true) {
            _logger          = logger;
            _uiDispatcher    = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
            _payloadBigEndian = payloadBigEndian;
            _sendBytes       = sendBytes;
            _enableSplashAudio = enableSplashAudio;
            _dumper          = SplashRawDumper.CreateFromEnv(logger);

            _wire.ServerHandshakeReceived += OnServerHandshake;
            _wire.BufferReady             += OnBufferReady;
            _wire.ShutdownReceived        += OnShutdown;
            _wire.ParseError              += OnParseError;
        }

        public void AttachHost(SplashRenderHost host) {
            if (_host != null) {
                try { _host.SizeChanged -= HostSizeChanged; } catch { }
            }
            _host = host;
            if (_host != null) {
                _host.SizeChanged += HostSizeChanged;
            }
        }

        /// <summary>
        /// Register a callback that receives <c>HostWindow_SetBackgroundColor</c>
        /// (MS-RRSP2 §2.2.4.23.2) and <c>Window_SetBackgroundColor</c>
        /// (§2.2.4.10.1) pushes. Wired by <see cref="Shell.ExtenderSessionControl"/>
        /// to a Rectangle that sits at the very bottom of the WPF Grid so
        /// the FFME video plane and the splash scene-graph naturally
        /// composite over the background colour without any special
        /// "transparent background during video" handling.
        /// </summary>
        public void SetBackgroundColorSink(Action<System.Windows.Media.Color> sink) {
            _backgroundColorSink = sink;
        }

        private void HostSizeChanged(object sender, SizeChangedEventArgs e) {
            // v1 surface routing: every registered surface gets the
            // host's full client rect — so a host resize broadcasts
            // to every subscriber.
            RaiseSurfaceScreenRectChanged();
        }

        public void Reset() {
            _wire.Reset();
            _uiDispatcher.BeginInvoke(new Action(() => {
                // Stop the per-frame animation tick AND drop refs to
                // every animation. Otherwise the rendering tick keeps
                // referencing handles that no longer exist in the
                // registry, and (worse) keeps stale visuals alive until
                // GC walks the strong-ref chain.
                foreach (var a in _playingAnimations) {
                    try { a.Playing = false; } catch { }
                }
                _playingAnimations.Clear();
                _animClock.Reset(); // re-zero so the next session's StartTimeMs is 0

                StopVideoRectPolling();   // drop the per-frame PiP rect hook
                _videoBoundVisualHandle = 0;
                _videoBoundLastRect = Rect.Empty;

                _registry.Clear();
                _host?.ClearAll();
                _windowHandle    = 0;
                _sceneRootHandle = 0;
                _idContextApp    = 0;
                _idContextRender = 0;
                // Drop the dynamic-surface uid mapping — a fresh session
                // re-issues DynamicSurfaceFactory_CreateXxxInstance for any
                // surfaces it cares about. Leaving stale uids would cause
                // the next session's DMCT OpenMedia lookup to resolve to
                // a destroyed handle.
                _dynamicSurfaceByUid.Clear();
                _activeVideoInstanceCount = 0;
                _videoFamilyHighBytes.Clear();
                _videoDrawByRb.Clear();
                _currentPipVisualHandle = 0;
                _currentPipFit          = 0;
                _currentPipRect         = Rect.Empty;
                // Binding-driven tracker reset — drop the bound-Visual
                // handle so the next session's [PIP-BINDING] starts clean.
                _videoBoundVisualHandle = 0;
                _videoBoundPoolHandle   = 0;
                _videoBoundUid          = 0;
                _videoBoundLastRect     = Rect.Empty;
                // PiP-candidate birth registry: drop everything so the
                // next session starts with a clean lifespan record.
                _pipCandidateBirths.Clear();
            }));
        }

        /// <summary>Called by VirtualChannelSplashHandler whenever the splash VC delivers bytes.</summary>
        public void PushVcData(byte[] chunk) {
            if (chunk == null || chunk.Length == 0) return;
            _dumper?.OnVcBytes(chunk);
            _wire.Push(chunk);
        }

        // -------- Reassembler callbacks --------

        private void OnServerHandshake(object s, SplashWireReassembler.ServerHandshakeArgs a) {
            _logger?.LogDebug(
                $"SPLASH: RemoteServerInformation version=0x{a.DwVersion:X8} magic=0x{a.DwMagic:X8} " +
                $"ctxApp={a.IdContextApp} ctxRender={a.IdContextRender} " +
                $"itemsPerGroupBits={a.CItemsPerGroupBits} groupBits={a.CGroupBits} brokerCls=0x{a.IdObjectBrokerCls:X8}");
            _dumper?.OnEvent($"SrvHandshake ver=0x{a.DwVersion:X8} magic=0x{a.DwMagic:X8} ctxApp={a.IdContextApp} ctxRender={a.IdContextRender} brokerCls=0x{a.IdObjectBrokerCls:X8}");
            // Pre-register the broker class AND register the broker class
            // handle as an *object* too. The MS-RRSP2 server uses
            // idObjectBrokerClass (e.g. 0x01000000) as the
            // _idObjectSubject of every Broker_CreateClass /
            // Broker_CreateObject message — i.e. the broker acts as both
            // the class and the implicit singleton instance. Without the
            // object registration, ClassifySubject() returns "?" and the
            // dispatcher never routes to DispatchBroker.
            uint brokerHandle = a.IdObjectBrokerCls;
            _idContextApp     = a.IdContextApp;
            _idContextRender  = a.IdContextRender;
            _uiDispatcher.BeginInvoke(new Action(() => {
                _registry.RegisterClass(brokerHandle, "Broker");
                _registry.RegisterObject(brokerHandle,
                    new SplashGenericObject(brokerHandle, SplashClassKind.Broker, "Broker"));
            }));
        }

        private void OnShutdown(object s, EventArgs e) {
            _logger?.LogDebug("SPLASH: Shutdown command received");
            _dumper?.OnEvent("Shutdown");
            // Release the NAudio output device(s) — otherwise a fresh
            // session would accumulate a stale player whose disposed
            // outputs still hold device handles.
            try { _soundPlayer?.Dispose(); } catch { }
            _soundPlayer = null;
        }

        private void OnParseError(object s, SplashWireReassembler.ParseErrorArgs e) {
            _logger?.LogError("SPLASH: wire parse error: " + e.Message);
            _dumper?.OnEvent("ParseError: " + e.Message);
        }

        private void OnBufferReady(object s, SplashWireReassembler.BufferReadyArgs e) {
            _dumper?.OnBuffer(e);
            // Marshal to UI thread before touching the registry or the visual tree.
            _uiDispatcher.BeginInvoke(new Action(() => DispatchBufferOnUi(e)));
        }

        // -------- UI-thread dispatch --------

        private void DispatchBufferOnUi(SplashWireReassembler.BufferReadyArgs buf) {
            try {
                switch (buf.Kind) {
                    case SplashWireReassembler.BufferKind.DataBuffer:
                        // Per spec 2.2.3.1, a Buffer with idBuffer != 0
                        // is an implicit DataBuffer instance — no
                        // preceding Broker_CreateObject. We register it
                        // by its idBuffer handle so Rasterizer_LoadRawImage
                        // can later fetch the bytes by reference.
                        _registry.RegisterObject(buf.IdBuffer,
                            new Objects.SplashDataBuffer(buf.IdBuffer, buf.Payload));
                        _dumper?.OnEvent($"DataBuffer idBuffer=0x{buf.IdBuffer:X8} payload={buf.Payload.Length} B (registered)");
                        break;

                    case SplashWireReassembler.BufferKind.IndividualMessage:
                        DispatchMessage(buf.Payload, 0, buf.Payload.Length);
                        break;

                    case SplashWireReassembler.BufferKind.MessageBatch:
                        DispatchBatch(buf.Payload);
                        break;
                }
            } catch (Exception ex) {
                _logger?.LogError($"SPLASH: dispatch failed: {ex.Message}");
                _dumper?.OnEvent("Dispatch exception: " + ex.Message);
            }
        }

        // Set of DataBuffer IDs we're currently dispatching as predicates,
        // used to break cycles. Spec §2.2.3.2 says "that buffer can also
        // refer to another predicate buffer (and so on)" — we walk that
        // chain, but if it loops we'd recurse infinitely without this guard.
        private readonly System.Collections.Generic.HashSet<uint> _predicateInFlight
            = new System.Collections.Generic.HashSet<uint>();

        private void DispatchBatch(byte[] payload) {
            if (payload.Length < 8) {
                _dumper?.OnEvent("Batch too short (no header)");
                return;
            }
            // Outermost batch resets the StartAnimation timestamp latch
            // (see comment on _batchAnimStartLatched). Nested batches
            // (from predicate-buffer reentry) share the outer batch's
            // latched value so cross-batch animations stay lock-step
            // with each other too — predicate buffers carry setup for
            // the outer batch's Plays and conceptually belong to the
            // same logical wire event.
            bool isOutermostBatch = (_batchDepth == 0);
            if (isOutermostBatch) _batchAnimStartLatched = false;
            _batchDepth++;
            try {
                DispatchBatchInner(payload);
            } finally {
                _batchDepth--;
            }
        }

        private void DispatchBatchInner(byte[] payload) {
            // MessageBatch header (8 B, BE per framing rules).
            uint idPredicateBuffer = ReadU32BE(payload, 0);
            uint uOffsetFirstEntry = ReadU32BE(payload, 4);
            _dumper?.OnEvent($"Batch predBuf={idPredicateBuffer} firstEntryOffset={uOffsetFirstEntry} totalLen={payload.Length}");

            // Sanity-check the first-entry offset before we walk entries.
            // Real batches always set uOffsetFirstEntry=8 (immediately
            // after the 8-byte header). Anything outside [8, len) means
            // these bytes aren't a real batch — most likely we're being
            // mis-dispatched as part of the predicate-buffer fallback
            // (e.g. a Rasterizer pixel buffer that happens to share a
            // handle with what we thought was a batch).
            if (uOffsetFirstEntry < 8 || uOffsetFirstEntry > (uint)payload.Length) {
                _dumper?.OnEvent($"  (batch header out of range, len={payload.Length}; skipping)");
                return;
            }

            // Per spec §2.2.3.2, if idPredicateBuffer != 0 the named
            // DataBuffer MUST be processed as a batch buffer before
            // processing this batch's entries. WMC uses this to ship a
            // large block of Broker_CreateObject / Visual / RB messages
            // ahead of a smaller batch that references those handles —
            // skipping the predicate leaves us with hundreds of orphan
            // handles and "RB not found" errors.
            if (idPredicateBuffer != 0) {
                if (_predicateInFlight.Add(idPredicateBuffer)) {
                    try {
                        if (_registry.TryGetObject(idPredicateBuffer, out var pObj)
                            && pObj is Objects.SplashDataBuffer pdb
                            && pdb.Bytes != null
                            && pdb.Bytes.Length >= 8) {
                            _dumper?.OnEvent($"  -> processing predicate buffer 0x{idPredicateBuffer:X8} ({pdb.Bytes.Length} B) as batch first");
                            DispatchBatch(pdb.Bytes);
                            _dumper?.OnEvent($"  <- finished predicate buffer 0x{idPredicateBuffer:X8}, resuming outer batch");
                        } else {
                            _dumper?.OnEvent($"  (predicate buffer 0x{idPredicateBuffer:X8} not registered or empty — skipping)");
                        }
                    } finally {
                        _predicateInFlight.Remove(idPredicateBuffer);
                    }
                } else {
                    _dumper?.OnEvent($"  (predicate buffer 0x{idPredicateBuffer:X8} cycle detected — skipping)");
                }
            }

            uint cursor = uOffsetFirstEntry;
            int safety = 0;
            while (cursor + 4 <= (uint)payload.Length && safety++ < 100000) {
                // Each batch entry: 4-B uOffsetNextEntry (BE), then the
                // payload-format message starting at cursor + 4.
                uint nextOff = ReadU32BE(payload, (int)cursor);
                int  msgStart = (int)cursor + 4;
                int  msgEnd   = nextOff == 0 ? payload.Length : (int)nextOff;
                if (msgEnd < msgStart || msgEnd > payload.Length) {
                    _dumper?.OnEvent($"Batch entry has bad next-offset {nextOff} (msgStart={msgStart})");
                    return;
                }
                DispatchMessage(payload, msgStart, msgEnd - msgStart);
                if (nextOff == 0) return;
                cursor = nextOff;
            }
        }

        private void DispatchMessage(byte[] payload, int offset, int length) {
            if (length < 12) {
                _dumper?.OnEvent($"Msg too short ({length} B)");
                return;
            }
            var rdr = new SplashPayloadReader(payload, offset, length, _payloadBigEndian);
            rdr.ReadMessageHeader(out uint size, out int msgid, out uint subj);

            // size is the *total* including the 12-byte header. Cap our
            // reader to that so messages with trailing padding don't bleed
            // into the next one. Defensive — should equal `length`.
            if (size < 12 || size > (uint)length) {
                _dumper?.OnEvent($"Msg invalid size={size} (have {length} B), skipping");
                return;
            }

            string subjClass = ClassifySubject(subj);
            _dumper?.OnEvent($"Msg subj=0x{subj:X8} ({subjClass}) msgid={msgid} size={size}");

            // Loud trace for any message targeting a registered
            // dynamic-surface / video-pool handle. WMC's PiP-position
            // signal — if it exists in the splash protocol — would
            // arrive as a message on one of these handles; surfacing
            // every such message at INFO level makes the signal
            // immediately visible in the captured log (task #178).
            if (IsDynamicSurfaceOrPool(subj)) {
                _logger?.LogInfo($"[splash] [DYNAMIC-{(IsDynamicSurface(subj) ? "SURFACE" : "POOL")}-TRACE] " +
                                 $"subj=0x{subj:X8} class={subjClass} msgid={msgid} size={size}");
            }

            // Slice 1 dispatch — keyed on the *subject's* class, not on
            // the raw msgid number. This is a deliberate simplification:
            // many msgids are unique per class so we can dispatch by
            // (class, msgid). Unknown classes/msgids are logged and ignored.

            // The broker is special: subject == 0 means "the broker class
            // singleton" (spec 2.2.4.3). _idObjectSubject also frequently
            // equals an actual broker handle the server has assigned us.
            if (subj == 0 || subjClass == "Broker") {
                DispatchBroker(rdr, msgid);
                return;
            }

            if (_registry.TryGetObject(subj, out var obj)) {
                // Class-bootstrap pattern: msgid=11 with body=8 is the
                // post-construction "Create" finalisation (priv_objcb +
                // priv_ctxcb) for nearly every class. We don't need to
                // do anything with it but acknowledge instead of logging
                // "unhandled" 90+ times per session.
                if (msgid == 11 && (int)size - 12 == 8) {
                    _dumper?.OnEvent($"  {obj.Kind}_Create (post-init, ack)");
                    return;
                }
                switch (obj.Kind) {
                    case SplashClassKind.Window:        DispatchWindow((SplashWindow)obj, rdr, msgid); break;
                    case SplashClassKind.HostWindow:    DispatchHostWindow(obj, rdr, msgid); break;
                    case SplashClassKind.Visual:        DispatchVisual((SplashVisual)obj, rdr, msgid); break;
                    case SplashClassKind.RenderBuilder: DispatchRenderBuilder((SplashRenderBuilder)obj, rdr, msgid); break;
                    // Device-derived classes share msgids 0-5 (Stop/Restart/
                    // DrawLine/DrawOutline/DrawSolid/CreateSurfacePool).
                    // XeDevice + Dx9Device add their own methods on top
                    // (CreateVideoPool=7, CreateGradient=9, DrawNotify=0xA,
                    // etc.); DispatchDevice handles both.
                    case SplashClassKind.Device:
                    case SplashClassKind.Dx9Device:
                    case SplashClassKind.XeDevice:
                    case SplashClassKind.NullDevice:    DispatchDevice(rdr, msgid); break;
                    case SplashClassKind.Context:       DispatchContext(rdr, msgid); break;
                    case SplashClassKind.SurfacePool:   DispatchSurfacePool((Objects.SplashSurfacePool)obj, rdr, msgid); break;
                    case SplashClassKind.Surface:       DispatchSurface((Objects.SplashSurface)obj, rdr, msgid); break;
                    case SplashClassKind.Rasterizer:    DispatchRasterizer(rdr, msgid); break;
                    case SplashClassKind.AnimationManager: DispatchAnimationManager(rdr, msgid); break;
                    case SplashClassKind.Animation:     DispatchAnimation((Objects.SplashAnimation)obj, rdr, msgid); break;
                    case SplashClassKind.Gradient:      DispatchGradient(obj as Objects.SplashGradient, rdr, msgid); break;
                    case SplashClassKind.DataBuffer:    DispatchDataBuffer(obj, rdr, msgid); break;
                    case SplashClassKind.XAudSoundDevice: DispatchXAudSoundDevice(rdr, msgid); break;
                    case SplashClassKind.SoundBuffer:   DispatchSoundBuffer(obj as Objects.SplashSoundBuffer, rdr, msgid); break;
                    case SplashClassKind.Sound:         DispatchSound(obj as Objects.SplashSound, rdr, msgid); break;
                    case SplashClassKind.SoundDevice:   DispatchSoundDeviceLegacy(rdr, msgid); break;
                    case SplashClassKind.WaitCursor:    DispatchWaitCursor(obj as Objects.SplashWaitCursor, rdr, msgid); break;
                    // Group 3 — defensive dispatchers. Spec-coded but
                    // without observed wire activity in our test logs;
                    // decode known fields cleanly so any future wire
                    // activity is named rather than "Unknown msgid=N".
                    case SplashClassKind.Line:                  DispatchLine(obj, rdr, msgid); break;
                    case SplashClassKind.VideoPool:             DispatchVideoPool(obj, rdr, msgid); break;
                    case SplashClassKind.ContextRelay:          DispatchContextRelay(obj, rdr, msgid); break;
                    case SplashClassKind.DynamicSurfaceFactory: DispatchDynamicSurfaceFactory(obj, rdr, msgid); break;
                    case SplashClassKind.ParticleSystem:        DispatchParticleSystem(obj, rdr, msgid); break;
                    case SplashClassKind.InputRouter:           DispatchInputRouter(obj, rdr, msgid); break;
                    case SplashClassKind.DesktopManager:        DispatchDesktopManager(obj, rdr, msgid); break;
                    default:
                        _dumper?.OnEvent($"  {obj.Kind} msgid={msgid} (unhandled, rem={rdr.Remaining})");
                        break;
                }
            } else {
                // Subject is neither the broker nor a known object.
                // Try to heuristic-promote based on (msgid, body-size)
                // shape so the most-common implicit-create cases still
                // render. Important: do NOT crash — the wire reassembler
                // framed this message exactly, so `length` is its end.
                //
                // bodyLen is the message's *real* body per the _size
                // field, not the frame's remaining bytes — message-batch
                // frames pad each entry up to a 4-byte stride, so
                // rdr.Remaining can be larger than the spec'd body.
                int bodyLen = (int)size - 12;
                if (bodyLen < 0) bodyLen = 0;
                if (TryPromoteUnknownSubject(subj, msgid, bodyLen, rdr)) return;
                _dumper?.OnEvent($"  (subject 0x{subj:X8} unknown, msgid={msgid}, body={bodyLen}, frameRem={rdr.Remaining})");
            }
        }

        /// <summary>
        /// When a known msgid+body shape arrives for an unregistered
        /// subject, auto-register the subject as the implied class and
        /// re-dispatch.
        ///
        /// MS-RRSP2 creates many objects implicitly — via construction
        /// blobs we don't decode, via Xenon-specific Device methods, etc.
        /// Rather than enumerate every implicit-create path, we
        /// pattern-match on the message shape and create the right class
        /// on first sight. Particularly important for Visual: thousands
        /// of visuals in the MCE shell are implicitly created, and
        /// without auto-promotion their parent chains break and any
        /// downstream child renders at the host root (= top-left).
        /// </summary>
        private bool TryPromoteUnknownSubject(uint subj, int msgid, int bodyLen, SplashPayloadReader rdr) {
            if (msgid == 1 && bodyLen == 40) {
                // Surface_Draw shape (rb 4 + srcRect 16 + dstRect 16 + fNeverStretch 4 = 40)
                var surf = new Objects.SplashSurface(subj, "Surface");
                _registry.RegisterObject(subj, surf);
                _dumper?.OnEvent($"  (auto-promoted 0x{subj:X8} to Surface on Surface_Draw shape)");
                DispatchSurface(surf, rdr, msgid);
                return true;
            }
            // Visual-shaped messages — use the real body length per
            // _size (not rdr.Remaining, which includes batch padding).
            // Body sizes are pinned exactly per spec section 2.2.4.6.
            if (IsVisualShapedMsg(msgid, bodyLen)) {
                var nv = AutoCreateVisual(subj, $"Visual-on-msgid{msgid}");
                DispatchVisual(nv, rdr, msgid);
                return true;
            }
            return false;
        }

        private static bool IsVisualShapedMsg(int msgid, int bodyLen) {
            // CAUTION: msgid is per-class. Several Animation msgids share
            // their number with Visual msgids. The auto-promote path only
            // fires for *unregistered* subjects, and AnimationManager_Build*
            // always pre-registers its handle, so practically we're safe —
            // but to be defensive, we ONLY promote on body shapes that are
            // unique to Visual (no Animation msgid has the same body size).
            //
            // Collisions intentionally NOT auto-promoted (let them log as
            // unknown rather than misclassify an Animation handle):
            //   msgid 1  body 12 — ChangeParent vs SetEaseOut (both 12 B)
            //   msgid 4  body  4 — SetColor     vs SetCosine  (both 4 B)
            //   msgid 6  body  4 — SetAlpha     vs SetSCurve  (8 B; the
            //                       4-byte SetAlpha pad case collides
            //                       only with broken encoders)
            //   msgid 8  body  4 — SetLayer     vs SetLinear  (both 4 B)
            //   msgid 24 body  4 — SetVisible   vs Stop       (both 4 B)
            //   msgid 26 body  0 — Create       vs Play       (both 0 B)
            switch (msgid) {
                case 0:  return bodyLen == 8;                       // Visual_ChangeDataBits (unique)
                case 1:  return bodyLen == 4;                       // ChangeParent short form (unique;
                                                                    //   12-B form COLLIDES with SetEaseOut)
                case 6:  return bodyLen == 1;                       // SetAlpha proper (unique;
                                                                    //   4-B pad form collides; drop it)
                case 10: return bodyLen == 16;                      // Visual_SetRotation (unique)
                case 12: return bodyLen == 12 || bodyLen == 16;     // SetCenterPointScale (Animation SetColorF is 20 B; safe)
                case 14: return bodyLen == 12 || bodyLen == 16;     // SetCenterPointOffset (8-B form collides with Animation SetDynamicRGB; drop)
                case 16: return bodyLen == 12 || bodyLen == 16;     // SetScale (avoid colliding with SetARGBColor 8 B)
                case 18: return bodyLen == 12 || bodyLen == 16;     // SetSize  (avoid colliding with SetDynamicVector3 8 B)
                case 20: return bodyLen == 12 || bodyLen == 16;     // SetPosition (avoid colliding with SetFloat 8 B)
                case 23: return bodyLen == 4;                       // Visual_SetContent (Animation AddKeyframe = 8 B)
                default: return false;
            }
        }

        private SplashVisual AutoCreateVisual(uint handle, string label) {
            var v = new SplashVisual(handle, label, _host);
            _registry.RegisterObject(handle, v);

            // Prefer attaching under the declared scene root (HostWindow_SetRoot)
            // so the auto-created visual inherits the scene's coordinate
            // space + transforms. Use the regular ChangeParent path so
            // SplashVisual.Parent / Children bookkeeping stays consistent
            // — otherwise a later ChangeParent on this visual won't
            // properly detach it from sceneRoot and WPF will throw
            // "Specified Visual is already a child of another Visual".
            //
            // Fall back to host-root attach if the scene root isn't
            // declared yet (early in the boot sequence).
            string attachedTo;
            if (_sceneRootHandle != 0
                && _registry.TryGetObject(_sceneRootHandle, out var sObj)
                && sObj is SplashVisual sceneRoot
                && sceneRoot != v) {
                v.ChangeParent(sceneRoot, SplashVisual.ChildOrder.Top);
                attachedTo = "sceneRoot";
            } else {
                _host?.AttachToRoot(v.DrawingVisual);
                attachedTo = "hostRoot";
            }
            _dumper?.OnEvent($"  (auto-created Visual 0x{handle:X8} from implicit construction, attached={attachedTo})");
            return v;
        }

        /// <summary>
        /// Resolve a handle to a Visual, auto-creating an empty placeholder
        /// if the handle is non-zero but not yet known. Returns null only
        /// for handle 0 (which the wire uses to mean "no sibling" /
        /// "no parent").
        /// </summary>
        private SplashVisual ResolveOrCreateVisual(uint handle) {
            if (handle == 0) return null;
            if (_registry.TryGetObject(handle, out var obj) && obj is SplashVisual vv) return vv;
            return AutoCreateVisual(handle, "Visual-on-parent-ref");
        }

        private string ClassifySubject(uint subj) {
            if (subj == 0) return "Broker";
            if (_registry.TryGetObject(subj, out var obj)) return obj.Kind.ToString();
            return "?";
        }

        // -------- Per-class dispatchers --------

        private void DispatchBroker(SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // Broker_DestroyObject — section 2.2.4.3.1
                    {
                        uint idObject = rdr.ReadU32();
                        _dumper?.OnEvent($"  Broker_DestroyObject(0x{idObject:X8})");
                        // Belt-and-braces leak guard: SplashAnimation.OnDestroyed
                        // only flips Playing=false and nulls Keyframes — it can't
                        // reach back into the controller's _playingAnimations
                        // HashSet. Without this Remove(), destroyed animations
                        // linger in the set forever; the per-tick scratch copy
                        // grows unbounded across a long session.
                        if (_registry.TryGetObject(idObject, out var destroyed)
                            && destroyed is Objects.SplashAnimation destroyedAnim) {
                            _playingAnimations.Remove(destroyedAnim);
                        }
                        // Drop any video-pool-draw binding tracked on
                        // this handle — RBs are short-lived (one per
                        // frame on the busy WMC shell) so leaving stale
                        // entries would falsely tag the next reused
                        // handle as a video target.
                        _videoDrawByRb.Remove(idObject);
                        // PiP-candidate lifespan trace: if this destroyed
                        // handle was previously fingerprinted as a PiP
                        // candidate, report its lifespan. Short lifespans
                        // (≤3s) flag selector-ring behaviour; long
                        // lifespans (≥10s) flag genuine placeholder
                        // behaviour. This is the highest-value signal for
                        // separating the two.
                        ReportPipCandidateDeath(idObject);
                        // If the destroyed object is the Visual we
                        // currently lock the video element onto, reset
                        // the PiP state and broadcast an empty rect so
                        // the SurfaceRouter can revert to full canvas.
                        if (_currentPipVisualHandle != 0 && idObject == _currentPipVisualHandle) {
                            _dumper?.OnEvent($"    [PIP-UNLOCK] vis=0x{idObject:X8} destroyed — reverting routing");
                            _logger?.LogInfo($"[splash] PIP-UNLOCK vis=0x{idObject:X8} destroyed — reverting video to full canvas");
                            _currentPipVisualHandle = 0;
                            _currentPipFit          = 0;
                            _currentPipRect         = Rect.Empty;
                            RaiseVideoPipCandidateChanged(0, Rect.Empty);
                        }
                        // Same for the binding-driven tracker: if the
                        // destroyed object is the video-bound Visual, clear
                        // it so the next [PIP-BINDING] captures fresh.
                        if (_videoBoundVisualHandle != 0 && idObject == _videoBoundVisualHandle) {
                            ClearVideoBoundVisual("Visual destroyed");
                        }
                        _registry.RemoveObject(idObject);
                    }
                    break;
                case 1: // Broker_CreateObject — section 2.2.4.3.2
                    {
                        uint idClass = rdr.ReadU32();
                        uint idNew   = rdr.ReadU32();
                        rdr.ReadBlobRef(out ushort cbSize, out ushort cbOff);
                        CreateObject(idClass, idNew);
                        if (cbSize > 0) {
                            byte[] blob = rdr.ResolveBlob(cbSize, cbOff);
                            _dumper?.OnEvent($"    msgConstruction[{cbSize}]={Hex(blob)}");
                            // Some construction blobs are themselves a
                            // payload message that initialises the new
                            // object (or, for composite objects, creates
                            // sub-objects via a sub-batch). Dispatch the
                            // blob as a regular payload message so any
                            // sub-creates/inits run automatically.
                            if (blob.Length >= 12) {
                                try { DispatchMessage(blob, 0, blob.Length); }
                                catch (Exception ex) {
                                    _dumper?.OnEvent($"    (construction blob dispatch failed: {ex.Message})");
                                }
                            }
                        }
                    }
                    break;
                case 2: // Broker_CreateClass — section 2.2.4.3.3
                    {
                        rdr.ReadBlobRef(out ushort stSize, out ushort stOff);
                        uint idClass = rdr.ReadU32();
                        // The class-name BLOB is UTF-16 BIG-ENDIAN in the
                        // observed wire (one byte 0x00 prefix per ASCII
                        // char). Use BigEndianUnicode explicitly — WMC
                        // sends names like "Splash::Messaging::Context".
                        string name  = System.Text.Encoding.BigEndianUnicode
                                            .GetString(rdr.ResolveBlob(stSize, stOff))
                                            .TrimEnd('\0');
                        _registry.RegisterClass(idClass, name);
                        // Also register the class handle as an implicit
                        // class-singleton object. WMC sends class-level
                        // methods (e.g. Context_ForwardMessage) targeted
                        // at the class handle itself rather than at a
                        // CreateObject-returned instance. Without this
                        // every class-level call would log as "unknown".
                        SplashClassKind kind = SplashClassKind.Unknown;
                        if (_registry.TryGetClass(idClass, out var rec)) kind = rec.Kind;
                        _registry.RegisterObject(idClass,
                            new SplashGenericObject(idClass, kind, name));
                        _dumper?.OnEvent($"  Broker_CreateClass(name=\"{name}\", id=0x{idClass:X8}, kind={kind})");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Broker msgid={msgid} (unhandled)");
                    break;
            }
        }

        private void CreateObject(uint idClass, uint idNew) {
            string clsName = "?";
            SplashClassKind kind = SplashClassKind.Unknown;
            if (_registry.TryGetClass(idClass, out var rec)) {
                clsName = rec.Name;
                kind    = rec.Kind;
            }
            ISplashObject instance;
            switch (kind) {
                case SplashClassKind.Window:
                    instance = new SplashWindow(idNew, clsName, _host);
                    _windowHandle = idNew;
                    break;
                case SplashClassKind.Visual:
                    var v = new SplashVisual(idNew, clsName, _host);
                    _host?.AttachToRoot(v.DrawingVisual);
                    instance = v;
                    break;
                case SplashClassKind.RenderBuilder:
                    instance = new SplashRenderBuilder(idNew, clsName);
                    break;
                case SplashClassKind.Device:
                    instance = new SplashDevice(idNew, clsName);
                    break;
                case SplashClassKind.Context:
                    instance = new SplashContext(idNew, clsName);
                    break;
                case SplashClassKind.SurfacePool:
                    instance = new Objects.SplashSurfacePool(idNew, clsName);
                    break;
                case SplashClassKind.Surface:
                    instance = new Objects.SplashSurface(idNew, clsName);
                    break;
                case SplashClassKind.WaitCursor:
                    instance = new Objects.SplashWaitCursor(idNew, clsName);
                    break;
                default:
                    instance = new SplashGenericObject(idNew, kind, clsName);
                    break;
            }
            _registry.RegisterObject(idNew, instance);
            _dumper?.OnEvent($"  Broker_CreateObject(class=\"{clsName}\" [{kind}], handle=0x{idNew:X8})");
        }

        // msgid table for Window (spec section 2.2.4.10):
        //   0 = SetBackgroundColor   (clrBack: u32 ARGB)
        //   1 = SetPerspectiveSettings (flZn, flEye: float, float)
        //   5 = ChangeDataBits       (nValue, nMask: u32, u32)
        //   7 = SetContent           (rbContent: u32 handle)
        //   8 = SetRoot              (visRoot: u32 handle, msgid inferred from HostWindow analogue)
        private void DispatchWindow(SplashWindow w, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // Window_SetBackgroundColor
                    if (rdr.Remaining >= 4) {
                        uint argb = rdr.ReadU32();
                        var c = ArgbU32ToColor(argb);
                        w.SetBackgroundColor(c);
                        // Window_SetBackgroundColor is functionally a
                        // duplicate of HostWindow_SetBackgroundColor —
                        // both target the host's background plane.
                        // Route through the same sink so the layered
                        // Rectangle picks up the colour.
                        _backgroundColorSink?.Invoke(c);
                        _dumper?.OnEvent($"  Window_SetBackgroundColor argb=0x{argb:X8} -> A={c.A} R={c.R} G={c.G} B={c.B}");
                    }
                    break;
                case 7: // Window_SetContent
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        w.ContentVisualHandle = rbH;
                        if (_registry.TryGetObject(rbH, out var rbObj) && rbObj is SplashRenderBuilder rb) {
                            _dumper?.OnEvent($"  Window_SetContent rb=0x{rbH:X8} ops={rb.OpCount}");
                            // Window background tile is the host's background visual; bake the
                            // RenderBuilder ops onto it for slice 3 (placeholder until we
                            // properly model the Window's root visual).
                        } else {
                            _dumper?.OnEvent($"  Window_SetContent rb=0x{rbH:X8} (not found)");
                        }
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Window msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // msgid table for HostWindow (spec section 2.2.4.23):
        //   0 = SetBackgroundColor   (clrBack: u32 ARGB)
        //   1 = SetPerspectiveSettings
        //   5 = ChangeDataBits
        //   7 = SetContent           (rbContent: u32 handle)
        //   8 = SetRoot              (visRoot: u32 handle)
        //  10 = SetCloseReason       (nCloseReason: i32)
        //
        // HostWindow is the Xenon/Xbox 360 variant of Window — same shape
        // but its own msgid table. WMC's MCE shell uses HostWindow.
        private void DispatchHostWindow(ISplashObject host, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // HostWindow_SetBackgroundColor
                    if (rdr.Remaining >= 4) {
                        uint argb = rdr.ReadU32();
                        var c = ArgbU32ToColor(argb);
                        // Push to the layered background sink rather than
                        // painting inside splashHost — this keeps the FFME
                        // video plane and the splash scene graph cleanly
                        // separated in z-order (background → video →
                        // scene-graph). See the sink wiring comment in
                        // ExtenderSessionControl.xaml.cs.
                        _backgroundColorSink?.Invoke(c);
                        _dumper?.OnEvent($"  HostWindow_SetBackgroundColor argb=0x{argb:X8} -> A={c.A} R={c.R} G={c.G} B={c.B}");
                    }
                    break;
                case 1: // HostWindow_SetPerspectiveSettings (flZn, flEye) — we're 2D, ack only
                    if (rdr.Remaining >= 8) {
                        float zn  = rdr.ReadFloat32();
                        float eye = rdr.ReadFloat32();
                        _dumper?.OnEvent($"  HostWindow_SetPerspectiveSettings flZn={zn:F2} flEye={eye:F2} (2D — ignored)");
                    }
                    break;
                case 5: // HostWindow_ChangeDataBits (nValue, nMask)
                    if (rdr.Remaining >= 8) {
                        uint v = rdr.ReadU32();
                        uint m = rdr.ReadU32();
                        _dumper?.OnEvent($"  HostWindow_ChangeDataBits value=0x{v:X8} mask=0x{m:X8}");
                    }
                    break;
                case 10: // HostWindow_SetCloseReason (nCloseReason i32)
                    if (rdr.Remaining >= 4) {
                        int reason = rdr.ReadI32();
                        _dumper?.OnEvent($"  HostWindow_SetCloseReason reason={reason}");
                    }
                    break;
                case 7: // HostWindow_SetContent
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        _dumper?.OnEvent($"  HostWindow_SetContent rb=0x{rbH:X8}");
                    }
                    break;
                case 8: // HostWindow_SetRoot — declares the scene-graph root visual
                    if (rdr.Remaining >= 4) {
                        uint visRoot = rdr.ReadU32();
                        _sceneRootHandle = visRoot;
                        if (_registry.TryGetObject(visRoot, out var vObj) && vObj is SplashVisual vis) {
                            // Detach every other root-level visual so only
                            // the declared root is rendered. Important:
                            // do NOT call _host.ClearAll() — that also
                            // wipes the background fill we just painted.
                            _host?.SetSceneRoot(vis.DrawingVisual);
                            // If the root already had a Size set before
                            // we noticed it as root, push it now so the
                            // first frame is already scaled-to-fit.
                            if (vis.SizeX > 0 && vis.SizeY > 0) {
                                _host?.SetLogicalCanvasSize(vis.SizeX, vis.SizeY);
                            }
                            _dumper?.OnEvent($"  HostWindow_SetRoot vis=0x{visRoot:X8} (re-anchored, size={vis.SizeX:F0}×{vis.SizeY:F0})");
                        } else {
                            _dumper?.OnEvent($"  HostWindow_SetRoot vis=0x{visRoot:X8} (not found)");
                        }
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  HostWindow msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        /// <summary>Decode a 32-bit ARGB integer (high byte = A) into a WPF Color.</summary>
        private static Color ArgbU32ToColor(uint argb) {
            return Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8)  & 0xFF),
                (byte)(argb & 0xFF));
        }

        // msgid table for Visual (spec section 2.2.4.6):
        //   0 = ChangeDataBits        (nValue, nMask: u32, u32)
        //   1 = ChangeParent          (visNewParent, visSibling: u32; nOrder: i32)
        //   4 = SetColor              (clr: u32 ARGB)
        //   6 = SetAlpha              (bAlpha: 1 byte)
        //   8 = SetLayer              (layer: u32)
        //  10 = SetRotation           (rotRotation: 16 B Rotation = Vector3 axis + float angle)
        //  12 = SetCenterPointScale   (vCenterPointScale: 12 B Vector3)
        //  14 = SetCenterPointOffset  (vCenterPointOffset: 12 B Vector3)
        //  16 = SetScale              (vScale: 12 B Vector3)
        //  18 = SetSize               (vSizePxl: 12 B Vector3)
        //  20 = SetPosition           (vPositionPxl: 12 B Vector3)
        //  23 = SetContent            (rbContent: u32 handle)
        //  24 = SetVisible            (fVisible: u32)
        //  26 = Create                (second-stage init)
        private void DispatchVisual(SplashVisual v, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // Visual_ChangeDataBits — user-defined bits.
                        // Empirically WMC uses bit 6 (0x40) to mark a
                        // visual as a vertical-stack row container, and
                        // bit 4 (0x10) to mark it as the currently-active
                        // row. SplashVisual.ApplyDataBits honours the
                        // "row container without active flag → hide
                        // subtree" rule so non-selected rows' tile
                        // strips disappear.
                    if (rdr.Remaining >= 8) {
                        uint nValue = rdr.ReadU32();
                        uint nMask  = rdr.ReadU32();
                        v.ApplyDataBits(nValue, nMask);
                        _dumper?.OnEvent($"  Visual_ChangeDataBits value=0x{nValue:X8} mask=0x{nMask:X8} -> bits=0x{v.DataBits:X8}");
                        // During active video, emit DataBits changes that
                        // touch bits OUTSIDE the well-known WMC envelope
                        // (row-container/active-flag at 0x00700077, plus
                        // bits 0..6 of the low byte ⇒ 0x00700077). The
                        // tightening matters: the 0x00700077 mask trips
                        // the high nibble of byte 2 (0x70) which would
                        // otherwise yield false positives on every row
                        // toggle. After this filter, any line that DOES
                        // appear is genuinely a novel bit pattern that
                        // could be a "this Visual is the video plane"
                        // marker — the original motivation for the trace.
                        if (_activeVideoInstanceCount > 0) {
                            const uint knownMask = 0x00700077u;
                            if (((nValue | nMask) & ~knownMask) != 0) {
                                _dumper?.OnEvent($"    [BITS-DURING-VIDEO] subj=0x{v.Handle:X8} value=0x{nValue:X8} mask=0x{nMask:X8} NOVEL-BITS-OUTSIDE-0x00700077");
                            }
                        }
                    }
                    break;
                case 1: // Visual_ChangeParent (visNewParent, visSibling, nOrder)
                    if (rdr.Remaining >= 12) {
                        uint subjectH = v.Handle; // captured for log clarity
                        // Capture the OLD parent before ChangeParent moves
                        // the visual — we re-evaluate viewport-clip on
                        // both the previous and new parents so a child
                        // moving out of a virtual-strip viewport drops
                        // the clip on the old viewport.
                        SplashVisual oldParent = v.Parent;
                        uint parentH  = rdr.ReadU32();
                        uint siblingH = rdr.ReadU32();
                        int  nOrder   = rdr.ReadI32();
                        SplashVisual parent  = ResolveOrCreateVisual(parentH);
                        SplashVisual sibling = ResolveOrCreateVisual(siblingH);
                        var order = (SplashVisual.ChildOrder)nOrder;
                        v.ChangeParent(parent, order, sibling);
                        // After re-parenting, both the old and new parent
                        // may need to flip their viewport-clip status.
                        oldParent?.EvaluateViewportClip();
                        parent?.EvaluateViewportClip();
                        // Re-apply Layer ordering: if Visual_SetLayer
                        // arrived BEFORE this ChangeParent (WMC sometimes
                        // sets state then attaches), our nOrder insertion
                        // above ignored the layer. ApplyLayer re-sorts
                        // this visual amongst its new siblings.
                        v.ApplyLayer();
                        // Video-family auto-expansion: if we're parenting
                        // into the video tree, the child inherits the
                        // family even if it lives in a different handle
                        // namespace (rare but possible). Lets us catch
                        // any video-subtree Visual whose high byte we
                        // didn't pre-seed at CreateVideoInstance time.
                        if (parentH != 0 && IsVideoFamilyHandle(parentH) && !IsVideoFamilyHandle(subjectH)) {
                            if (TryAddVideoFamilyHighByte(subjectH)) {
                                _dumper?.OnEvent($"    [VFAM+ inherited] child 0x{subjectH:X8} hi=0x{(byte)(subjectH >> 24):X2} added via parent 0x{parentH:X8}");
                            }
                        }
                        _dumper?.OnEvent($"  Visual_ChangeParent subj=0x{subjectH:X8} parent=0x{parentH:X8} sibling=0x{siblingH:X8} order={order}");
                        // Re-parenting the video Visual (or any of its
                        // ancestors) relocates it on screen even with no
                        // SetPosition/SetSize. WMC's "transition to fullscreen
                        // playback" sequence is conjectured to work this
                        // way — reparent the video Visual from the row-tile
                        // container into the fullscreen scene root.
                        RefreshVideoRectIfRelevant(v, "ChangeParent");
                    } else if (rdr.Remaining >= 4) {
                        // Some encoders omit the sibling/order trailing fields.
                        uint subjectH = v.Handle;
                        SplashVisual oldParentShort = v.Parent;
                        uint parentH = rdr.ReadU32();
                        SplashVisual parent = null;
                        if (parentH != 0 && _registry.TryGetObject(parentH, out var p) && p is SplashVisual pv) {
                            parent = pv;
                        }
                        v.ChangeParent(parent);
                        oldParentShort?.EvaluateViewportClip();
                        parent?.EvaluateViewportClip();
                        v.ApplyLayer();
                        _dumper?.OnEvent($"  Visual_ChangeParent subj=0x{subjectH:X8} parent=0x{parentH:X8} (short)");
                        RefreshVideoRectIfRelevant(v, "ChangeParent(short)");
                    }
                    break;
                case 4: // Visual_SetColor (ARGB u32)
                    if (rdr.Remaining >= 4) {
                        uint argb = rdr.ReadU32();
                        v.Color = ArgbU32ToColor(argb);
                        _dumper?.OnEvent($"  Visual_SetColor argb=0x{argb:X8}");
                    }
                    break;
                case 6: // Visual_SetAlpha (1 byte)
                    if (rdr.Remaining >= 1) {
                        v.AlphaByte = rdr.ReadByte();
                        v.ApplyAlpha();
                        _dumper?.OnEvent($"  Visual_SetAlpha={v.AlphaByte}");
                        // SetAlpha on the video Visual or an ancestor —
                        // emit a [VIDEO-TOUCH] line so we can correlate
                        // alpha changes with the lifecycle. WMC could
                        // fade the video out during transitions; if so
                        // we want to mirror that in our renderer.
                        if (_videoBoundVisualHandle != 0
                            && IsVideoBoundOrAncestor(v, out int alphaHops, out _)) {
                            _dumper?.OnEvent($"    [VIDEO-ALPHA] touched=0x{v.Handle:X8} hops={alphaHops} alpha={v.AlphaByte} (bound=0x{_videoBoundVisualHandle:X8})");
                        }
                    }
                    break;
                case 8: // Visual_SetLayer (layer: u32)
                        // Spec §2.2.4.6.6 — re-positions this visual in
                        // its parent's z-order. Lower Layer = back,
                        // higher = front. WMC ships this 580+ times per
                        // session (mostly 0/1 toggles) so silently
                        // logging it loses real ordering.
                    if (rdr.Remaining >= 4) {
                        uint layer = rdr.ReadU32();
                        v.Layer = layer;
                        v.ApplyLayer();
                        _dumper?.OnEvent($"  Visual_SetLayer={layer}");
                    }
                    break;
                case 10: // Visual_SetRotation (Rotation = Vector3 axis + float angle)
                    if (rdr.Remaining >= 16) {
                        rdr.ReadVector3(out v.RotAxisX, out v.RotAxisY, out v.RotAxisZ);
                        // WIRE DEVIATION FROM SPEC: §2.2.6.2 documents
                        // flAngle as "the degree of rotation" but the
                        // wire actually carries RADIANS — e.g. the
                        // "VIEW CATEGORIES" guide-screen panel that's
                        // visibly 90° rotated ships angle=1.5708 ≈ π/2,
                        // and a 270° rotation arrives as 4.7124 ≈ 3π/2.
                        // Reading these as degrees gives us a barely-
                        // visible 1.6° tilt and the panel renders
                        // horizontally instead of vertically. Convert
                        // to degrees up-front so the rest of the
                        // pipeline (ApplyTransform → WPF RotateTransform
                        // which takes degrees) sees the right unit.
                        float rad = rdr.ReadFloat32();
                        v.RotAngleDeg = rad * (180f / (float)Math.PI);
                        v.ApplyTransform();
                        _dumper?.OnEvent($"  Visual_SetRotation axis=({v.RotAxisX:F2},{v.RotAxisY:F2},{v.RotAxisZ:F2}) angleRad={rad:F4} angleDeg={v.RotAngleDeg:F1}");
                        RefreshVideoRectIfRelevant(v, "SetRotation");
                    }
                    break;
                case 12: // Visual_SetCenterPointScale (Vector3) — fraction of Size
                    if (rdr.Remaining >= 12) {
                        rdr.ReadVector3(out v.CenterScaleX, out v.CenterScaleY, out v.CenterScaleZ);
                        v.ApplyTransform();
                        _dumper?.OnEvent($"  Visual_SetCenterPointScale=({v.CenterScaleX:F2},{v.CenterScaleY:F2},{v.CenterScaleZ:F2})");
                        RefreshVideoRectIfRelevant(v, "SetCenterPointScale");
                    }
                    break;
                case 14: // Visual_SetCenterPointOffset (Vector3) — absolute pixel offset
                    if (rdr.Remaining >= 12) {
                        rdr.ReadVector3(out v.CenterOffsetX, out v.CenterOffsetY, out v.CenterOffsetZ);
                        v.ApplyTransform();
                        _dumper?.OnEvent($"  Visual_SetCenterPointOffset=({v.CenterOffsetX:F2},{v.CenterOffsetY:F2},{v.CenterOffsetZ:F2})");
                        RefreshVideoRectIfRelevant(v, "SetCenterPointOffset");
                    }
                    break;
                case 16: // Visual_SetScale (Vector3)
                    if (rdr.Remaining >= 12) {
                        rdr.ReadVector3(out v.ScaleX, out v.ScaleY, out v.ScaleZ);
                        v.ApplyTransform();
                        _dumper?.OnEvent($"  Visual_SetScale=({v.ScaleX:F2},{v.ScaleY:F2},{v.ScaleZ:F2})");
                        RefreshVideoRectIfRelevant(v, "SetScale");
                    }
                    break;
                case 18: // Visual_SetSize (Vector3)
                    if (rdr.Remaining >= 12) {
                        rdr.ReadVector3(out v.SizeX, out v.SizeY, out v.SizeZ);
                        // Pivot point depends on Size (center = Size * CenterScale + CenterOffset),
                        // so a SetSize after SetCenterPointScale must redo the transform.
                        v.ApplyTransform();
                        // Re-paint any cached content so stretch-to-visual surface ops
                        // pick up the new bounds (Size animation case).
                        v.RepaintContent();
                        // WMC's renderer implicitly clips children to the
                        // container's own bounds. Without this, selection
                        // glow animations inside a menu-item box leak
                        // outside the row. Re-apply on every SetSize so
                        // the clip tracks the live size.
                        v.ApplyBoundsClip();
                        // Viewport-clip evaluation: this visual's own Size
                        // changed — re-check whether any child overshoots
                        // by the 3× threshold and update the viewport flag.
                        v.EvaluateViewportClip();
                        // And re-check the PARENT — this visual's Size
                        // change might have just promoted/demoted it as
                        // a virtual-strip child relative to its parent's
                        // size (the 1001×16,777,220-inside-1001×733 case
                        // fires here on the strip's SetSize, asking the
                        // viewport to flip its clip on).
                        v.Parent?.EvaluateViewportClip();
                        v.ApplyAlpha();
                        // If THIS visual is the declared HostWindow scene root,
                        // its size dictates the logical canvas the shell composes
                        // for. Push it to the host so we scale-to-fit instead of
                        // letting a 1280×720 region get stamped in the top-left.
                        if (v.Handle == _sceneRootHandle && v.SizeX > 0 && v.SizeY > 0) {
                            _host?.SetLogicalCanvasSize(v.SizeX, v.SizeY);
                        }
                        _dumper?.OnEvent($"  Visual_SetSize=({v.SizeX:F2},{v.SizeY:F2},{v.SizeZ:F2})");
                        // Diagnostic: when WMC sizes a popup-sized
                        // container (≥800 px in either dimension and the
                        // size just changed), dump its visual subtree so
                        // we can correlate handles → parent chain → bits
                        // → size. Throttled per-handle so a sub-pixel
                        // animation on a popup-sized visual doesn't spam.
                        MaybeDumpPopupSubtree(v);
                        RefreshVideoRectIfRelevant(v, "SetSize");
                    }
                    break;
                case 20: // Visual_SetPosition (Vector3)
                    if (rdr.Remaining >= 12) {
                        rdr.ReadVector3(out v.PosX, out v.PosY, out v.PosZ);
                        v.ApplyTransform();
                        // RULE 2/3 in ComputeEffectiveOpacity reads
                        // Parent.PosX AND Parent.Parent.PosX (the
                        // scroll container two levels above). So
                        // re-evaluate down to GRANDCHILDREN, not just
                        // direct children.
                        ReapplyAlphaToDescendants(v, depth: 3);
                        _dumper?.OnEvent($"  Visual_SetPosition=({v.PosX:F2},{v.PosY:F2},{v.PosZ:F2})");
                        RefreshVideoRectIfRelevant(v, "SetPosition");
                    }
                    break;
                case 23: // Visual_SetContent (rbContent: u32 handle)
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        if (_registry.TryGetObject(rbH, out var rbObj) && rbObj is SplashRenderBuilder rb) {
                            // Resolve any Gradient_Draw / Gradient_Push
                            // handles queued onto this RB into live
                            // SplashGradient objects, then pass to the
                            // visual. SetContentFromRenderBuilder applies
                            // the last gradient as an OpacityMask.
                            // Gradients referencing destroyed/unknown
                            // handles are silently dropped.
                            var gradientHandles = rb.ConsumePendingGradients();
                            System.Collections.Generic.List<Objects.SplashGradient> pendingGradients = null;
                            if (gradientHandles.Length > 0) {
                                pendingGradients = new System.Collections.Generic.List<Objects.SplashGradient>(gradientHandles.Length);
                                foreach (uint gh in gradientHandles) {
                                    if (_registry.TryGetObject(gh, out var gObj)
                                        && gObj is Objects.SplashGradient grad) {
                                        pendingGradients.Add(grad);
                                    }
                                }
                            }
                            int opsBefore = rb.OpCount;
                            v.SetContentFromRenderBuilder(rb, pendingGradients);
                            // Track last-bound visual for diagnostic
                            // logging (LB-chain fitness output) — and
                            // for any future binding model that wants to
                            // know which visual just consumed this RB.
                            rb.LastBoundVisualHandle = v.Handle;
                            if (pendingGradients != null && pendingGradients.Count > 0) {
                                var lastG = pendingGradients[pendingGradients.Count - 1];
                                string stopSummary = lastG == null ? "null" :
                                    $"{lastG.Stops.Count} stops, dir={lastG.Direction}";
                                // Fit score for the chosen NEXT-BOUND target — pairs
                                // with the LB / LB.P / LB.PP fit lines emitted at
                                // Gradient_Draw time so a popup-repro log can show
                                // which candidate the wire actually meant.
                                double nbFit = lastG?.FitScoreForVisual(v.SizeX, v.SizeY) ?? 0.0;
                                _dumper?.OnEvent($"  Visual_SetContent vis=0x{v.Handle:X8} size=({v.SizeX:F0}x{v.SizeY:F0}) rb=0x{rbH:X8} ops={opsBefore} gradients={pendingGradients.Count} lastGrad=0x{lastG?.Handle ?? 0:X8} [{stopSummary}] NB.fit={nbFit:F2}");
                            } else {
                                _dumper?.OnEvent($"  Visual_SetContent vis=0x{v.Handle:X8} size=({v.SizeX:F0}x{v.SizeY:F0}) rb=0x{rbH:X8} ops={opsBefore}");
                            }
                            // ---- THE definitive PiP-target signal ----
                            // If this RB was primed by VideoPool_Draw
                            // (msgid=0 on a video pool, captured in
                            // _videoDrawByRb), then this very Visual_-
                            // SetContent is what binds the video pool's
                            // draw to its on-screen Visual. The Visual's
                            // current SizeX/SizeY/PosX/PosY are the
                            // absolute PiP rectangle. This is what the
                            // SurfaceRouter needs to position the video
                            // element correctly. (Combined with the
                            // VideoPool_Draw dst rect: dst=(0,0,-1,-1)
                            // means "stretch to visual" → Visual rect
                            // is the destination; explicit dst means
                            // the rect is anchored at the Visual's pos
                            // but sized by dst.)
                            if (_videoDrawByRb.TryGetValue(rbH, out var vd)) {
                                uint parentH = v.Parent?.Handle ?? 0u;
                                _dumper?.OnEvent($"    [PIP-BINDING] video uid={vd.VideoUid} pool=0x{vd.PoolHandle:X8} rb=0x{rbH:X8} " +
                                                 $"-> vis=0x{v.Handle:X8} size=({v.SizeX:F0}x{v.SizeY:F0}) pos=({v.PosX:F0},{v.PosY:F0}) " +
                                                 $"poolDst=({vd.DstX:F1},{vd.DstY:F1},{vd.DstW:F1},{vd.DstH:F1}) parent=0x{parentH:X8}");
                                _logger?.LogInfo($"[splash] PIP-BINDING video uid={vd.VideoUid} -> vis=0x{v.Handle:X8} " +
                                                 $"size=({v.SizeX:F0}x{v.SizeY:F0}) pos=({v.PosX:F0},{v.PosY:F0}) " +
                                                 $"— this is where to position the video element");
                                // ---- Definitive capture ----
                                // Hand the bound Visual to the geometry
                                // tracker. From now on every SetSize /
                                // SetPosition / SetScale on this Visual
                                // or any ancestor refreshes the video
                                // rect and routes through SurfaceRouter.
                                SetVideoBoundVisual(v, vd.PoolHandle, vd.VideoUid);
                            }
                            // ---- PiP-candidate sniffer (fallback) ----
                            // During active video playback, any Visual that
                            // gets a gradient-only fill (no Surface_Draw or
                            // other ops) in a 16:9 aspect window is a
                            // likely PiP-placeholder. Used when there's
                            // no [PIP-BINDING] — i.e. WMC didn't issue a
                            // VideoPool_Draw and instead relies on the
                            // Xbox hardware-overlay convention. From our
                            // captured corpus, the PiP placeholder is a
                            // 256×144 gradient-only Visual at pos=(1,36)
                            // inside a 258×180 tile wrapper.
                            //
                            // Three tiers of evidence:
                            //   tier 1 — broad: any gradient-only Visual
                            //     in 1.55..1.95 aspect, size ≥ 96×54.
                            //     LOGGED as [PIP-CAND] for diagnosis.
                            //   tier 2 — strict: aspect 1.70..1.85 (true
                            //     16:9), vertical gradient (placeholders
                            //     fade vertically), LB.fit ≥ 0.85 (the
                            //     gradient's reference axis matches the
                            //     visual's size — i.e. the gradient was
                            //     SIZED for this rectangle). LOCKED as
                            //     the routing target via VideoPipCandidateChanged.
                            if (_activeVideoInstanceCount > 0
                                && opsBefore == 0
                                && pendingGradients != null
                                && pendingGradients.Count > 0
                                && v.SizeX > 96 && v.SizeY > 54) {
                                double aspect = v.SizeX / (double)v.SizeY;
                                // Widened 2026-06-12: the Recorded-TV video-window
                                // placeholder is a ~4:3 gradient-only box (user-
                                // confirmed: empty box, lighter-blue gradient,
                                // bottom-left), NOT 16:9 — the old 1.55 lower bound
                                // rejected it outright. Accept 4:3..16:9 so it gets
                                // logged. DIAGNOSTIC ONLY — nothing routes off this.
                                if (aspect >= 1.15 && aspect <= 1.95) {
                                    uint parentH = v.Parent?.Handle ?? 0u;
                                    var  lastGrad = pendingGradients[pendingGradients.Count - 1];
                                    double lbFit  = lastGrad?.FitScoreForVisual(v.SizeX, v.SizeY) ?? 0.0;
                                    bool   isVert = lastGrad?.Direction == Objects.SplashGradient.Orientation.Vertical;
                                    uint   gradColor = lastGrad?.ColorMask ?? 0u;
                                    int    gradStops = lastGrad?.Stops.Count ?? 0;
                                    string aspectClass = (aspect >= 1.2 && aspect <= 1.45) ? "4:3"
                                                       : (aspect >= 1.6 && aspect <= 1.95) ? "16:9" : "other";

                                    // Compute absolute rect for diagnosis
                                    // — wanted even on non-locking
                                    // candidates so we can see where
                                    // false-positive shapes land (e.g.
                                    // Home screen carousel tiles vs the
                                    // real Recorded TV PiP).
                                    bool gotAbs = TryComputeVisualAbsoluteRect(v, out var candAbs);
                                    string absTag = gotAbs
                                        ? $"abs=({candAbs.X:F0},{candAbs.Y:F0} {candAbs.Width:F0}x{candAbs.Height:F0})"
                                        : "abs=unresolved";

                                    _dumper?.OnEvent($"    [PIP-CAND] vis=0x{v.Handle:X8} size=({v.SizeX:F0}x{v.SizeY:F0}) pos=({v.PosX:F0},{v.PosY:F0}) " +
                                                     $"{absTag} aspect={aspect:F2}({aspectClass}) bits=0x{v.DataBits:X8} alpha={v.AlphaByte} parent=0x{parentH:X8} " +
                                                     $"grad=({(isVert ? "V" : "H")},color=0x{gradColor:X8},stops={gradStops},LBfit={lbFit:F2}) vfam={(IsVideoFamilyHandle(v.Handle) ? "Y" : "N")}");

                                    // ---- Strict locking ----
                                    //
                                    // The captured corpus has lots of
                                    // PiP-shaped Visuals during active
                                    // video — both on the Recorded TV
                                    // screen (real PiP) AND on the Home
                                    // screen (gallery carousel tiles).
                                    // Geometry alone (256×144 Vertical-
                                    // gradient) can't tell them apart;
                                    // we also need POSITION.
                                    //
                                    // Discriminating signal (revised after
                                    // the May 30 log capture showed the
                                    // real PiP placeholder ships a
                                    // HORIZONTAL gradient, not vertical —
                                    // the earlier "vertical only" filter
                                    // was rejecting the actual signal):
                                    //   * Gradient direction is now
                                    //     informational only (logged but
                                    //     not gated).  The 258×145 16:9
                                    //     visual at (288,502) — true PiP
                                    //     slot — uses a horizontal
                                    //     gradient.
                                    //   * aspect 1.76..1.80 — true 16:9
                                    //     (1.7778). Rejects the 256×148
                                    //     metadata strip (1.73) seen at
                                    //     the same pos=(1,36) within
                                    //     Recent-media tiles.
                                    //   * AlphaByte > 0 — visible
                                    //   * Absolute rect in bottom-left
                                    //     quadrant (Y ≥ host*0.5, X <
                                    //     host*0.5) — Xbox PiP is anchored
                                    //     at the bottom strip's left side
                                    //     in every captured screen.
                                    //   * Absolute rect is mostly
                                    //     on-screen (rejects the
                                    //     scrolled-away carousel tiles
                                    //     whose chain-walked Y can land
                                    //     at negative values like -94).
                                    //
                                    // First strict match per video
                                    // session wins; subsequent rebinds
                                    // of the same shape are ignored
                                    // until the current PIP visual is
                                    // destroyed or video closes.
                                    bool aspectIs16x9 = aspect >= 1.76 && aspect <= 1.80;
                                    bool inBottomLeft = false;
                                    bool mostlyOnScreen = false;
                                    bool inCorner = false;
                                    double hostW = 0, hostH = 0;
                                    if (_host != null) {
                                        if (_uiDispatcher.CheckAccess()) {
                                            hostW = _host.ActualWidth;
                                            hostH = _host.ActualHeight;
                                        } else {
                                            double capW = 0, capH = 0;
                                            try {
                                                _uiDispatcher.Invoke(new Action(() => {
                                                    capW = _host.ActualWidth;
                                                    capH = _host.ActualHeight;
                                                }));
                                            } catch { }
                                            hostW = capW;
                                            hostH = capH;
                                        }
                                    }
                                    // Geometry tests use the RENDERED rect (WPF
                                    // coords via TransformToAncestor) vs the host's
                                    // WPF size — consistent units. The earlier code
                                    // mixed LOGICAL abs coords with WPF host dims,
                                    // which mis-judged "on-screen" and made the
                                    // suspect fire on a carousel tile.
                                    bool gotRen = TryComputeVisualRenderedRect(v, out var renRect);
                                    if (gotRen && hostW > 0 && hostH > 0) {
                                        inBottomLeft = renRect.Y >= hostH * 0.5 && renRect.X < hostW * 0.5;
                                        mostlyOnScreen = renRect.Y >= -8 && renRect.Y + renRect.Height <= hostH + 8
                                                         && renRect.X >= -8 && renRect.X + renRect.Width <= hostW + 8;
                                        // PiP video window = extreme bottom-LEFT
                                        // CORNER, left portion only — excludes the
                                        // recorded-TV carousel tiles (~17% from the
                                        // left, arriving as a stepping column).
                                        inCorner = renRect.X < hostW * 0.10 && renRect.Y > hostH * 0.55
                                                   && renRect.X + renRect.Width < hostW * 0.55;
                                    }
                                    // Strict criteria: aspect 16:9, visible,
                                    // in bottom-left quadrant of screen,
                                    // and mostly on-screen. Direction-of-
                                    // gradient is INFORMATIONAL only — May 30
                                    // capture confirmed the WMC PiP slot
                                    // ships a horizontal gradient (the
                                    // 258×145 at abs=(288,502)). Earlier
                                    // "vertical only" filter rejected that
                                    // exact match.
                                    bool isStrict = aspectIs16x9 && v.AlphaByte > 0
                                                    && inBottomLeft && mostlyOnScreen;
                                    // ---- DIAGNOSTIC-ONLY for now ----
                                    // The May 30 second capture proved the
                                    // 258×145 H-gradient at (288,502) that
                                    // we'd been locking onto is the home-
                                    // screen SELECTOR RING, not the PiP
                                    // placeholder. The selector gets
                                    // destroyed and re-created on every
                                    // navigation (lifespan 7–16 s in the
                                    // capture, three distinct handles in
                                    // a single video session) — diagnostic
                                    // signature of a transient highlight,
                                    // not a stable container.
                                    //
                                    // Until we identify a positive marker
                                    // for the real PiP, the heuristic
                                    // refrains from binding — it logs as
                                    // [PIP-STRICT-MATCH] so we can see what
                                    // it WOULD have locked, plus the
                                    // candidate's lifespan via the new
                                    // CandidateBirth tracking below. The
                                    // SurfaceRouter no longer receives a
                                    // VideoPipCandidateChanged event on
                                    // heuristic match, which means no
                                    // mis-routing during this exploratory
                                    // phase.
                                    // Track lifespan for EVERY gradient-only
                                    // candidate now (not just 16:9). The real
                                    // video window lives the whole time on the
                                    // page and never receives image content, so
                                    // its [PIP-CAND-DEATH] lifespan will read
                                    // LONG-LIVED — distinguishing it from transient
                                    // tiles / selector rings.
                                    RecordPipCandidateBirth(v.Handle, gotAbs ? candAbs : Rect.Empty, isVert, lbFit, aspect);

                                    // ---- PiP video-window BIND (2026-06-12) ----
                                    // The persistent PiP container is a gradient-only
                                    // box in the bottom-left CORNER. Binding it routes
                                    // the video plane to its rendered rect
                                    // (SetVideoBoundVisual -> RefreshVideoRectIfRelevant
                                    // -> VideoPipCandidateChanged -> SurfaceRouter).
                                    // Binds only when nothing is bound yet; the bound
                                    // visual's destroy (Broker_DestroyObject) reverts
                                    // to fullscreen. Always active (no config gate).
                                    if (_videoBoundVisualHandle == 0
                                        && v.AlphaByte > 0
                                        && aspect >= 1.2 && aspect <= 1.95
                                        && gotRen && inCorner
                                        && renRect.Width > 48 && renRect.Height > 32) {
                                        _logger?.LogInfo($"[splash] PIP-CORNER-BIND vis=0x{v.Handle:X8} " +
                                                         $"rendered=({renRect.X:F0},{renRect.Y:F0} {renRect.Width:F0}x{renRect.Height:F0}) " +
                                                         $"aspect={aspect:F2} grad=0x{gradColor:X8} -- routing video to bottom-left PiP window");
                                        _dumper?.OnEvent($"      [PIP-CORNER-BIND] vis=0x{v.Handle:X8} rendered=({renRect.X:F0},{renRect.Y:F0} {renRect.Width:F0}x{renRect.Height:F0})");
                                        SetVideoBoundVisual(v, 0u, 0);
                                    } else if (aspectClass == "4:3" && v.AlphaByte > 0
                                               && inBottomLeft && mostlyOnScreen && gotRen) {
                                        // Broader bottom-left 4:3 box that ISN'T the
                                        // corner — log as diagnostic so we can tune
                                        // the filter if the bind ever misses.
                                        _logger?.LogInfo($"[splash] PIP-4x3-SUSPECT vis=0x{v.Handle:X8} " +
                                                         $"rendered=({renRect.X:F0},{renRect.Y:F0} {renRect.Width:F0}x{renRect.Height:F0}) " +
                                                         $"aspect={aspect:F2} grad=0x{gradColor:X8} inCorner={inCorner} " +
                                                         $"parent=0x{parentH:X8} vfam={(IsVideoFamilyHandle(v.Handle) ? "Y" : "N")} (diagnostic)");
                                        _dumper?.OnEvent($"      [PIP-4x3-SUSPECT] vis=0x{v.Handle:X8} rendered=({renRect.X:F0},{renRect.Y:F0} {renRect.Width:F0}x{renRect.Height:F0}) grad=0x{gradColor:X8} inCorner={inCorner}");
                                    }

                                    // Legacy 16:9 strict-match diagnostic (kept for
                                    // continuity with the earlier captures).
                                    if (isStrict) {
                                        _dumper?.OnEvent($"      [PIP-STRICT-MATCH] vis=0x{v.Handle:X8} absRect=({candAbs.X:F0},{candAbs.Y:F0} {candAbs.Width:F0}x{candAbs.Height:F0}) aspect={aspect:F2} grad={(isVert ? "V" : "H")} host=({hostW:F0}x{hostH:F0}) (NO LOCK — diagnostic)");
                                    }
                                }
                            }
                        } else {
                            _dumper?.OnEvent($"  Visual_SetContent rb=0x{rbH:X8} (not found)");
                        }
                    }
                    break;
                case 24: // Visual_SetVisible (fVisible: u32)
                    if (rdr.Remaining >= 4) {
                        uint fVisible = rdr.ReadU32();
                        v.Visible = fVisible != 0;
                        v.ApplyVisibility();
                        _dumper?.OnEvent($"  Visual_SetVisible={v.Visible}");
                        // Visibility change on the video Visual or an
                        // ancestor: emphatically log — this is one of the
                        // few ways WMC could "hide the video" without
                        // sending position/size. The SurfaceRouter doesn't
                        // currently key on visibility but the signal is
                        // strong evidence about how WMC orchestrates
                        // transitions.
                        if (_videoBoundVisualHandle != 0
                            && IsVideoBoundOrAncestor(v, out int visHops, out _)) {
                            _dumper?.OnEvent($"    [VIDEO-VISIBLE] touched=0x{v.Handle:X8} hops={visHops} visible={v.Visible} (bound=0x{_videoBoundVisualHandle:X8})");
                            _logger?.LogInfo($"[splash] VIDEO-VISIBLE change visible={v.Visible} on 0x{v.Handle:X8} (bound=0x{_videoBoundVisualHandle:X8})");
                        }
                    }
                    break;
                case 26: // Visual_Create — second-stage init, no body
                    _dumper?.OnEvent($"  Visual_Create (post-init)");
                    break;
                default:
                    _dumper?.OnEvent($"  Visual msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    MaybeDumpVfamHex(v.Handle, "  Visual", rdr);
                    // ---- Fishing pass ----
                    // Any unhandled msgid landing on the video-bound Visual
                    // or one of its ancestors is high-signal: it's a wire
                    // message the spec might document that we silently
                    // drop. Dump the full body in hex so we can match it
                    // against MS-RRSP2 §2.2.4.6.x post-mortem. The wider
                    // ancestor reach matters — WMC could push transforms
                    // onto a group container above the video.
                    if (_videoBoundVisualHandle != 0
                        && IsVideoBoundOrAncestor(v, out int unhHops, out _)) {
                        _dumper?.OnEvent($"    [VIDEO-VISUAL-UNHANDLED] touched=0x{v.Handle:X8} hops={unhHops} bound=0x{_videoBoundVisualHandle:X8} msgid={msgid} body={rdr.PeekRemainingHex(128)}");
                        _logger?.LogInfo($"[splash] VIDEO-VISUAL-UNHANDLED msgid={msgid} on 0x{v.Handle:X8} (hops={unhHops} to bound=0x{_videoBoundVisualHandle:X8}) — possible PiP geometry signal");
                    }
                    break;
            }
        }

        // msgid table for RenderBuilder (spec section 2.2.4.5):
        //   0 = Clear   (no body)
        //   1 = Create  (cat: i32 = 0 Pre-scene / 1 In-scene)
        private void DispatchRenderBuilder(SplashRenderBuilder rb, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // RenderBuilder_Clear
                    rb.Clear();
                    _dumper?.OnEvent($"  RenderBuilder_Clear handle=0x{rb.Handle:X8}");
                    break;
                case 1: // RenderBuilder_Create (cat: i32, Pre-scene=0 / In-scene=1)
                    if (rdr.Remaining >= 4) {
                        int cat = rdr.ReadI32();
                        _dumper?.OnEvent($"  RenderBuilder_Create handle=0x{rb.Handle:X8} cat={(cat == 0 ? "Pre-scene" : cat == 1 ? "In-scene" : ("?(" + cat + ")"))}");
                    } else {
                        _dumper?.OnEvent($"  RenderBuilder_Create handle=0x{rb.Handle:X8} (no cat body)");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  RenderBuilder msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // msgid table for Device (spec section 2.2.4.9):
        //   0 = Stop
        //   1 = Restart
        //   2 = DrawLine          (rb, clrLine, flThickness, vStart, vEnd)
        //   3 = DrawOutline       (rb, clrOutline, flThickness, rcfOutline)
        //   4 = DrawSolid         (rb, clrFill, rcfFill)
        //   5 = CreateSurfacePool
        //
        // CRITICAL: each Draw message embeds its *own* RenderBuilder
        // handle (the `rb` field) — no "current RB" state needed. Slice 1
        // mishandled this by reading rect first; we now read rb,
        // colour, then geometry in spec order.
        private void DispatchDevice(SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // Device_Stop
                    _dumper?.OnEvent($"  Device_Stop");
                    break;
                case 1: // Device_Restart
                    _dumper?.OnEvent($"  Device_Restart");
                    break;
                case 2: // Device_DrawLine
                    if (rdr.Remaining >= 4 + 4 + 4 + 12 + 12) {
                        uint rbH    = rdr.ReadU32();
                        uint argb   = rdr.ReadU32();
                        float th    = rdr.ReadFloat32();
                        rdr.ReadVector3(out float x1, out float y1, out _);
                        rdr.ReadVector3(out float x2, out float y2, out _);
                        if (TryResolveRb(rbH, out var rb)) {
                            rb.AddLine(new Point(x1, y1), new Point(x2, y2), ArgbU32ToColor(argb), th);
                        }
                        _dumper?.OnEvent($"  Device_DrawLine rb=0x{rbH:X8} ({x1:F1},{y1:F1})->({x2:F1},{y2:F1}) th={th:F1} argb=0x{argb:X8}");
                    }
                    break;
                case 3: // Device_DrawOutline
                    if (rdr.Remaining >= 4 + 4 + 4 + 16) {
                        uint rbH  = rdr.ReadU32();
                        uint argb = rdr.ReadU32();
                        float th  = rdr.ReadFloat32();
                        float x   = rdr.ReadFloat32(), y = rdr.ReadFloat32(),
                              w   = rdr.ReadFloat32(), h = rdr.ReadFloat32();
                        if (TryResolveRb(rbH, out var rb)) {
                            // WMC sometimes ships (w, h)=(-1, -1) as a
                            // "stretch to bounding visual" sentinel —
                            // same convention as Surface_Draw dst rect.
                            // Mark for paint-time resolution rather than
                            // throwing "non-negative" from new Rect().
                            bool stretch = (w <= 0 || h <= 0);
                            rb.AddOutline(stretch ? Rect.Empty : new Rect(x, y, w, h),
                                          ArgbU32ToColor(argb), th, stretch);
                        }
                        _dumper?.OnEvent($"  Device_DrawOutline rb=0x{rbH:X8} rect=({x:F1},{y:F1},{w:F1},{h:F1}) th={th:F1} argb=0x{argb:X8}");
                    }
                    break;
                case 4: // Device_DrawSolid
                    if (rdr.Remaining >= 4 + 4 + 16) {
                        uint rbH  = rdr.ReadU32();
                        uint argb = rdr.ReadU32();
                        float x   = rdr.ReadFloat32(), y = rdr.ReadFloat32(),
                              w   = rdr.ReadFloat32(), h = rdr.ReadFloat32();
                        if (TryResolveRb(rbH, out var rb)) {
                            // Same fill-to-visual sentinel as Surface_Draw.
                            bool stretch = (w <= 0 || h <= 0);
                            rb.AddSolid(stretch ? Rect.Empty : new Rect(x, y, w, h),
                                        ArgbU32ToColor(argb), stretch);
                        }
                        _dumper?.OnEvent($"  Device_DrawSolid rb=0x{rbH:X8} rect=({x:F1},{y:F1},{w:F1},{h:F1}) argb=0x{argb:X8}");
                    }
                    break;
                case 5: // Device_CreateSurfacePool — idNewSurface u32 + sizeGutterPxl
                    if (rdr.Remaining >= 12) {
                        uint  idNewPool = rdr.ReadU32();
                        // WIRE DEVIATION: spec §2.2.6.6 declares Size as
                        // two single-precision floats, but the WMC wire
                        // empirically ships dimension fields as 32-bit
                        // signed integers (same pattern as the ImageHeader
                        // sizeActual/sizeOriginal fields — see
                        // DispatchRasterizer comment). Float-read produces
                        // denormalized garbage like 1.6E-43 × 3.8E-44.
                        int gutterW = rdr.ReadI32();
                        int gutterH = rdr.ReadI32();
                        // Some XeDevice CreateSurfacePool messages have a
                        // 4-byte tail beyond the 12-B body (size=24 inside
                        // a 28-B frame). The trailing word may be a flag
                        // or alignment padding — we ignore it here.
                        if (!_registry.TryGetObject(idNewPool, out var existing) || !(existing is Objects.SplashSurfacePool)) {
                            _registry.RegisterObject(idNewPool,
                                new Objects.SplashSurfacePool(idNewPool, "SurfacePool"));
                        }
                        _dumper?.OnEvent($"  Device_CreateSurfacePool -> pool=0x{idNewPool:X8} gutter=({gutterW},{gutterH})");
                    }
                    break;
                // ---- XeDevice / Dx9Device-specific msgids ----
                case 7: // XeDevice_CreateVideoPool — 2 cb fields + idNewSurface
                    if (rdr.Remaining >= 12) {
                        rdr.ReadU32(); rdr.ReadU32(); // _priv_objcbOwner, _priv_ctxcbOwner
                        uint idNewVideo = rdr.ReadU32();
                        // Use SurfacePool kind as a stand-in — VideoPool's
                        // wire surface is similar (Allocate, CreateSurface,
                        // SetEmptyColor, ...). The pixel-format choice is
                        // YUV but we don't yet handle dynamic video.
                        //
                        // NOTE: we DON'T extend the video family from
                        // this call. The first XeDevice_CreateVideoPool
                        // at session startup creates a *generic surface
                        // pool* (handle 0x0100001E in the captured corpus)
                        // for the initial DynamicSurfaceFactory_Create-
                        // SurfaceInstance (uid=1) — not a video instance.
                        // Tagging its 0x01 high byte as "video family"
                        // would mark the entire root-window UI namespace
                        // as video and ruin the diagnostic signal. Only
                        // CreateVideoInstance (DSF msgid=1) extends the
                        // family.
                        if (!_registry.TryGetObject(idNewVideo, out var existing2) || !(existing2 is Objects.SplashSurfacePool)) {
                            _registry.RegisterObject(idNewVideo,
                                new Objects.SplashSurfacePool(idNewVideo, "VideoPool"));
                        }
                        _dumper?.OnEvent($"  XeDevice_CreateVideoPool -> pool=0x{idNewVideo:X8}");
                    }
                    break;
                case 9: // XeDevice_CreateGradient — idNewGradient u32
                    if (rdr.Remaining >= 4) {
                        uint idNewGrad = rdr.ReadU32();
                        _registry.RegisterObject(idNewGrad,
                            new Objects.SplashGradient(idNewGrad, "Gradient"));
                        _dumper?.OnEvent($"  XeDevice_CreateGradient -> gradient=0x{idNewGrad:X8}");
                    }
                    break;
                case 10: // XeDevice_DrawNotify (rb, uId)
                    _dumper?.OnEvent($"  XeDevice_DrawNotify (skipped)");
                    break;
                case 11: // XeDevice_EndVideoSurfaceAllocation
                case 12: // XeDevice_BeginVideoSurfaceAllocation
                    _dumper?.OnEvent($"  XeDevice video-surface-alloc msgid={msgid}");
                    break;
                case 13: // XeDevice_Enter3DMode (rb u32)
                    _dumper?.OnEvent($"  XeDevice_Enter3DMode");
                    break;
                case 26: // XeDevice_Create — post-init ping
                    _dumper?.OnEvent($"  XeDevice_Create (post-init)");
                    break;
                default:
                    // Device/XeDevice/Dx9Device default — always include
                    // the body hex during active video. A "video plane
                    // location" hint, if one exists, could plausibly ride
                    // on a Device-class msgid we don't yet recognise.
                    string deviceBodyHex = (_activeVideoInstanceCount > 0)
                        ? $" body={rdr.PeekRemainingHex(128)}"
                        : "";
                    _dumper?.OnEvent($"  Device/XeDevice msgid={msgid} (unhandled, rem={rdr.Remaining}){deviceBodyHex}");
                    break;
            }
        }

        private bool TryResolveRb(uint handle, out SplashRenderBuilder rb) {
            rb = null;
            if (_registry.TryGetObject(handle, out var obj) && obj is SplashRenderBuilder r) {
                rb = r;
                return true;
            }
            _dumper?.OnEvent($"    (RenderBuilder 0x{handle:X8} not found)");
            return false;
        }

        // -------- Surface / SurfacePool / Rasterizer (slice 4) --------

        // msgid table for SurfacePool (spec section 2.2.4.12):
        //   0 = Draw              (rb)
        //   1 = CreateSurface     (idNewSurface: u32)
        //   2 = Free              (no body)
        //   3 = Allocate          (sizePxl: Size 8 B, nOptions: u32 format)
        //   4 = SetEmptyColor     (clrFill: u32 ARGB)
        //   6 = SetPriority       (nPriority: i32)   — NOTE: msgid 5 is unused
        //                                              per spec; SetPriority is 6
        private void DispatchSurfacePool(Objects.SplashSurfacePool pool, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                // ---- THE PiP-POSITIONING SIGNAL ----
                // MS-RRSP2 §2.2.4.12.1 (SurfacePool_Draw) and §2.2.4.13.1
                // (VideoPool_Draw) are the SAME msgid (0) with the SAME body
                // shape: rb (4 B) + rcfSrcPxl (16 B) + rcfDestPxl (16 B).
                //
                // VideoPool's distinction from SurfacePool is purely class-
                // level — both pools are registered as SplashSurfacePool in
                // our object registry (see DispatchDevice case 7) because
                // the wire surface is identical. The destination rectangle
                // (rcfDestPxl) is THE message that tells the client where
                // to composite the pool's content — including the PiP
                // location for a video pool. We've been silently dropping
                // every one of these.
                //
                // The companion Visual_SetContent that binds the
                // SurfacePool_Draw's RenderBuilder to a Visual completes
                // the binding: pool → RB → Visual → screen rectangle.
                case 0:
                    if (rdr.Remaining >= 4 + 16 + 16) {
                        uint  rbH = rdr.ReadU32();
                        float sx  = rdr.ReadFloat32(), sy = rdr.ReadFloat32(),
                              sw  = rdr.ReadFloat32(), sh = rdr.ReadFloat32();
                        float dx  = rdr.ReadFloat32(), dy = rdr.ReadFloat32(),
                              dw  = rdr.ReadFloat32(), dh = rdr.ReadFloat32();
                        bool isVideoPool = false;
                        int  videoUid = -1;
                        foreach (var kv in _dynamicSurfaceByUid) {
                            if (kv.Value.PoolHandle == pool.Handle && kv.Value.IsVideo) {
                                isVideoPool = true;
                                videoUid = kv.Key;
                                break;
                            }
                        }
                        string tag = isVideoPool ? $" [VIDEO-POOL uid={videoUid}]" : "";
                        _dumper?.OnEvent($"  SurfacePool_Draw pool=0x{pool.Handle:X8} rb=0x{rbH:X8} " +
                                         $"src=({sx:F1},{sy:F1},{sw:F1},{sh:F1}) " +
                                         $"dst=({dx:F1},{dy:F1},{dw:F1},{dh:F1}){tag}");
                        // The destination rect IS the PiP signal — when
                        // it's a video pool draw, surface this to the
                        // app log too so SurfaceRouter can react. The
                        // dest rect may be relative to the bound Visual
                        // (dw=-1, dh=-1 in the Surface_Draw idiom means
                        // "stretch to visual"); a Visual_SetContent on
                        // the same RB pins the absolute rectangle.
                        if (isVideoPool) {
                            _logger?.LogInfo($"[splash] VideoPool_Draw uid={videoUid} pool=0x{pool.Handle:X8} " +
                                             $"rb=0x{rbH:X8} dst=({dx:F1},{dy:F1},{dw:F1},{dh:F1}) " +
                                             $"— PiP destination rectangle (spec §2.2.4.13.1)");
                            // Remember so the next Visual_SetContent that
                            // binds this RB to a Visual can name THIS as
                            // the [PIP-BINDING] target — that Visual's
                            // SizeX/SizeY/PosX/PosY then give the absolute
                            // PiP rectangle, completing pool → RB → Visual.
                            _videoDrawByRb[rbH] = new VideoDrawBinding {
                                PoolHandle = pool.Handle,
                                VideoUid   = videoUid,
                                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                            };
                        }
                    }
                    break;
                // VideoPool-only msgids (3.1.5.14.7/.8). These live on the
                // same dispatcher because the pool is registered as
                // SurfacePool in our registry. They never appear on a
                // regular surface pool; if they do here, the pool was a
                // video pool all along.
                case 9: // VideoPool_SetContentOverscan (§2.2.4.13.7) —
                        // body: flContentOverscan (float)
                    if (rdr.Remaining >= 4) {
                        float overscanPct = rdr.ReadFloat32();
                        _dumper?.OnEvent($"  VideoPool_SetContentOverscan pool=0x{pool.Handle:X8} overscan={overscanPct:F3}");
                    }
                    break;
                case 10: // VideoPool_NotifyVideoSizeChanged (§2.2.4.13.8) —
                         // body: sizeTargetPxl (Size 8 B) — empirically ints
                         // on the WMC wire like every other Size field.
                    if (rdr.Remaining >= 8) {
                        int tw = rdr.ReadI32();
                        int th = rdr.ReadI32();
                        _dumper?.OnEvent($"  VideoPool_NotifyVideoSizeChanged pool=0x{pool.Handle:X8} size=({tw}×{th})");
                    }
                    break;
                case 1: // SurfacePool_CreateSurface — pre-register a Surface under this pool
                    if (rdr.Remaining >= 4) {
                        uint idNewSurface = rdr.ReadU32();
                        // The Surface object isn't usually instantiated
                        // via Broker_CreateObject — CreateSurface is the
                        // implicit constructor. Register one now.
                        var s = new Objects.SplashSurface(idNewSurface, "Surface") {
                            PoolHandle = pool.Handle,
                        };
                        _registry.RegisterObject(idNewSurface, s);
                        pool.SurfaceHandles.Add(idNewSurface);
                        _dumper?.OnEvent($"  SurfacePool_CreateSurface pool=0x{pool.Handle:X8} -> surface=0x{idNewSurface:X8}");
                    }
                    break;
                case 2: // SurfacePool_Free
                    foreach (var sh in pool.SurfaceHandles) _registry.RemoveObject(sh);
                    pool.SurfaceHandles.Clear();
                    _dumper?.OnEvent($"  SurfacePool_Free pool=0x{pool.Handle:X8}");
                    break;
                case 4: // SurfacePool_SetEmptyColor (clrFill: u32 ARGB)
                    if (rdr.Remaining >= 4) {
                        uint argb = rdr.ReadU32();
                        pool.EmptyColor = ArgbU32ToColor(argb);
                        _dumper?.OnEvent($"  SurfacePool_SetEmptyColor pool=0x{pool.Handle:X8} argb=0x{argb:X8}");
                    }
                    break;
                case 6: // SurfacePool_SetPriority (nPriority i32)
                    if (rdr.Remaining >= 4) {
                        int pr = rdr.ReadI32();
                        pool.Priority = pr;
                        _dumper?.OnEvent($"  SurfacePool_SetPriority pool=0x{pool.Handle:X8} pri={pr}");
                    }
                    break;
                case 3: // SurfacePool_Allocate (size: 2 dimensions, nOptions: u32)
                    if (rdr.Remaining >= 12) {
                        // WIRE DEVIATION: spec §2.2.6.6 declares Size as
                        // two floats. WMC empirically ships these as
                        // 32-bit ints — reading as float produces
                        // denormalized garbage (~1E-43). Same pattern as
                        // ImageHeader and Device_CreateSurfacePool.
                        int  w      = rdr.ReadI32();
                        int  h      = rdr.ReadI32();
                        uint format = rdr.ReadU32();
                        pool.AllocatedWidth  = w;
                        pool.AllocatedHeight = h;
                        pool.Format          = format;
                        _dumper?.OnEvent($"  SurfacePool_Allocate pool=0x{pool.Handle:X8} size=({w}×{h}) format=0x{format:X8}");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  SurfacePool msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    MaybeDumpVfamHex(pool.Handle, "  SurfacePool", rdr);
                    break;
            }
        }

        /// <summary>
        /// True if <paramref name="surfaceHandle"/> is the display surface
        /// (<c>surScene</c>) of an active VIDEO DynamicSurface instance — and
        /// returns its DMCT uid + content pool. This is the crux of WMC's
        /// pull-style video placement: CreateVideoInstance (§2.2.4.18.2) names
        /// <c>surScene</c> as "the surface to display", and WMC places it on
        /// screen through the ordinary scene-graph path — a <c>Surface_Draw</c>
        /// of <c>surScene</c> into a RenderBuilder, which a later
        /// <c>Visual_SetContent</c> binds to a Visual whose composed transform
        /// IS the video's screen rectangle. (WMC does NOT issue VideoPool_Draw
        /// for this — the pool only holds content; the surface is what gets
        /// drawn.) Matching here lets us prime <see cref="_videoDrawByRb"/> off
        /// the message WMC actually sends.
        /// </summary>
        private bool TryGetVideoSurfaceUid(uint surfaceHandle, out int uid, out uint poolHandle) {
            foreach (var kv in _dynamicSurfaceByUid) {
                if (kv.Value.IsVideo && kv.Value.SurfaceHandle == surfaceHandle) {
                    uid = kv.Key;
                    poolHandle = kv.Value.PoolHandle;
                    return true;
                }
            }
            uid = -1;
            poolHandle = 0;
            return false;
        }

        // msgid table for Surface (spec section 2.2.4.11):
        //   0 = DrawGrid          (rb, flX1..flY2, rcfDest)
        //   1 = Draw              (rb, rcfSrc, rcfDest, fNeverStretch)
        //   2 = RemapContainer    (poolNewContainer)
        //   3 = RemapLocation     (rcContentPxl)
        //   4 = MarkContentValid
        //   5 = Clear             (rcContentPxl, clrFill)
        //   6 = SetRotation
        //   7 = SetStorageSize
        private void DispatchSurface(Objects.SplashSurface surf, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // Surface_DrawGrid
                    if (rdr.Remaining >= 4 + 4 * 4 + 16) {
                        uint  rbH   = rdr.ReadU32();
                        float x1    = rdr.ReadFloat32(), x2 = rdr.ReadFloat32(),
                              y1    = rdr.ReadFloat32(), y2 = rdr.ReadFloat32();
                        float dx    = rdr.ReadFloat32(), dy = rdr.ReadFloat32(),
                              dw    = rdr.ReadFloat32(), dh = rdr.ReadFloat32();
                        if (TryResolveRb(rbH, out var rb) && surf.Bitmap != null) {
                            // Real 9-slice rendering per spec §2.2.4.11.1.
                            // Previously this was a uniform-stretch stub
                            // which squashed shadow/highlight bands
                            // embedded in chrome edges (visible as a gap
                            // between the focus selector and its drop
                            // shadow). x1/x2/y1/y2 are pixel widths of
                            // the left/right/top/bottom edge cells.
                            // Clamp dst dims to >=0 — WPF Rect ctor
                            // throws on negative width/height.
                            double surfW = surf.Width  > 0 ? surf.Width  : 0;
                            double surfH = surf.Height > 0 ? surf.Height : 0;
                            bool gridStretch = (dw <= 0 || dh <= 0);
                            Rect gridDst = gridStretch
                                ? new Rect(0, 0, surfW, surfH)
                                : new Rect(dx, dy, dw, dh);
                            rb.AddNineSliceImage(surf.Bitmap,
                                x1, x2, y1, y2,
                                gridDst, gridStretch);
                        }
                        _dumper?.OnEvent($"  Surface_DrawGrid surf=0x{surf.Handle:X8} rb=0x{rbH:X8} grid=({x1},{x2},{y1},{y2}) dst=({dx},{dy},{dw},{dh})");

                        // Same video-placement priming as Surface_Draw, in case
                        // WMC ever draws the video surScene 9-slice. Unlikely
                        // for a video surface, but harmless and self-documenting.
                        if (TryGetVideoSurfaceUid(surf.Handle, out int vGridUid, out uint vGridPool)) {
                            _videoDrawByRb[rbH] = new VideoDrawBinding {
                                PoolHandle = vGridPool,
                                VideoUid   = vGridUid,
                                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                            };
                            _logger?.LogInfo($"[splash] VIDEO-SURFACE-DRAWGRID uid={vGridUid} surScene=0x{surf.Handle:X8} " +
                                             $"rb=0x{rbH:X8} dst=({dx:F1},{dy:F1},{dw:F1},{dh:F1})");
                            _dumper?.OnEvent($"    [VIDEO-SURFACE-DRAWGRID] uid={vGridUid} surScene=0x{surf.Handle:X8} rb=0x{rbH:X8}");
                        }
                    }
                    break;
                case 1: // Surface_Draw
                    if (rdr.Remaining >= 4 + 16 + 16 + 4) {
                        uint  rbH = rdr.ReadU32();
                        float sx  = rdr.ReadFloat32(), sy = rdr.ReadFloat32(),
                              sw  = rdr.ReadFloat32(), sh = rdr.ReadFloat32();
                        float dx  = rdr.ReadFloat32(), dy = rdr.ReadFloat32(),
                              dw  = rdr.ReadFloat32(), dh = rdr.ReadFloat32();
                        uint  fNeverStretch = rdr.ReadU32();

                        // WMC wires dst=(0,0,-1,-1) as a sentinel meaning
                        // "stretch to fill the target visual's bounds".
                        // We can't resolve "visual bounds" until
                        // Visual_SetContent later assigns this RB to a
                        // specific visual, so we forward the intent via
                        // SplashRenderBuilder.AddImage(stretchToVisual=true)
                        // and the op runs at paint time with the visual
                        // size in hand.
                        //
                        // (An earlier attempt to treat (-1,-1) as "native
                        // size at (dx,dy)" was wrong — the page-root logo
                        // case looked OK but most other rendering went
                        // sideways. WMC really does want stretch.)
                        bool stretchToVisual = (dw <= 0 || dh <= 0);
                        // Source rect carries the same (-1,-1) "natural
                        // size" sentinel WMC uses for the dst. Resolve
                        // it to the surface's actual dimensions before
                        // handing it to WPF — `new Rect(...)` throws
                        // "Width and Height must be non-negative" on
                        // negative dims, killing the whole dispatch.
                        float srcW = sw > 0 ? sw : (surf.Width  > 0 ? surf.Width  : 0);
                        float srcH = sh > 0 ? sh : (surf.Height > 0 ? surf.Height : 0);
                        Rect srcRect = new Rect(sx, sy, srcW, srcH);
                        Rect dstRect = stretchToVisual
                            // Placeholder; final dst is resolved at paint
                            // time once the visual size is known.
                            ? new Rect(0, 0, srcW, srcH)
                            : new Rect(dx, dy, dw, dh);

                        if (TryResolveRb(rbH, out var rb) && surf.Bitmap != null && surf.ContentValid) {
                            rb.AddImage(surf.Bitmap, srcRect, dstRect, stretchToVisual);
                        } else if (surf.Bitmap == null) {
                            _dumper?.OnEvent($"    (surface 0x{surf.Handle:X8} has no bitmap yet — Surface_Draw skipped)");
                        } else if (!surf.ContentValid) {
                            _dumper?.OnEvent($"    (surface 0x{surf.Handle:X8} has bitmap but ContentValid=false — Surface_Draw skipped)");
                        }
                        _dumper?.OnEvent($"  Surface_Draw surf=0x{surf.Handle:X8} rb=0x{rbH:X8} src=({sx},{sy},{sw},{sh}) dst=({dx},{dy},{dw},{dh}) noStretch={fNeverStretch} stretchToVisual={stretchToVisual}");

                        // ---- THE missing video-placement signal ----
                        // If this surface is a video instance's surScene, THIS
                        // Surface_Draw is what places the video on screen (the
                        // surface has no CPU bitmap — it's live video on our D3D
                        // plane — so the render above is correctly skipped; we
                        // only need the rb→Visual binding). Prime _videoDrawByRb
                        // exactly like the VideoPool_Draw path so the following
                        // Visual_SetContent fires [PIP-BINDING] and hands the
                        // bound Visual (its composed transform = the video rect)
                        // to the geometry tracker → SurfaceRouter.
                        if (TryGetVideoSurfaceUid(surf.Handle, out int vSurfUid, out uint vSurfPool)) {
                            _videoDrawByRb[rbH] = new VideoDrawBinding {
                                PoolHandle = vSurfPool,
                                VideoUid   = vSurfUid,
                                DstX = dx, DstY = dy, DstW = dw, DstH = dh,
                            };
                            _logger?.LogInfo($"[splash] VIDEO-SURFACE-DRAW uid={vSurfUid} surScene=0x{surf.Handle:X8} " +
                                             $"rb=0x{rbH:X8} dst=({dx:F1},{dy:F1},{dw:F1},{dh:F1}) " +
                                             $"— video placement via Surface_Draw; next Visual_SetContent pins the rect");
                            _dumper?.OnEvent($"    [VIDEO-SURFACE-DRAW] uid={vSurfUid} surScene=0x{surf.Handle:X8} " +
                                             $"rb=0x{rbH:X8} dst=({dx:F1},{dy:F1},{dw:F1},{dh:F1})");
                        }
                    }
                    break;
                case 2: // Surface_RemapContainer
                    if (rdr.Remaining >= 4) {
                        surf.PoolHandle = rdr.ReadU32();
                        _dumper?.OnEvent($"  Surface_RemapContainer surf=0x{surf.Handle:X8} newPool=0x{surf.PoolHandle:X8}");
                    }
                    break;
                case 3: // Surface_RemapLocation — spec §2.2.4.11.4
                        // Body: rcContentPxl (Rectangle 16 B = x,y,w,h as i32).
                        // Likely candidate for "tell the device where this
                        // surface lives on screen" — log at INFO so we
                        // can see if WMC fires it on the video surface
                        // during a PiP transition (task #178).
                    if (rdr.Remaining >= 16) {
                        int rx = rdr.ReadI32();
                        int ry = rdr.ReadI32();
                        int rw = rdr.ReadI32();
                        int rh = rdr.ReadI32();
                        bool isDyn = IsDynamicSurface(surf.Handle);
                        string tag = isDyn ? " [DYNAMIC-SURFACE]" : "";
                        _logger?.LogInfo($"[splash] Surface_RemapLocation surf=0x{surf.Handle:X8}{tag} -> rect=({rx},{ry},{rw},{rh})");
                        _dumper?.OnEvent($"  Surface_RemapLocation surf=0x{surf.Handle:X8} rect=({rx},{ry},{rw},{rh})");
                        // If this is the dynamic video surface, notify
                        // SurfaceRouter via the screen-rect change event
                        // so FFME repositions.
                        if (isDyn) {
                            RaiseSurfaceScreenRectChanged();
                        }
                    }
                    break;
                case 4: // Surface_MarkContentValid
                    surf.ContentValid = true;
                    _dumper?.OnEvent($"  Surface_MarkContentValid surf=0x{surf.Handle:X8}");
                    break;
                case 5: // Surface_Clear — spec §2.2.4.11.6
                        // Body: rcContentPxl (16 B) + clrFill (u32 ARGB)
                    if (rdr.Remaining >= 20) {
                        int rx = rdr.ReadI32();
                        int ry = rdr.ReadI32();
                        int rw = rdr.ReadI32();
                        int rh = rdr.ReadI32();
                        uint argb = rdr.ReadU32();
                        _dumper?.OnEvent($"  Surface_Clear surf=0x{surf.Handle:X8} rect=({rx},{ry},{rw},{rh}) argb=0x{argb:X8}");
                    }
                    break;
                case 6: // Surface_SetRotation — spec §2.2.4.11.7
                        // Body: dwRotation (u32)
                    if (rdr.Remaining >= 4) {
                        uint rot = rdr.ReadU32();
                        _dumper?.OnEvent($"  Surface_SetRotation surf=0x{surf.Handle:X8} rotation={rot}");
                    }
                    break;
                case 7: // Surface_SetStorageSize — spec §2.2.4.11.8
                        // Body: sizeStoragePxl (Size 8 B)
                    if (rdr.Remaining >= 8) {
                        int sw = rdr.ReadI32();
                        int sh = rdr.ReadI32();
                        bool isDyn = IsDynamicSurface(surf.Handle);
                        string tag = isDyn ? " [DYNAMIC-SURFACE]" : "";
                        _logger?.LogInfo($"[splash] Surface_SetStorageSize surf=0x{surf.Handle:X8}{tag} -> {sw}x{sh}");
                        _dumper?.OnEvent($"  Surface_SetStorageSize surf=0x{surf.Handle:X8} size=({sw}×{sh})");
                    }
                    break;
                default:
                    // Promote any unhandled msgid on a dynamic surface
                    // (video) to INFO so we don't miss a PiP-position
                    // signal hidden behind a quiet "msgid=N unhandled"
                    // dump line.
                    if (IsDynamicSurface(surf.Handle)) {
                        _logger?.LogInfo($"[splash] Surface msgid={msgid} on DYNAMIC-SURFACE 0x{surf.Handle:X8} (unhandled, rem={rdr.Remaining}) — possible PiP-position signal?");
                    }
                    _dumper?.OnEvent($"  Surface msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    MaybeDumpVfamHex(surf.Handle, "  Surface", rdr);
                    break;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the given splash object handle is
        /// currently registered as a DynamicSurfaceFactory video / surface
        /// instance — i.e. it's a candidate target for "WMC just told us
        /// the video should move" signals.
        /// </summary>
        private bool IsDynamicSurface(uint handle) {
            foreach (var kv in _dynamicSurfaceByUid) {
                if (kv.Value.SurfaceHandle == handle) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns <c>true</c> when the given handle is either a registered
        /// dynamic-surface instance OR its owning pool — used by the
        /// dispatch-entry trace to catch ANY wire activity on the
        /// video-related handles.
        /// </summary>
        private bool IsDynamicSurfaceOrPool(uint handle) {
            foreach (var kv in _dynamicSurfaceByUid) {
                if (kv.Value.SurfaceHandle == handle) return true;
                if (kv.Value.PoolHandle    == handle) return true;
            }
            return false;
        }

        // msgid table for Rasterizer (spec section 2.2.4.14):
        //   0 = LoadRawImage      (surContent, buffer, ImageHeader[24], Point[8])
        private void DispatchRasterizer(SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // Rasterizer_LoadRawImage
                    if (rdr.Remaining >= 4 + 4 + 24 + 8) {
                        uint surContent  = rdr.ReadU32();
                        uint bufferH     = rdr.ReadU32();

                        // Capture the raw 32-byte info+offset block so we
                        // can see exactly what bytes are there — slice 4
                        // first attempt parsed sizeActual/Original as
                        // floats per the ImageHeader spec §2.2.6.7
                        // (which references Size §2.2.6.6 = 2 floats)
                        // but they came out as 0 on the WMC wire even
                        // though stride/format read correctly.
                        int  rawStart = rdr.Position;
                        byte[] rawInfo = new byte[32];
                        for (int i = 0; i < 32; i++) rawInfo[i] = rdr.ReadByte();
                        rdr.Seek(rawStart);

                        // ImageHeader on the WMC wire is laid out as eight
                        // int32 fields — NOT four floats + two ints as
                        // spec §2.2.6.7 suggests. Confirmed by hex dump:
                        //   00 00 01 F4  = 500  (sizeActual.width)
                        //   00 00 00 CA  = 202  (sizeActual.height)
                        //   00 00 01 F4  = 500  (sizeOriginal.width)
                        //   00 00 00 CA  = 202  (sizeOriginal.height)
                        //   00 00 07 D0  = 2000 (nStride)
                        //   00 20 88 88  = ARGB32 (nFormat)
                        //   00 00 00 00  = offX
                        //   00 00 00 00  = offY
                        int   actW   = rdr.ReadI32(), actH  = rdr.ReadI32();
                        int   origW  = rdr.ReadI32(), origH = rdr.ReadI32();
                        int   stride = rdr.ReadI32();
                        int   format = rdr.ReadI32();
                        int   offX   = rdr.ReadI32();
                        int   offY   = rdr.ReadI32();
                        _dumper?.OnEvent($"    rawInfo+offset[32]={Hex(rawInfo)}");

                        // Belt-and-braces stride-based fallback in case
                        // some image header arrives with the size fields
                        // genuinely zeroed.
                        if ((actW <= 0 || actH <= 0) && stride > 0
                            && _registry.TryGetObject(bufferH, out var bufObjPre)
                            && bufObjPre is Objects.SplashDataBuffer dbPre) {
                            int inferredH = dbPre.Bytes.Length / stride;
                            int inferredW = stride / 4; // assumes 32-bit format
                            if (inferredW > 0 && inferredH > 0) {
                                actW = inferredW;
                                actH = inferredH;
                                _dumper?.OnEvent($"    (size inferred from stride: {inferredW}×{inferredH})");
                            }
                        }

                        // Auto-register the destination Surface if it wasn't
                        // previously created via Broker_CreateObject or
                        // SurfacePool_CreateSurface. The WMC server creates
                        // most of its Surfaces through Xenon-specific Device
                        // methods (XeDevice msgid=7) and implicit construction
                        // blobs we don't model — so by the time
                        // Rasterizer_LoadRawImage fires, the surface handle
                        // is real on the server but unknown on our side.
                        // LoadRawImageInto will create the WriteableBitmap
                        // backing on first hit, so a default-constructed
                        // Surface is enough.
                        Objects.SplashSurface surf;
                        if (_registry.TryGetObject(surContent, out var surfObj) && surfObj is Objects.SplashSurface s) {
                            surf = s;
                        } else {
                            surf = new Objects.SplashSurface(surContent, "Surface");
                            _registry.RegisterObject(surContent, surf);
                            _dumper?.OnEvent($"  (auto-registered Surface 0x{surContent:X8} for Rasterizer)");
                        }
                        if (!(_registry.TryGetObject(bufferH, out var bufObj) && bufObj is Objects.SplashDataBuffer db)) {
                            _dumper?.OnEvent($"  Rasterizer_LoadRawImage: buffer 0x{bufferH:X8} not found");
                            return;
                        }

                        int pxW = actW, pxH = actH;
                        LoadRawImageInto(surf, db, pxW, pxH, stride, (uint)format, offX, offY);
                        _dumper?.OnEvent($"  Rasterizer_LoadRawImage surf=0x{surContent:X8} buf=0x{bufferH:X8} {pxW}×{pxH} stride={stride} fmt=0x{format:X8} off=({offX},{offY}) bytes={db.Bytes.Length}");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Rasterizer msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- Animation / AnimationManager (slice 5) --------

        // msgid table for AnimationManager (spec section 2.2.4.7):
        //   0x03 (3)  BuildGradientColorMaskAnimation
        //   0x04 (4)  BuildGradientOffsetAnimation
        //   0x05 (5)  BuildRotationAnimation
        //   0x06 (6)  BuildSizeAnimation
        //   0x07 (7)  BuildScaleAnimation
        //   0x08 (8)  BuildPositionAnimation
        //   0x09 (9)  BuildColorAnimation
        //   0x0A (10) BuildAlphaAnimation
        //   0x0B (11) Create  (post-init, no body)
        //
        // Each BuildXxx body is (viSubject u32, idAnimation u32) — creates
        // a new Animation object with handle = idAnimation, tweening the
        // named property on the given visual.
        private void DispatchAnimationManager(SplashPayloadReader rdr, int msgid) {
            Objects.AnimationKind kind = msgid >= 3 && msgid <= 10
                ? new[] {
                    Objects.AnimationKind.Unknown,         // 0 (unused)
                    Objects.AnimationKind.Unknown,         // 1 (unused)
                    Objects.AnimationKind.Unknown,         // 2 (unused)
                    Objects.AnimationKind.GradientColorMask, // 3
                    Objects.AnimationKind.GradientOffset,    // 4
                    Objects.AnimationKind.Rotation,          // 5
                    Objects.AnimationKind.Size,              // 6
                    Objects.AnimationKind.Scale,             // 7
                    Objects.AnimationKind.Position,          // 8
                    Objects.AnimationKind.Color,             // 9
                    Objects.AnimationKind.Alpha,             // 10
                }[msgid]
                : Objects.AnimationKind.Unknown;

            if (msgid >= 3 && msgid <= 10) {
                if (rdr.Remaining >= 8) {
                    uint viSubject   = rdr.ReadU32();
                    uint idAnimation = rdr.ReadU32();
                    if (!_registry.TryGetObject(idAnimation, out var existing) || !(existing is Objects.SplashAnimation)) {
                        _registry.RegisterObject(idAnimation,
                            new Objects.SplashAnimation(idAnimation, kind, viSubject));
                    }
                    _dumper?.OnEvent($"  AnimationManager_Build{kind}Animation viSubject=0x{viSubject:X8} anim=0x{idAnimation:X8}");
                }
                return;
            }
            if (msgid == 11) {
                _dumper?.OnEvent($"  AnimationManager_Create (post-init)");
                return;
            }
            _dumper?.OnEvent($"  AnimationManager msgid={msgid} (unhandled, rem={rdr.Remaining})");
        }

        // msgid table for Animation (spec section 2.2.4.17). Each
        // per-keyframe op carries an idxKeyframe (i32) plus type-
        // specific value(s); easing setters also accept curve weights.
        //   0x00 ( 0) AddCompletionLink   (aniToPlayNext u32)               §17.1
        //   0x01 ( 1) SetEaseOut          (idx + flWeight + flHandle)       §17.2
        //   0x02 ( 2) SetEaseIn           (idx + flWeight + flHandle)       §17.3
        //   0x03 ( 3) SetBezier           (idx + flHandle1 + flHandle2)     §17.4
        //   0x04 ( 4) SetCosine           (idx)                             §17.5
        //   0x05 ( 5) SetSine             (idx)                             §17.6
        //   0x06 ( 6) SetSCurve           (idx + flWeight)                  §17.7
        //   0x07 ( 7) SetLogarithmic      (idx + flWeight)                  §17.8
        //   0x08 ( 8) SetLinear           (idx)                             §17.9
        //   0x09 ( 9) SetExponential      (idx + flWeight)                  §17.10
        //   0x0A (10) SetDynamicRotation  (idx)                             §17.11
        //   0x0B (11) SetRotation         (idx + Rotation16)                §17.12
        //   0x0C (12) SetColorF           (idx + ColorF16)                  §17.13
        //   0x0D (13) SetDynamicARGBColor (idx)                             §17.14
        //   0x0E (14) SetDynamicRGBColor  (idx + fMultiply u32)             §17.15
        //   0x0F (15) SetARGBColor        (idx + clr u32)                   §17.16
        //   0x10 (16) SetRGBColor         (idx + clr u32)                   §17.17
        //   0x11 (17) SetDynamicVector3   (idx + fMultiply u32)             §17.18
        //   0x12 (18) SetVector3          (idx + Vector3 12B)               §17.19
        //   0x13 (19) SetDynamicFloat     (idx + fMultiply u32)             §17.20
        //   0x14 (20) SetFloat            (idx + flValue)                   §17.21
        //   0x15 (21) RemoveCallback      (_objcb u32, _ctxcb u32)          §17.22
        //   0x16 (22) AddCallback         (_objcb u32, _ctxcb u32)          §17.23
        //   0x17 (23) AddKeyframe         (idx + flTimeSec)                 §17.24
        //   0x18 (24) Stop                (cmd i32)                         §17.25
        //   0x1A (26) Play                (no body)                         §17.26
        //   0x1B (27) SetStopCommand      (cmd i32)                         §17.27
        //   0x1D (29) SetAutoStop         (fAutoStop u32)                   §17.28
        //   0x1E (30) SetRepeatCount      (cRepeats i32)                    §17.29
        //   0x21 (33) SetKeyframeTime     (idx + flTimeSec)                 §17.30
        //   0x23 (35) SetKeyframeCount    (cKeyframes i32)                  §17.31
        // (msgid=31/0x1F appears 170× in the wire with body=4; not in
        // spec but harmless to acknowledge.)
        private void DispatchAnimation(Objects.SplashAnimation anim, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                // ---- Lifecycle linkage -----------------------------------
                case 0:  // AddCompletionLink: aniToPlayNext u32 (chain animations)
                    if (rdr.Remaining >= 4) {
                        uint nextAnim = rdr.ReadU32();
                        anim.NextOnComplete = nextAnim;
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_AddCompletionLink anim=0x{anim.Handle:X8} -> next=0x{nextAnim:X8}");
                    }
                    break;
                // ---- Easing curve setters (per-keyframe) -----------------
                case 1:  // SetEaseOut: idxKeyframe + flWeight + flHandle
                case 2:  // SetEaseIn:  same layout
                    if (rdr.Remaining >= 12) {
                        int   idx = rdr.ReadI32();
                        float w   = rdr.ReadFloat32();
                        float h   = rdr.ReadFloat32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].Easing = msgid == 1 ? Objects.AnimationEasing.EaseOut : Objects.AnimationEasing.EaseIn;
                            kfs[idx].EaseP1 = w;
                            kfs[idx].EaseP2 = h;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_{(msgid == 1 ? "SetEaseOut" : "SetEaseIn")} anim=0x{anim.Handle:X8} kf[{idx}] flWeight={w:0.0000} flHandle={h:0.0000}");
                    }
                    break;
                case 3:  // SetBezier: idxKeyframe + flHandle1 + flHandle2
                    if (rdr.Remaining >= 12) {
                        int   idx = rdr.ReadI32();
                        float h1  = rdr.ReadFloat32();
                        float h2  = rdr.ReadFloat32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].Easing = Objects.AnimationEasing.Bezier;
                            kfs[idx].EaseP1 = h1;
                            kfs[idx].EaseP2 = h2;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetBezier anim=0x{anim.Handle:X8} kf[{idx}] flHandle1={h1:0.0000} flHandle2={h2:0.0000}");
                    }
                    break;
                case 4:  // SetCosine  (idx)
                case 5:  // SetSine    (idx)
                    if (rdr.Remaining >= 4) {
                        int idx = rdr.ReadI32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].Easing = msgid == 4 ? Objects.AnimationEasing.Cosine
                                            :              Objects.AnimationEasing.Sine;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_{(msgid == 4 ? "SetCosine" : "SetSine")} anim=0x{anim.Handle:X8} kf[{idx}]");
                    }
                    break;
                case 6:  // SetSCurve       (idx + flWeight)
                case 7:  // SetLogarithmic  (idx + flWeight)
                case 9:  // SetExponential  (idx + flWeight)
                    if (rdr.Remaining >= 8) {
                        int   idx = rdr.ReadI32();
                        float w   = rdr.ReadFloat32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].Easing = msgid == 6 ? Objects.AnimationEasing.SCurve
                                            : msgid == 7 ? Objects.AnimationEasing.Logarithmic
                                            :              Objects.AnimationEasing.Exponential;
                            kfs[idx].EaseP1 = w; // weight (curve steepness)
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_{(msgid == 6 ? "SetSCurve" : msgid == 7 ? "SetLogarithmic" : "SetExponential")} anim=0x{anim.Handle:X8} kf[{idx}] flWeight={w:0.0000}");
                    }
                    break;
                // ---- Value setters ---------------------------------------
                case 10: // SetDynamicRotation: idxKeyframe ONLY (4 B body)
                    if (rdr.Remaining >= 4) {
                        int idx = rdr.ReadI32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].IsDynamic = true;
                            // No fMultiply on this dynamic setter per spec §17.11.
                        }
                    }
                    break;
                case 11: // SetRotation: idxKeyframe + Rotation (16 B) = 20 B body
                    if (rdr.Remaining >= 20) {
                        int idx = rdr.ReadI32();
                        rdr.ReadVector3(out float rx, out float ry, out float rz);
                        // Wire ships flAngle in RADIANS despite spec
                        // wording — convert to degrees for the rest of
                        // the pipeline. See Visual_SetRotation for the
                        // detailed rationale.
                        float angle = rdr.ReadFloat32() * (180f / (float)Math.PI);
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].VecX = rx; kfs[idx].VecY = ry; kfs[idx].VecZ = rz;
                            kfs[idx].RotAngleDeg = angle;
                        }
                    }
                    break;
                case 12: // SetColorF: idxKeyframe + ColorF (16 B = 4×float a,r,g,b in 0..1) = 20 B body
                    if (rdr.Remaining >= 20) {
                        int idx = rdr.ReadI32();
                        rdr.ReadColorF(out float ca, out float cr, out float cg, out float cb);
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            // Pack the 0..1 floats back into the ARGB u32
                            // so the rest of the pipeline (ApplyAnimationValue,
                            // LerpArgb) treats SetColorF and SetARGBColor
                            // keyframes uniformly.
                            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(ca * 255f)));
                            byte r = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(cr * 255f)));
                            byte g = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(cg * 255f)));
                            byte b = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(cb * 255f)));
                            kfs[idx].ArgbValue = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                        }
                    }
                    break;
                case 13: // SetDynamicARGBColor: idxKeyframe ONLY (4 B body) per spec §17.14
                    if (rdr.Remaining >= 4) {
                        int idx = rdr.ReadI32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].IsDynamic = true;
                            // No fMultiply on this dynamic setter per spec §17.14.
                        }
                    }
                    break;
                case 14: // SetDynamicRGBColor: idxKeyframe + fMultiply (8 B body) per spec §17.15
                case 17: // SetDynamicVector3:  idxKeyframe + fMultiply (8 B body) per spec §17.18
                case 19: // SetDynamicFloat:    idxKeyframe + fMultiply (8 B body) per spec §17.20
                    // Per spec §17.18/§17.20, fMultiply is u32 ("indicates
                    // whether the values can be multiplied or added"). The
                    // wire-decoded value is 0 for Position scrolls; Float
                    // (alpha) animations carry small integers in 14..21
                    // and occasional NaN-pattern u32s. We don't have a
                    // confirmed semantic mapping for the non-zero cases
                    // yet, so we log the raw value and continue treating
                    // the keyframe as "snap to the visual's current
                    // property value" at Play time (see
                    // ResolveDynamicKeyframes). That matches the empirical
                    // scroll behavior.
                    if (rdr.Remaining >= 8) {
                        int  idx = rdr.ReadI32();
                        uint fm  = rdr.ReadU32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].IsDynamic = true;
                            kfs[idx].DynamicMultiply = fm;
                        }
                        string name = msgid == 14 ? "SetDynamicRGBColor"
                                    : msgid == 17 ? "SetDynamicVector3"
                                    :               "SetDynamicFloat";
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_{name} anim=0x{anim.Handle:X8} kf[{idx}] fMultiply=0x{fm:X8}");
                    }
                    break;
                case 15: // SetARGBColor: idxKeyframe + ARGB u32
                case 16: // SetRGBColor: idxKeyframe + RGB u32 (we treat as ARGB)
                    if (rdr.Remaining >= 8) {
                        int  idx  = rdr.ReadI32();
                        uint argb = rdr.ReadU32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].ArgbValue = argb;
                        }
                    }
                    break;
                case 18: // SetVector3: idxKeyframe + Vector3 = 16 B body
                    if (rdr.Remaining >= 16) {
                        int idx = rdr.ReadI32();
                        rdr.ReadVector3(out float vx, out float vy, out float vz);
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].VecX = vx; kfs[idx].VecY = vy; kfs[idx].VecZ = vz;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetVector3 anim=0x{anim.Handle:X8} kf[{idx}] vec=({vx:F3},{vy:F3},{vz:F3})");
                    }
                    break;
                case 20: // SetFloat: idxKeyframe + flValue
                    if (rdr.Remaining >= 8) {
                        int   idx = rdr.ReadI32();
                        float fl  = rdr.ReadFloat32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].FloatValue = fl;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetFloat anim=0x{anim.Handle:X8} kf[{idx}] flValue={fl:F4}");
                    }
                    break;
                // ---- Lifecycle -------------------------------------------
                case 21: // RemoveCallback: _objcb u32, _ctxcb u32 (body=8) per spec §17.22
                    if (rdr.Remaining >= 8) {
                        uint objcb = rdr.ReadU32();
                        uint ctxcb = rdr.ReadU32();
                        // Only forget the callback if the (objcb, ctxcb)
                        // pair matches what we registered. Defensive in
                        // case the server uses multiple distinct callbacks
                        // (only one is supported per animation in our model).
                        if (anim.CallbackObj == objcb && anim.CallbackCtx == ctxcb) {
                            anim.CallbackObj = 0;
                            anim.CallbackCtx = 0;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_RemoveCallback anim=0x{anim.Handle:X8} objcb=0x{objcb:X8} ctxcb=0x{ctxcb:X8}");
                    }
                    break;
                case 22: // AddCallback: _objcb u32, _ctxcb u32 (body=8) per spec §17.23
                    if (rdr.Remaining >= 8) {
                        uint objcb = rdr.ReadU32();
                        uint ctxcb = rdr.ReadU32();
                        // Record both — needed at Stop time to send
                        // LocalAnimationCallback_OnComplete back to the server.
                        anim.CallbackObj = objcb;
                        anim.CallbackCtx = ctxcb;
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_AddCallback anim=0x{anim.Handle:X8} objcb=0x{objcb:X8} ctxcb=0x{ctxcb:X8}");
                    } else {
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_AddCallback anim=0x{anim.Handle:X8}");
                    }
                    break;
                case 23: // AddKeyframe: idxKeyframe + flTimeSec
                    if (rdr.Remaining >= 8) {
                        int   idx = rdr.ReadI32();
                        float t   = rdr.ReadFloat32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].TimeSec = t;
                            // Spec has a separate SetKeyframeCount (msgid=35)
                            // but WMC never calls it — instead it adds
                            // keyframes one by one via AddKeyframe and
                            // expects the count to grow implicitly.
                            if (idx + 1 > anim.KeyframeCount) anim.KeyframeCount = idx + 1;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_AddKeyframe anim=0x{anim.Handle:X8} kf[{idx}] t={t:F3}s");
                    }
                    break;
                case 8:  // SetLinear (spec §17.9, body=4 idxKeyframe).
                         // Explicitly resets the keyframe's easing to
                         // linear. Default-easing is also linear, so this
                         // is a no-op for any keyframe that hasn't been
                         // overridden by a later setter — but we still
                         // honour it so that "explicit Linear after
                         // SetEaseOut" reverts the curve correctly.
                    if (rdr.Remaining >= 4) {
                        int idx = rdr.ReadI32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].Easing = Objects.AnimationEasing.Linear;
                            kfs[idx].EaseP1 = 0;
                            kfs[idx].EaseP2 = 0;
                        }
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetLinear anim=0x{anim.Handle:X8} kf[{idx}]");
                    }
                    break;
                case 24: // Stop (cmd i32) — per spec §17.25, cmd OVERRIDES the
                         // SetStopCommand default for this call only.
                         //   0 = leave value where it is
                         //   1 = snap to keyframe 0
                         //   2 = snap to last keyframe (END)
                         // Critical for "snap to end" fade-outs: without
                         // reading cmd, a Stop arriving mid-fade leaves
                         // the visual stuck at whatever alpha the
                         // animation reached, instead of jumping to the
                         // final (usually 0 = invisible) value.
                    if (rdr.Remaining >= 4) {
                        int cmd = rdr.ReadI32();
                        StopAnimation(anim, cmd);
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_Stop anim=0x{anim.Handle:X8} cmd={cmd}");
                    } else {
                        // Some encoders elide the body even though spec
                        // mandates it — fall back to the stored default.
                        StopAnimation(anim);
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_Stop anim=0x{anim.Handle:X8} (no cmd)");
                    }
                    break;
                case 25: // Animation msgid=0x19 (25) — NOT in any published
                         // MS-RRSP2 revision (verified across all 8 PDFs
                         // 2013-2017; msgid catalogue tops out at 0x23 and
                         // 0x19 is one of the gap slots, alongside 0x1C
                         // 0x1F 0x20 0x22). Treating it as "Stop with the
                         // stored SetStopCommand" — by analogy with
                         // msgid=24 (Stop with explicit cmd) but without
                         // a body — produced visually correct results
                         // across the home / EPG / settings / Recorded TV
                         // / FORMULA 1 flows. Wire pattern is unambiguous:
                         //   * Body=0 (no cmd override)
                         //   * Fires terminally — anims receiving msgid=25
                         //     never get Played again
                         //   * Fires when WMC starts NEW animations on the
                         //     same target visual (so the old animation
                         //     needs to be torn down)
                         // Confirmed NOT the cause of a navigation-transition
                         // bleed-through fragment we suspected earlier
                         // (verified by toggling this to a no-op and
                         // re-running; the fragment was still present,
                         // which turned out to be a Movies-row tile-strip
                         // mask leak — a "y" descender from "movie library"
                         // poking past the row OpacityMask. Tracked
                         // separately.).
                    StopAnimation(anim);
                    _dumper?.OnEvent($"  Animation[{anim.Kind}]_StopDefault anim=0x{anim.Handle:X8} cmd={anim.StopCommand} (msgid=25, no body)");
                    break;
                case 26: // Play
                    StartAnimation(anim);
                    _dumper?.OnEvent($"  Animation[{anim.Kind}]_Play anim=0x{anim.Handle:X8} target=0x{anim.TargetVisual:X8} kfs={anim.KeyframeCount}");
                    break;
                case 27: // SetStopCommand
                    if (rdr.Remaining >= 4) {
                        anim.StopCommand = rdr.ReadI32();
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetStopCommand anim=0x{anim.Handle:X8} cmd={anim.StopCommand}");
                    }
                    break;
                case 29: // SetAutoStop
                    if (rdr.Remaining >= 4) {
                        uint fAutoStop = rdr.ReadU32();
                        anim.AutoStop = fAutoStop != 0;
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetAutoStop anim=0x{anim.Handle:X8} fAutoStop={fAutoStop}");
                    }
                    break;
                case 30: // SetRepeatCount
                    if (rdr.Remaining >= 4) {
                        anim.RepeatCount = rdr.ReadI32();
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetRepeatCount anim=0x{anim.Handle:X8} cRepeats={anim.RepeatCount}");
                    }
                    break;
                case 31: // SetRepeatCount (de-facto). The documented
                         // SetRepeatCount at msgid=0x1E (30) is NEVER
                         // observed on the wire; WMC unconditionally
                         // ships its repeat count via msgid=0x1F (31)
                         // instead. Body is i32: -1 = infinite loop,
                         // 0 = play once (matches our defensive
                         // interpretation), N>0 = repeat N times.
                         // The wait-cursor spinner's rotation animation
                         // depends on this being honored — without it,
                         // the rotation plays once for 0.5s then stops
                         // and the spinner just sits there.
                    if (rdr.Remaining >= 4) {
                        int cRepeats = rdr.ReadI32();
                        anim.RepeatCount = cRepeats;
                        _dumper?.OnEvent($"  Animation[{anim.Kind}]_SetRepeatCount(0x1F) anim=0x{anim.Handle:X8} cRepeats={cRepeats}");
                    }
                    break;
                case 33: // SetKeyframeTime: idxKeyframe + flTimeSec (override time)
                    if (rdr.Remaining >= 8) {
                        int   idx = rdr.ReadI32();
                        float t   = rdr.ReadFloat32();
                        var kfs = anim.EnsureKeyframeAt(idx);
                        if (kfs != null && idx >= 0 && idx < kfs.Length) {
                            kfs[idx].TimeSec = t;
                        }
                    }
                    break;
                case 35: // SetKeyframeCount
                    if (rdr.Remaining >= 4) {
                        anim.KeyframeCount = rdr.ReadI32();
                        anim.EnsureKeyframeAt(anim.KeyframeCount - 1);
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Animation[{anim.Kind}] msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- Animation playback engine (slice 5.2) --------

        /// <summary>
        /// Recursively re-applies the effective opacity to descendants
        /// after a Position change. ComputeEffectiveOpacity has hide
        /// rules that read ancestor PosX values, so an animated scroll
        /// at a parent visual must re-evaluate the bit-marked level-2
        /// wrappers below it.
        ///
        /// Depth-limited (defaults to 3 — enough to reach the level-2
        /// wrapper from the scroll container 2 levels above) so we
        /// don't walk the entire scene on every tick.
        /// </summary>
        private static void ReapplyAlphaToDescendants(SplashVisual v, int depth) {
            if (v == null || depth <= 0) return;
            for (int i = 0; i < v.Children.Count; i++) {
                var child = v.Children[i];
                child.ApplyAlpha();
                if (depth > 1) ReapplyAlphaToDescendants(child, depth - 1);
            }
        }

        private void StartAnimation(Objects.SplashAnimation anim) {
            EnsureAnimTickHooked();
            if (!_animClock.IsRunning) _animClock.Start();
            // Batch-synchronised StartTimeMs: every Play inside a single
            // DispatchBatch shares ONE timestamp, captured at the first
            // Play within that batch. Animations the wire intended to
            // start in lock-step (e.g. all show-anims for a single
            // navigation: row-A fade-out + row-B slide-in + spinner
            // fade-in + tile rotation) therefore see identical progress
            // values on every tick. Without this, each anim captured
            // its own _animClock.Elapsed at message-dispatch time and
            // they drifted by a few ms — visible as a Movies-row "y"
            // descender peeking past the new-row mask during the brief
            // window where the fade-out lagged the slide-in.
            //
            // Outside a batch (e.g. anim chained from OnComplete on the
            // anim tick), use the fresh clock — those are standalone
            // chains, not co-scheduled wire events.
            double startMs;
            if (_batchDepth > 0) {
                if (!_batchAnimStartLatched) {
                    _batchAnimStartMs = _animClock.Elapsed.TotalMilliseconds;
                    _batchAnimStartLatched = true;
                }
                startMs = _batchAnimStartMs;
            } else {
                startMs = _animClock.Elapsed.TotalMilliseconds;
            }
            anim.StartTimeMs = startMs;
            anim.CompletedRepeats = 0;
            anim.Playing = true;
            anim.OnCompleteFired = false; // re-arm for this Play cycle
            ResolveDynamicKeyframes(anim);
            BuildSortedKeyframeIndices(anim);
            _playingAnimations.Add(anim);
        }

        /// <summary>
        /// Build <see cref="Objects.SplashAnimation.SortedIndices"/> so that
        /// SortedIndices[0] is the index of the earliest-time keyframe,
        /// SortedIndices[N-1] is the latest. WMC routinely ships a
        /// "start value" keyframe (t=0, v=0) as the LAST index — without
        /// sorting, our tick loop treats it as the final frame and the
        /// animation snaps to invisible immediately.
        /// </summary>
        private static void BuildSortedKeyframeIndices(Objects.SplashAnimation a) {
            int n = a.KeyframeCount;
            if (a.Keyframes == null || n <= 0) {
                a.SortedIndices = null;
                return;
            }
            if (a.SortedIndices == null || a.SortedIndices.Length != n) {
                a.SortedIndices = new int[n];
            }
            for (int i = 0; i < n; i++) a.SortedIndices[i] = i;
            // Stable insertion sort by TimeSec — n is small (typically
            // 2-8 keyframes), so O(n²) is fine and stability matters when
            // multiple keyframes share the same time.
            var kfs = a.Keyframes;
            for (int i = 1; i < n; i++) {
                int cur = a.SortedIndices[i];
                float curT = kfs[cur].TimeSec;
                int j = i - 1;
                while (j >= 0 && kfs[a.SortedIndices[j]].TimeSec > curT) {
                    a.SortedIndices[j + 1] = a.SortedIndices[j];
                    j--;
                }
                a.SortedIndices[j + 1] = cur;
            }
        }

        /// <summary>
        /// Replace any keyframe marked <see cref="Objects.AnimationKeyframe.IsDynamic"/>
        /// with a value sampled from the target visual's current property
        /// state at Play time. This is how the WMC server's
        /// <c>Animation_SetDynamicXxx</c> messages declare keyframes
        /// whose value depends on the visual's runtime state — e.g. a
        /// "fade from current alpha" or "slide from current position".
        /// </summary>
        private void ResolveDynamicKeyframes(Objects.SplashAnimation a) {
            if (a.Keyframes == null || a.KeyframeCount <= 0) return;
            if (!_registry.TryGetObject(a.TargetVisual, out var obj) || !(obj is SplashVisual v)) return;
            for (int i = 0; i < a.KeyframeCount; i++) {
                if (!a.Keyframes[i].IsDynamic) continue;
                switch (a.Kind) {
                    case Objects.AnimationKind.Position:
                        a.Keyframes[i].VecX = v.PosX;
                        a.Keyframes[i].VecY = v.PosY;
                        a.Keyframes[i].VecZ = v.PosZ;
                        break;
                    case Objects.AnimationKind.Size:
                        a.Keyframes[i].VecX = v.SizeX;
                        a.Keyframes[i].VecY = v.SizeY;
                        a.Keyframes[i].VecZ = v.SizeZ;
                        break;
                    case Objects.AnimationKind.Scale:
                        a.Keyframes[i].VecX = v.ScaleX;
                        a.Keyframes[i].VecY = v.ScaleY;
                        a.Keyframes[i].VecZ = v.ScaleZ;
                        break;
                    case Objects.AnimationKind.Rotation:
                        a.Keyframes[i].VecX = v.RotAxisX;
                        a.Keyframes[i].VecY = v.RotAxisY;
                        a.Keyframes[i].VecZ = v.RotAxisZ;
                        a.Keyframes[i].RotAngleDeg = v.RotAngleDeg;
                        break;
                    case Objects.AnimationKind.Alpha:
                        // Normalize to 0..1 so the dynamic and static
                        // keyframes share a single domain. WMC's static
                        // alpha SetFloat values (wire-observed: 0.5020,
                        // 0.2510, 0.1255 etc.) are normalized; storing
                        // dynamic ones as raw byte 0..255 would put the
                        // two domains on either side of the apply-side
                        // 0..1 / 0..255 heuristic boundary, so lerps
                        // between them produced non-monotone alpha
                        // (e.g. 100% → 25% → 50% instead of a clean
                        // 100% → 75% → 50%).
                        a.Keyframes[i].FloatValue = v.AlphaByte / 255.0f;
                        break;
                    case Objects.AnimationKind.Color:
                        a.Keyframes[i].ArgbValue =
                            ((uint)v.Color.A << 24) | ((uint)v.Color.R << 16) |
                            ((uint)v.Color.G << 8)  | v.Color.B;
                        break;
                }
                // We don't currently apply DynamicMultiply (the spec's
                // "values can be multiplied or added" is ambiguous and
                // the WMC wire we see uses fMultiply=0 most of the time).
            }
        }

        /// <summary>
        /// Stop an animation, applying the post-stop command. The
        /// <paramref name="cmdOverride"/> arg (when non-null) supersedes
        /// <c>anim.StopCommand</c> — Animation_Stop's wire cmd field takes
        /// precedence over the SetStopCommand default per spec §17.25.
        /// </summary>
        private void StopAnimation(Objects.SplashAnimation anim, int? cmdOverride = null) {
            // Capture progress BEFORE we flip Playing=false, so the
            // OnComplete callback reports an accurate flAnimationProgress.
            float progress = ComputeAnimationProgress(anim);
            anim.Playing = false;
            _playingAnimations.Remove(anim);
            int cmd = cmdOverride ?? anim.StopCommand;
            // Apply the stop-command per spec 2.2.4.17.25:
            //   0 = no move  (leave at current value)
            //   1 = reset to keyframe 0
            //   2 = advance to last keyframe
            // Both "0" and "last" use the SORTED order, since WMC's
            // index order doesn't follow timeline order.
            if (anim.Keyframes != null && anim.KeyframeCount > 0 && anim.SortedIndices != null) {
                if (cmd == 1) {
                    ApplyAnimationValue(anim, anim.Keyframes[anim.SortedIndices[0]]);
                } else if (cmd == 2) {
                    ApplyAnimationValue(anim, anim.Keyframes[anim.SortedIndices[anim.KeyframeCount - 1]]);
                    progress = 1.0f; // explicitly snapped to end
                }
            }
            // Fire LocalAnimationCallback_OnComplete (spec §2.2.5.1)
            // to whatever (objcb, ctxcb) the server registered via
            // Animation_AddCallback. Without this the server-side state
            // machine never advances and the splash UI gets stuck:
            // row titles never appear, selection follow-ups never run,
            // navigation cleanup never fires.
            //
            // Fire at most ONCE per Play cycle. WMC frequently sends an
            // explicit Animation_Stop *after* a natural completion, and
            // double-firing OnComplete with progress=1.0 then again with
            // progress=1.0 confuses the server's state machine.
            if (anim.CallbackObj != 0 && anim.CallbackCtx != 0 && !anim.OnCompleteFired) {
                anim.OnCompleteFired = true;
                SendAnimationOnComplete(anim, progress);
            }
            // Chain to the next animation if one was wired via
            // Animation_AddCompletionLink (spec §17.1).
            if (anim.NextOnComplete != 0
                && _registry.TryGetObject(anim.NextOnComplete, out var nextObj)
                && nextObj is Objects.SplashAnimation nextAnim
                && nextAnim != anim) {
                StartAnimation(nextAnim);
            }
        }

        /// <summary>
        /// Estimate the fraction (0..1) of the animation that played
        /// before stop. Used as <c>flAnimationProgress</c> in the
        /// OnComplete callback.
        /// </summary>
        private float ComputeAnimationProgress(Objects.SplashAnimation a) {
            if (a.Keyframes == null || a.KeyframeCount <= 0) return 0f;
            // Use the time-sorted last keyframe for duration. WMC ships
            // out-of-order keyframes where the highest INDEX often has
            // t=0; that's not the timeline end.
            float duration = a.SortedIndices != null
                ? a.Keyframes[a.SortedIndices[a.KeyframeCount - 1]].TimeSec
                : a.Keyframes[a.KeyframeCount - 1].TimeSec;
            if (duration <= 0) return 1f;
            double elapsedMs = _animClock.Elapsed.TotalMilliseconds - a.StartTimeMs;
            float frac = (float)(elapsedMs / 1000.0 / duration);
            if (frac < 0) frac = 0;
            else if (frac > 1) frac = 1;
            return frac;
        }

        /// <summary>
        /// Send LocalAnimationCallback_OnComplete (spec §2.2.5.1) back to
        /// the server. The message is a 20-byte payload addressed to the
        /// callback context, wrapped in our standard
        /// Command + BufferInfo envelope by SendIndividualMessageToServer.
        ///
        /// Payload layout (12-byte header + 8-byte body = 20 B total,
        /// all in payload byte order which is BE under DSPA BIG=true):
        ///   _size            (u32) = 20
        ///   _msgid           (i32) = 0      (LocalAnimationCallback_OnComplete)
        ///   _idObjectSubject (u32) = callback object handle (objcb)
        ///   target           (u32) = the animation handle
        ///   flAnimationProgress (f32) = 0..1
        /// </summary>
        private void SendAnimationOnComplete(Objects.SplashAnimation anim, float progress) {
            byte[] payload = new byte[20];
            int p = 0;
            WriteU32Payload(payload, ref p, 20);                  // _size = 20 (header+body)
            WriteU32Payload(payload, ref p, 0);                   // _msgid = 0
            WriteU32Payload(payload, ref p, anim.CallbackObj);    // _idObjectSubject = objcb
            WriteU32Payload(payload, ref p, anim.Handle);         // target = anim handle
            WriteF32Payload(payload, ref p, progress);            // flAnimationProgress
            _dumper?.OnEvent($"  -> OnComplete anim=0x{anim.Handle:X8} -> objcb=0x{anim.CallbackObj:X8} ctxcb=0x{anim.CallbackCtx:X8} progress={progress:F2}");
            SendIndividualMessageToServer(anim.CallbackCtx, payload);
        }

        /// <summary>Endian-aware u32 writer matching the payload byte order.</summary>
        private void WriteU32Payload(byte[] buf, ref int p, uint v) {
            if (_payloadBigEndian) {
                buf[p++] = (byte)(v >> 24);
                buf[p++] = (byte)(v >> 16);
                buf[p++] = (byte)(v >> 8);
                buf[p++] = (byte)v;
            } else {
                buf[p++] = (byte)v;
                buf[p++] = (byte)(v >> 8);
                buf[p++] = (byte)(v >> 16);
                buf[p++] = (byte)(v >> 24);
            }
        }

        /// <summary>Endian-aware f32 writer matching the payload byte order.</summary>
        private void WriteF32Payload(byte[] buf, ref int p, float f) {
            // BitConverter.SingleToInt32Bits isn't on net461; use unsafe.
            uint bits;
            unsafe { bits = *((uint*)&f); }
            WriteU32Payload(buf, ref p, bits);
        }

        private void EnsureAnimTickHooked() {
            if (_animTickHooked) return;
            _animTickHooked = true;
            // CompositionTarget.Rendering fires once per frame on the WPF
            // dispatcher, so we can mutate scene-graph state from inside
            // safely. Static-style event; we never need to unhook for a
            // single-controller-per-process lifetime.
            System.Windows.Media.CompositionTarget.Rendering += OnAnimTick;
        }

        // Reusable scratch buffer for the per-tick snapshot of playing
        // animations. The set can mutate during iteration (Stop removes
        // entries), so we copy into a list. Allocating a fresh List<>
        // every frame is unnecessary GC churn at 60 Hz — keep one and
        // Clear() it between ticks.
        private readonly System.Collections.Generic.List<Objects.SplashAnimation> _animTickScratch
            = new System.Collections.Generic.List<Objects.SplashAnimation>();

        private void OnAnimTick(object sender, EventArgs e) {
            if (_playingAnimations.Count == 0) return;
            double nowMs = _animClock.Elapsed.TotalMilliseconds;
            _animTickScratch.Clear();
            _animTickScratch.AddRange(_playingAnimations);
            for (int i = 0; i < _animTickScratch.Count; i++) {
                TickAnimation(_animTickScratch[i], nowMs);
            }
            _animTickScratch.Clear(); // drop refs eagerly
        }

        private void TickAnimation(Objects.SplashAnimation a, double nowMs) {
            if (!a.Playing) return;
            int kfCount = Math.Min(a.KeyframeCount, a.Keyframes?.Length ?? 0);
            if (kfCount <= 0 || a.SortedIndices == null || a.SortedIndices.Length < kfCount) {
                // No keyframes — nothing to interpolate. Auto-stop.
                if (a.AutoStop) StopAnimation(a);
                return;
            }
            float elapsedSec = (float)((nowMs - a.StartTimeMs) / 1000.0);
            // Duration = the LATEST keyframe's time, after time-sorting
            // the indices. Crucial: WMC sometimes ships a "start state"
            // keyframe at t=0 as the highest INDEX, so naively using
            // Keyframes[kfCount-1].TimeSec gives 0 and collapses the
            // animation.
            float duration   = a.Keyframes[a.SortedIndices[kfCount - 1]].TimeSec;
            if (duration <= 0) {
                // Degenerate animation. Apply final value and stop.
                ApplyAnimationValue(a, a.Keyframes[a.SortedIndices[kfCount - 1]]);
                if (a.AutoStop) StopAnimation(a);
                return;
            }

            // Handle looping / repeat-count.
            //   RepeatCount  -1 = infinite loop (spec convention)
            //   RepeatCount   0 = treat as play-once (defensive: a literal
            //                     `>0` predicate would loop forever, but the
            //                     spec doesn't define cRepeats=0 explicitly
            //                     so the safer interpretation is "one shot")
            //   RepeatCount >=1 = play that many times
            if (elapsedSec >= duration) {
                a.CompletedRepeats++;
                bool finished = (a.RepeatCount != -1
                    && a.CompletedRepeats >= Math.Max(1, a.RepeatCount));
                if (finished) {
                    ApplyAnimationValue(a, a.Keyframes[a.SortedIndices[kfCount - 1]]);
                    if (a.AutoStop) StopAnimation(a);
                    return;
                }
                // Wrap around for the next repeat.
                a.StartTimeMs = nowMs;
                elapsedSec = 0;
            }

            // Find the bracketing keyframes (sorted[i], sorted[i+1]) such
            // that kfs[sorted[i]].TimeSec <= elapsedSec < kfs[sorted[i+1]].TimeSec.
            int si = 0;
            while (si < kfCount - 1 && a.Keyframes[a.SortedIndices[si + 1]].TimeSec <= elapsedSec) si++;
            if (si >= kfCount - 1) {
                ApplyAnimationValue(a, a.Keyframes[a.SortedIndices[kfCount - 1]]);
                return;
            }

            var k0 = a.Keyframes[a.SortedIndices[si]];
            var k1 = a.Keyframes[a.SortedIndices[si + 1]];
            float span = k1.TimeSec - k0.TimeSec;
            float t = span > 0 ? (elapsedSec - k0.TimeSec) / span : 0f;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            float eased = ApplyEasing(t, k0.Easing, k0.EaseP1, k0.EaseP2);

            // Interpolate between k0 and k1 and apply to target visual.
            ApplyAnimationValue(a, InterpolateKeyframe(k0, k1, eased));
        }

        private static Objects.AnimationKeyframe InterpolateKeyframe(
                Objects.AnimationKeyframe k0, Objects.AnimationKeyframe k1, float t) {
            var r = k0;
            r.VecX = Lerp(k0.VecX, k1.VecX, t);
            r.VecY = Lerp(k0.VecY, k1.VecY, t);
            r.VecZ = Lerp(k0.VecZ, k1.VecZ, t);
            r.RotAngleDeg = Lerp(k0.RotAngleDeg, k1.RotAngleDeg, t);
            r.FloatValue  = Lerp(k0.FloatValue, k1.FloatValue, t);
            r.ArgbValue   = LerpArgb(k0.ArgbValue, k1.ArgbValue, t);
            return r;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static uint LerpArgb(uint a, uint b, float t) {
            byte a0 = (byte)((a >> 24) & 0xFF), r0 = (byte)((a >> 16) & 0xFF),
                 g0 = (byte)((a >>  8) & 0xFF), b0 = (byte)(a & 0xFF);
            byte a1 = (byte)((b >> 24) & 0xFF), r1 = (byte)((b >> 16) & 0xFF),
                 g1 = (byte)((b >>  8) & 0xFF), b1 = (byte)(b & 0xFF);
            byte aL = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Lerp(a0, a1, t))));
            byte rL = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Lerp(r0, r1, t))));
            byte gL = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Lerp(g0, g1, t))));
            byte bL = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Lerp(b0, b1, t))));
            return ((uint)aL << 24) | ((uint)rL << 16) | ((uint)gL << 8) | bL;
        }

        /// <summary>
        /// Map a linear 0..1 progress through the easing curve specified by
        /// the keyframe. Returns the eased progress in 0..1 to use as the
        /// Lerp factor.
        /// </summary>
        private static float ApplyEasing(float t, Objects.AnimationEasing ease, float p1, float p2) {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            // For SCurve / Logarithmic / Exponential the wire carries an
            // `flWeight` per spec §17.7/§17.8/§17.10. Spec wording is
            // "weight of the interpolation as compared to a linear
            // interpolation" — we interpret weight=0 as pure linear and
            // weight=1 as pure curve, mixing in between. Weights outside
            // [0,1] are clamped so they don't fly past the keyframe target.
            float w = p1 <= 0 ? 1f : (p1 > 1 ? 1f : p1);
            switch (ease) {
                case Objects.AnimationEasing.Linear:       return t;
                case Objects.AnimationEasing.Cosine:       return (float)((1.0 - Math.Cos(t * Math.PI)) * 0.5);
                case Objects.AnimationEasing.Sine:         return (float)Math.Sin(t * Math.PI * 0.5);
                case Objects.AnimationEasing.SCurve: {
                    float curve = t * t * (3f - 2f * t);
                    return t + (curve - t) * w;
                }
                // Single-piece power curve. p1=flWeight (steepness),
                // p2=flHandle (additional shaping; spec says it's the
                // exp↔linear transition point, but empirically WMC ships
                // EaseOut with both params=0 on 100% of instances, so the
                // safest interpretation is "p2 adds to the exponent" —
                // keeps the curve monotone for any wire-observed value.
                // Default (w=0,h=0) gives exponent=2 → smooth quadratic
                // ease, matching the legacy implementation. A piecewise
                // spec-accurate curve was tried and degenerated to "pegs
                // at 1.0 from t=0.5" with default params, which broke
                // every EaseOut animation in the system.
                case Objects.AnimationEasing.EaseIn:
                    return (float)Math.Pow(t,
                        1.0 + (p1 > 0 ? p1 : 1.0) + (p2 > 0 ? p2 : 0.0));
                case Objects.AnimationEasing.EaseOut:
                    return 1f - (float)Math.Pow(1f - t,
                        1.0 + (p1 > 0 ? p1 : 1.0) + (p2 > 0 ? p2 : 0.0));
                case Objects.AnimationEasing.Logarithmic: {
                    float curve = (float)Math.Log(1 + t * (Math.E - 1));
                    return t + (curve - t) * w;
                }
                case Objects.AnimationEasing.Exponential: {
                    float curve = (float)((Math.Exp(t) - 1.0) / (Math.E - 1.0));
                    return t + (curve - t) * w;
                }
                case Objects.AnimationEasing.Bezier:
                    // 1D cubic Bezier with handles (p1, p2) interpreted as
                    // control points on the Y axis (x evenly spaced).
                    float u = 1f - t;
                    return u * u * u * 0f
                         + 3f * u * u * t * p1
                         + 3f * u * t * t * p2
                         + t * t * t * 1f;
                default: return t;
            }
        }

        /// <summary>
        /// Apply an interpolated keyframe value to the target visual's
        /// matching property and re-issue the WPF transform.
        /// </summary>
        private void ApplyAnimationValue(Objects.SplashAnimation a, Objects.AnimationKeyframe kf) {
            // Gradient-targeted animations: the AnimationManager_Build*
            // call's viSubject was a Gradient handle, not a Visual.
            // Resolve as Gradient, mutate the property, and rebuild every
            // visual's OpacityMask that's currently using this gradient.
            // The selection-sweep highlight in MCE menus (e.g. the
            // "Recorder Storage" focus animation) uses this pattern —
            // without it the gradient stays at its initial Offset (0)
            // and the U-shape mask sits permanently centred over text.
            if (a.Kind == Objects.AnimationKind.GradientOffset
                || a.Kind == Objects.AnimationKind.GradientColorMask) {
                if (!_registry.TryGetObject(a.TargetVisual, out var gObj)
                    || !(gObj is Objects.SplashGradient g)) return;
                if (a.Kind == Objects.AnimationKind.GradientOffset) {
                    g.Offset = kf.FloatValue;
                } else {
                    g.ColorMask = kf.ArgbValue;
                }
                RebuildMasksForGradient(g.Handle);
                return;
            }
            if (!_registry.TryGetObject(a.TargetVisual, out var obj) || !(obj is SplashVisual v)) return;
            switch (a.Kind) {
                case Objects.AnimationKind.Position:
                    v.PosX = kf.VecX; v.PosY = kf.VecY; v.PosZ = kf.VecZ;
                    v.ApplyTransform();
                    // Position changes ripple to descendants' hide rules
                    // (Rule 2/3 in ComputeEffectiveOpacity reads
                    // Parent.PosX + GrandParent.PosX, so a Position
                    // animation on the scroll container must re-evaluate
                    // the level-2 wrappers' opacity each tick).
                    ReapplyAlphaToDescendants(v, depth: 3);
                    break;
                case Objects.AnimationKind.Size:
                    v.SizeX = kf.VecX; v.SizeY = kf.VecY; v.SizeZ = kf.VecZ;
                    // Pivot depends on Size, so the transform must redo,
                    // and stretch-to-visual surface ops need a re-paint.
                    v.ApplyTransform();
                    v.RepaintContent();
                    // Keep the bounds-clip in lockstep with the live Size
                    // so children stay clipped during Size animations
                    // (otherwise a fast Size pulse would briefly let
                    // children render past the parent's edges).
                    v.ApplyBoundsClip();
                    break;
                case Objects.AnimationKind.Scale:
                    v.ScaleX = kf.VecX; v.ScaleY = kf.VecY; v.ScaleZ = kf.VecZ;
                    v.ApplyTransform();
                    break;
                case Objects.AnimationKind.Rotation:
                    v.RotAxisX = kf.VecX; v.RotAxisY = kf.VecY; v.RotAxisZ = kf.VecZ;
                    v.RotAngleDeg = kf.RotAngleDeg;
                    v.ApplyTransform();
                    break;
                case Objects.AnimationKind.Alpha:
                    // SetFloat values used for alpha tend to be in 0..1 or
                    // 0..255 depending on context. Accept both.
                    int alphaInt = kf.FloatValue <= 1.0f && kf.FloatValue >= 0f
                        ? (int)Math.Round(kf.FloatValue * 255f)
                        : (int)Math.Round(kf.FloatValue);
                    v.AlphaByte = (byte)Math.Max(0, Math.Min(255, alphaInt));
                    v.ApplyAlpha();
                    break;
                case Objects.AnimationKind.Color:
                    v.Color = ArgbU32ToColor(kf.ArgbValue);
                    // We don't yet apply v.Color to rendering (no tint
                    // pipeline). Logged for visibility.
                    break;
                // GradientColorMask / GradientOffset are handled at the
                // top of this method (they target a Gradient, not a
                // Visual) — see the gradient-animation early-return.
            }
        }

        /// <summary>
        /// Look up every <see cref="SplashVisual"/> whose OpacityMask
        /// is currently sourced from the given gradient, and rebuild the
        /// WPF brush against the visual's current Size. Called whenever
        /// the gradient's Offset / ColorMask changes — either statically
        /// (Gradient_SetOffset / Gradient_SetColorMask messages) or
        /// dynamically (Animation[GradientOffset] / [GradientColorMask]
        /// tick).
        ///
        /// Cheap in practice: the registry is small (a few thousand
        /// objects), the per-visual cost is one WPF LinearGradientBrush
        /// allocation which is then frozen, and gradient mutations are
        /// rare relative to per-frame redraws.
        /// </summary>
        private void RebuildMasksForGradient(uint gradHandle) {
            if (!_registry.TryGetObject(gradHandle, out var gObj)
                || !(gObj is Objects.SplashGradient g)) return;
            foreach (var v in _registry.EnumerateVisualsWithGradient(gradHandle)) {
                if (v.SizeX <= 0 || v.SizeY <= 0) continue;
                v.DrawingVisual.OpacityMask = g.BuildOpacityMask(v.SizeX, v.SizeY);
            }
        }

        /// <summary>
        /// Decode a raw image buffer into a Surface's WriteableBitmap.
        ///
        /// MS-RRSP2 pixel formats (per spec 2.2.6.7) all describe byte
        /// sequences in source-order. For the common case (ARGB32 /
        /// Bpp32) the wire layout is byte sequence A,R,G,B per pixel;
        /// WPF's <c>PixelFormats.Bgra32</c> wants B,G,R,A so we channel-
        /// swap on copy. Other formats fall through with a warning.
        /// </summary>
        private void LoadRawImageInto(Objects.SplashSurface surf, Objects.SplashDataBuffer db,
                                      int width, int height, int stride, uint format, int offX, int offY) {
            if (width <= 0 || height <= 0) return;
            if (surf.Bitmap == null || surf.Bitmap.PixelWidth < width || surf.Bitmap.PixelHeight < height) {
                surf.Bitmap = new System.Windows.Media.Imaging.WriteableBitmap(
                    System.Math.Max(width, 1),
                    System.Math.Max(height, 1),
                    96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                surf.Width  = width;
                surf.Height = height;
                surf.Stride = stride;
                surf.Format = format;
            }

            byte[] src = db.Bytes;
            int srcStride = stride > 0 ? stride : width * 4;
            int dstStride = surf.Bitmap.BackBufferStride;
            int pxBytes = 4;

            // Channel-swap into a temporary BGRA buffer the size of the
            // image, then write it via WriteableBitmap.WritePixels at the
            // requested (offX,offY). Format codes:
            //   0x00208888 ARGB32  — wire bytes A,R,G,B
            //   0x00200000 Bpp32   — assume same layout as ARGB32
            //   0x00200888 RGB32   — wire bytes _,R,G,B  (treat alpha as 255)
            //   0x00180888 RGB24   — 3 bytes per pixel
            byte[] bgra = new byte[width * height * pxBytes];
            try {
                switch (format) {
                    case 0x00208888u: // ARGB32
                    case 0x00200000u: // Bpp32 (assume ARGB)
                        for (int y = 0; y < height; y++) {
                            int srow = y * srcStride;
                            int drow = y * width * pxBytes;
                            for (int x = 0; x < width; x++) {
                                int si = srow + x * pxBytes;
                                int di = drow + x * pxBytes;
                                if (si + 3 >= src.Length) return;
                                byte a = src[si + 0], r = src[si + 1], g = src[si + 2], b = src[si + 3];
                                bgra[di + 0] = b;
                                bgra[di + 1] = g;
                                bgra[di + 2] = r;
                                bgra[di + 3] = a;
                            }
                        }
                        break;
                    case 0x00200888u: // RGB32 (no alpha)
                        for (int y = 0; y < height; y++) {
                            int srow = y * srcStride;
                            int drow = y * width * pxBytes;
                            for (int x = 0; x < width; x++) {
                                int si = srow + x * pxBytes;
                                int di = drow + x * pxBytes;
                                if (si + 3 >= src.Length) return;
                                bgra[di + 0] = src[si + 3]; // B
                                bgra[di + 1] = src[si + 2]; // G
                                bgra[di + 2] = src[si + 1]; // R
                                bgra[di + 3] = 0xFF;
                            }
                        }
                        break;
                    case 0x00180888u: // RGB24
                        for (int y = 0; y < height; y++) {
                            int srow = y * srcStride;
                            int drow = y * width * pxBytes;
                            for (int x = 0; x < width; x++) {
                                int si = srow + x * 3;
                                int di = drow + x * pxBytes;
                                if (si + 2 >= src.Length) return;
                                bgra[di + 0] = src[si + 2]; // B
                                bgra[di + 1] = src[si + 1]; // G
                                bgra[di + 2] = src[si + 0]; // R
                                bgra[di + 3] = 0xFF;
                            }
                        }
                        break;
                    default:
                        _dumper?.OnEvent($"    (unsupported pixel format 0x{format:X8} — skipping decode)");
                        return;
                }

                surf.Bitmap.WritePixels(
                    new System.Windows.Int32Rect(0, 0, width, height),
                    bgra, width * pxBytes, offX, offY);
                surf.ContentValid = true;
            } catch (Exception ex) {
                _dumper?.OnEvent($"    (WritePixels failed: {ex.Message})");
            }
        }

        // -------- Gradient (spec §2.2.4.15) --------
        // msgid table:
        //   0 = Pop                (rb u32)         — remove from active stack
        //   1 = Push               (rb u32)         — add to active stack
        //   2 = Draw               (rb u32)         — apply to next render op
        //   3 = Clear              (no body)        — wipe this gradient's stops
        //   4 = AddValue           (flValue, flPosition, relative)
        //   5 = SetOffset          (flOffset f32)
        //   7 = SetColorMask       (clrMask u32 ARGB)
        //   9 = SetOrientation     (dir i32, 0=Horizontal / 1=Vertical)
        //
        // Implementation — NEXT-BOUND model:
        // Push/Pop/Draw attach this gradient to the target
        // RenderBuilder's `PendingGradients` queue. The next
        // Visual_SetContent that consumes the RB picks up the queue and
        // builds a WPF LinearGradientBrush from the gradient stops to use
        // as the visual's OpacityMask. This is the spec's "soft fade
        // clipping" hook — the only built-in clipping mechanism in
        // MS-RRSP2 (Visual has no SetClip message).
        //
        // NEXT-BOUND was chosen empirically because it gives correct
        // results for the home-row tile-window masks and the EPG
        // right-side bounding gradient (both cases where the "next"
        // visual is the container whose subtree needs masking). It does
        // not always reach the right target for raised popup row
        // containers — known issue, tracked separately. A naive
        // "apply to both LAST-BOUND and NEXT-BOUND" workaround was
        // tried and reverted because LAST-BOUND apply broke the home
        // strip (single-item rows) and the EPG bound (gradient to
        // nothing); the LAST-BOUND visual is frequently a content tile
        // whose mask sizing doesn't match the wire-supplied gradient
        // stops. The right fix likely involves walking the parent chain
        // to a container size that matches the gradient's coordinate
        // span — left for follow-up.
        private void DispatchGradient(Objects.SplashGradient g, SplashPayloadReader rdr, int msgid) {
            if (g == null) {
                _dumper?.OnEvent($"  Gradient msgid={msgid} (no instance — ignored, rem={rdr.Remaining})");
                return;
            }
            switch (msgid) {
                case 0: // Pop — for the NEXT-BOUND model this just shrinks
                        // the pending-queue (the queue is consumed by the
                        // next SetContent so popping before that drops
                        // the gradient unobtrusively).
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        if (_registry.TryGetObject(rbH, out var rbObj) && rbObj is SplashRenderBuilder rb) {
                            rb.PendingGradients.Remove(g.Handle);
                        }
                        _dumper?.OnEvent($"  Gradient_Pop rb=0x{rbH:X8} grad=0x{g.Handle:X8}");
                    }
                    break;
                case 1: // Push
                case 2: // Draw
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        if (_registry.TryGetObject(rbH, out var rbObj) && rbObj is SplashRenderBuilder rb) {
                            // NEXT-BOUND queue: the next Visual_SetContent
                            // on this RB consumes the queue and applies
                            // the most recent gradient as that visual's
                            // OpacityMask. Empirically correct for the
                            // home-row tile-window masks, EPG bound, and
                            // most title overlays. Popup row containment
                            // is a known open issue tracked separately
                            // — the items still escape because the
                            // gradient's intended target isn't picked up
                            // by either LB-chain walk or NEXT-BOUND.
                            rb.PendingGradients.Add(g.Handle);
                            // Diagnostic only — log the gradient's
                            // effective pixel span and fitness across
                            // the LB ancestor chain. NEXT-BOUND fit is
                            // logged at SetContent time as NB.fit.
                            LogGradientFitness(rbH, rb, g, msgid);
                        }
                        _dumper?.OnEvent($"  Gradient_{(msgid==1?"Push":"Draw")} rb=0x{rbH:X8} grad=0x{g.Handle:X8} stops={g.Stops.Count} dir={g.Direction}");
                    }
                    break;
                case 3: // Clear
                    g.Clear();
                    _dumper?.OnEvent($"  Gradient_Clear grad=0x{g.Handle:X8}");
                    break;
                case 4: // AddValue
                    if (rdr.Remaining >= 12) {
                        float v = rdr.ReadFloat32();
                        float p = rdr.ReadFloat32();
                        int   r = rdr.ReadI32();
                        g.AddStop(v, p, r);
                        _dumper?.OnEvent($"  Gradient_AddValue grad=0x{g.Handle:X8} val={v:F3} pos={p:F3} relative={r}");
                    }
                    break;
                case 5: // SetOffset
                    if (rdr.Remaining >= 4) {
                        float o = rdr.ReadFloat32();
                        g.Offset = o;
                        // If the gradient was already bound as a mask on
                        // any visual, rebuild — Offset shifts the stop
                        // positions and changes which pixels are masked.
                        RebuildMasksForGradient(g.Handle);
                        _dumper?.OnEvent($"  Gradient_SetOffset grad=0x{g.Handle:X8} off={o:F3}");
                    }
                    break;
                case 7: // SetColorMask
                    if (rdr.Remaining >= 4) {
                        uint argb = rdr.ReadU32();
                        g.ColorMask = argb;
                        // ColorMask doesn't affect the current alpha-only
                        // BuildOpacityMask output, but rebuild defensively
                        // — if BuildOpacityMask ever starts honouring it
                        // (e.g. for tinted overlays), the existing masks
                        // would otherwise be stale until next SetContent.
                        RebuildMasksForGradient(g.Handle);
                        _dumper?.OnEvent($"  Gradient_SetColorMask grad=0x{g.Handle:X8} argb=0x{argb:X8}");
                    }
                    break;
                case 9: // SetOrientation
                    if (rdr.Remaining >= 4) {
                        int dir = rdr.ReadI32();
                        g.Direction = dir == 1
                            ? Objects.SplashGradient.Orientation.Vertical
                            : Objects.SplashGradient.Orientation.Horizontal;
                        _dumper?.OnEvent($"  Gradient_SetOrientation grad=0x{g.Handle:X8} dir={g.Direction}");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Gradient msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        /// <summary>
        /// Best-fit gradient binder. At Gradient_Push/Draw time, walk the
        /// last-bound visual's ancestor chain (LB → LB.P → LB.PP → …) and
        /// apply the gradient as <c>OpacityMask</c> to the visual whose
        /// <see cref="SplashGradient.FitScoreForVisual"/> is highest.
        ///
        /// <para><b>Why best-fit:</b> the wire pattern is invariant — WMC
        /// ships <c>(Surface_Draw → Visual_SetContent vis=A → Gradient_Draw
        /// → Visual_SetContent vis=B [ops=0])</c>. The gradient's stop
        /// coordinates are sized for some specific visual in the
        /// neighbourhood (sometimes vis A itself, sometimes vis A's
        /// content panel parent, sometimes the eventual vis B container);
        /// the fit-score directly measures how well the gradient's pixel
        /// span maps onto each candidate's Size, so the highest-scoring
        /// candidate is almost always the intended target.</para>
        ///
        /// <para>This unifies the previous three competing models —
        /// LAST-BOUND, NEXT-BOUND, and per-context heuristics — into one
        /// principled rule that the wire data directly validates.</para>
        ///
        /// <para>Silently no-ops if no candidate scores above
        /// <see cref="kMinAcceptableFit"/>; that prevents a gradient
        /// intended for a totally different visual (e.g. shipped between
        /// unrelated subtrees) from accidentally masking whichever
        /// container happens to be closest in the chain.</para>
        /// </summary>
        private void ApplyGradientToLastBound(uint rbHandle, Objects.SplashGradient g, bool isPop) {
            if (!_registry.TryGetObject(rbHandle, out var rbObj)
                || !(rbObj is SplashRenderBuilder rb)
                || rb.LastBoundVisualHandle == 0) return;
            if (!_registry.TryGetObject(rb.LastBoundVisualHandle, out var vObj)
                || !(vObj is SplashVisual lb)) return;
            if (isPop) {
                // For pop, walk the chain and clear masks on every
                // candidate that might have received this gradient.
                // Cheaper: just clear LB and parents up to depth 8.
                int popDepth = 0;
                for (var p = lb; p != null && popDepth < 8; p = p.Parent, popDepth++) {
                    p.DrawingVisual.OpacityMask = null;
                }
                return;
            }

            // Walk LB → LB.P → … up to 8 levels and find the highest-fit
            // candidate. Track size so we can size the mask correctly.
            SplashVisual best = null;
            double bestFit = -1;
            int depth = 0;
            for (var cand = lb; cand != null && depth < 8; cand = cand.Parent, depth++) {
                if (cand.SizeX <= 0 || cand.SizeY <= 0) continue;
                double fit = g.FitScoreForVisual(cand.SizeX, cand.SizeY);
                if (fit > bestFit) { bestFit = fit; best = cand; }
            }

            if (best == null || bestFit < kMinAcceptableFit) {
                _dumper?.OnEvent($"    GradApply grad=0x{g.Handle:X8} (no candidate above fit>={kMinAcceptableFit:F2}, bestFit={bestFit:F2})");
                return;
            }

            var mask = g.BuildOpacityMask(best.SizeX, best.SizeY);
            best.DrawingVisual.OpacityMask = mask;
            _dumper?.OnEvent($"    GradApply grad=0x{g.Handle:X8} -> vis=0x{best.Handle:X8} size=({best.SizeX:F0}x{best.SizeY:F0}) fit={bestFit:F2}");
        }

        // Minimum FitScoreForVisual a candidate must achieve to receive
        // the OpacityMask. Below this threshold, the gradient is
        // considered too poorly aligned with any candidate to be
        // confidently bound and is dropped (silently — the diagnostic
        // log records the decision). 0.30 catches well-aligned edge
        // fades and band-pass masks while rejecting strays.
        private const double kMinAcceptableFit = 0.30;

        /// <summary>
        /// Diagnostic-only — emits one event-log line per Gradient_Push /
        /// Gradient_Draw showing the gradient's effective pixel span and a
        /// fitness score (0..1, higher = better fit) for each candidate
        /// target visual. The "candidates" are the visual most recently
        /// bound to this RB (LAST-BOUND), its parent, and its grandparent
        /// — i.e. the path up from where the wire just rendered content.
        ///
        /// Output format (one line):
        ///   GradFit grad=0x.. dir=H stops=N span=(min..max)
        ///     LB     vis=0x.. size=(WxH) fit=0.93
        ///     LB.P   vis=0x.. size=(WxH) fit=0.18
        ///     LB.PP  vis=0x.. size=(WxH) fit=0.04
        ///
        /// Reading rule: the candidate with fit ≈ 1.0 is the visual whose
        /// Size best matches the gradient's coordinate range — most
        /// likely the intended mask target. The current NEXT-BOUND model
        /// applies the gradient to whatever visual happens to consume the
        /// RB next, regardless of fit; comparing the next-bound apply log
        /// line against these LAST-BOUND fit scores reveals when the
        /// chosen target is wrong.
        /// </summary>
        private void LogGradientFitness(uint rbHandle, SplashRenderBuilder rb,
                                        Objects.SplashGradient g, int msgid) {
            if (_dumper == null) return;
            if (g.Stops.Count == 0) return;

            // Resolve LAST-BOUND and walk the full ancestor chain up to
            // 8 levels — enough to cross a typical popup body, dialog
            // root, and scene root without flooding the log.
            SplashVisual lbVis = null;
            if (rb.LastBoundVisualHandle != 0
                && _registry.TryGetObject(rb.LastBoundVisualHandle, out var lbObj)
                && lbObj is SplashVisual v) {
                lbVis = v;
            }

            // Compute span against LB's size if available, otherwise pick
            // a 1×1 reference so the per-stop position values dominate.
            double refW = lbVis?.SizeX ?? 1.0;
            double refH = lbVis?.SizeY ?? 1.0;
            g.GetEffectiveSpan(refW, refH, out double minPx, out double maxPx, out double axisLen);

            _dumper.OnEvent($"  GradFit grad=0x{g.Handle:X8} dir={(g.Direction == Objects.SplashGradient.Orientation.Horizontal ? "H" : "V")} "
                          + $"stops={g.Stops.Count} span=({minPx:F1}..{maxPx:F1}) ref_axisLen={axisLen:F1}");

            // Walk the chain LB → LB.P → LB.PP → ... up to 8 levels and
            // emit the candidate's size + bits + fit score. Stops at the
            // first null Parent (scene root).
            SplashVisual cand = lbVis;
            int depth = 0;
            while (cand != null && depth < 8) {
                double fit = g.FitScoreForVisual(cand.SizeX, cand.SizeY);
                string tag = depth == 0 ? "LB" : ("LB." + new string('P', depth));
                _dumper.OnEvent($"    {tag,-12} vis=0x{cand.Handle:X8} size=({cand.SizeX:F0}x{cand.SizeY:F0}) "
                              + $"pos=({cand.PosX:F0},{cand.PosY:F0}) bits=0x{cand.DataBits:X8} "
                              + $"alpha={cand.AlphaByte} clip={(cand.DrawingVisual.Clip != null ? "Y" : "N")} "
                              + $"fit={fit:F2}");
                cand = cand.Parent;
                depth++;
            }
            if (cand != null) {
                _dumper.OnEvent("    ... (chain truncated at 8 levels)");
            }
        }

        // Set of visuals we've already dumped subtrees for, so that
        // animated SetSize on a popup-sized visual (e.g. the popup
        // expand-in scale) doesn't dump the subtree on every frame.
        // Cleared on session reset.
        private readonly System.Collections.Generic.HashSet<uint> _dumpedSubtrees
            = new System.Collections.Generic.HashSet<uint>();

        /// <summary>
        /// Diagnostic — if this visual is "popup-sized" (≥800 px in
        /// either dimension), dump the full visual subtree starting at
        /// THIS visual to the event log. Throttled per-handle so a popup
        /// being scaled in an animation only dumps once.
        /// </summary>
        private void MaybeDumpPopupSubtree(SplashVisual v) {
            if (_dumper == null || v == null) return;
            if (v.SizeX < 800 && v.SizeY < 800) return;
            if (!_dumpedSubtrees.Add(v.Handle)) return; // already dumped
            _dumper.OnEvent($"  VTREE-root vis=0x{v.Handle:X8} size=({v.SizeX:F0}x{v.SizeY:F0}) bits=0x{v.DataBits:X8}");
            DumpVisualSubtree(v, depth: 1, maxDepth: 12);
        }

        /// <summary>
        /// Recursive walk of a visual's children, indented by depth.
        /// Emits one event-log line per visual showing handle, size,
        /// pos, bits, alpha, clip, child count. Stops at
        /// <paramref name="maxDepth"/> to avoid runaway logs on deep
        /// trees. Cycles are prevented by the WPF parent/child contract
        /// (a visual can't be a child of two parents).
        /// </summary>
        private void DumpVisualSubtree(SplashVisual v, int depth, int maxDepth) {
            if (v == null || depth > maxDepth) return;
            string indent = new string(' ', depth * 2 + 4);
            foreach (var child in v.Children) {
                _dumper.OnEvent(
                    $"{indent}vis=0x{child.Handle:X8} "
                    + $"size=({child.SizeX:F0}x{child.SizeY:F0}) "
                    + $"pos=({child.PosX:F0},{child.PosY:F0}) "
                    + $"bits=0x{child.DataBits:X8} "
                    + $"alpha={child.AlphaByte} "
                    + $"vis={(child.Visible ? "Y" : "N")} "
                    + $"clip={(child.DrawingVisual.Clip != null ? "Y" : "N")} "
                    + $"children={child.Children.Count}");
                DumpVisualSubtree(child, depth + 1, maxDepth);
            }
        }

        // -------- DataBuffer (spec §2.2.4.1) --------
        //   0 = RegisterOwner       (_objcb u32, _ctxcb u32) — body=8
        // The owner callback isn't something we model — we just hold the
        // bytes for predicate-batch processing and Rasterizer_LoadRawImage.
        private void DispatchDataBuffer(ISplashObject db, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // RegisterOwner
                    if (rdr.Remaining >= 8) {
                        uint objcb = rdr.ReadU32();
                        uint ctxcb = rdr.ReadU32();
                        _dumper?.OnEvent($"  DataBuffer_RegisterOwner db=0x{db.Handle:X8} objcb=0x{objcb:X8} ctxcb=0x{ctxcb:X8}");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  DataBuffer msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- XAudSoundDevice (spec §2.2.4.24) --------
        // Drives the splash UI sound system. CreateSound and
        // CreateSoundBuffer set up the playback objects; the actual
        // playback path is in DispatchSound / DispatchSoundBuffer.
        //   0 = CreateSound        (idNewSound u32, soundBuffer u32) — body=8
        //   1 = CreateSoundBuffer  (idNewBuffer i32, info SoundHeader 22B, _priv_objcb u32, _priv_ctxcb u32)
        //   6 = Create             (post-init)
        private void DispatchXAudSoundDevice(SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // CreateSound — binds a new Sound to an existing
                        // SoundBuffer. The pairing must be captured here so
                        // Sound_Play (which carries no buffer reference) can
                        // resolve back to the source bytes.
                    if (rdr.Remaining >= 8) {
                        uint idNewSound = rdr.ReadU32();
                        uint sndBuf     = rdr.ReadU32();
                        _registry.RegisterObject(idNewSound,
                            new Objects.SplashSound(idNewSound, sndBuf, "Sound"));
                        _dumper?.OnEvent($"  XAudSoundDevice_CreateSound -> sound=0x{idNewSound:X8} buf=0x{sndBuf:X8}");
                    }
                    break;
                case 1: // CreateSoundBuffer — empty buffer awaiting
                        // SoundBuffer_LoadSoundData to populate its bytes.
                    if (rdr.Remaining >= 4) {
                        int idNewBuf = rdr.ReadI32();
                        _registry.RegisterObject((uint)idNewBuf,
                            new Objects.SplashSoundBuffer((uint)idNewBuf, "SoundBuffer"));
                        _dumper?.OnEvent($"  XAudSoundDevice_CreateSoundBuffer -> buf=0x{idNewBuf:X8}");
                    }
                    break;
                case 6: // Create (post-init)
                    _dumper?.OnEvent($"  XAudSoundDevice_Create (post-init)");
                    break;
                default:
                    _dumper?.OnEvent($"  XAudSoundDevice msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- SoundBuffer (spec §2.2.4.19) --------
        //   0 = LoadSoundData (dataBuffer u32) — references a DataBuffer
        //       whose bytes become this SoundBuffer's payload. Spec body
        //       is documented as 4 bytes (the dataBuffer ref) but the
        //       wire ships 8 — the extra 4 are probably a format hint;
        //       reading just the first u32 is sufficient.
        private void DispatchSoundBuffer(Objects.SplashSoundBuffer sb, SplashPayloadReader rdr, int msgid) {
            if (sb == null) {
                _dumper?.OnEvent($"  SoundBuffer msgid={msgid} (no instance — ignored, rem={rdr.Remaining})");
                return;
            }
            switch (msgid) {
                case 0: // LoadSoundData
                    if (rdr.Remaining >= 4) {
                        uint dataBufferH = rdr.ReadU32();
                        sb.DataBufferHandle = dataBufferH;
                        if (_registry.TryGetObject(dataBufferH, out var dbObj)
                            && dbObj is Objects.SplashDataBuffer db) {
                            sb.Bytes = db.Bytes;
                            _dumper?.OnEvent($"  SoundBuffer_LoadSoundData buf=0x{sb.Handle:X8} data=0x{dataBufferH:X8} bytes={db.Bytes.Length}");
                        } else {
                            _dumper?.OnEvent($"  SoundBuffer_LoadSoundData buf=0x{sb.Handle:X8} data=0x{dataBufferH:X8} (DataBuffer not found — sound will be silent)");
                        }
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  SoundBuffer msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- Sound (spec §2.2.4.20) --------
        //   0 = Stop — stops playback, releases lock from Play
        //   1 = Play — starts playback (restarts if already playing)
        private void DispatchSound(Objects.SplashSound snd, SplashPayloadReader rdr, int msgid) {
            if (snd == null) {
                _dumper?.OnEvent($"  Sound msgid={msgid} (no instance — ignored, rem={rdr.Remaining})");
                return;
            }
            switch (msgid) {
                case 0: // Stop
                    _dumper?.OnEvent($"  Sound_Stop sound=0x{snd.Handle:X8}");
                    if (_enableSplashAudio) _soundPlayer?.Stop(snd.Handle);
                    break;
                case 1: // Play
                    if (_enableSplashAudio) {
                        if (_soundPlayer == null) {
                            // Lazy init — defer NAudio device opening until
                            // the first Play. Saves resources in sessions
                            // that never trigger splash audio.
                            _soundPlayer = new SplashSoundPlayer(_logger);
                        }
                        byte[] bytes = ResolveSoundBytes(snd);
                        if (bytes != null && bytes.Length > 0) {
                            _soundPlayer.Play(snd.Handle, bytes);
                            _dumper?.OnEvent($"  Sound_Play sound=0x{snd.Handle:X8} buf=0x{snd.SoundBufferHandle:X8} bytes={bytes.Length}");
                        } else {
                            _dumper?.OnEvent($"  Sound_Play sound=0x{snd.Handle:X8} buf=0x{snd.SoundBufferHandle:X8} (no bytes — skipped)");
                        }
                    } else {
                        _dumper?.OnEvent($"  Sound_Play sound=0x{snd.Handle:X8} (audio disabled by config)");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Sound msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        /// <summary>
        /// Resolve a Sound's playable bytes by walking
        /// Sound.SoundBufferHandle → SoundBuffer.Bytes. Returns null if
        /// any link is missing (the SoundBuffer was destroyed, never had
        /// LoadSoundData called, etc.).
        /// </summary>
        private byte[] ResolveSoundBytes(Objects.SplashSound snd) {
            if (snd.SoundBufferHandle == 0) return null;
            if (!_registry.TryGetObject(snd.SoundBufferHandle, out var sbObj)
                || !(sbObj is Objects.SplashSoundBuffer sb)) return null;
            return sb.Bytes;
        }

        // -------- WaitCursor (spec §2.2.4.8) --------
        // msgid table:
        //   0 = Show               (no body)
        //   1 = Hide               (no body)
        //   2 = SetVisuals         (BLOBREF -> u32[] of visual handles)
        //   3 = SetShowAnimations  (BLOBREF -> u32[] of animation handles)
        //   4 = SetHideAnimations  (BLOBREF -> u32[] of animation handles)
        //   5 = Create             (no body, post-init ack)
        //
        // The MCE shell uses this for the centre-of-screen "busy" spinner
        // shown while a navigation page is loading. The configured visuals
        // are normally hidden by an explicit Visual_SetVisible=False that
        // arrives right before SetVisuals; Show flips them visible and
        // plays the fade-in animations, Hide plays fade-outs and (after
        // the OnComplete acks) the same Visible=False arrives again.
        private void DispatchWaitCursor(Objects.SplashWaitCursor wc, SplashPayloadReader rdr, int msgid) {
            if (wc == null) {
                _dumper?.OnEvent($"  WaitCursor msgid={msgid} (no instance — class-singleton ack, rem={rdr.Remaining})");
                return;
            }
            switch (msgid) {
                case 0: // Show
                    _dumper?.OnEvent($"  WaitCursor_Show wc=0x{wc.Handle:X8} visuals={wc.Visuals.Length} showAnims={wc.ShowAnimations.Length}");
                    foreach (uint vh in wc.Visuals) {
                        if (_registry.TryGetObject(vh, out var vObj) && vObj is SplashVisual v) {
                            v.Visible = true;
                            v.ApplyAlpha();
                        }
                    }
                    foreach (uint ah in wc.ShowAnimations) {
                        if (_registry.TryGetObject(ah, out var aObj) && aObj is Objects.SplashAnimation a) {
                            StartAnimation(a);
                        }
                    }
                    break;
                case 1: // Hide
                    _dumper?.OnEvent($"  WaitCursor_Hide wc=0x{wc.Handle:X8} hideAnims={wc.HideAnimations.Length}");
                    foreach (uint ah in wc.HideAnimations) {
                        if (_registry.TryGetObject(ah, out var aObj) && aObj is Objects.SplashAnimation a) {
                            StartAnimation(a);
                        }
                    }
                    // Visibility is left alone — the hide animations fade
                    // alpha to 0; if WMC wants the visual fully hidden it
                    // sends Visual_SetVisible=False after OnComplete.
                    break;
                case 2: // SetVisuals
                    wc.Visuals = ReadHandleArray(rdr);
                    _dumper?.OnEvent($"  WaitCursor_SetVisuals wc=0x{wc.Handle:X8} count={wc.Visuals.Length} handles=[{string.Join(",", System.Linq.Enumerable.Select(wc.Visuals, h => $"0x{h:X8}"))}]");
                    break;
                case 3: // SetShowAnimations
                    wc.ShowAnimations = ReadHandleArray(rdr);
                    _dumper?.OnEvent($"  WaitCursor_SetShowAnimations wc=0x{wc.Handle:X8} count={wc.ShowAnimations.Length} handles=[{string.Join(",", System.Linq.Enumerable.Select(wc.ShowAnimations, h => $"0x{h:X8}"))}]");
                    break;
                case 4: // SetHideAnimations
                    wc.HideAnimations = ReadHandleArray(rdr);
                    _dumper?.OnEvent($"  WaitCursor_SetHideAnimations wc=0x{wc.Handle:X8} count={wc.HideAnimations.Length} handles=[{string.Join(",", System.Linq.Enumerable.Select(wc.HideAnimations, h => $"0x{h:X8}"))}]");
                    break;
                case 5: // Create (post-init ack)
                    _dumper?.OnEvent($"  WaitCursor_Create wc=0x{wc.Handle:X8} (post-init)");
                    break;
                default:
                    _dumper?.OnEvent($"  WaitCursor msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        /// <summary>
        /// Read a BLOBREF whose payload is a packed u32[] of object
        /// handles (big-endian). Used by <c>WaitCursor_SetVisuals</c> /
        /// <c>SetShowAnimations</c> / <c>SetHideAnimations</c>.
        /// </summary>
        private uint[] ReadHandleArray(SplashPayloadReader rdr) {
            if (rdr.Remaining < 4) return System.Array.Empty<uint>();
            rdr.ReadBlobRef(out ushort blobSize, out ushort blobOff);
            if (blobSize == 0 || (blobSize % 4) != 0) return System.Array.Empty<uint>();
            byte[] blob;
            try { blob = rdr.ResolveBlob(blobSize, blobOff); }
            catch { return System.Array.Empty<uint>(); }
            int n = blob.Length / 4;
            var handles = new uint[n];
            for (int i = 0; i < n; i++) {
                int o = i * 4;
                handles[i] = ((uint)blob[o] << 24) | ((uint)blob[o + 1] << 16)
                           | ((uint)blob[o + 2] << 8)  |  blob[o + 3];
            }
            return handles;
        }

        private void DispatchContext(SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 2: // Context_ForwardMessage — section 2.2.4.4.1
                    {
                        if (rdr.Remaining < 8) {
                            _dumper?.OnEvent($"  Context_ForwardMessage too short (rem={rdr.Remaining})");
                            return;
                        }
                        uint idContextDest = rdr.ReadU32();
                        rdr.ReadBlobRef(out ushort msgSize, out ushort msgOff);
                        byte[] callbackMsg = rdr.ResolveBlob(msgSize, msgOff);
                        _dumper?.OnEvent($"  Context_ForwardMessage dstCtx={idContextDest} retLen={callbackMsg.Length} retHex={Hex(callbackMsg)}");

                        // Echo the embedded message back to the server.
                        // Per spec 2.2.4.4.1 this is a "callback" the
                        // server expects after it's done sending us a
                        // batch — exactly the dashed "Message Callback"
                        // arrow in the MS-RRSP2 initialization diagram.
                        //
                        // The BLOB is already a fully-formed payload
                        // message (12-B header + body); we wrap it in
                        // our own Command + BufferInfo as a single
                        // Individual Message Buffer addressed to the
                        // context the server requested.
                        SendIndividualMessageToServer(idContextDest, callbackMsg);
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Context msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        /// <summary>
        /// Emit a single payload message to the server as an
        /// IndividualMessage buffer (idBuffer=0, nFlags=0). Frames the
        /// payload with the standard big-endian Command + BufferInfo
        /// envelope.
        /// </summary>
        private void SendIndividualMessageToServer(uint idContextDest, byte[] payload) {
            if (_sendBytes == null) {
                _dumper?.OnEvent($"  (send skipped — no send callback wired)");
                return;
            }
            if (payload == null) payload = new byte[0];

            // Command (4 B BE = 1) + BufferInfo (20 B BE) + payload.
            int total = 4 + 20 + payload.Length;
            byte[] buf = new byte[total];
            int p = 0;

            // Command: 1 (Buffer follows)
            WriteU32BE(buf, ref p, 1u);

            // BufferInfo
            WriteU32BE(buf, ref p, _idContextRender);   // idContextSrc = us
            WriteU32BE(buf, ref p, idContextDest);      // idContextDest = where server asked
            WriteU32BE(buf, ref p, 0u);                 // idBuffer = 0 (no DataBuffer instance)
            WriteU32BE(buf, ref p, 0u);                 // nFlags = 0 (not a batch)
            WriteU32BE(buf, ref p, (uint)payload.Length); // cbSizeBuffer

            // Payload
            if (payload.Length > 0) {
                Buffer.BlockCopy(payload, 0, buf, p, payload.Length);
            }

            _dumper?.OnEvent($"  -> SEND IndividualBuffer src={_idContextRender} dst={idContextDest} payloadLen={payload.Length}");
            try { _sendBytes(buf); } catch (Exception ex) {
                _logger?.LogError("SPLASH: send failed: " + ex.Message);
                _dumper?.OnEvent("  -> SEND failed: " + ex.Message);
            }
        }

        // -------- Helpers --------

        private static uint ReadU32BE(byte[] buf, int off) {
            return ((uint)buf[off] << 24) | ((uint)buf[off + 1] << 16) | ((uint)buf[off + 2] << 8) | buf[off + 3];
        }

        private static void WriteU32BE(byte[] buf, ref int p, uint v) {
            buf[p++] = (byte)(v >> 24);
            buf[p++] = (byte)(v >> 16);
            buf[p++] = (byte)(v >> 8);
            buf[p++] = (byte)v;
        }

        private static string Hex(byte[] data) {
            if (data == null || data.Length == 0) return "(empty)";
            var sb = new System.Text.StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++) {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        // ================================================================
        // Group 3 — Spec-coded, low-activity / unobserved class dispatchers.
        //
        // Each handler decodes the spec-documented fields and logs cleanly
        // so any wire activity is named rather than "Unknown msgid=N". None
        // of these have functional rendering hooks yet — they're stubs ready
        // to be promoted to real handlers when WMC's MCE shell actually
        // exercises them. The decoded field names + handle references in the
        // logs make that promotion straightforward: when a class fires, the
        // log already tells us what bytes to interpret.
        // ================================================================

        // -------- Line (spec §2.2.4.16) — never observed --------
        //   0 = SetThickness   (flThickness f32)
        //   1 = SetColor       (clr u32 ARGB)
        //   2 = CommitLine     (rb u32)     — adds the line to a RB
        //   3 = DrawPoint      (rb u32)     — adds a polyline vertex
        private void DispatchLine(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0:
                    if (rdr.Remaining >= 4) {
                        float th = rdr.ReadFloat32();
                        _dumper?.OnEvent($"  Line_SetThickness line=0x{obj.Handle:X8} flThickness={th:F2}");
                    }
                    break;
                case 1:
                    if (rdr.Remaining >= 4) {
                        uint argb = rdr.ReadU32();
                        _dumper?.OnEvent($"  Line_SetColor line=0x{obj.Handle:X8} argb=0x{argb:X8}");
                    }
                    break;
                case 2:
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        _dumper?.OnEvent($"  Line_CommitLine line=0x{obj.Handle:X8} rb=0x{rbH:X8} (no-op stub)");
                    }
                    break;
                case 3:
                    if (rdr.Remaining >= 4) {
                        uint rbH = rdr.ReadU32();
                        _dumper?.OnEvent($"  Line_DrawPoint line=0x{obj.Handle:X8} rb=0x{rbH:X8} (no-op stub)");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  Line msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- VideoPool (spec §2.2.4.13) — never observed --------
        // Mirrors SurfacePool structurally (Allocate/Free/CreateSurface/etc.)
        // but specifically for video frame surfaces. Promoting to a real
        // handler requires plumbing into the playback surface allocator.
        //   0 = Draw, 1 = CreateSurface, 2 = Free, 3 = Allocate,
        //   4 = SetEmptyColor, 5 = SetPriority, 7 = NotifyVideoSizeChanged
        private void DispatchVideoPool(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            // VideoPool messages are interesting: this class is declared
            // in MS-RRSP2 but we've observed zero msgs on a VideoPool-
            // typed handle in any of our test captures (the pool object
            // created via XeDevice_CreateVideoPool is registered as
            // SurfacePool — see DispatchDevice case 7 — so SurfacePool
            // takes its dispatch). If WMC ever sends a real VideoPool_*
            // message it goes here; hex-dump it unconditionally so the
            // bytes are recoverable.
            _dumper?.OnEvent($"  VideoPool pool=0x{obj.Handle:X8} msgid={msgid} rem={rdr.Remaining} " +
                             $"body={rdr.PeekRemainingHex(128)}");
        }

        // -------- ContextRelay (spec §2.2.4.2) — never observed --------
        //   0 = UnlinkContext  (idExisting u32, idAlias u32)
        //   1 = LinkContext    (idExisting u32, idAlias u32)
        //   2 = Create         (protocol i32, stServer BLOBREF, stSession BLOBREF)
        // Inter-context message routing for multi-app scenarios; our
        // implementation is single-context so these are metadata only.
        private void DispatchContextRelay(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0:
                case 1:
                    if (rdr.Remaining >= 8) {
                        uint existing = rdr.ReadU32();
                        uint alias    = rdr.ReadU32();
                        string op = msgid == 1 ? "Link" : "Unlink";
                        _dumper?.OnEvent($"  ContextRelay_{op}Context relay=0x{obj.Handle:X8} existing=0x{existing:X8} alias=0x{alias:X8}");
                    }
                    break;
                case 2:
                    if (rdr.Remaining >= 4) {
                        int proto = rdr.ReadI32();
                        string protoName = proto == 1 ? "RDP-VC"
                                         : proto == 2 ? "TCP"
                                         : proto == 3 ? "UDP"
                                         : proto == 4 ? "NamedPipes"
                                         : $"?({proto})";
                        _dumper?.OnEvent($"  ContextRelay_Create relay=0x{obj.Handle:X8} protocol={protoName} (BLOBREFs unread, rem={rdr.Remaining})");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  ContextRelay msgid={msgid} (unhandled, rem={rdr.Remaining})");
                    break;
            }
        }

        // -------- DynamicSurfaceFactory (spec §2.2.4.18) --------
        //   0 = CloseInstance         (nUniqueID i32)
        //   1 = CreateVideoInstance   (nUniqueID, idClassContext, devOwner, surScene, poolScene) — 20 B
        //   2 = CreateSurfaceInstance (nUniqueID, idClassContext, devOwner, surScene, poolScene) — 20 B
        //
        // Real handler: maintains the uid → (surface, pool, isVideo)
        // mapping that MS-DMCT OpenMedia (SurfaceID = uid) needs to
        // resolve the splash Surface handle for video positioning.
        // The composition layering is handled by the XAML Grid z-order
        // (splashBackgroundFill → MediaCanvas → splashHost) — this
        // handler doesn't need to twiddle anything at the host to
        // achieve "video appears on top of background, underneath the
        // splash scene graph". _activeVideoInstanceCount is tracked
        // purely for diagnostic logging now.
        private void DispatchDynamicSurfaceFactory(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            switch (msgid) {
                case 0: // CloseInstance
                    if (rdr.Remaining >= 4) {
                        int uid = rdr.ReadI32();
                        if (_dynamicSurfaceByUid.TryGetValue(uid, out var entry)) {
                            _dynamicSurfaceByUid.Remove(uid);
                            if (entry.IsVideo) {
                                _activeVideoInstanceCount--;
                                if (_activeVideoInstanceCount < 0) _activeVideoInstanceCount = 0;
                            }
                            // Clear the video-family tracking when the
                            // last active video instance closes — the
                            // next session re-issues CreateVideoInstance
                            // and we repopulate from fresh handle high
                            // bytes (which may differ from this session).
                            string vfamCleared = "";
                            if (_activeVideoInstanceCount == 0 && _videoFamilyHighBytes.Count > 0) {
                                _videoFamilyHighBytes.Clear();
                                vfamCleared = "   [VFAM cleared]";
                            }
                            // Also drop any PiP lock and tell the router
                            // to revert. The Visual that hosted the PiP
                            // placeholder is usually destroyed in the
                            // same batch (it's part of the chrome that
                            // appears with playback), but firing here
                            // ensures the router state matches the video
                            // lifecycle even if the host's tree teardown
                            // races us.
                            string pipCleared = "";
                            if (_activeVideoInstanceCount == 0 && _currentPipVisualHandle != 0) {
                                _currentPipVisualHandle = 0;
                                _currentPipFit          = 0;
                                _currentPipRect         = Rect.Empty;
                                pipCleared = "   [PIP cleared]";
                                RaiseVideoPipCandidateChanged(0, Rect.Empty);
                            }
                            // Binding-driven tracker cleanup: clear when
                            // the last active video instance closes.
                            if (_activeVideoInstanceCount == 0 && _videoBoundVisualHandle != 0) {
                                ClearVideoBoundVisual("video instance closed");
                            }
                            _dumper?.OnEvent($"  DynamicSurfaceFactory_CloseInstance dsf=0x{obj.Handle:X8} uid={uid} " +
                                             $"(was {(entry.IsVideo ? "Video" : "Surface")} → surface=0x{entry.SurfaceHandle:X8}, " +
                                             $"activeVideoCount={_activeVideoInstanceCount}){vfamCleared}{pipCleared}");
                        } else {
                            _dumper?.OnEvent($"  DynamicSurfaceFactory_CloseInstance dsf=0x{obj.Handle:X8} uid={uid} (no mapping)");
                        }
                    }
                    break;
                case 1: // CreateVideoInstance
                case 2: // CreateSurfaceInstance
                    if (rdr.Remaining >= 20) {
                        int  uid       = rdr.ReadI32();
                        uint clsCtx    = rdr.ReadU32();
                        uint devOwner  = rdr.ReadU32();
                        uint surScene  = rdr.ReadU32();
                        uint poolScene = rdr.ReadU32();
                        bool isVideo   = (msgid == 1);
                        string kind    = isVideo ? "Video" : "Surface";

                        // Replace any pre-existing entry for this uid —
                        // CreateXxxInstance is rare but defensive against
                        // a wire that fails to send CloseInstance first.
                        _dynamicSurfaceByUid[uid] = new DynamicSurfaceEntry {
                            SurfaceHandle = surScene,
                            PoolHandle    = poolScene,
                            IsVideo       = isVideo,
                        };
                        if (isVideo) _activeVideoInstanceCount++;
                        // When a video instance starts, extend the video
                        // family with the surface + pool high bytes so
                        // unhandled msgids on either get [VFAM] tagged
                        // and hex-dumped — see _videoFamilyHighBytes.
                        string vfamTrail = "";
                        if (isVideo) {
                            bool addSurf = TryAddVideoFamilyHighByte(surScene);
                            bool addPool = TryAddVideoFamilyHighByte(poolScene);
                            if (addSurf || addPool) {
                                vfamTrail = $"   [VFAM+ surfHi=0x{(byte)(surScene >> 24):X2}{(addSurf ? "*" : "")}" +
                                            $" poolHi=0x{(byte)(poolScene >> 24):X2}{(addPool ? "*" : "")}]";
                            }
                        }
                        _logger?.LogInfo($"[splash] DynamicSurfaceFactory_Create{kind}Instance uid={uid} " +
                                         $"-> surface=0x{surScene:X8} pool=0x{poolScene:X8} " +
                                         $"(this is the DMCT OpenMedia SurfaceID for video binding)");
                        _dumper?.OnEvent($"  DynamicSurfaceFactory_Create{kind}Instance dsf=0x{obj.Handle:X8} uid={uid} " +
                                         $"clsCtx=0x{clsCtx:X8} dev=0x{devOwner:X8} surface=0x{surScene:X8} pool=0x{poolScene:X8} " +
                                         $"activeVideoCount={_activeVideoInstanceCount}{vfamTrail}");
                    }
                    break;
                default:
                    _dumper?.OnEvent($"  DynamicSurfaceFactory msgid={msgid} (unhandled, rem={rdr.Remaining}) " +
                                     $"body={rdr.PeekRemainingHex(128)}");
                    break;
            }
        }

        // -------- ParticleSystem — never observed, not in our extract of spec --------
        // Decorative class for particle effects. Probably unused in the MCE
        // shell. Logged only.
        private void DispatchParticleSystem(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            _dumper?.OnEvent($"  ParticleSystem ps=0x{obj.Handle:X8} msgid={msgid} rem={rdr.Remaining} (stub — class not in spec extract)");
        }

        // -------- SoundDevice (spec §2.2.4.21) — legacy, superseded by XAudSoundDevice --------
        // Older WMC builds used SoundDevice for audio; this WMC build
        // uses XAudSoundDevice exclusively (we handle that elsewhere).
        // Kept as a stub for compatibility with older WMC server builds.
        //   0 = CreateSound, 1 = CreateSoundBuffer, 2 = EvictExternalResources, 3 = CreateExternalResources
        private void DispatchSoundDeviceLegacy(SplashPayloadReader rdr, int msgid) {
            _dumper?.OnEvent($"  SoundDevice msgid={msgid} rem={rdr.Remaining} (legacy stub — XAudSoundDevice is primary)");
        }

        // -------- InputRouter (Splash::Desktop::InputRouter) --------
        // Not in MS-RRSP2 spec. Observed during shell init: 2 messages
        // (msgid=1, 2; both 8-byte body) on a single instance, never
        // touched again. Configuration only — no rendering side effects.
        private void DispatchInputRouter(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            _dumper?.OnEvent($"  InputRouter ir=0x{obj.Handle:X8} msgid={msgid} rem={rdr.Remaining} (Splash::Desktop — spec undocumented, ack only)");
        }

        // -------- DesktopManager (Splash::Desktop::DesktopManager) --------
        // Not in MS-RRSP2 spec. Observed during shell init: msgid=2 (body=0)
        // and msgid=4 (body=8) on a single instance, never touched again.
        // Configuration only — no rendering side effects.
        private void DispatchDesktopManager(ISplashObject obj, SplashPayloadReader rdr, int msgid) {
            _dumper?.OnEvent($"  DesktopManager dm=0x{obj.Handle:X8} msgid={msgid} rem={rdr.Remaining} (Splash::Desktop — spec undocumented, ack only)");
        }
    }
}
