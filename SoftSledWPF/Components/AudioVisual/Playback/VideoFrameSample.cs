using System;
using System.Collections.Concurrent;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// One decoded video frame, converted to BGRA32 ready for a
    /// <c>WriteableBitmap</c> blit. Produced by <see cref="VideoDecoder"/>,
    /// consumed by <see cref="WpfVideoRenderer"/>.
    ///
    /// BGRA32 chosen to match the <c>PixelFormats.Bgra32</c> format the
    /// existing FreeRDP rendering path uses (zero conversion at blit time).
    ///
    /// Pixel buffer is rented from <see cref="BufferPool"/> rather than
    /// allocated per frame — at 1080p 60 fps a fresh <c>new byte[8 MB]</c>
    /// per frame would burn ~500 MB/s on the LOH and trigger frequent
    /// Gen-2 collections. The renderer is expected to call
    /// <see cref="Release"/> after presentation so the buffer goes back
    /// to the pool.
    /// </summary>
    internal sealed class VideoFrameSample {
        public long PresentationMs;
        public int Width;
        public int Height;
        public int Stride;          // bytes per row, may be > Width*4 for padding
        public byte[] Bgra32;       // exactly Stride * Height bytes; from BufferPool

        /// <summary>Return the pixel buffer to the pool. Safe to call
        /// once per sample; subsequent calls are no-ops.</summary>
        public void Release() {
            byte[] b = System.Threading.Interlocked.Exchange(ref Bgra32, null);
            if (b != null) BufferPool.Return(b);
        }
    }

    /// <summary>
    /// Bounded pool of BGRA32 byte buffers shared between the video decoder
    /// (producer) and the WPF renderer (consumer). Each frame at 1080p is
    /// ~8 MB which lives on the LOH; without pooling we'd churn the LOH
    /// hard at 60 fps. The pool is keyed by buffer length so different
    /// resolutions don't share slots — same-resolution streams reuse the
    /// same handful of buffers indefinitely.
    /// </summary>
    internal static class BufferPool {
        // ConcurrentDictionary keyed by buffer length; each value is a
        // small concurrent queue of free buffers of exactly that size.
        // Cap each per-size bucket at 8 buffers: enough for the decoder
        // queue (32 cap) + the renderer cycle (~16ms tick) without
        // unbounded growth, while still letting transient bursts run
        // without falling back to allocation.
        private const int MaxBuffersPerSize = 8;
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> _pool
            = new ConcurrentDictionary<int, ConcurrentQueue<byte[]>>();

        public static byte[] Rent(int size) {
            if (_pool.TryGetValue(size, out var q) && q.TryDequeue(out var b)) {
                return b;
            }
            return new byte[size];
        }

        public static void Return(byte[] buf) {
            if (buf == null) return;
            var q = _pool.GetOrAdd(buf.Length, _ => new ConcurrentQueue<byte[]>());
            // Best-effort cap — ConcurrentQueue has no SoftLimit so we
            // just check Count. Slight race window where two threads
            // both add past the limit; harmless (a couple extra buffers).
            if (q.Count < MaxBuffersPerSize) q.Enqueue(buf);
        }

        /// <summary>Drop all pooled buffers (e.g. on session teardown).</summary>
        public static void Clear() { _pool.Clear(); }
    }
}
