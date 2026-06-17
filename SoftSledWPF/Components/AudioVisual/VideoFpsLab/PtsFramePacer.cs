using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

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
        // memory (decoded BGRA frames: ~8 MB each at 1080p25 → ~1.66 GB at
        // 8 s). The good 4 s-jitter session already ran a 6 s buffer fine.
        private const int MaxBufferHardCapMs = 8000;

        public int PrerollMs => _prerollMs;

        private readonly Action<IntPtr, int, int, int> _present;
        private readonly Logger _log;

        private readonly object _gate = new object();
        private readonly Queue<Frame> _queue = new Queue<Frame>();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();
        private int _bufBytes;

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

        public void SetMasterClock(Func<long> masterClockMs) { _masterClockMs = masterClockMs; }

        /// <summary>Set the A/V sync offset INSTANTLY (startup / seek). Also
        /// clears any in-flight slew so the value sticks.</summary>
        public void SetSyncOffsetMs(long offsetMs) {
            System.Threading.Interlocked.Exchange(ref _syncOffsetMs, offsetMs);
            System.Threading.Interlocked.Exchange(ref _targetOffsetMs, offsetMs);
            SizeBufferForOffset(offsetMs, slew: false);
        }

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

        public long Released   => Interlocked.Read(ref _released);
        public long Underflows => Interlocked.Read(ref _underflows);
        public long Dropped    => Interlocked.Read(ref _dropped);

        /// <summary>Largest gap (ms) between consecutive released frames since
        /// the last read. Reading resets it. Ideal ≈ the frame period (~40 ms).</summary>
        public double ReadMaxReleaseGapMs() {
            lock (_gate) { double v = _maxReleaseGapMs; _maxReleaseGapMs = 0; return v; }
        }

        public PtsFramePacer(Action<IntPtr, int, int, int> present, int prerollMs, Logger log) {
            _present = present;
            _log = log;
            _prerollMs = prerollMs > 0 ? prerollMs : 250;
            // Headroom above the pre-roll so the buffer can fill to the
            // pre-roll depth, plus slack for drift before overflow-dropping.
            _maxBufferMs = _prerollMs + 2000;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PtsFramePacer" };
            _thread.Start();
        }

        /// <summary>Submit a decoded BGRA frame. Copies into a pooled buffer so
        /// the caller's decode buffer can be reused immediately.</summary>
        public void Submit(IntPtr src, int stride, int w, int h, long ptsMs) {
            if (_disposed || src == IntPtr.Zero) return;
            int need = stride * h;
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
                _queue.Enqueue(new Frame { Buf = buf, Stride = stride, W = w, H = h, PtsMs = ptsMs });
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
                    } else {
                        bool slaved = _masterClockMs != null;
                        if (_pts0 == long.MinValue) {
                            _pts0 = _queue.Peek().PtsMs;
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
                                // Only a post-seek re-anchor uses the current
                                // audio position (the seek point).
                                _masterAtAnchor = _anchored ? _masterClockMs() : 0;
                                _anchored = true;
                            }
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
                            if (due != null) _lastReleasedElapsed = due.Value.PtsMs - _pts0;
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
                        // SubmitFrame copies synchronously, so the pin only
                        // needs to last for this call.
                        _present(gch.AddrOfPinnedObject(), f.Stride, f.W, f.H);
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
            double now = _wall?.Elapsed.TotalMilliseconds ?? 0;
            lock (_gate) {
                if (_lastReleaseMs >= 0) {
                    double gap = now - _lastReleaseMs;
                    if (gap > _maxReleaseGapMs) _maxReleaseGapMs = gap;
                }
                _lastReleaseMs = now;
            }
        }

        private void Recycle(byte[] buf) {
            lock (_gate) {
                if (_pool.Count < 8) _pool.Push(buf);
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            try { _thread?.Join(2000); } catch { }
            _thread = null;
            lock (_gate) { _queue.Clear(); _pool.Clear(); }
        }

        private struct Frame {
            public byte[] Buf;
            public int Stride, W, H;
            public long PtsMs;
        }
    }
}
