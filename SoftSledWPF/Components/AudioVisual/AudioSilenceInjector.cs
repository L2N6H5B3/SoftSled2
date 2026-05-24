using SoftSled.Components.Diagnostics;
using System;
using System.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Watchdog that keeps FFME's audio buffer fed during server-side
    /// trick play.
    ///
    /// <para>The problem this exists to solve: WMPNss-class media
    /// servers (the DLNA convention) stop transmitting audio for
    /// playback rates ≠ 1×. FFME's render pipeline is audio-clock-
    /// locked by default — when its audio buffer drains it enters
    /// <c>SYNC-BUFFER</c> state and the video element freezes,
    /// even though we still have dozens of decoded video frames queued.
    /// Setting <c>IsTimeSyncDisabled = true</c> avoids the freeze but
    /// breaks per-stream lip-sync at 1× because the master clock no
    /// longer exists. This watchdog lets us keep
    /// <c>IsTimeSyncDisabled = false</c> (correct sync at 1×) while
    /// still preventing trick-play freezes by ensuring the audio queue
    /// never goes dry.</para>
    ///
    /// <para>How: tap the real audio MAU stream. Track wall-clock
    /// arrival times to learn the cadence and RTP-timestamp deltas to
    /// learn frame duration. When no real MAU has arrived for >200 ms
    /// AND we've seen enough real MAUs to know the cadence, emit
    /// copies of the most recent MAU into the downstream pipeline at
    /// the observed cadence with monotonically-incrementing RTP
    /// timestamps. The injected MAUs are bit-identical to the last
    /// real frame — codec-agnostic, no encoding work, no extradata
    /// dependency. Resumes are silent: on the next real MAU we mark
    /// injection off and let the wire take over again.</para>
    ///
    /// <para>Side effects:
    /// <list type="bullet">
    ///   <item>Output is the last frame's audio repeated — typically
    ///   the listener won't hear it (trick play is a visual
    ///   operation; users aren't paying attention to audio at 10× FF).
    ///   For codecs where every frame is silence-decoder-friendly
    ///   (MP2/MP3 frame headers stay valid when re-PTS'd) this is
    ///   inaudible loop artifacts; for AC3 there may be a faint
    ///   ticking at the frame boundary.</item>
    ///   <item>PTS values stay monotonic because we add the running
    ///   average delta on each synthetic frame. FFME's demuxer accepts
    ///   them as normal frames.</item>
    /// </list></para>
    ///
    /// <para>Thread model: all mutation happens under <c>_gate</c>.
    /// The watchdog timer fires on a thread-pool thread; consumers
    /// receive both real and synthetic MAUs on whatever thread
    /// produced them.</para>
    /// </summary>
    internal sealed class AudioSilenceInjector : IDisposable {

        // Tick period for the watchdog. Each tick: check whether the
        // wire has gone quiet, and (if injecting) emit any synthetic
        // frames whose wall-clock arrival time has passed. 10 ms gives
        // sub-frame resolution for typical 24 ms (MP2/MP3) and 32 ms
        // (AC3) frame durations.
        private const int TickPeriodMs = 10;

        // Wall-clock gap that triggers injection. Pickered to be
        // larger than a couple of frame durations (~50 ms for AC3) but
        // small enough that the audio buffer doesn't drain first
        // (FFME's buffer typically holds 300–500 ms).
        private const int QuietThresholdMs = 200;

        // How many real MAUs we need to observe before we have a
        // reliable cadence estimate. After this we'll consent to
        // inject if the wire goes quiet. Below this we stay passive
        // (better to allow a brief freeze than to inject noise based
        // on a bad estimate).
        private const int MinSamplesForInjection = 8;

        private readonly Logger _log;
        private readonly Action<byte[], uint> _emit;
        private readonly object _gate = new object();

        // Last real MAU + arrival metadata. The byte[] is held by
        // reference; we never mutate it.
        private byte[] _lastMau;
        private uint _lastRtpTs;
        private long _lastRealMauTickMs;
        private int _realMauCount;

        // Running EWMA of RTP-timestamp delta between consecutive real
        // MAUs. RTP units (per-stream clock).
        private uint _avgRtpDelta;

        // Running EWMA of wall-clock interval between consecutive real
        // MAUs (milliseconds). Drives the synthetic emission cadence
        // during injection.
        private double _avgWallIntervalMs;

        // Injection state. When _injecting is true, the timer emits
        // synthetic frames; nextInjectionWallMs tracks when the next
        // synthetic frame is due.
        private bool _injecting;
        private long _nextInjectionWallMs;
        private long _syntheticEmitCount;

        // Trick-play gate. The injector does NOTHING until the host
        // tells us trick play is in progress (rate ≠ 1.0). This
        // prevents the original false-positive: any wire-side
        // buffering pause >200 ms would trigger injection, producing
        // an audible rapid-repeat artefact when the real wire
        // resumed and we'd been queueing synthetic frames the whole
        // time. The host calls SetTrickPlayActive(true) when it
        // issues a SetRate ≠ 1.0 and (false) when it returns to 1.0,
        // which matches the only situation where the server actually
        // stops the audio stream (server-side trick play per the
        // WMPNss / DLNA convention).
        private bool _trickPlayActive;

        private Timer _timer;
        private bool _disposed;

        /// <summary>
        /// Construct an injector that emits synthetic audio MAUs to
        /// <paramref name="emit"/>. The injector starts armed (timer
        /// running); it stays passive until the first real MAU
        /// arrives via <see cref="NoticeReal"/>.
        /// </summary>
        public AudioSilenceInjector(Action<byte[], uint> emit, Logger log) {
            _emit = emit ?? throw new ArgumentNullException(nameof(emit));
            _log = log;
            _timer = new Timer(Tick, null, TickPeriodMs, TickPeriodMs);
        }

        /// <summary>
        /// Notify the injector that a real audio MAU arrived from the
        /// wire. Updates cadence/delta running averages and resets the
        /// quiet-wall-clock timer. Caller is still responsible for
        /// forwarding the MAU to its downstream consumer — this method
        /// does NOT re-emit the real MAU.
        /// </summary>
        public void NoticeReal(byte[] mau, uint rtpTs) {
            if (_disposed || mau == null || mau.Length == 0) return;
            long nowMs = NowMs();
            lock (_gate) {
                if (_lastMau != null) {
                    // RTP delta — guard against the rare out-of-order
                    // delivery where rtpTs went backwards.
                    uint delta = unchecked(rtpTs - _lastRtpTs);
                    if (delta > 0 && delta < 90000) {
                        _avgRtpDelta = (_avgRtpDelta == 0)
                            ? delta
                            : (_avgRtpDelta * 7 + delta) / 8;
                    }
                    long wallDelta = nowMs - _lastRealMauTickMs;
                    if (wallDelta > 0 && wallDelta < 1000) {
                        _avgWallIntervalMs = (_avgWallIntervalMs == 0)
                            ? wallDelta
                            : (_avgWallIntervalMs * 7 + wallDelta) / 8;
                    }
                }
                _lastMau = mau;
                _lastRtpTs = rtpTs;
                _lastRealMauTickMs = nowMs;
                _realMauCount++;

                if (_injecting) {
                    _log?.LogInfo($"[silence] real audio resumed after " +
                                  $"{_syntheticEmitCount} synthetic frames; stopping injection");
                    _injecting = false;
                    _syntheticEmitCount = 0;
                }
            }
        }

        /// <summary>
        /// Arm or disarm the injector. The host calls this with
        /// <c>true</c> when it asks the server for trick play
        /// (PLAY-with-Scale / rate ≠ 1.0) and <c>false</c> on
        /// return-to-1×. When disarmed, the injector takes no action
        /// regardless of wire-quiet duration — normal buffering and
        /// brief network hiccups are passed through to FFME without
        /// us papering over them with synthetic audio.
        /// </summary>
        public void SetTrickPlayActive(bool active) {
            if (_disposed) return;
            lock (_gate) {
                if (_trickPlayActive == active) return;
                _trickPlayActive = active;
                if (!active && _injecting) {
                    _log?.LogInfo($"[silence] trick play deactivated after " +
                                  $"{_syntheticEmitCount} synthetic frames; stopping injection");
                    _injecting = false;
                    _syntheticEmitCount = 0;
                } else {
                    _log?.LogInfo($"[silence] trick play {(active ? "ACTIVE" : "INACTIVE")} " +
                                  $"(injector {(active ? "armed" : "disarmed")})");
                }
            }
        }

        /// <summary>
        /// Stop the watchdog. Subsequent <see cref="NoticeReal"/>
        /// calls are ignored; no further synthetic MAUs are emitted.
        /// </summary>
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            lock (_gate) {
                _lastMau = null;
            }
        }

        // ---- Timer-driven check + emit ----

        private void Tick(object state) {
            if (_disposed) return;
            byte[] toEmit = null;
            uint emitRtpTs = 0;
            try {
                lock (_gate) {
                    // Gate #1: must be in known trick play. Without
                    // this we'd treat any wire-side buffering pause as
                    // "audio is gone" and start injecting; the user
                    // hears that as a rapid-repeat artefact once the
                    // real wire resumes. The host knows when trick
                    // play is active (it asked for it via SetRate)
                    // and tells us via SetTrickPlayActive.
                    if (!_trickPlayActive) return;
                    if (_lastMau == null) return;
                    if (_realMauCount < MinSamplesForInjection) return;
                    if (_avgRtpDelta == 0 || _avgWallIntervalMs <= 0) return;

                    long nowMs = NowMs();
                    int sinceLastReal = (int)(nowMs - _lastRealMauTickMs);

                    if (!_injecting) {
                        if (sinceLastReal < QuietThresholdMs) return;
                        _injecting = true;
                        _syntheticEmitCount = 0;
                        // Start the synthetic cadence FROM NOW, not
                        // from when real audio last arrived — we don't
                        // want to "catch up" several seconds of missed
                        // frames in one tick, that's what produced the
                        // audible rapid-repeat. Just emit one frame
                        // per cadence period going forward.
                        _nextInjectionWallMs = nowMs + (long)_avgWallIntervalMs;
                        _log?.LogInfo($"[silence] trick-play wire quiet for {sinceLastReal}ms; " +
                                      $"injecting (cadence~{_avgWallIntervalMs:F0}ms, " +
                                      $"rtpDelta={_avgRtpDelta})");
                        return;  // wait one cadence period before first emit
                    }

                    // Emit at most ONE frame per tick when the cadence
                    // period elapses — no batching, no catch-up. If
                    // the timer was starved and several periods went
                    // by, we just skip the missed ones; PTS still
                    // advances by one delta, and FFME's audio buffer
                    // will simply drain by that much. Better than the
                    // alternative (dumping a burst that plays through
                    // the speakers in a fraction of a second).
                    if (_nextInjectionWallMs > nowMs) return;
                    _lastRtpTs = unchecked(_lastRtpTs + _avgRtpDelta);
                    emitRtpTs = _lastRtpTs;
                    toEmit = _lastMau;
                    _nextInjectionWallMs = nowMs + (long)_avgWallIntervalMs;
                    _syntheticEmitCount++;
                }

                // Emit OUTSIDE the lock for safety — downstream may
                // take its own locks (muxer / producer queue).
                if (toEmit != null) {
                    try { _emit(toEmit, emitRtpTs); }
                    catch (Exception ex) {
                        _log?.LogError($"[silence] emit threw: {ex.Message}");
                    }
                }
            } catch (Exception ex) {
                _log?.LogError($"[silence] tick exception: {ex.Message}");
            }
        }

        private static long NowMs() {
            // TickCount64 isn't on .NET 4.6.1; environment ticks wrap
            // every ~49 days but our intervals are sub-second so the
            // arithmetic stays correct.
            return Environment.TickCount;
        }
    }
}
