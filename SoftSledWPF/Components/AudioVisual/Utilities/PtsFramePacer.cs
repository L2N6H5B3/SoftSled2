using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoftSled.Components.AudioVisual.Utilities {

    /// <summary>
    /// Jitter buffer + PTS-scheduled frame release. Decoded BGRA frames are
    /// submitted with a presentation timestamp (ms); the pacer buffers a short
    /// pre-roll, anchors a wall clock to the first frame's PTS, then releases
    /// each frame to the present callback at its scheduled time. This smooths
    /// the bursty RTSP arrival (which the lab measured at ~160 ms gaps) into an
    /// even ~40 ms (25 fps) presentation cadence.
    ///
    /// <para>This is a self-clocked prototype of the Stage-1 video presenter.
    /// The real integration will slave the schedule to the NAudio master clock
    /// instead of a free-running wall clock (which avoids long-term drift); the
    /// buffering / scheduled-release mechanics are otherwise the same.</para>
    /// </summary>
    internal sealed class PtsFramePacer : IDisposable {

        // Pre-roll: hold this much buffered media before starting the clock, so
        // the buffer can absorb arrival gaps. Must exceed the worst arrival gap
        // (~160 ms observed) with headroom. Configurable from the lab UI so we
        // can sweep it and watch the effect on smoothness.
        private readonly int _prerollMs;
        // Bound the buffer so a slow drift / stall can't grow it unbounded.
        // Kept comfortably above the pre-roll so the buffer can actually reach
        // the pre-roll depth before the overflow-drop kicks in. NOT readonly:
        // grown by SetSyncOffsetMs to also hold the A/V-offset-induced delay
        // (see there) — otherwise a large server pre-play skew overflows the
        // buffer every cycle and video oscillates freeze↔catch-up.
        private int _maxBufferMs;
        // Upper bound on the buffer so a pathological offset can't blow up
        // memory. Frames are buffered as packed yuv420p (~3 MB each at 1080p,
        // vs ~8 MB for BGRA) so 10 s at 1080p25 ≈ 750 MB worst case (was ~1.66 GB
        // pre-YUV). Sized to cover the largest REALISTIC content offset: the
        // buffer must hold preroll + 2000 + |offset| (see SizeBufferForOffset),
        // and observed long-GOP I-frame lead-ins reach ~6.2 s → need ~8.45 s. At
        // 8 s the buffer couldn't hold those and dropped a frame every cycle
        // (judder); 10 s covers offsets up to ~7.75 s. Offsets beyond that (the
        // controller accepts up to MaxPlausibleContentOffsetMs = 15 s) still
        // exceed the cap and drop frames — logged in SizeBufferForOffset — but
        // holding 15 s of decoded video (~1.3 GB) is impractical, especially on
        // the 32-bit build, so the cap deliberately trades those rare extremes
        // for a bounded footprint.
        private const int MaxBufferHardCapMs = 10000;

        public int PrerollMs => _prerollMs;

        private readonly Action<IntPtr, int, int, int> _present;
        private readonly Logger _log;

        // Queued frames are stored as packed yuv420p (1.5 B/px); this converts
        // each released frame to BGRA at present time (see Submit / Loop). Only
        // frames actually shown are converted — dropped frames cost nothing.
        private readonly Yuv420ToBgra _converter;

        private readonly object _gate = new object();
        private readonly Queue<Frame> _queue = new Queue<Frame>();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();
        private int _bufBytes;

        // Packed yuv420p byte count for a w×h frame: Y plane w*h plus U and V
        // planes cw*ch each (cw=(w+1)/2, ch=(h+1)/2). This is what a queued frame
        // occupies — 1.5× the pixel count vs 4× for BGRA.
        private static int Yuv420Size(int w, int h) {
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            return w * h + 2 * cw * ch;
        }

        private Thread _thread;
        private volatile bool _stop;
        private bool _disposed;

        private long _pts0 = long.MinValue;
        private bool _started;
        private Stopwatch _wall;

        // Audio-slaved mode (production). When _masterClockMs is set, frames
        // are released against an external clock (the NAudio audio position)
        // instead of the self-clocked wall clock: a frame is due when
        // (framePts - firstPts) <= masterClockMs() + _syncOffsetMs. The offset
        // carries the wire A/V skew (audioFirstWirePts - videoFirstWirePts)
        // plus the user's manual audio-sync trim, so video lands in lip-sync
        // with audio (and the server's pre-play padding frames are skipped at
        // startup). _freeRun bypasses scheduling entirely (trick play / no
        // audio clock): present the newest frame as it arrives.
        private Func<long> _masterClockMs;
        private long _syncOffsetMs;
        private long _targetOffsetMs;   // slew goal; _syncOffsetMs eases toward it
        private long _slewLastMs = -1;  // _diagClock ms at last slew step
        private const int OffsetSlewMsPerSec = 150; // ≤15% momentary video speed change during a correction
        private volatile bool _freeRun;
        private long _masterAtAnchor;   // master-clock value when _pts0 was captured
        private bool _anchored;         // false until the first anchor; gates initial-vs-seek anchor
        // Master value to use as masterAtAnchor for the NEXT re-anchor (the audio
        // segment's true start, from NAudioMasterRenderer.SegmentStartMasterMs).
        // long.MinValue = unset → fall back to the current master. Consumed once.
        private long _reanchorMasterOverride = long.MinValue;

        public void SetMasterClock(Func<long> masterClockMs) { _masterClockMs = masterClockMs; }

        // STAGE B2: content-time source for video frames. Returns (content − wire)
        // ms for the video stream, measured continuously by the controller's
        // content-clock survey and constant within a position. A frame's content
        // time = its wire PtsMs + this drift. long.MinValue = not yet measured.
        // Kept as drift-reconstruction (not pkt->pts = content) so the decoder
        // keeps reordering on the wire pts the offset rule still depends on; the
        // reconstruction is exact within a position because the drift is constant.
        // Stage C releases against content using this; for now it only feeds the
        // shadow's frameContent so BOTH sides are productionised.
        private Func<long> _videoContentDrift;
        public void SetVideoContentDrift(Func<long> drift) { _videoContentDrift = drift; }

        // STAGE C: content-release mode. When enabled, the release loop ignores
        // the derived offset + pts0/masterAtAnchor anchor entirely and instead
        // releases each frame when its CONTENT time (PtsMs + drift) has been
        // reached by the audible content clock:  PtsMs + drift <= audible + trim.
        // The offset (SetSyncOffsetMs) is still honoured for BUFFER SIZING only.
        private volatile bool _contentRelease;
        private Func<long> _contentClockMs;   // smoothed audible content time
        private long _contentTrimMs;
        public void SetContentReleaseMode(bool on) { _contentRelease = on; }
        public void SetContentClock(Func<long> audibleContentMs) { _contentClockMs = audibleContentMs; }
        public void SetContentTrimMs(long trimMs) { Interlocked.Exchange(ref _contentTrimMs, trimMs); }
        private long _lastReleasedContentMs = long.MinValue;
        /// <summary>Content time (ms) of the most recently released frame —
        /// PtsMs + video drift. long.MinValue until drift is available.</summary>
        public long LastReleasedContentMs { get { lock (_gate) { return _lastReleasedContentMs; } } }

        /// <summary>Set the masterAtAnchor to use on the NEXT re-anchor (see
        /// <see cref="_reanchorMasterOverride"/>). Call right before Reanchor().</summary>
        public void SetReanchorMasterOverride(long masterMs) {
            System.Threading.Interlocked.Exchange(ref _reanchorMasterOverride, masterMs);
        }

        /// <summary>Set the A/V sync offset INSTANTLY (startup / seek). Also
        /// clears any in-flight slew so the value sticks.</summary>
        public void SetSyncOffsetMs(long offsetMs) {
            System.Threading.Interlocked.Exchange(ref _syncOffsetMs, offsetMs);
            System.Threading.Interlocked.Exchange(ref _targetOffsetMs, offsetMs);
            SizeBufferForOffset(offsetMs, slew: false);
        }

        /// <summary>The sync offset currently in effect (ms). Diagnostic
        /// (av-timing): the pacer releases a frame when
        /// <c>elapsed &lt;= master + this</c>, so on-screen video content ≈
        /// <c>master + this</c>.</summary>
        public long CurrentSyncOffsetMs => System.Threading.Interlocked.Read(ref _syncOffsetMs);

        /// <summary>Ease the offset toward <paramref name="target"/> at
        /// <see cref="OffsetSlewMsPerSec"/> (done in the release loop) so an
        /// auto-correction lands as a brief, smooth video speed nudge rather
        /// than a visible jump. Used when the Correspondence estimate converges
        /// to a better offset than the initial RTP-Info value.</summary>
        public void SlewSyncOffsetMs(long target) {
            System.Threading.Interlocked.Exchange(ref _targetOffsetMs, target);
            SizeBufferForOffset(target, slew: true);
        }

        // The buffer must hold the WHOLE offset-induced video delay PLUS the
        // jitter/pre-roll headroom, or it overflows every cycle (freeze↔catch-up
        // stutter). Grow to fit |offset|; never shrink mid-slew below what the
        // current offset still needs.
        private void SizeBufferForOffset(long offsetMs, bool slew) {
            int want = _prerollMs + 2000 + (int)Math.Abs(offsetMs);
            lock (_gate) {
                int newMax = Math.Min(want, MaxBufferHardCapMs);
                if (slew && newMax < _maxBufferMs) newMax = _maxBufferMs; // don't shrink during a correction
                _maxBufferMs = newMax;
                if (want > MaxBufferHardCapMs) {
                    _log?.LogInfo($"[pacer] offset {offsetMs}ms needs {want}ms buffer but capped at " +
                                  $"{MaxBufferHardCapMs}ms — video may drop frames (raise cap or reduce offset)");
                } else {
                    _log?.LogInfo($"[pacer] maxBuffer set to {_maxBufferMs}ms (preroll {_prerollMs} + 2000 + |offset| {Math.Abs(offsetMs)})");
                }
            }
        }
        public void SetFreeRun(bool freeRun) { _freeRun = freeRun; }

        // Pause: hold the release loop without touching the queue or the
        // anchor. Queued frames are retained; no frame is released, no
        // underflow is counted, and the offset slew is frozen. The master
        // clock (audio device position) freezes during a pause anyway, so on
        // resume the timeline simply continues from where it stopped — no
        // re-anchor needed.
        private volatile bool _paused;
        public void SetPaused(bool paused) { _paused = paused; }

        /// <summary>Drop the timeline anchor so the next frame re-anchors both
        /// the video PTS and the master clock (used after a seek, where both
        /// streams jump). Also clears any queued frames so stale pre-seek
        /// frames aren't presented.</summary>
        public void Reanchor() {
            lock (_gate) {
                _pts0 = long.MinValue;
                _started = false;
                while (_queue.Count > 0) { Recycle(_queue.Dequeue().Buf); }
                _bufBytes = 0;
            }
        }

        private long _released;
        private long _underflows;
        private long _dropped;
        private double _lastReleaseMs = -1;
        private double _maxReleaseGapMs;

        // Once-per-second diagnostic so the A/V lag is visible in the log file.
        private readonly Stopwatch _diagClock = Stopwatch.StartNew();
        private long _lastDiagMs;
        private long _lastReleasedElapsed;
        private long _diagPrevReleased, _diagPrevDropped, _diagPrevUnderflows;

        /// <summary>Current buffered video duration in ms — the PTS span of the
        /// queued (decoded, not-yet-presented) frames. This is the honest
        /// occupancy to report in the video RTCP BFR W3 field: when the server
        /// under-delivers video the span shrinks, signalling it to speed up
        /// (mirrors NAudioMasterRenderer.BufferedMs for audio).</summary>
        public int BufferedMs {
            get {
                lock (_gate) {
                    if (_queue.Count < 2) return 0;
                    var arr = _queue.ToArray();
                    long span = arr[arr.Length - 1].PtsMs - arr[0].PtsMs;
                    return span < 0 ? 0 : (span > 65535 ? 65535 : (int)span);
                }
            }
        }

        /// <summary>Target decode depth for the video decoder's backpressure: fill
        /// the decoded-frame buffer to here but no further, so it stays BELOW the
        /// drop-NEW cap (<see cref="_maxBufferMs"/>). A FIXED threshold breaks for
        /// small offsets — maxBuffer=250+2000+|offset|, so e.g. offset 379 →
        /// maxBuffer 2629; a 3000ms fixed high-water never engages, the bursty
        /// decoder overflows the buffer, the overflow drops FUTURE frames, and
        /// video freezes. Tracking maxBuffer−margin keeps backpressure effective
        /// at every offset.</summary>
        public int BackpressureTargetMs {
            get { lock (_gate) { int t = _maxBufferMs - 500; return t < 1000 ? 1000 : t; } }
        }

        public long Released   => Interlocked.Read(ref _released);
        public long Underflows => Interlocked.Read(ref _underflows);
        public long Dropped    => Interlocked.Read(ref _dropped);

        /// <summary>PTS (relative to the anchor frame, ms) of the most recently
        /// RELEASED video frame — i.e. the video content the pacer believes is
        /// on screen now. Diagnostic: the controller's [av-timing] snapshot
        /// compares this against the audio master clock to expose the residual
        /// A/V skew the pacer's own drift accounting can't see (release→present
        /// and master→audible latencies).</summary>
        public long LastReleasedElapsedMs { get { lock (_gate) { return _lastReleasedElapsed; } } }

        /// <summary>STAGE A (content-clock migration): ABSOLUTE pts (ms) of the
        /// most recently released frame — not anchor-relative like
        /// <see cref="LastReleasedElapsedMs"/>. The shadow comparison needs the
        /// raw wire pts so it can be converted to content time independently of
        /// pts0/anchor, which is exactly the machinery under evaluation.
        /// long.MinValue until the first release.</summary>
        public long LastReleasedPtsMs { get { lock (_gate) { return _lastReleasedPts; } } }
        private long _lastReleasedPts = long.MinValue;

        /// <summary>Largest gap (ms) between consecutive released frames since
        /// the last read. Reading resets it. Ideal ≈ the frame period (~40 ms).</summary>
        public double ReadMaxReleaseGapMs() {
            lock (_gate) { double v = _maxReleaseGapMs; _maxReleaseGapMs = 0; return v; }
        }

        public PtsFramePacer(Action<IntPtr, int, int, int> present, int prerollMs, Logger log) {
            _present = present;
            _log = log;
            _converter = new Yuv420ToBgra(log);
            _prerollMs = prerollMs > 0 ? prerollMs : 250;
            // Headroom above the pre-roll so the buffer can fill to the
            // pre-roll depth, plus slack for drift before overflow-dropping.
            _maxBufferMs = _prerollMs + 2000;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PtsFramePacer" };
            _thread.Start();
        }

        /// <summary>Submit a decoded frame as packed yuv420p (from
        /// <see cref="LibAvVideoPushDecoder"/>). Copies into a pooled buffer so
        /// the caller's decode buffer can be reused immediately; the frame is
        /// colour-converted to BGRA only when it is later released to the screen.
        /// <paramref name="stride"/> is the luma stride (= width); the packed
        /// size is derived from width/height. <paramref name="colorspace"/> /
        /// <paramref name="range"/> are the source frame's AVColorSpace /
        /// AVColorRange, carried to the deferred YUV→BGRA conversion so it picks
        /// the right matrix (BT.709 HD / BT.601 SD).</summary>
        public void Submit(IntPtr src, int stride, int w, int h, long ptsMs, int colorspace, int range) {
            if (_disposed || src == IntPtr.Zero || w <= 0 || h <= 0) return;
            int need = Yuv420Size(w, h);
            lock (_gate) {
                // Audio-slaved overflow: drop the NEWEST (this) frame, not the
                // oldest. When the master (audio) clock runs slower than video
                // arrives — e.g. the server under-delivers audio at ~94% of
                // real-time, so the audio-bytes clock advances at ~94% while
                // video MAUs keep coming at ~100% — the video buffer climbs to
                // its bound and the surplus is always FUTURE frames the clock
                // won't reach for a while. Dropping the oldest (the original
                // behaviour) discards the unplayed frame nearest the clock and
                // slides the queue head ahead of the clock, so nothing ever
                // satisfies the release gate again and video FREEZES on the
                // last frame while audio plays on. Dropping the newest keeps
                // the head pinned to the audio clock: video stays in sync and
                // degrades to a small, spread-out frame-drop instead.
                if (_masterClockMs != null && !_freeRun && _queue.Count > 0
                    && (ptsMs - _queue.Peek().PtsMs) > _maxBufferMs) {
                    Interlocked.Increment(ref _dropped);
                    return;
                }

                byte[] buf = (_pool.Count > 0 && _pool.Peek().Length >= need)
                             ? _pool.Pop() : new byte[need];
                Marshal.Copy(src, buf, 0, need);
                _queue.Enqueue(new Frame { Buf = buf, Stride = stride, W = w, H = h, PtsMs = ptsMs,
                                           Colorspace = colorspace, Range = range });
                _bufBytes += need;

                // Free-run / self-clocked: keep latest-wins by dropping oldest
                // if the buffer overruns its time bound.
                while (_queue.Count > 1 &&
                       (_queue.ToArray()[_queue.Count - 1].PtsMs - _queue.Peek().PtsMs) > _maxBufferMs) {
                    var old = _queue.Dequeue();
                    _bufBytes -= old.Buf.Length;
                    Recycle(old.Buf);
                    Interlocked.Increment(ref _dropped);
                }
            }
        }

        private void Loop() {
            while (!_stop) {
                // Paused: hold without releasing, dropping, or counting
                // underflows. Queued frames stay put; resume continues against
                // the master clock (which is also frozen while paused).
                if (_paused) { Thread.Sleep(5); continue; }

                Frame? due = null;
                // Diagnostic snapshot for the once-per-second log line.
                bool sSlaved = false; long sMaster = 0, sAnchor = 0, sOffset = 0, sElapsed = 0;
                int sQueue = 0; long sBufSpan = 0; bool sFreeRun = false;
                lock (_gate) {
                    // Ease _syncOffsetMs toward the slew target (auto-correction),
                    // capped at OffsetSlewMsPerSec so the offset change reads as a
                    // brief smooth video speed nudge rather than a jump.
                    {
                        long cur = Interlocked.Read(ref _syncOffsetMs);
                        long tgt = Interlocked.Read(ref _targetOffsetMs);
                        long nowMs = _diagClock.ElapsedMilliseconds;
                        if (_slewLastMs < 0) _slewLastMs = nowMs;
                        long dt = nowMs - _slewLastMs;
                        _slewLastMs = nowMs;
                        if (cur != tgt) {
                            long step = OffsetSlewMsPerSec * dt / 1000;
                            if (step < 1) step = 1;
                            long diff = tgt - cur;
                            if (Math.Abs(diff) <= step) cur = tgt;
                            else cur += Math.Sign(diff) * step;
                            Interlocked.Exchange(ref _syncOffsetMs, cur);
                        }
                    }
                    // Snapshot the true state up-front so the periodic diagnostic
                    // is meaningful even on empty-queue / free-run iterations
                    // (which skip the slaved branch below).
                    sSlaved = _masterClockMs != null;
                    sFreeRun = _freeRun;
                    sMaster = sSlaved ? _masterClockMs() : 0;
                    sAnchor = _masterAtAnchor;
                    sOffset = Interlocked.Read(ref _syncOffsetMs);
                    sQueue = _queue.Count;
                    if (_queue.Count > 0) {
                        var arrS = _queue.ToArray();
                        sBufSpan = arrS[arrS.Length - 1].PtsMs - arrS[0].PtsMs;
                    }
                    if (_queue.Count == 0) {
                        if (_started) Interlocked.Increment(ref _underflows);
                    } else if (_freeRun) {
                        // Trick play / no clock: present the newest frame now,
                        // drop the rest.
                        while (_queue.Count > 0) {
                            if (due != null) { Recycle(due.Value.Buf); Interlocked.Increment(ref _dropped); }
                            var f = _queue.Dequeue();
                            _bufBytes -= f.Buf.Length;
                            due = f;
                        }
                    } else if (_contentRelease && _contentClockMs != null) {
                        // ---- STAGE C: content-time release ----
                        // Release each frame when its content time has been
                        // reached by the audible content clock. No offset, no
                        // pts0/anchor — the shared timeline aligns the streams
                        // directly, so a seek/reposition needs no re-derivation.
                        long audible = _contentClockMs();
                        var vcd = _videoContentDrift;
                        long drift = vcd != null ? vcd() : long.MinValue;
                        long trim = Interlocked.Read(ref _contentTrimMs);
                        if (audible != long.MinValue && drift != long.MinValue) {
                            _started = true;
                            // frameContent = PtsMs + drift <= audible + trim
                            long threshold = audible + trim - drift;
                            while (_queue.Count > 0 && _queue.Peek().PtsMs <= threshold) {
                                if (due != null) {
                                    Recycle(due.Value.Buf);
                                    Interlocked.Increment(ref _dropped);
                                }
                                var f = _queue.Dequeue();
                                _bufBytes -= f.Buf.Length;
                                due = f;
                            }
                            if (due != null) {
                                _lastReleasedPts = due.Value.PtsMs;
                                _lastReleasedContentMs = due.Value.PtsMs + drift;
                                // Diag: released content vs the target (audible+trim);
                                // ~0 means the frame on screen matches the audio.
                                _lastReleasedElapsed = _lastReleasedContentMs;
                                sElapsed = audible + trim;
                            }
                        }
                        // audible/drift not ready (post-seek re-establish) → hold.
                        sQueue = _queue.Count;
                    } else {
                        bool slaved = _masterClockMs != null;
                        if (_pts0 == long.MinValue) {
                            _pts0 = _queue.Peek().PtsMs;
                            // DIAGNOSTIC: the frame the pacer ACTUALLY anchors to.
                            // Compare against the controller's offset video
                            // reference (_firstVideoMauWirePtsMs, logged as
                            // "videoPts=" in [ext-sync] A/V offset): if pts0 !=
                            // videoPts, the offset used the wrong video origin
                            // (first-ARRIVED MAU vs first-DECODED frame) — the
                            // variable-lead-in-drop skew.
                            long mnAnchor = slaved ? _masterClockMs() : 0;
                            if (slaved) {
                                // The FIRST anchor of the session ties video to
                                // audio's ORIGIN (master clock = 0 = first audio
                                // sample = the play point). Audio reaches the
                                // device sooner than video does (video has more
                                // startup latency: first I-frame + reorder
                                // buffer), so anchoring to the audio clock's
                                // CURRENT value here would bake that startup gap
                                // in as a permanent video lag. Anchoring to 0
                                // lets a late video decoder catch up instead.
                                //
                                // A post-seek/FF/pause RE-ANCHOR instead uses the
                                // master value at which the newly-buffered audio
                                // segment STARTS playing (SetReanchorMasterOverride,
                                // from the renderer). Using the current master (=
                                // the video-frame-arrival time, which trails the
                                // audio restart by the video decode latency) baked
                                // that latency in as a permanent audio-ahead lag.
                                // Sanity-clamp the override to ±8s of the current
                                // master, else fall back to current.
                                if (!_anchored) {
                                    _masterAtAnchor = 0;
                                } else {
                                    long ov = Interlocked.Exchange(ref _reanchorMasterOverride, long.MinValue);
                                    _masterAtAnchor = (ov != long.MinValue && Math.Abs(mnAnchor - ov) <= 8000)
                                                      ? ov : mnAnchor;
                                }
                                _anchored = true;
                            }
                            _log?.LogInfo($"[pacer] anchored pts0={_pts0}ms " +
                                          $"(slaved={slaved}, masterNow={mnAnchor}ms, anchor={_masterAtAnchor}ms)");
                        }

                        long elapsed;
                        bool release;
                        long masterNow = slaved ? _masterClockMs() : 0;
                        long offNow = Interlocked.Read(ref _syncOffsetMs);
                        if (slaved) {
                            // Audio-slaved: the timeline is the audio position,
                            // measured from the anchor so a post-seek re-anchor
                            // (which resets both _pts0 and _masterAtAnchor) lines
                            // up cleanly. offset carries the wire A/V skew + trim.
                            elapsed = (masterNow - _masterAtAnchor) + offNow;
                            release = true; // the audio clock governs, no pre-roll gate
                            _started = true;
                        } else {
                            // Self-clocked (lab): wait for the pre-roll span,
                            // then run a free wall clock.
                            if (!_started) {
                                var arr = _queue.ToArray();
                                long span = arr[arr.Length - 1].PtsMs - _pts0;
                                if (span >= _prerollMs) { _started = true; _wall = Stopwatch.StartNew(); }
                            }
                            elapsed = _started ? _wall.ElapsedMilliseconds : 0;
                            release = _started;
                        }

                        if (release) {
                            // Release every frame whose schedule has passed; if
                            // several are due (e.g. startup pre-play padding, or
                            // we fell behind), keep only the latest and recycle
                            // the skipped ones.
                            while (_queue.Count > 0 && (_queue.Peek().PtsMs - _pts0) <= elapsed) {
                                if (due != null) {
                                    Recycle(due.Value.Buf);
                                    Interlocked.Increment(ref _dropped);
                                }
                                var f = _queue.Dequeue();
                                _bufBytes -= f.Buf.Length;
                                due = f;
                            }
                            if (due != null) {
                                _lastReleasedElapsed = due.Value.PtsMs - _pts0;
                                _lastReleasedPts = due.Value.PtsMs;   // Stage A shadow
                                // Stage B2: express the released frame on the
                                // content timeline (wire pts + measured drift).
                                var vcd = _videoContentDrift;
                                long d = vcd != null ? vcd() : long.MinValue;
                                _lastReleasedContentMs = d != long.MinValue
                                    ? due.Value.PtsMs + d : long.MinValue;
                            }
                        }

                        // elapsed is only computed in this branch; the rest of
                        // the snapshot was already taken above the if/else.
                        sElapsed = elapsed; sQueue = _queue.Count;
                    }
                }

                if (due != null) {
                    var f = due.Value;
                    var gch = GCHandle.Alloc(f.Buf, GCHandleType.Pinned);
                    try {
                        // Convert the packed yuv420p frame to BGRA (into the
                        // converter's own native scratch), then present. The pin
                        // only needs to last for the conversion (which reads the
                        // pinned YUV); _present then copies the BGRA scratch
                        // synchronously (D3D SubmitFrame copies into its staging).
                        IntPtr bgra = _converter.Convert(gch.AddrOfPinnedObject(), f.W, f.H, f.Colorspace, f.Range);
                        if (bgra != IntPtr.Zero) {
                            _present(bgra, _converter.Stride, f.W, f.H);
                        }
                    } catch (Exception ex) {
                        _log?.LogError($"[pacer] present threw: {ex.Message}");
                    } finally {
                        gch.Free();
                    }
                    RecordRelease();
                    Recycle(f.Buf);
                    Interlocked.Increment(ref _released);
                } else {
                    Thread.Sleep(2); // sub-frame granularity; next due check soon
                }

                // Once-per-second state dump (via _log → configured log file when
                // A/V logging is on). drift<0 = released frame is BEHIND the
                // target (video lagging — decoder not delivering ahead, or the
                // offset is too small); off=0 = SR offset not finalised yet.
                if (_log != null && _diagClock.ElapsedMilliseconds - _lastDiagMs >= 1000) {
                    _lastDiagMs = _diagClock.ElapsedMilliseconds;
                    long rel = Interlocked.Read(ref _released);
                    long drp = Interlocked.Read(ref _dropped);
                    long und = Interlocked.Read(ref _underflows);
                    long relD = rel - _diagPrevReleased; _diagPrevReleased = rel;
                    long drpD = drp - _diagPrevDropped;  _diagPrevDropped = drp;
                    long undD = und - _diagPrevUnderflows; _diagPrevUnderflows = und;
                    _log.LogInfo(
                        $"[pacer] slaved={sSlaved} freeRun={sFreeRun} off={sOffset}ms master={sMaster}ms anchor={sAnchor}ms " +
                        $"target={sElapsed}ms lastRelEl={_lastReleasedElapsed}ms " +
                        $"drift={_lastReleasedElapsed - sElapsed}ms q={sQueue} bufSpan={sBufSpan}ms " +
                        $"rel/s={relD} drop/s={drpD} under/s={undD}");
                }
            }
        }

        private void RecordRelease() {
            // Use the always-running diagnostic clock, not _wall: _wall is only
            // started in self-clocked (lab) mode, so in audio-slaved (production)
            // mode it is null and the release-gap metric silently stayed 0.
            double now = _diagClock.Elapsed.TotalMilliseconds;
            lock (_gate) {
                if (_lastReleaseMs >= 0) {
                    double gap = now - _lastReleaseMs;
                    if (gap > _maxReleaseGapMs) _maxReleaseGapMs = gap;
                }
                _lastReleaseMs = now;
            }
        }

        // Pool cap: a small ring of reusable frame buffers to avoid per-frame LOH
        // allocation. Kept low (3) — the queue itself holds the working set, so a
        // deep idle pool is just retained RAM. At yuv420p a 1080p buffer is ~3 MB,
        // so 3 pooled ≈ 9 MB worst case.
        private const int PoolCap = 3;

        private void Recycle(byte[] buf) {
            lock (_gate) {
                if (_pool.Count < PoolCap) _pool.Push(buf);
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            try { _thread?.Join(2000); } catch { }
            _thread = null;
            lock (_gate) { _queue.Clear(); _pool.Clear(); }
            try { _converter.Dispose(); } catch { }
        }

        private struct Frame {
            public byte[] Buf;
            public int Stride, W, H;
            public long PtsMs;
            public int Colorspace, Range;   // source AVColorSpace / AVColorRange
        }
    }
}
