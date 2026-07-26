using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;

namespace SoftSled.Components.AudioVisual.Utilities {

    /// <summary>
    /// Push-fed libav video decoder: decodes raw elementary-stream packets
    /// (H.264 Annex-B access units or MPEG-1/2 video access units) submitted
    /// via <see cref="SubmitPacket"/>, emits each frame as packed <c>yuv420p</c>
    /// (1.5 B/px; colour conversion to BGRA is deferred to present time so the
    /// pacer buffers cheaply), and raises <see cref="OnFrame"/>.
    ///
    /// <para>This is the decoder the live playback path uses: it is fed the
    /// video MAUs arriving from RTSP and drives the GPU present path
    /// (<see cref="PtsFramePacer"/> → <see cref="D3DImagePresenter"/>).</para>
    ///
    /// <para>Mirrors <c>LibAvAudioDecoder</c>: a bounded queue + dedicated
    /// worker thread; <see cref="SubmitPacket"/> is safe from any thread and
    /// drops the oldest packet under backpressure rather than blocking the
    /// depacketizer.</para>
    /// </summary>
    internal sealed unsafe class LibAvVideoPushDecoder : IDisposable {

        private readonly AVCodecID _codecId;
        private readonly int _clockHz;
        private readonly Logger _log;
        // Deep input queue: with decode backpressure (see WorkerLoop) coded MAUs
        // accumulate here while we decode at the pacer's drain rate. H.264 arrives
        // in large bursts (100+ fps vs the 25 fps the pacer releases); decoding
        // the whole burst into BGRA overflows the pacer's frame buffer, which then
        // drops FUTURE frames → a content gap → the video freezes. Holding the
        // compressed MAUs here instead (each a small H.264 AU) lets the pacer
        // buffer stay bounded. Flush() empties it on seek/trick-play.
        private readonly BlockingCollection<QueuedPacket> _queue
            = new BlockingCollection<QueuedPacket>(boundedCapacity: 2048);
        private long _frameCounter; // fallback PTS source when the wire carries none

        // Decode backpressure. Returns true while the pacer already holds enough
        // decoded video (>= its BackpressureTargetMs, which tracks the pacer's
        // offset-dependent drop cap). The worker then holds coded MAUs in the
        // (deep) input queue instead of decoding — so we decode at the pacer's
        // (audio-slaved) drain rate, not the server's bursty delivery. Using the
        // pacer's dynamic target (not a fixed ms) is essential: a fixed high-water
        // above maxBuffer never engages for small offsets, so the burst overflows
        // and video freezes. Null → no backpressure.
        public Func<bool> ShouldBackpressure { get; set; }

        private AVCodecContext* _ctx;
        private SwsContext* _sws;
        private Thread _worker;
        private bool _disposed;

        // yadif deinterlace filter graph (buffer → yadif → buffersink). Built
        // lazily on the first frame, ONLY for MPEG-2 (WMC broadcast recordings
        // are 576i and comb heavily on motion). H.264 is left untouched
        // (progressive). If avfilter/yadif can't init, _deintDisabled falls back
        // to raw passthrough — deinterlace is best-effort, never fatal.
        private AVFilterGraph* _graph;
        private AVFilterContext* _srcCtx;
        private AVFilterContext* _sinkCtx;
        private AVFrame* _filtFrame;
        private bool _deintActive;
        private bool _deintDisabled;
        private int _deintW, _deintH, _deintFmt;

        // Pause gate. Set = running; reset = paused. The worker blocks on this
        // before decoding each packet so a Pause() holds decoding (retaining
        // the input queue + the pacer's already-decoded frames) without a
        // teardown. Starts running; Dispose sets it so a paused worker exits.
        private readonly ManualResetEventSlim _runGate = new ManualResetEventSlim(true);

        // Decoded frames are emitted as PACKED yuv420p (Y plane w*h, then U and
        // V planes cw*ch, tight strides) — 1.5 bytes/pixel — NOT BGRA. The heavy
        // YUV→BGRA colour conversion is deferred to present time (Yuv420ToBgra)
        // so the pacer's jitter buffer holds frames at 1.5 B/px instead of 4 B/px
        // (a 2.67× cut on the dominant playback buffer). _sws is built lazily and
        // ONLY for non-yuv420p inputs (e.g. full-range yuvj420p), to normalise
        // them to yuv420p before packing; native yuv420p takes a plain plane-copy
        // fast path with no colour conversion at all.
        private byte[] _packed;
        private int _width, _height;
        private AVPixelFormat _srcFmt = AVPixelFormat.AV_PIX_FMT_NONE;

