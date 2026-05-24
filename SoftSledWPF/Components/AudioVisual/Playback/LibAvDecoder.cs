using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Common base class for the per-stream libav decoder. Owns one
    /// <c>AVCodecContext</c>, a thread-safe queue of <c>AVPacket</c>s, and
    /// a background thread that pulls packets off the queue, feeds them
    /// to libav, and invokes <see cref="OnFrameDecoded"/> for each
    /// produced frame.
    ///
    /// Why a class rather than a function: keeps every <c>unsafe</c>
    /// FFmpeg.AutoGen API call quarantined here so the rest of the
    /// playback engine stays in safe code.
    ///
    /// Thread model:
    ///  * Construction + <see cref="Start"/> + <see cref="Stop"/> from the
    ///    engine's owner thread.
    ///  * <see cref="SubmitPacket"/> called from the RTSP depacketizer
    ///    event-handler thread (or any thread — packet queue is
    ///    thread-safe).
    ///  * <see cref="OnFrameDecoded"/> invoked on the decoder's worker
    ///    thread; subclasses must NOT touch UI directly — render queue
    ///    handover is the subclass's responsibility.
    /// </summary>
    internal abstract unsafe class LibAvDecoder : IDisposable {

        protected readonly Logger Log;
        private readonly AVCodecID _codecId;
        private readonly byte[] _extradata;          // may be null
        private readonly string _streamLabel;        // "video" / "audio" — diagnostic
        private readonly BlockingCollection<QueuedPacket> _queue
            = new BlockingCollection<QueuedPacket>(boundedCapacity: 256);

        protected AVCodecContext* CodecCtx;
        private Thread _worker;
        private bool _disposed;
        private long _packetsSubmitted;
        private long _framesDecoded;
        private long _framesDropped;        // overrun

        // Sliding-window throughput counters. Exchange'd to 0 at each
        // log emit so the reported numbers are per-second, not
        // cumulative. Tells us decisively whether a ~10fps cap is
        // upstream (packets-in low) or the decoder itself (packets-in
        // high but frames-out low).
        private long _windowPacketsIn;
        private long _windowFramesOut;
        private long _windowDecodeNanos;     // total time spent inside avcodec_send_packet + receive_frame
        private long _windowFrameCallbackNanos; // total time spent inside OnFrameDecoded
        private long _windowStartMs;
        private readonly System.Diagnostics.Stopwatch _statsClock = System.Diagnostics.Stopwatch.StartNew();

        /// <param name="codecId">libav decoder identifier (see CodecRegistry).</param>
        /// <param name="extradata">Codec extradata (SPS/PPS for H264, WAVEFORMATEX for
        /// some audio paths). Pass null for codecs whose configuration is
        /// in-band (MPEG-1/2 video, MP3, AC3).</param>
        /// <param name="streamLabel">Short tag used in diagnostic logs.</param>
        protected LibAvDecoder(AVCodecID codecId, byte[] extradata, string streamLabel, Logger log) {
            _codecId = codecId;
            _extradata = extradata;
            _streamLabel = streamLabel ?? "stream";
            Log = log;
        }

        /// <summary>Open the codec context and spin up the worker thread.
        /// Idempotent — calling twice is harmless.</summary>
        public void Start() {
            if (_worker != null) return;

            AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
            if (codec == null) {
                Log?.LogError($"[libav-{_streamLabel}] avcodec_find_decoder({_codecId}) → null");
                throw new InvalidOperationException($"libav: no decoder for {_codecId}");
            }
            CodecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (CodecCtx == null) {
                throw new InvalidOperationException($"libav: avcodec_alloc_context3 failed for {_codecId}");
            }

            if (_extradata != null && _extradata.Length > 0) {
                // libav requires extradata to be padded with AV_INPUT_BUFFER_PADDING_SIZE
                // zero bytes at the end to satisfy alignment assumptions in some
                // decoders (notably H264). Allocate via av_mallocz so the padding
                // is zero-filled automatically.
                int paddedLen = _extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;
                byte* ed = (byte*)ffmpeg.av_mallocz((ulong)paddedLen);
                if (ed == null) {
                    AVCodecContext* tmp1 = CodecCtx;
                    ffmpeg.avcodec_free_context(&tmp1);
                    CodecCtx = null;
                    throw new InvalidOperationException("libav: extradata av_mallocz failed");
                }
                fixed (byte* src = _extradata) {
                    Buffer.MemoryCopy(src, ed, paddedLen, _extradata.Length);
                }
                CodecCtx->extradata = ed;
                CodecCtx->extradata_size = _extradata.Length;
            }

            // Enable multi-threaded decode — this is the single biggest
            // throughput knob for software video decode and the reason
            // FFME (which sets it by default) produced smooth playback
            // on the same hardware where our direct-libav path was
            // capping at ~10 fps.
            //
            // thread_count = 0 lets libav pick (typically = number of
            // logical cores). thread_type combines FRAME + SLICE:
            //   * FF_THREAD_FRAME (=1): decode frame N+1 in parallel
            //     with frame N. Big win for I/P/B-heavy codecs like
            //     MPEG-2 and H.264; adds ~(N-1) frames of latency,
            //     absorbed by our clock-driven renderer.
            //   * FF_THREAD_SLICE (=2): parallel slice decode within a
            //     frame. Helps when the encoder used multiple slices
            //     per frame (common for H.264, rarer for MPEG-2);
            //     no latency cost. libav's H.264 decoder uses this
            //     transparently when supported.
            // Audio decoders ignore both fields (single-threaded by
            // design), so this is safe to set unconditionally.
            CodecCtx->thread_count = 0;
            CodecCtx->thread_type  = 1 /* FF_THREAD_FRAME */ | 2 /* FF_THREAD_SLICE */;

            // Subclass hook to set codec-specific context fields (sample_rate /
            // channels for raw PCM, etc.) BEFORE avcodec_open2 runs.
            ConfigureContext(CodecCtx);

            int ret = ffmpeg.avcodec_open2(CodecCtx, codec, null);
            if (ret < 0) {
                Log?.LogError($"[libav-{_streamLabel}] avcodec_open2 failed: {AvStrError(ret)}");
                AVCodecContext* tmp = CodecCtx;
                ffmpeg.avcodec_free_context(&tmp);
                CodecCtx = null;
                throw new InvalidOperationException($"libav: avcodec_open2 failed for {_codecId}: {AvStrError(ret)}");
            }

            Log?.LogInfo($"[libav-{_streamLabel}] codec opened: {_codecId} " +
                         $"(extradata={(_extradata?.Length ?? 0)}B, " +
                         $"threads={CodecCtx->thread_count}, " +
                         $"active_thread_type=0x{CodecCtx->active_thread_type:X})");

            _worker = new Thread(WorkerLoop) {
                IsBackground = true,
                Name = $"LibAvDecoder-{_streamLabel}",
            };
            _worker.Start();
        }

        /// <summary>Submit one access unit / frame for decoding. PTS is in
        /// milliseconds (engine-canonical units — RTP-clock conversion
        /// happens at the engine layer). Non-blocking; if the queue is
        /// full the oldest packet is dropped (overrun tracked).</summary>
        public void SubmitPacket(byte[] data, long ptsMs) {
            if (_disposed || data == null || data.Length == 0) return;
            Interlocked.Increment(ref _windowPacketsIn);
            var qp = new QueuedPacket { Data = data, PtsMs = ptsMs };
            if (!_queue.TryAdd(qp)) {
                // Bounded queue full — drop oldest. Most realistic cause
                // is decoder thread starvation; logging at ~32-drop
                // intervals to avoid log spam.
                if (_queue.TryTake(out _)) {
                    long n = Interlocked.Increment(ref _framesDropped);
                    if ((n & 0x1F) == 1) {
                        Log?.LogError($"[libav-{_streamLabel}] queue full, dropped oldest packet " +
                                      $"(total drops {n})");
                    }
                }
                _queue.TryAdd(qp);  // best-effort second attempt
            }
            Interlocked.Increment(ref _packetsSubmitted);
        }

        /// <summary>Signal end-of-stream to the decoder thread. Worker
        /// drains its in-flight packets, sends a flush packet, and exits.</summary>
        public void Complete() {
            try { _queue.CompleteAdding(); } catch { }
        }

        /// <summary>Stop the worker thread and free libav resources. Safe
        /// to call concurrently with Submit; subsequent submits no-op.</summary>
        public virtual void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _worker?.Join(2000); } catch { }
            try { _queue.Dispose(); } catch { }
            if (CodecCtx != null) {
                AVCodecContext* tmp = CodecCtx;
                ffmpeg.avcodec_free_context(&tmp);
                CodecCtx = null;
            }
            Log?.LogInfo($"[libav-{_streamLabel}] disposed: " +
                         $"submitted={_packetsSubmitted} decoded={_framesDecoded} dropped={_framesDropped}");
        }

        // ---------- Subclass hooks ----------

        /// <summary>Set any codec-specific AVCodecContext fields before
        /// avcodec_open2 runs. PCM decoders set sample_rate / channels /
        /// sample_fmt here; container-described codecs typically need
        /// nothing.</summary>
        protected virtual void ConfigureContext(AVCodecContext* ctx) { }

        /// <summary>Called on the decoder thread for each frame libav
        /// produces. <paramref name="ptsMs"/> is the engine-time PTS for
        /// the source packet (libav may emit several frames per packet —
        /// each gets the same PTS for now; refinement via container
        /// time-base is a future optimization).</summary>
        protected abstract void OnFrameDecoded(AVFrame* frame, long ptsMs);

        // ---------- Worker thread ----------

        private void WorkerLoop() {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null) {
                Log?.LogError($"[libav-{_streamLabel}] av_packet_alloc / av_frame_alloc failed");
                if (pkt != null) { AVPacket* t = pkt; ffmpeg.av_packet_free(&t); }
                if (frame != null) { AVFrame* t = frame; ffmpeg.av_frame_free(&t); }
                return;
            }
            try {
                foreach (var qp in _queue.GetConsumingEnumerable()) {
                    long t0 = _statsClock.ElapsedTicks;
                    SendPacket(pkt, qp);
                    DrainFrames(frame, qp.PtsMs);
                    long dtNanos = (_statsClock.ElapsedTicks - t0)
                        * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency;
                    Interlocked.Add(ref _windowDecodeNanos, dtNanos);
                    MaybeEmitStats();
                }
                // Flush: send a null packet to flush the decoder, then drain.
                ffmpeg.avcodec_send_packet(CodecCtx, null);
                DrainFrames(frame, ptsMs: 0);
            } catch (Exception ex) {
                Log?.LogError($"[libav-{_streamLabel}] worker exception: {ex.Message}");
            } finally {
                AVPacket* pTmp = pkt;
                AVFrame* fTmp = frame;
                ffmpeg.av_packet_free(&pTmp);
                ffmpeg.av_frame_free(&fTmp);
            }
        }

        private void SendPacket(AVPacket* pkt, QueuedPacket qp) {
            fixed (byte* src = qp.Data) {
                // av_packet_from_data hands ownership to libav; we'd need
                // a libav-allocated buffer for that. Simpler: set the
                // packet to reference our managed array directly via
                // pkt->data + pkt->size — libav copies what it needs
                // inside avcodec_send_packet's internal reference machinery.
                pkt->data = src;
                pkt->size = qp.Data.Length;
                pkt->pts = qp.PtsMs;     // engine units (ms); we read back at decode
                pkt->dts = qp.PtsMs;
                pkt->flags = 0;
                int ret = ffmpeg.avcodec_send_packet(CodecCtx, pkt);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                    // Many cases here are transient (e.g. first packet of a stream
                    // before a sync point — decoder rejects with INVALIDDATA, then
                    // recovers once it sees a keyframe). Log at debug.
                    Log?.LogDebug($"[libav-{_streamLabel}] send_packet ret={AvStrError(ret)}");
                }
                pkt->data = null;
                pkt->size = 0;
            }
        }

        private void DrainFrames(AVFrame* frame, long ptsMs) {
            while (true) {
                int ret = ffmpeg.avcodec_receive_frame(CodecCtx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) return;
                if (ret < 0) {
                    Log?.LogDebug($"[libav-{_streamLabel}] receive_frame ret={AvStrError(ret)}");
                    return;
                }
                // Some decoders emit frames with their own pts (set from the
                // packet we just sent). Fall back to the packet's ptsMs if
                // libav loses track. AV_NOPTS_VALUE is long.MinValue.
                long emitPts = ptsMs;
                if (frame->pts != ffmpeg.AV_NOPTS_VALUE) emitPts = frame->pts;
                try {
                    long cb0 = _statsClock.ElapsedTicks;
                    OnFrameDecoded(frame, emitPts);
                    long cbNanos = (_statsClock.ElapsedTicks - cb0)
                        * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency;
                    Interlocked.Add(ref _windowFrameCallbackNanos, cbNanos);
                    Interlocked.Increment(ref _framesDecoded);
                    Interlocked.Increment(ref _windowFramesOut);
                } catch (Exception ex) {
                    Log?.LogError($"[libav-{_streamLabel}] OnFrameDecoded threw: {ex.Message}");
                } finally {
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }

        /// <summary>
        /// Emit a one-line throughput summary once per ~1 s of wall
        /// time. Tells us, in order: how many packets are arriving
        /// (upstream pacing), how many frames come out (decoder
        /// pacing), how long each phase is taking on average. If
        /// pktsIn is low (e.g. ~10/s) the bottleneck is upstream
        /// (depacketizer / RTP); if pktsIn is high but framesOut is
        /// low and decodeAvgMs is high, the decoder itself is slow;
        /// if both are high but the renderer's fps is still low,
        /// look at the callback (sws_scale / blit) cost.
        /// </summary>
        private void MaybeEmitStats() {
            long nowMs = _statsClock.ElapsedMilliseconds;
            long start = Interlocked.Read(ref _windowStartMs);
            long elapsed = nowMs - start;
            if (elapsed < 1000) return;
            // Compare-exchange so concurrent callers don't double-emit.
            if (Interlocked.CompareExchange(ref _windowStartMs, nowMs, start) != start) return;

            long pkts = Interlocked.Exchange(ref _windowPacketsIn, 0);
            long frames = Interlocked.Exchange(ref _windowFramesOut, 0);
            long decNs = Interlocked.Exchange(ref _windowDecodeNanos, 0);
            long cbNs  = Interlocked.Exchange(ref _windowFrameCallbackNanos, 0);
            double decAvgMs = pkts   > 0 ? (decNs / 1_000_000.0) / pkts   : 0;
            double cbAvgMs  = frames > 0 ? (cbNs  / 1_000_000.0) / frames : 0;
            double pktsPerSec   = pkts   * 1000.0 / elapsed;
            double framesPerSec = frames * 1000.0 / elapsed;
            Log?.LogInfo($"[libav-{_streamLabel}] pktsIn={pktsPerSec:F1}/s " +
                         $"framesOut={framesPerSec:F1}/s " +
                         $"decodeAvg={decAvgMs:F2}ms/pkt " +
                         $"callbackAvg={cbAvgMs:F2}ms/frame " +
                         $"queueDepth={_queue.Count} window={elapsed}ms");
        }

        // ---------- Utilities ----------

        protected static string AvStrError(int ret) {
            const int BufSize = 256;
            byte* buf = stackalloc byte[BufSize];
            ffmpeg.av_strerror(ret, buf, (ulong)BufSize);
            return new string((sbyte*)buf);
        }

        private struct QueuedPacket {
            public byte[] Data;
            public long PtsMs;
        }
    }
}
