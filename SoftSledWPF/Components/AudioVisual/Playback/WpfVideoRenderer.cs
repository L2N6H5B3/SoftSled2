using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// WPF video renderer for <see cref="VideoDecoder"/> output. Owns the
    /// <c>WriteableBitmap</c> attached to the <c>Image</c> element in
    /// <c>ExtenderSessionControl</c>; consumes <see cref="VideoFrameSample"/>s
    /// from the decoder thread; presents them on the dispatcher in sync
    /// with the <see cref="PlaybackClock"/>.
    ///
    /// Tick driver: <c>CompositionTarget.Rendering</c> fires on every WPF
    /// composition pass (~60 Hz with vsync). Each tick we read the
    /// clock, drain all queued frames with <c>PresentationMs ≤ now</c>,
    /// keep only the most recent one (frame drop on lag is the right
    /// behaviour for live RTSP — better than building up latency), and
    /// blit it into the bitmap.
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
    internal sealed class WpfVideoRenderer : IDisposable {

        private readonly Image _target;
        private readonly PlaybackClock _clock;
        private readonly Logger _log;
        private readonly ConcurrentQueue<VideoFrameSample> _queue = new ConcurrentQueue<VideoFrameSample>();
        private const int MaxQueuedFrames = 32;

        private WriteableBitmap _bitmap;
        private int _bitmapW, _bitmapH;
        private long _framesPresented;
        private long _framesDroppedLate;     // dropped because a newer frame was also due
        private long _framesDroppedOverflow; // dropped at enqueue because queue full
        private bool _disposed;
        private EventHandler _renderingHandler;

        public WpfVideoRenderer(Image target, PlaybackClock clock, Logger log) {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log;

            _renderingHandler = OnRenderingTick;
            // Hook on the UI thread.
            _target.Dispatcher.BeginInvoke(new Action(() => {
                if (_disposed) return;
                CompositionTarget.Rendering += _renderingHandler;
            }));
        }

        /// <summary>Enqueue a decoded frame for presentation. Safe from
        /// any thread.</summary>
        public void EnqueueFrame(VideoFrameSample sample) {
            if (_disposed || sample == null) return;
            if (_queue.Count >= MaxQueuedFrames) {
                // Drop oldest to bound memory. Live RTSP can produce
                // bursts faster than 60 Hz can drain them; a 32-frame
                // ceiling caps memory at ~30 MB for 720p without
                // adding visible latency.
                if (_queue.TryDequeue(out _)) {
                    long n = Interlocked.Increment(ref _framesDroppedOverflow);
                    if ((n & 0x1F) == 1) {
                        _log?.LogError($"[wpf-video] queue overflow, dropped oldest " +
                                       $"(total {n})");
                    }
                }
            }
            _queue.Enqueue(sample);
        }

        // -- UI thread (CompositionTarget.Rendering) --

        private void OnRenderingTick(object sender, EventArgs e) {
            if (_disposed) return;
            long now = _clock.CurrentMediaTimeMs;

            // Drain all frames whose presentation time has arrived. Keep
            // the last one to actually blit (the others are "late" and
            // would only produce visible jutter if we tried to blit
            // them all).
            VideoFrameSample dueFrame = null;
            while (_queue.TryPeek(out var head) && head.PresentationMs <= now) {
                if (!_queue.TryDequeue(out head)) break;
                if (dueFrame != null) Interlocked.Increment(ref _framesDroppedLate);
                dueFrame = head;
            }
            if (dueFrame == null) return;

            Present(dueFrame);
            Interlocked.Increment(ref _framesPresented);
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

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try {
                _target.Dispatcher.Invoke(() => {
                    if (_renderingHandler != null) {
                        CompositionTarget.Rendering -= _renderingHandler;
                        _renderingHandler = null;
                    }
                    _target.Source = null;
                });
            } catch { }
            while (_queue.TryDequeue(out _)) { }
            _log?.LogInfo($"[wpf-video] disposed: presented={_framesPresented} " +
                          $"droppedLate={_framesDroppedLate} droppedOverflow={_framesDroppedOverflow}");
        }
    }
}
