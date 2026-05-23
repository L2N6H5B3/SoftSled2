using NAudio.Wave;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// NAudio-backed audio renderer for <see cref="AudioDecoder"/> output.
    /// Owns one <c>WaveOutEvent</c> + <c>BufferedWaveProvider</c> for the
    /// life of the session.
    ///
    /// Pattern reused from <see cref="WmrptPcmNAudioSink"/> — the same
    /// <c>WaveOutEvent</c> + <c>BufferedWaveProvider</c> + 200 ms latency
    /// configuration that already serves the WMRPT raw-PCM and WMC
    /// fastpath paths reliably.
    ///
    /// Sample handover: a background tick (~20 Hz) consumes queued
    /// samples whose <c>PresentationMs</c> has arrived per the
    /// <see cref="PlaybackClock"/>, and writes them into NAudio's
    /// buffered provider. NAudio's own thread paces the playback at the
    /// device's clock; the buffer absorbs ~10 s of input.
    ///
    /// Trick-play behaviour: when the server is delivering at non-1×
    /// (per DLNA convention, no audio at FF/RW), the queue stays empty
    /// and NAudio's buffer drains. NO blocking — the video renderer
    /// (which is on its own clock) keeps advancing. When the server
    /// resumes 1× audio after the user presses Play, the queue starts
    /// filling again and audio resumes within ~1 buffer-fill (~200 ms).
    /// </summary>
    internal sealed class NAudioRenderer : IDisposable {

        private readonly PlaybackClock _clock;
        private readonly Logger _log;
        private readonly ConcurrentQueue<AudioFrameSample> _queue = new ConcurrentQueue<AudioFrameSample>();
        private const int MaxQueuedSamples = 256;

        private WaveFormat _format;
        private BufferedWaveProvider _buffer;
        private IWavePlayer _output;
        private Timer _tick;
        private bool _disposed;
        private long _samplesPresented;
        private long _samplesDroppedOverflow;

        public NAudioRenderer(PlaybackClock clock, int sampleRate, int channels, Logger log) {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log;
            _format = new WaveFormat(sampleRate, /*bits*/ 16, channels);
            _buffer = new BufferedWaveProvider(_format) {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
            };
            _output = new WaveOutEvent { DesiredLatency = 200 };
            _output.Init(_buffer);
            _output.Play();
            // ~50 Hz tick. Audio underrun starts to matter at ~200 ms; tick
            // every 20 ms gives 10× headroom for the buffer-fill loop to
            // catch up if it briefly stalls.
            _tick = new Timer(_ => TryDrainQueue(), null,
                              dueTime: 20, period: 20);
            _log?.LogInfo($"[naudio-renderer] armed: {sampleRate} Hz, {channels} ch, 16-bit");
        }

        /// <summary>Enqueue a decoded audio sample. Safe from any thread.</summary>
        public void EnqueueSample(AudioFrameSample sample) {
            if (_disposed || sample == null || sample.PcmS16Le == null || sample.PcmS16Le.Length == 0) return;
            if (_queue.Count >= MaxQueuedSamples) {
                if (_queue.TryDequeue(out _)) {
                    long n = Interlocked.Increment(ref _samplesDroppedOverflow);
                    if ((n & 0x3F) == 1) {
                        _log?.LogError($"[naudio-renderer] queue overflow, dropped oldest (total {n})");
                    }
                }
            }
            _queue.Enqueue(sample);
        }

        private void TryDrainQueue() {
            if (_disposed) return;
            long now = _clock.CurrentMediaTimeMs;
            // Drain everything whose presentation time has arrived. Unlike
            // video we don't drop late samples — NAudio's playback paces
            // itself off the device clock, so even if we hand it slightly
            // late audio it just keeps the buffer level steady (or fills
            // a brief underrun gap).
            while (_queue.TryPeek(out var head) && head.PresentationMs <= now) {
                if (!_queue.TryDequeue(out head)) break;
                try {
                    _buffer.AddSamples(head.PcmS16Le, 0, head.PcmS16Le.Length);
                    Interlocked.Increment(ref _samplesPresented);
                } catch (Exception ex) {
                    _log?.LogError($"[naudio-renderer] AddSamples failed: {ex.Message}");
                }
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _tick?.Dispose(); } catch { }
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            while (_queue.TryDequeue(out _)) { }
            _log?.LogInfo($"[naudio-renderer] disposed: presented={_samplesPresented} " +
                          $"droppedOverflow={_samplesDroppedOverflow}");
        }
    }
}
