using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Codec-agnostic byte-chunk producer feeding an FFME
    /// <c>IMediaInputStream</c> consumer. Originally built for ASF-mode
    /// streams (header object + data packets), so the historical name
    /// "Asf" is preserved to minimise churn — but the producer itself
    /// makes no assumptions about content. It is used for:
    /// <list type="bullet">
    ///   <item>ASF mode: <see cref="SubmitAsfHeader"/> then
    ///     <see cref="SubmitMau"/> per ASF Data Packet.</item>
    ///   <item>MPEG-ES mode (recorded TV, <c>vnd.ms.wm-MPV</c>):
    ///     <see cref="SubmitChunk"/> per MPEG video access unit.</item>
    ///   <item>Any future raw-elementary-stream payload (H.264 Annex-B,
    ///     AC-3, etc): <see cref="SubmitChunk"/>.</item>
    /// </list>
    ///
    /// Threading: producer-side calls come from the WMRTP listener
    /// thread (one per session); <see cref="Read"/> is called from
    /// FFME's demux thread. A bounded <see cref="BlockingCollection{T}"/>
    /// bridges the two; no extra locking required.
    /// </summary>
    internal sealed class AsfStreamProducer : IDisposable {

        // Default backpressure cap. ASF Data Packets in WMC streams are
        // typically 5760 / 8000 / 9216 bytes (the server's "Maximum Data
        // Packet Size" property in the ASF File Properties Object), so
        // 256 packets covers ~1.5–2 MB of buffered audio/video — plenty
        // of slack for a brief decoder stall while still bounded.
        private const int DefaultMaxQueuedChunks = 256;

        private readonly Logger _log;
        private readonly int _maxQueuedChunks;
        private readonly BlockingCollection<byte[]> _queue;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // The chunk currently being copied out via Read(). Holds remaining
        // bytes when an FFmpeg probe-read is smaller than the chunk size.
        private byte[] _currentChunk;
        private int _currentOffset;

        private bool _headerSubmitted;
        private bool _completed;
        private long _bytesProduced;
        private long _bytesConsumed;
        private long _droppedChunks;

        public AsfStreamProducer(Logger log)
            : this(log, DefaultMaxQueuedChunks) { }

        public AsfStreamProducer(Logger log, int maxQueuedChunks) {
            _log = log;
            _maxQueuedChunks = maxQueuedChunks;
            // Bounded BlockingCollection — TryAdd returns false rather
            // than blocking the producer thread when full, letting our
            // backpressure logic decide whether to drop or wait briefly.
            _queue = new BlockingCollection<byte[]>(
                new ConcurrentQueue<byte[]>(), maxQueuedChunks);
        }

        // ----------- Producer side (RTSP / depacketizer thread) ---------

        /// <summary>
        /// Enqueue the ASF Header Object. Must be called once, before any
        /// <see cref="SubmitMau"/>. The header bytes form the prefix of
        /// the ASF stream FFmpeg will consume.
        /// </summary>
        public void SubmitAsfHeader(byte[] asfHeaderObjectBytes) {
            if (asfHeaderObjectBytes == null || asfHeaderObjectBytes.Length == 0) {
                _log?.LogError("[asfprod] SubmitAsfHeader called with empty header — refusing");
                return;
            }
            if (_headerSubmitted) {
                _log?.LogError($"[asfprod] SubmitAsfHeader called twice — ignoring " +
                               $"(prior {asfHeaderObjectBytes.Length}B header already in queue)");
                return;
            }
            if (!_queue.TryAdd(asfHeaderObjectBytes)) {
                _log?.LogError("[asfprod] failed to enqueue ASF header — queue full at startup?");
                return;
            }
            _headerSubmitted = true;
            Interlocked.Add(ref _bytesProduced, asfHeaderObjectBytes.Length);
            _log?.LogInfo($"[asfprod] ASF header submitted: {asfHeaderObjectBytes.Length}B " +
                          $"(starts {DescribeFirstBytes(asfHeaderObjectBytes)})");
        }

        /// <summary>
        /// Enqueue one ASF Data Packet (the contents of one WMRTP MAU).
        /// </summary>
        /// <param name="asfDataPacket">Raw bytes of the ASF Data Packet.</param>
        /// <param name="streamId">ASF stream ID (from rtpmap mapping). Used
        /// for diagnostics only — the packet is self-identifying.</param>
        /// <param name="isSyncPoint">True if this MAU was flagged as a
        /// sync point (S bit set on a F=1 or F=3 fragment). Currently used
        /// only for backpressure-eviction policy; the ASF demuxer doesn't
        /// need it externally signalled.</param>
        public void SubmitMau(byte[] asfDataPacket, uint streamId, bool isSyncPoint) {
            if (asfDataPacket == null || asfDataPacket.Length == 0) return;
            if (_completed) return;

            if (!_headerSubmitted) {
                // Data before header: refuse rather than feed FFmpeg an
                // ASF stream that starts mid-packet. This is a real bug
                // case, not a transient — log loudly so it's noticed.
                _log?.LogError($"[asfprod] SubmitMau before SubmitAsfHeader — dropping " +
                               $"{asfDataPacket.Length}B packet (stream {streamId})");
                return;
            }

            if (!_queue.TryAdd(asfDataPacket)) {
                // Queue full → consumer (FFME) is stalled. Drop the
                // oldest packet to make room. This is preferable to
                // blocking the depacketizer thread (which would back
                // up the whole RTSP receive loop). Sync-point packets
                // are kept preferentially; non-sync packets feed off
                // the front first.
                ApplyBackpressureAndRetry(asfDataPacket);
                return;
            }
            Interlocked.Add(ref _bytesProduced, asfDataPacket.Length);
        }

        /// <summary>
        /// Enqueue an arbitrary byte chunk for header-less elementary
        /// streams (e.g. <c>vnd.ms.wm-MPV</c> MAUs, each one a complete
        /// MPEG video access unit). Unlike <see cref="SubmitMau"/>, this
        /// does NOT require <see cref="SubmitAsfHeader"/> to have been
        /// called first — the stream is self-describing via in-band
        /// start codes (FFmpeg's <c>mpegvideo</c> demuxer probes them
        /// directly).
        /// </summary>
        public void SubmitChunk(byte[] chunk) {
            if (chunk == null || chunk.Length == 0) return;
            if (_completed) return;

            if (!_queue.TryAdd(chunk)) {
                ApplyBackpressureAndRetry(chunk);
                return;
            }
            Interlocked.Add(ref _bytesProduced, chunk.Length);
        }

        private void ApplyBackpressureAndRetry(byte[] newPacket) {
            // BlockingCollection doesn't expose pop-from-front so we
            // dequeue one item via TryTake (which is FIFO under the
            // ConcurrentQueue we passed in) and discard it.
            byte[] discarded;
            if (_queue.TryTake(out discarded)) {
                Interlocked.Increment(ref _droppedChunks);
                if ((_droppedChunks & 0x1F) == 1) {  // log once per 32 drops
                    _log?.LogError($"[asfprod] backpressure: queue full ({_maxQueuedChunks} " +
                                   $"chunks), dropping oldest {discarded.Length}B " +
                                   $"(total drops {_droppedChunks})");
                }
            }
            // Try again. If it still fails the consumer is hopelessly
            // stalled — drop the new packet too rather than spin.
            if (!_queue.TryAdd(newPacket)) {
                Interlocked.Increment(ref _droppedChunks);
                _log?.LogError($"[asfprod] dropped {newPacket.Length}B packet — queue " +
                               $"refused even after eviction (consumer stuck?)");
            } else {
                Interlocked.Add(ref _bytesProduced, newPacket.Length);
            }
        }

        /// <summary>
        /// Mark the stream as ended. <see cref="Read"/> will drain the
        /// remaining queue then return 0 (EOF).
        /// </summary>
        public void Complete() {
            if (_completed) return;
            _completed = true;
            try { _queue.CompleteAdding(); } catch { }
            _log?.LogInfo($"[asfprod] complete: produced={_bytesProduced}B " +
                          $"consumed={_bytesConsumed}B drops={_droppedChunks}");
        }

        // ----------- Consumer side (FFME demux thread) ------------------

        /// <summary>
        /// Read up to <paramref name="count"/> bytes into <paramref name="buffer"/>
        /// starting at <paramref name="offset"/>. Blocks until at least
        /// one byte is available or the producer is completed and the
        /// queue drained (returns 0 = EOF).
        /// </summary>
        public int Read(byte[] buffer, int offset, int count) {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();
            if (count == 0) return 0;

            int totalCopied = 0;

            // Drain residue from a previously-dequeued chunk first.
            if (_currentChunk != null) {
                totalCopied = CopyFromCurrent(buffer, offset, count);
                if (totalCopied >= count) {
                    Interlocked.Add(ref _bytesConsumed, totalCopied);
                    return totalCopied;
                }
            }

            // Keep pulling chunks until count is filled OR the queue runs
            // dry. The very first iteration blocks (we have to deliver
            // SOMETHING — libav treats an early 0 return as EOF); after
            // that, switch to non-blocking so we don't hold the demux
            // thread waiting for bytes that haven't arrived yet.
            //
            // This matters during init: bigger Read returns mean libav's
            // AVIO buffer fills in fewer round-trips, which is what makes
            // the MP3 init seek-back work — see the InternalReadBufferLength
            // comment in AsfFfmeInputStream for the full story.
            while (totalCopied < count) {
                byte[] next;
                bool gotChunk;
                try {
                    if (totalCopied == 0) {
                        gotChunk = _queue.TryTake(out next, Timeout.Infinite, _cts.Token);
                    } else {
                        gotChunk = _queue.TryTake(out next);
                    }
                } catch (OperationCanceledException) {
                    break;  // disposed during read
                } catch (InvalidOperationException) {
                    break;  // adding-completed + empty
                }
                if (!gotChunk || next == null) break;

                _currentChunk = next;
                _currentOffset = 0;
                int copied = CopyFromCurrent(buffer, offset + totalCopied, count - totalCopied);
                totalCopied += copied;
            }

            Interlocked.Add(ref _bytesConsumed, totalCopied);
            return totalCopied;
        }

        private int CopyFromCurrent(byte[] buffer, int offset, int count) {
            int remaining = _currentChunk.Length - _currentOffset;
            int toCopy = remaining < count ? remaining : count;
            Buffer.BlockCopy(_currentChunk, _currentOffset, buffer, offset, toCopy);
            _currentOffset += toCopy;
            if (_currentOffset >= _currentChunk.Length) {
                _currentChunk = null;
                _currentOffset = 0;
            }
            return toCopy;
        }

        // ----------- Diagnostics -----------------------------------------

        public long BytesProduced => Interlocked.Read(ref _bytesProduced);
        public long BytesConsumed => Interlocked.Read(ref _bytesConsumed);
        public long DroppedChunks => Interlocked.Read(ref _droppedChunks);
        public int  QueueDepth    => _queue.Count;

        private static string DescribeFirstBytes(byte[] data) {
            // The ASF Header Object's leading GUID is
            //   0x75B22630-668E-11CF-A6D9-00AA0062CE6C
            // serialised as 30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C
            // (little-endian fields per ASF spec §3). Spotting it confirms
            // the header bytes are the right thing.
            if (data.Length < 16) return $"len<16, first={Hex(data, 0, data.Length)}";
            const string AsfHeaderGuid = "30-26-B2-75-8E-66-CF-11-A6-D9-00-AA-00-62-CE-6C";
            string actual = Hex(data, 0, 16);
            return actual.Equals(AsfHeaderGuid, StringComparison.OrdinalIgnoreCase)
                ? $"GUID match (ASF Header Object)"
                : $"GUID={actual} (expected {AsfHeaderGuid})";
        }

        private static string Hex(byte[] data, int off, int len) {
            var sb = new System.Text.StringBuilder(len * 3);
            for (int i = 0; i < len; i++) {
                if (i > 0) sb.Append('-');
                sb.Append(data[off + i].ToString("X2"));
            }
            return sb.ToString();
        }

        // ----------- Lifecycle -------------------------------------------

        public void Dispose() {
            try { _cts.Cancel(); } catch { }
            try { _queue.CompleteAdding(); } catch { }
            try { _queue.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