        private long _framesDecoded;
        public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

        // Reorder-latency diagnostic (H.264 A/V residual hunt). Packets arrive in
        // DECODE order carrying PRESENTATION rtpTs; the decoder emits in DISPLAY
        // order only after buffering the B-frame reorder depth. Logged ONCE per
        // stream: has_b_frames, how many packets were swallowed before the first
        // frame emerged, the first decode-order timestamps, and the first emitted
        // display PTS — to test whether the per-file offset residual tracks the
        // decoder's reorder structure (audio is PCM = zero decode latency, so any
        // A/V asymmetry is entirely video-side). Same worker thread as decode, so
        // no locking needed.
        private bool _reorderDiagDone;
        private int _pktsSent;
        private readonly System.Collections.Generic.List<long> _firstDecodeOrderRtp = new System.Collections.Generic.List<long>();

        /// <summary>Approx ms of UNDECODED video held in the input queue (coded
        /// MAUs waiting behind decode backpressure). The video BFR adds this to
        /// the pacer's decoded-frame span so the server sees our TRUE total video
        /// buffering — otherwise the backpressure backlog is invisible, the server
        /// thinks we're empty and floods a burst, the backlog balloons, then it
        /// over-corrects and stalls (delivery oscillates → freeze + desync).
        ///
        /// <para>Scaled by the stream's REAL frame duration, not a fixed 40 ms.
        /// The old 25 fps assumption DOUBLED the reported backlog on 50 fps
        /// content (log softsled-20260725-193615: a 251-packet queue reported as
        /// ~10 s of video when it held ~5 s), so the server saw a full buffer and
        /// throttled video delivery to 6–11 fps at the very moment the decoder was
        /// already behind — deepening the starvation instead of relieving it.</para></summary>
        public int InputQueuedMs => _queue.Count * _msPerPacket;

        // Per-MAU duration in ms, from the decoder's detected frame rate (set on
        // the first decoded frame). 40 ms (25 fps) until then.
        private volatile int _msPerPacket = 40;

        // Set by Flush() (any thread); honoured on the worker thread before the
        // next decode. Seek/trick-play uses it to drop stale pre-seek frames.
        private int _flushRequested;

        // Periodic arrival/decode-rate diagnostic (mirrors [libav-audio] stats).
        // Reveals whether the video is UNDER-DELIVERED (arrival < source fps) —
        // the cause of the slow jitter-buffer drain → starvation → audio-leads.
        private long _statPktsIn;
        private long _statBytesIn;
        private long _lastStatBytes;
        private readonly System.Diagnostics.Stopwatch _statClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastStatMs;
        private long _lastStatPkts;
        private long _lastStatFrames;
        public int Width => _width;
        public int Height => _height;

        public event Action<int, int> OnFormatReady;

        /// <summary>Per decoded frame: (yuv420pPtr, lumaStride, width, height,
        /// ptsMs, colorspace, colorRange). The pointer is packed yuv420p (Y w*h,
        /// then U/V cw*ch, tight strides); lumaStride == width. ptsMs is the
        /// frame's presentation time in milliseconds, derived from the wire RTP
        /// timestamp via the stream clock — used by the pacer to schedule
        /// presentation. colorspace is the source frame's AVColorSpace and
        /// colorRange its AVColorRange (as ints) so the deferred YUV→BGRA
        /// conversion (<see cref="Yuv420ToBgra"/>) can pick the correct matrix
        /// (BT.709 for HD, BT.601 for SD) and range instead of swscale's BT.601
        /// default — otherwise HD H.264 (BT.709) renders with shifted colour.</summary>
        public event Action<IntPtr, int, int, int, long, int, int> OnFrame;

        public LibAvVideoPushDecoder(AVCodecID codecId, int clockHz, Logger log) {
            _codecId = codecId;
            _clockHz = clockHz > 0 ? clockHz : 90000;
            _log = log;
        }

