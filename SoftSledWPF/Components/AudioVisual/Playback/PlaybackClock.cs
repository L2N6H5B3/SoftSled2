using System.Diagnostics;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Single source of truth for "what is media-time right now" inside the
    /// playback engine. The video / audio renderers consult this clock to
    /// decide which queued frame or sample to emit next; AvCtrl's
    /// <c>GetPosition</c> handler reads it for position reporting; rate
    /// changes and pause toggles update it.
    ///
    /// Why a custom clock instead of System.Diagnostics.Stopwatch directly:
    /// trick play needs a wall→media slope multiplier (a 2× clock advances
    /// 2 ms of media per 1 ms wall), pause needs to freeze the accumulator
    /// without losing the position, and seek needs an atomic jump to a new
    /// media-time. Stopwatch alone doesn't support any of those cleanly.
    ///
    /// Thread-safe via a single lock — accessed at ~60 Hz from the WPF
    /// CompositionTarget.Rendering tick, ~50 Hz from the audio tick, plus
    /// occasional reads from AvCtrl GetPosition. Contention is negligible.
    /// </summary>
    public sealed class PlaybackClock {

        // The accumulator. _mediaTimeAtAnchorMs is the media-time the clock
        // was at when (_wall) was last started/restarted; _wall measures
        // elapsed real time since that anchor. Currentmedia = anchor + wall*rate
        // (when running) or anchor (when paused).
        private readonly Stopwatch _wall = new Stopwatch();
        private long _mediaTimeAtAnchorMs;
        private double _rate = 1.0;
        private bool _running;
        private readonly object _gate = new object();

        /// <summary>Begin tracking time, starting at the given media position
        /// (typically 0 for a fresh session, or a non-zero value if WMC
        /// asked for a seek-at-Start). Idempotent: re-Start with a new
        /// position acts as a seek.</summary>
        public void Start(long startMediaMs) {
            lock (_gate) {
                _mediaTimeAtAnchorMs = startMediaMs;
                _wall.Restart();
                _running = true;
            }
        }

        /// <summary>Freeze the clock at its current media-time. Subsequent
        /// reads return the frozen value until Resume.</summary>
        public void Pause() {
            lock (_gate) {
                if (!_running) return;
                // Capture the wall-elapsed component into the anchor before
                // stopping the stopwatch, so Resume continues from the right
                // place without losing the time that elapsed before Pause.
                _mediaTimeAtAnchorMs += (long)(_wall.Elapsed.TotalMilliseconds * _rate);
                _wall.Reset();
                _running = false;
            }
        }

        /// <summary>Un-freeze the clock. No-op if already running.</summary>
        public void Resume() {
            lock (_gate) {
                if (_running) return;
                _wall.Restart();
                _running = true;
            }
        }

        /// <summary>Change the wall→media slope. 1.0 = real time, 2.0 = 2×
        /// fast-forward, 0.5 = half-speed. The current media-time is
        /// preserved across the rate change (only future advancement uses
        /// the new rate). Negative rates are accepted for reverse playback,
        /// though the engine currently only honors them on the server-side
        /// path.</summary>
        public void SetRate(double rate) {
            lock (_gate) {
                // Capture the wall-elapsed component into the anchor under
                // the old rate first, then switch slope. This way the
                // current media-time is exactly preserved across the change.
                if (_running) {
                    _mediaTimeAtAnchorMs += (long)(_wall.Elapsed.TotalMilliseconds * _rate);
                    _wall.Restart();
                }
                _rate = rate;
            }
        }

        /// <summary>Atomically jump to a new media-time. Wall continues from
        /// the moment of the seek if currently running.</summary>
        public void Seek(long mediaMs) {
            lock (_gate) {
                _mediaTimeAtAnchorMs = mediaMs;
                if (_running) _wall.Restart();
            }
        }

        /// <summary>Current media-time in milliseconds. Reads cleanly from
        /// any thread.</summary>
        public long CurrentMediaTimeMs {
            get {
                lock (_gate) {
                    if (!_running) return _mediaTimeAtAnchorMs;
                    return _mediaTimeAtAnchorMs +
                           (long)(_wall.Elapsed.TotalMilliseconds * _rate);
                }
            }
        }

        /// <summary>Current rate. Useful for renderers that want to know
        /// whether they're in fast-forward (skip frames) or normal speed.</summary>
        public double Rate { get { lock (_gate) return _rate; } }

        /// <summary>True if the clock is running (not paused).</summary>
        public bool IsRunning { get { lock (_gate) return _running; } }
    }
}
