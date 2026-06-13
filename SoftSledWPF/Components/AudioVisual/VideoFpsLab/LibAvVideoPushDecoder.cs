using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// Push-fed libav video decoder: decodes raw elementary-stream packets
    /// (H.264 Annex-B access units or MPEG-1/2 video access units) submitted
    /// via <see cref="SubmitPacket"/>, colour-converts each frame to BGRA
    /// with swscale, and raises <see cref="OnFrame"/>.
    ///
    /// <para>This is the live-stream counterpart of
    /// <see cref="LibAvVideoDecoder"/> (which opens a file via avformat). It
    /// is fed the same video MAUs the FFME producer path receives, so the
    /// FPS Lab can benchmark the GPU present path against the *real* RTSP
    /// workload — and it is the exact decoder a Stage-1 integration would
    /// reuse to replace FFME.</para>
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
        private readonly BlockingCollection<QueuedPacket> _queue
            = new BlockingCollection<QueuedPacket>(boundedCapacity: 64);
        private long _frameCounter; // fallback PTS source when the wire carries none

        private AVCodecContext* _ctx;
        private SwsContext* _sws;
        private Thread _worker;
        private bool _disposed;

        private byte[] _bgra;
        private int _width, _height, _stride;

        private long _framesDecoded;
        public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
        public int Width => _width;
        public int Height => _height;

        public event Action<int, int> OnFormatReady;

        /// <summary>Per decoded frame: (bgraPtr, stride, width, height, ptsMs).
        /// ptsMs is the frame's presentation time in milliseconds, derived
        /// from the wire RTP timestamp via the stream clock — used by the
        /// pacer to schedule presentation.</summary>
        public event Action<IntPtr, int, int, int, long> OnFrame;

        public LibAvVideoPushDecoder(AVCodecID codecId, int clockHz, Logger log) {
            _codecId = codecId;
            _clockHz = clockHz > 0 ? clockHz : 90000;
            _log = log;
        }

        public void Start() {
            if (_worker != null) return;
            try {
                string ffmpegDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                                   + "\\Tools\\ffmpeg\\x64";
                if (string.IsNullOrEmpty(ffmpeg.RootPath)) ffmpeg.RootPath = ffmpegDir;
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
            // Low-latency decode for live playback. Frame-threading (the
            // default with thread_count=0) parallelises by buffering ~N future
            // frames before emitting, adding ~0.5–1 s of output delay at 25fps
            // — which makes video lag audio, because the pacer can only present
            // frames the decoder has actually emitted. Single-threaded decode
            // removes that delay entirely (only the codec's inherent B-frame
            // reorder remains, a few frames). The FPS lab showed ample decode
            // headroom, so real-time SD/HD playback is fine single-threaded.
            _ctx->thread_count = 1;

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
            if (!_queue.TryAdd(new QueuedPacket { Data = data, RtpTs = rtpTs })) {
                if (_queue.TryTake(out _)) _queue.TryAdd(new QueuedPacket { Data = data, RtpTs = rtpTs });
            }
        }

        public void Complete() { try { _queue.CompleteAdding(); } catch { } }

        private void WorkerLoop() {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            try {
                foreach (var qp in _queue.GetConsumingEnumerable()) {
                    SendPacket(pkt, qp);
                    DrainFrames(frame);
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

        private void SendPacket(AVPacket* pkt, QueuedPacket qp) {
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

                EnsureSws(frame);
                if (_sws == null) { ffmpeg.av_frame_unref(frame); continue; }

                // Presentation timestamp: prefer the reorder-correct
                // best_effort_timestamp (carries the rtpTs we set on the
                // packet), fall back to pts, then to a synthetic counter.
                long ts = frame->best_effort_timestamp;
                if (ts == ffmpeg.AV_NOPTS_VALUE) ts = frame->pts;
                long ptsMs;
                if (ts == ffmpeg.AV_NOPTS_VALUE) {
                    ptsMs = _frameCounter * 1000 / 25; // assume 25fps if no PTS
                } else {
                    ptsMs = ts * 1000 / _clockHz;
                }
                _frameCounter++;

                fixed (byte* dst = _bgra) {
                    var dstData = new byte_ptrArray8();
                    dstData[0] = dst;
                    var dstLines = new int_array8();
                    dstLines[0] = _stride;
                    ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0,
                                     frame->height, dstData, dstLines);
                    Interlocked.Increment(ref _framesDecoded);
                    try { OnFrame?.Invoke((IntPtr)dst, _stride, _width, _height, ptsMs); }
                    catch (Exception ex) { _log?.LogError($"[libav-vpush] OnFrame threw: {ex.Message}"); }
                }
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void EnsureSws(AVFrame* frame) {
            if (_sws != null && frame->width == _width && frame->height == _height) return;
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }

            _width = frame->width;
            _height = frame->height;
            _stride = _width * 4;
            _bgra = new byte[_stride * _height];

            _sws = ffmpeg.sws_getContext(
                _width, _height, (AVPixelFormat)frame->format,
                _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null) { _log?.LogError("[libav-vpush] sws_getContext failed"); return; }
            _log?.LogInfo($"[libav-vpush] sws ready: {_width}x{_height} " +
                          $"{(AVPixelFormat)frame->format} → BGRA");
            try { OnFormatReady?.Invoke(_width, _height); }
            catch (Exception ex) { _log?.LogError($"[libav-vpush] OnFormatReady threw: {ex.Message}"); }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _worker?.Join(2000); } catch { }
            try { _queue.Dispose(); } catch { }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
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