        public void Start() {
            if (_worker != null) return;
            try {
                if (string.IsNullOrEmpty(ffmpeg.RootPath)) ffmpeg.RootPath = FfmpegRuntime.NativeDir();
            } catch (Exception ex) {
                _log?.LogError($"[libav-vpush] could not set ffmpeg.RootPath: {ex.Message}");
            }

            AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
            if (codec == null) {
                _log?.LogError($"[libav-vpush] no decoder for {_codecId}");
                throw new InvalidOperationException($"libav: no decoder for {_codecId}");
            }
            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null) throw new InvalidOperationException("libav: avcodec_alloc_context3 failed");
            // Decode threading. FRAME threading (the default with thread_count=0)
            // parallelises by buffering ~N future frames before emitting, adding
            // ~0.5–1 s of output delay at 25fps — which makes video lag audio,
            // because the pacer can only present frames the decoder has emitted.
            // So we do NOT use frame threading.
            //
            // SLICE threading, by contrast, parallelises WITHIN each frame across
            // cores with NO multi-frame output delay — the frame is emitted as
            // soon as its slices finish. Single-threaded 1080p H.264 was too heavy
            // on this hardware (~16fps observed vs 25fps needed → the input queue
            // backed up and playback slowed); slice threading restores the decode
            // headroom without reintroducing the frame-threading lag. If the
            // stream has only one slice per frame it transparently runs
            // single-threaded, so SD / MPEG-2 (already fast) is unaffected.
            _ctx->thread_count = Math.Min(Environment.ProcessorCount, 8);
            _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;

            int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
            if (ret < 0) {
                _log?.LogError($"[libav-vpush] avcodec_open2 failed: {AvStrError(ret)}");
                AVCodecContext* tmp = _ctx; ffmpeg.avcodec_free_context(&tmp); _ctx = null;
                throw new InvalidOperationException($"libav: avcodec_open2 failed: {AvStrError(ret)}");
            }
            _log?.LogInfo($"[libav-vpush] codec opened: {_codecId}");

            _worker = new Thread(WorkerLoop) {
                IsBackground = true,
                Name = $"LibAvVideoPushDecoder-{_codecId}",
            };
            _worker.Start();
        }

        public void SubmitPacket(byte[] data, uint rtpTs) {
            if (_disposed || data == null || data.Length == 0) return;
            Interlocked.Increment(ref _statPktsIn);
            Interlocked.Add(ref _statBytesIn, data.Length);
            if (!_queue.TryAdd(new QueuedPacket { Data = data, RtpTs = rtpTs })) {
                // Queue saturated (backpressure + the BFR occupancy report should
                // prevent this — a full 2048-deep queue is ~80 s of coded video).
                // Drop the INCOMING packet rather than pulling one off the decode
                // head: removing the head punches a hole right where the decoder
                // is about to read and could discard an imminent IDR, corrupting
                // decode until the next one. Dropping the newest keeps the queued
                // run contiguous so the decoder keeps making clean progress; only
                // the tail loses packets, and delivery recovers once it drains.
                long n = Interlocked.Increment(ref _queueFullDrops);
                if ((n & 0x3F) == 1)
                    _log?.LogError($"[libav-vpush] input queue full (depth={_queue.Count}) — " +
                                   $"dropping incoming packet (total {n}); decoder can't keep up / backpressure stuck");
            }
        }
        private long _queueFullDrops;

        public void Complete() { try { _queue.CompleteAdding(); } catch { } }

        /// <summary>Hold decoding (worker blocks before decoding the next
        /// packet). Input queue + already-decoded frames are retained; no CPU
        /// spent while paused. Idempotent.</summary>
        public void Pause() { if (!_disposed) _runGate.Reset(); }

        /// <summary>Resume decoding after <see cref="Pause"/>. Idempotent.</summary>
        public void Resume() { if (!_disposed) _runGate.Set(); }

        /// <summary>Discard queued (undecoded) packets and reset the codec's
        /// internal state. Called on seek / trick-play so no stale pre-seek
        /// frame reaches the pacer (which would corrupt the pts0 anchor). Safe
        /// from any thread: the queue drain is lock-free; the codec flush is
        /// deferred to the worker thread via <c>_flushRequested</c>.</summary>
        public void Flush() {
            if (_disposed) return;
            while (_queue.TryTake(out _)) { }
            Interlocked.Exchange(ref _flushRequested, 1);
        }

