using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// WPF video renderer for <see cref="VideoDecoder"/> output. Owns the
    /// <c>WriteableBitmap</c> attached to the <c>Image</c> element in
    /// <c>ExtenderSessionControl</c>; consumes <see cref="VideoFrameSample"/>s
    /// from the decoder thread; presents them on the dispatcher in sync
    /// with the <see cref="PlaybackClock"/>.
    ///
    /// Tick driver: a <see cref="Timer"/> firing at ~16 ms (60 Hz target).
    /// Each tick the timer callback (running on a thread-pool thread)
    /// marshals to the UI thread via <see cref="Dispatcher.BeginInvoke"/>
    /// at <see cref="DispatcherPriority.Render"/>, then drains all
    /// queued frames with <c>PresentationMs ≤ now</c>, keeps only the
    /// most recent (frame drop on lag is the right behaviour for live
    /// RTSP — better than building up latency), and blits it into the
    /// bitmap.
    ///
    /// <para>Why a timer instead of <c>CompositionTarget.Rendering</c>?
    /// The compositor tick gets throttled under load (observed ~10 Hz
    /// with combined RDP + splash + video work). FFME-style smoothness
    /// requires a renderer that paces itself at the source frame rate
    /// regardless of when the compositor next runs — the timer hits at
    /// 60 Hz, queues a Render-priority dispatcher item per tick, and
    /// keeps the bitmap fresh; the compositor composes at its own
    /// rate but always sees an up-to-date frame.</para>
    ///
    /// Bitmap allocation: deferred to the first frame so we use the
    /// stream's actual dimensions. On a resolution change (rare; e.g. a
    /// server-side re-PROBE) we reallocate.
    ///
    /// Pattern reused from <c>FreeRdpClient.cs</c>'s framebuffer blit
    /// (Lock / WritePixels / AddDirtyRect / Unlock) — the existing
    /// allocator and dispatcher marshalling there is proven to handle
    /// 30+ fps without tearing.
    /// </summary>
    internal sealed class WpfVideoRenderer : IVideoRenderer {

        private readonly Image _target;
        private readonly Dispatcher _dispatcher;
        private readonly PlaybackClock _clock;
        private readonly Logger _log;
        private readonly ConcurrentQueue<VideoFrameSample> _queue = new ConcurrentQueue<VideoFrameSample>();
        // 60-frame cap = ~1 s of headroom at 60 fps for the rendering
        // tick to catch up after a brief CPU stall, without unbounded
        // memory growth. Pooled buffers mean the per-frame cost is
        // negligible past the first few.
        private const int MaxQueuedFrames = 60;

        // ~16 ms = 60 Hz tick. We coalesce multiple due frames per tick
        // (keep only the newest), so this is effectively an upper bound
        // on how late a frame can be displayed — actual display rate
        // adapts to the source.
        private const int TickPeriodMs = 16;

        private WriteableBitmap _bitmap;
        private int _bitmapW, _bitmapH;
        private long _framesPresented;
        private long _framesDroppedLate;     // dropped because a newer frame was also due
        private long _framesDroppedOverflow; // dropped at enqueue because queue full
        private bool _disposed;

        // Timer + re-entrancy guard. _tickPending ensures we never queue
        // more than one Render-priority dispatcher item at a time —
        // otherwise a slow blit could let multiple items pile up and
        // amplify backpressure.
        private Timer _tickTimer;
        private int _tickPending;
        private readonly Action _doTickAction;

        // FPS counter — runs over a sliding ~1 s window and logs at the
        // boundary. Cheap (one Interlocked + one Stopwatch read per
        // presented frame) and gives a clean steady-state fps number
        // in the log without per-frame log spam.
        private long _fpsWindowStartMs;
        private long _fpsWindowPresented;
        private long _fpsWindowDroppedLate;
        private long _fpsWindowDroppedOverflow;
        private readonly System.Diagnostics.Stopwatch _fpsSw = System.Diagnostics.Stopwatch.StartNew();

        public WpfVideoRenderer(Image target, PlaybackClock clock, Logger log) {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _dispatcher = _target.Dispatcher;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log;

            _doTickAction = DoTick;
            // Timer starts immediately; the first tick simply finds an
            // empty queue and returns. Cheaper than deferring the timer
            // start until the first frame arrives.
            _tickTimer = new Timer(OnTimerTick, null, TickPeriodMs, TickPeriodMs);
            _log?.LogInfo("[wpf-video] renderer started (timer-driven blit, 60 Hz target)");
        }

        /// <summary>Enqueue a decoded frame for presentation. Safe from
        /// any thread.</summary>
        public void EnqueueFrame(VideoFrameSample sample) {
            if (_disposed || sample == null) return;
            if (_queue.Count >= MaxQueuedFrames) {
                // Drop oldest to bound memory. Live RTSP can produce
                // bursts faster than 60 Hz can drain them; a 60-frame
                // ceiling caps memory at ~60 MB for 720p without
                // adding visible latency. Release the dropped frame's
                // pixel buffer back to the pool so it doesn't linger.
                if (_queue.TryDequeue(out var dropped)) {
                    dropped?.Release();
                    Interlocked.Increment(ref _fpsWindowDroppedOverflow);
                    long n = Interlocked.Increment(ref _framesDroppedOverflow);
                    if ((n & 0x1F) == 1) {
                        _log?.LogError($"[wpf-video] queue overflow, dropped oldest " +
                                       $"(total {n})");
                    }
                }
            }
            _queue.Enqueue(sample);
        }

        // -- Timer thread --

        private void OnTimerTick(object state) {
            if (_disposed) return;
            // Coalesce: if the previous Render-priority item hasn't run
            // yet (compositor under load), skip queuing another one. The
            // dispatcher item will drain whatever is queued when it
            // finally runs, so no frames are missed — they just batch.
            if (Interlocked.Exchange(ref _tickPending, 1) != 0) return;
            try {
                _dispatcher.BeginInvoke(_doTickAction, DispatcherPriority.Render);
            } catch {
                // Dispatcher may be shutting down — drop the tick and
                // clear the guard so the next one can try.
                Interlocked.Exchange(ref _tickPending, 0);
            }
        }

        // -- UI thread (dispatcher) --

        private void DoTick() {
            try {
                if (_disposed) return;
                long now = _clock.CurrentMediaTimeMs;

                // Drain all frames whose presentation time has arrived. Keep
                // the last one to actually blit (the others are "late" and
                // would only produce visible judder if we tried to blit
                // them all). Skipped frames have their pixel buffers
                // released back to the pool immediately.
                VideoFrameSample dueFrame = null;
                while (_queue.TryPeek(out var head) && head.PresentationMs <= now) {
                    if (!_queue.TryDequeue(out head)) break;
                    if (dueFrame != null) {
                        dueFrame.Release();
                        Interlocked.Increment(ref _framesDroppedLate);
                        Interlocked.Increment(ref _fpsWindowDroppedLate);
                    }
                    dueFrame = head;
                }
                if (dueFrame == null) return;

                try {
                    Present(dueFrame);
                    Interlocked.Increment(ref _framesPresented);
                    Interlocked.Increment(ref _fpsWindowPresented);
                } finally {
                    dueFrame.Release();
                }

                MaybeLogFps();
            } finally {
                Interlocked.Exchange(ref _tickPending, 0);
            }
        }

        private void Present(VideoFrameSample frame) {
            if (_bitmap == null || _bitmapW != frame.Width || _bitmapH != frame.Height) {
                _bitmap = new WriteableBitmap(
                    frame.Width, frame.Height, 96, 96,
                    PixelFormats.Bgra32, null);
                _bitmapW = frame.Width;
                _bitmapH = frame.Height;
                _target.Source = _bitmap;
                _target.Visibility = Visibility.Visible;
                _log?.LogInfo($"[wpf-video] bitmap allocated: {frame.Width}x{frame.Height}");
            }

            Int32Rect rect = new Int32Rect(0, 0, frame.Width, frame.Height);
            try {
                _bitmap.Lock();
                _bitmap.WritePixels(rect, frame.Bgra32, frame.Stride, 0);
                _bitmap.AddDirtyRect(rect);
            } finally {
                _bitmap.Unlock();
            }
        }

        private void MaybeLogFps() {
            // Log every ~1 second of WALL time (Stopwatch). Reading the
            // clock isn't enough — at non-1× rates media-time advances
            // differently from wall-time, and we want fps in user-
            // perception terms (frames per real second).
            long wallMs = _fpsSw.ElapsedMilliseconds;
            long sinceStart = wallMs - _fpsWindowStartMs;
            if (sinceStart < 1000) return;

            long presented = Interlocked.Exchange(ref _fpsWindowPresented, 0);
            long droppedLate = Interlocked.Exchange(ref _fpsWindowDroppedLate, 0);
            long droppedOverflow = Interlocked.Exchange(ref _fpsWindowDroppedOverflow, 0);
            _fpsWindowStartMs = wallMs;

            double fps = presented * 1000.0 / sinceStart;
            _log?.LogInfo($"[wpf-video] fps={fps:F1} " +
                          $"presented={presented} droppedLate={droppedLate} " +
                          $"droppedOverflow={droppedOverflow} window={sinceStart}ms " +
                          $"queueDepth={_queue.Count}");
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            // Stop the timer first so no new dispatcher items get queued.
            try {
                _tickTimer?.Dispose();
                _tickTimer = null;
            } catch { }
            try {
                _dispatcher.Invoke(() => { _target.Source = null; });
            } catch { }
            // Return any queued frames' buffers to the pool, then clear
            // the pool itself — these per-session buffers are sized to
            // the just-finished stream's resolution and would otherwise
            // hold ~30-60 MB of LOH for the lifetime of the process.
            while (_queue.TryDequeue(out var leftover)) {
                leftover?.Release();
            }
            BufferPool.Clear();
            _log?.LogInfo($"[wpf-video] disposed: presented={_framesPresented} " +
                          $"droppedLate={_framesDroppedLate} droppedOverflow={_framesDroppedOverflow}");
        }
    }
}