        private void WorkerLoop() {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            try {
                foreach (var qp in _queue.GetConsumingEnumerable()) {
                    // Block while paused — the dequeued packet is held in `qp`
                    // (not lost) and decoded once resumed. RTSP is also paused,
                    // so the queue simply holds in the meantime.
                    _runGate.Wait();
                    if (_disposed) break;

                    // Backpressure: don't decode ahead of the pacer's drain rate.
                    // While the pacer already holds >= PacerHighWaterMs of decoded
                    // video, wait — the server's burst stays in the (deep) coded
                    // queue rather than overflowing the pacer's BGRA buffer (which
                    // would drop future frames → a gap → freeze). The held `qp`
                    // decodes as soon as the pacer drains below the mark.
                    var probe = ShouldBackpressure;
                    if (probe != null) {
                        while (!_disposed && probe()) {
                            _runGate.Wait();               // honour pause during the wait
                            if (_disposed) break;
                            Thread.Sleep(8);
                        }
                        if (_disposed) break;
                    }

                    // Seek/trick-play flushed us: drop decoder state so we don't
                    // emit stale pre-seek frames (worker-thread only —
                    // avcodec_flush_buffers is not thread-safe).
                    if (Interlocked.Exchange(ref _flushRequested, 0) == 1) {
                        ffmpeg.avcodec_flush_buffers(_ctx);
                    }
                    SendPacket(pkt, qp);
                    DrainFrames(frame);
                    MaybeLogStats();
                }
                ffmpeg.avcodec_send_packet(_ctx, null);
                DrainFrames(frame);
            } catch (Exception ex) {
                _log?.LogError($"[libav-vpush] worker exception: {ex.Message}");
            } finally {
                AVPacket* p = pkt; ffmpeg.av_packet_free(&p);
                AVFrame* f = frame; ffmpeg.av_frame_free(&f);
            }
        }

        // Log arrival (MAUs submitted) + decode rate once per ~1s window. If
        // arrival fps sits below the source fps (25 for PAL) while decode keeps
        // up, the server is under-delivering video → the pacer's jitter buffer
        // drains → eventual starvation (video freezes, audio leads).
        private void MaybeLogStats() {
            if (_log == null) return;
            long now = _statClock.ElapsedMilliseconds;
            long win = now - _lastStatMs;
            if (win < 1000) return;
            long pkts = Interlocked.Read(ref _statPktsIn);
            long bytes = Interlocked.Read(ref _statBytesIn);
            long frames = Interlocked.Read(ref _framesDecoded);
            long dPkts = pkts - _lastStatPkts;
            long dBytes = bytes - _lastStatBytes;
            long dFrames = frames - _lastStatFrames;
            _lastStatMs = now; _lastStatPkts = pkts; _lastStatBytes = bytes; _lastStatFrames = frames;
            double arrFps = dPkts * 1000.0 / win;
            double decFps = dFrames * 1000.0 / win;
            // bytesIn/kbps answer the question the fps figures cannot: when decode
            // falls behind, did the STREAM get heavier (server changed bitrate —
            // e.g. a recording starting on the tuned live channel) or did WE get
            // slower (CPU)? Rising kbps with falling decode fps = the former;
            // flat kbps with falling decode fps = the latter.
            double kbps = dBytes * 8.0 / win;                       // bytes/ms*8 = kbit/s
            double srcFps = _msPerPacket > 0 ? 1000.0 / _msPerPacket : 25.0;
            _log.LogInfo($"[libav-vpush] stats: pktsIn={dPkts} bytesIn={dBytes} ({kbps:F0}kbps) " +
                         $"framesOut={dFrames} window={win}ms " +
                         $"→ arrival={arrFps:F1}fps decode={decFps:F1}fps " +
                         $"({decFps / srcFps * 100:F0}% of source {srcFps:F0}fps) " +
                         $"avgPkt={(dPkts > 0 ? dBytes / dPkts : 0)}B queueDepth={_queue.Count}");
        }

        private void SendPacket(AVPacket* pkt, QueuedPacket qp) {
            if (!_reorderDiagDone) {
                _pktsSent++;
                if (_firstDecodeOrderRtp.Count < 12) _firstDecodeOrderRtp.Add(qp.RtpTs);
            }
            fixed (byte* src = qp.Data) {
                pkt->data = src;
                pkt->size = qp.Data.Length;
                pkt->pts = qp.RtpTs;
                pkt->dts = qp.RtpTs;
                pkt->flags = 0;
                int ret = ffmpeg.avcodec_send_packet(_ctx, pkt);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                    _log?.LogDebug($"[libav-vpush] send_packet ret={AvStrError(ret)}");
                }
                pkt->data = null;
                pkt->size = 0;
            }
        }

        private void DrainFrames(AVFrame* frame) {
            while (true) {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) return;
                if (ret < 0) {
                    _log?.LogDebug($"[libav-vpush] receive_frame ret={AvStrError(ret)}");
                    return;
                }

                if (TryDeinterlace(frame)) {
                    // Push into the yadif graph; it may emit 0..N frames (buffers
                    // ~1 frame of context, so it's steady-state 1:1 at mode=0).
                    int aret = ffmpeg.av_buffersrc_add_frame(_srcCtx, frame);
                    if (aret < 0) {
                        _log?.LogDebug($"[libav-vpush] buffersrc_add ret={AvStrError(aret)}");
                    } else {
                        while (ffmpeg.av_buffersink_get_frame(_sinkCtx, _filtFrame) >= 0) {
                            EmitFrame(_filtFrame);
                            ffmpeg.av_frame_unref(_filtFrame);
                        }
                    }
                    ffmpeg.av_frame_unref(frame);
                } else {
                    EmitFrame(frame);
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }

        /// <summary>Pack one (already-deinterlaced, if applicable) frame as
        /// planar yuv420p and raise OnFrame with its presentation time. The
        /// heavy YUV→BGRA colour conversion is deferred to present time
        /// (<see cref="Yuv420ToBgra"/>) so the pacer buffers at 1.5 B/px.</summary>
        private void EmitFrame(AVFrame* frame) {
            if (!EnsurePacked(frame)) return;

            // Presentation timestamp: prefer the reorder-correct
            // best_effort_timestamp (carries the rtpTs we set on the
            // packet), fall back to pts, then to a synthetic counter.
            long ts = frame->best_effort_timestamp;
            if (ts == ffmpeg.AV_NOPTS_VALUE) ts = frame->pts;

            if (!_reorderDiagDone) {
                _reorderDiagDone = true;
                double fps = _ctx->framerate.den != 0 ? (double)_ctx->framerate.num / _ctx->framerate.den : 0;
                long frameDur = fps > 0 ? (long)(1000.0 / fps) : 0;
                // Feed the real frame duration to InputQueuedMs so the video BFR
                // reports the TRUE coded backlog (see there) — a 50 fps stream was
                // otherwise reported as twice its actual buffering.
                if (frameDur > 0 && frameDur <= 1000) _msPerPacket = (int)frameDur;
                // pktsBufferedBeforeFirst = reorder depth in packets; ×frameDur ≈ the
                // display latency the reorder adds. decodeOrderRtp non-monotonic ⇒
                // B-frames present (their count/pattern = the reorder structure).
                string decOrder = string.Join(",", _firstDecodeOrderRtp);
                _log?.LogInfo($"[libav-vpush] reorder-diag: has_b_frames={_ctx->has_b_frames} " +
                              $"codecDelay={_ctx->delay} fps={fps:F2} frameDurMs={frameDur} " +
                              $"pktsBufferedBeforeFirstFrame={_pktsSent} " +
                              $"reorderLatencyMs≈{(_pktsSent > 1 ? (_pktsSent - 1) * frameDur : 0)} " +
                              $"firstDisplayPts={ts} (bestEffort; rawPts={frame->pts}) " +
                              $"decodeOrderRtp=[{decOrder}]");
            }
            long ptsMs;
            if (ts == ffmpeg.AV_NOPTS_VALUE) {
                ptsMs = _frameCounter * 1000 / 25; // assume 25fps if no PTS
            } else {
                ptsMs = ts * 1000 / _clockHz;
            }
            _frameCounter++;

            int w = _width, h = _height;
            int ySize = w * h;
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            int cSize = cw * ch;

            // Colour metadata for the deferred YUV→BGRA conversion. The matrix
            // (colorspace) is preserved by both packing paths; the effective
            // sample RANGE differs: the fast path copies the source planes
            // verbatim (so its range is the source frame's), while the swscale
            // normalise path below writes limited-range (MPEG) yuv420p regardless
            // of the source (full-range yuvj is scaled down to limited).
            int csp = (int)frame->colorspace;
            int rng;

            fixed (byte* dst = _packed) {
                if ((AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_YUV420P) {
                    // Fast path: planes already in our target layout — straight
                    // plane copies into the tight packed buffer (honouring the
                    // decoder's padded linesize), no colour conversion.
                    ffmpeg.av_image_copy_plane(dst,                 w,  frame->data[0], frame->linesize[0], w,  h);
                    ffmpeg.av_image_copy_plane(dst + ySize,         cw, frame->data[1], frame->linesize[1], cw, ch);
                    ffmpeg.av_image_copy_plane(dst + ySize + cSize, cw, frame->data[2], frame->linesize[2], cw, ch);
                    rng = (int)frame->color_range;   // preserved verbatim
                } else {
                    // Any other format (incl. full-range yuvj420p): normalise to
                    // yuv420p via swscale, writing directly into the packed buffer.
                    if (_sws == null) return;
                    var dstData = new byte_ptrArray8();
                    dstData[0] = dst;
                    dstData[1] = dst + ySize;
                    dstData[2] = dst + ySize + cSize;
                    var dstLines = new int_array8();
                    dstLines[0] = w;
                    dstLines[1] = cw;
                    dstLines[2] = cw;
                    ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0, h, dstData, dstLines);
                    rng = (int)AVColorRange.AVCOL_RANGE_MPEG;   // sws output is limited
                }
                Interlocked.Increment(ref _framesDecoded);
                // stride param carries the luma stride (= width); the pacer derives
                // the chroma plane geometry from width/height.
                try { OnFrame?.Invoke((IntPtr)dst, w, w, h, ptsMs, csp, rng); }
                catch (Exception ex) { _log?.LogError($"[libav-vpush] OnFrame threw: {ex.Message}"); }
            }
        }

        // ---- yadif deinterlace (MPEG-2 only) ----------------------------

        /// <summary>True if the yadif graph is built and matches the frame's
        /// geometry (builds/rebuilds lazily). MPEG-2 only; false → raw path.</summary>
        private bool TryDeinterlace(AVFrame* frame) {
            if (_deintDisabled) return false;
            if (_codecId != AVCodecID.AV_CODEC_ID_MPEG2VIDEO) return false;
            if (_deintActive && frame->width == _deintW && frame->height == _deintH
                && (int)frame->format == _deintFmt) return true;
            TeardownDeinterlace();
            if (!BuildDeinterlace(frame)) { _deintDisabled = true; return false; }
            _deintActive = true;
            return true;
        }

        private bool BuildDeinterlace(AVFrame* frame) {
            try {
                _graph = ffmpeg.avfilter_graph_alloc();
                if (_graph == null) { _log?.LogError("[libav-vpush] avfilter_graph_alloc failed"); return false; }

                var srcDef  = ffmpeg.avfilter_get_by_name("buffer");
                var sinkDef = ffmpeg.avfilter_get_by_name("buffersink");
                var yadifDef = ffmpeg.avfilter_get_by_name("yadif");
                if (srcDef == null || sinkDef == null || yadifDef == null) {
                    _log?.LogError("[libav-vpush] deinterlace: buffer/buffersink/yadif filter not found (avfilter unavailable?)");
                    return false;
                }

                string pixName = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)frame->format) ?? "yuv420p";
                int sarN = frame->sample_aspect_ratio.num > 0 ? frame->sample_aspect_ratio.num : 1;
                int sarD = frame->sample_aspect_ratio.den > 0 ? frame->sample_aspect_ratio.den : 1;
                string args = $"video_size={frame->width}x{frame->height}:pix_fmt={pixName}:" +
                              $"time_base=1/{_clockHz}:pixel_aspect={sarN}/{sarD}";

                AVFilterContext* src = null, sink = null, yadif = null;
                if (ffmpeg.avfilter_graph_create_filter(&src, srcDef, "in", args, null, _graph) < 0) return false;
                if (ffmpeg.avfilter_graph_create_filter(&sink, sinkDef, "out", null, null, _graph) < 0) return false;
                // mode=0 (send_frame): ONE output per input — keeps the 25 fps the
                // pacer expects (mode=1 would double to 50 fps and break pacing).
                if (ffmpeg.avfilter_graph_create_filter(&yadif, yadifDef, "yadif", "mode=0", null, _graph) < 0) return false;
                if (ffmpeg.avfilter_link(src, 0, yadif, 0) < 0) return false;
                if (ffmpeg.avfilter_link(yadif, 0, sink, 0) < 0) return false;
                if (ffmpeg.avfilter_graph_config(_graph, null) < 0) return false;

                _srcCtx = src; _sinkCtx = sink;
                if (_filtFrame == null) _filtFrame = ffmpeg.av_frame_alloc();
                _deintW = frame->width; _deintH = frame->height; _deintFmt = (int)frame->format;
                _log?.LogInfo($"[libav-vpush] yadif deinterlace active ({frame->width}x{frame->height} {pixName})");
                return true;
            } catch (Exception ex) {
                _log?.LogError($"[libav-vpush] deinterlace setup threw: {ex.Message}");
                return false;
            }
        }

        private void TeardownDeinterlace() {
            if (_graph != null) { var g = _graph; ffmpeg.avfilter_graph_free(&g); _graph = null; }
            _srcCtx = null; _sinkCtx = null; _deintActive = false;
        }

        /// <summary>Size the packed yuv420p buffer for the frame and, for
        /// non-yuv420p inputs ONLY, (re)build a swscale context that normalises
        /// them to yuv420p before packing. Native yuv420p needs no context.
        /// Returns false if the buffer/context couldn't be prepared.</summary>
        private bool EnsurePacked(AVFrame* frame) {
            int w = frame->width, h = frame->height;
            var fmt = (AVPixelFormat)frame->format;
            bool sizeChanged = w != _width || h != _height;

            if (sizeChanged || _packed == null) {
                _width = w;
                _height = h;
                int cw = (w + 1) / 2, ch = (h + 1) / 2;
                _packed = new byte[w * h + 2 * cw * ch];
                try { OnFormatReady?.Invoke(w, h); }
                catch (Exception ex) { _log?.LogError($"[libav-vpush] OnFormatReady threw: {ex.Message}"); }
                _log?.LogInfo($"[libav-vpush] packed yuv420p ready: {w}x{h} (src {fmt})");
            }

            if (fmt != AVPixelFormat.AV_PIX_FMT_YUV420P
                && (_sws == null || sizeChanged || _srcFmt != fmt)) {
                if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
                _sws = ffmpeg.sws_getContext(
                    w, h, fmt, w, h, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    ffmpeg.SWS_BILINEAR, null, null, null);
                _srcFmt = fmt;
                if (_sws == null) {
                    _log?.LogError($"[libav-vpush] sws_getContext ({fmt} → yuv420p) failed");
                    return false;
                }
                _log?.LogInfo($"[libav-vpush] sws normalise {fmt} → yuv420p ({w}x{h})");
            }

            return _packed != null;
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            // Release the pause gate FIRST so a paused worker can exit (else Join
            // would block for the full timeout).
            try { _runGate.Set(); } catch { }
            try { _queue.CompleteAdding(); } catch { }
            try { _worker?.Join(2000); } catch { }
            try { _runGate.Dispose(); } catch { }
            try { _queue.Dispose(); } catch { }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            TeardownDeinterlace();
            if (_filtFrame != null) { AVFrame* ff = _filtFrame; ffmpeg.av_frame_free(&ff); _filtFrame = null; }
            if (_ctx != null) { AVCodecContext* t = _ctx; ffmpeg.avcodec_free_context(&t); _ctx = null; }
            _log?.LogInfo("[libav-vpush] disposed");
        }

        private static string AvStrError(int ret) {
            const int BufSize = 256;
            byte* buf = stackalloc byte[BufSize];
            ffmpeg.av_strerror(ret, buf, (ulong)BufSize);
            return new string((sbyte*)buf);
        }

        private struct QueuedPacket {
            public byte[] Data;
            public uint RtpTs;
        }
    }
}
