using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// Standalone libav video decoder used by the Video FPS Lab to benchmark
    /// a "decode with libav, present on the GPU" pipeline against FFME.
    /// Opens a media file with libavformat, decodes the first video stream,
    /// and colour-converts each frame to BGRA via swscale, handing the raw
    /// BGRA bytes to a consumer (the D3DImage presenter).
    ///
    /// <para>Deliberately unthrottled: it decodes + emits frames as fast as
    /// the decoder and consumer allow, and loops the file on EOF, so the
    /// lab measures the sustained decode+present ceiling rather than the
    /// source's nominal frame rate. That is the number that tells us whether
    /// the present path (not the source fps) is the FPS bottleneck.</para>
    ///
    /// <para>Threading: decode runs on a dedicated worker thread.
    /// <see cref="OnFrame"/> fires on that worker thread; the consumer must
    /// copy the bytes before returning (the BGRA buffer is reused).</para>
    /// </summary>
    internal sealed unsafe class LibAvVideoDecoder : IDisposable {

        private readonly string _path;
        private readonly Logger _log;

        private AVFormatContext* _fmt;
        private AVCodecContext* _ctx;
        private SwsContext* _sws;
        private int _videoStreamIndex = -1;

        private Thread _worker;
        private volatile bool _stop;
        private bool _disposed;

        // Reused BGRA destination buffer (one plane, stride = width*4).
        private byte[] _bgra;
        private int _width;
        private int _height;
        private int _stride;

        private long _framesDecoded;
        public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
        public int Width  => _width;
        public int Height => _height;

        /// <summary>Fires once the first frame's dimensions are known.</summary>
        public event Action<int, int> OnFormatReady;

        /// <summary>Fires per decoded frame: (bgraPtr, stride, width, height).
        /// The pointer is valid only for the duration of the call.</summary>
        public event Action<IntPtr, int, int, int> OnFrame;

        public LibAvVideoDecoder(string path, Logger log) {
            _path = path;
            _log = log;
        }

        public void Start() {
            if (_worker != null) return;

            // Point FFmpeg.AutoGen at the same native DLLs FFME uses. Safe to
            // set even if FFME already loaded them (AutoGen resolves lazily).
            try {
                if (string.IsNullOrEmpty(ffmpeg.RootPath)) ffmpeg.RootPath = FfmpegRuntime.NativeDir();
            } catch (Exception ex) {
                _log?.LogError($"[libav-video] could not set ffmpeg.RootPath: {ex.Message}");
            }

            _worker = new Thread(WorkerLoop) {
                IsBackground = true,
                Name = "LibAvVideoDecoder",
            };
            _worker.Start();
        }

        private bool OpenInput() {
            AVFormatContext* fmt = ffmpeg.avformat_alloc_context();
            int ret = ffmpeg.avformat_open_input(&fmt, _path, null, null);
            if (ret < 0) {
                _log?.LogError($"[libav-video] avformat_open_input failed: {AvStrError(ret)}");
                return false;
            }
            _fmt = fmt;

            ret = ffmpeg.avformat_find_stream_info(_fmt, null);
            if (ret < 0) {
                _log?.LogError($"[libav-video] avformat_find_stream_info failed: {AvStrError(ret)}");
                return false;
            }

            for (int i = 0; i < (int)_fmt->nb_streams; i++) {
                if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) {
                    _videoStreamIndex = i;
                    break;
                }
            }
            if (_videoStreamIndex < 0) {
                _log?.LogError("[libav-video] no video stream in input");
                return false;
            }

            AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
            AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (codec == null) {
                _log?.LogError($"[libav-video] no decoder for codec_id={par->codec_id}");
                return false;
            }
            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null) {
                _log?.LogError("[libav-video] avcodec_alloc_context3 failed");
                return false;
            }
            ret = ffmpeg.avcodec_parameters_to_context(_ctx, par);
            if (ret < 0) {
                _log?.LogError($"[libav-video] avcodec_parameters_to_context failed: {AvStrError(ret)}");
                return false;
            }
            // Let libav use all CPU cores for decode (the FPS benchmark wants
            // the decoder to keep up so the present path is what we measure).
            _ctx->thread_count = 0; // 0 = auto

            ret = ffmpeg.avcodec_open2(_ctx, codec, null);
            if (ret < 0) {
                _log?.LogError($"[libav-video] avcodec_open2 failed: {AvStrError(ret)}");
                return false;
            }

            _log?.LogInfo($"[libav-video] opened {Path.GetFileName(_path)}: " +
                          $"codec={par->codec_id} {par->width}x{par->height}");
            return true;
        }

        private void WorkerLoop() {
            try {
                if (!OpenInput()) return;

                AVPacket* pkt = ffmpeg.av_packet_alloc();
                AVFrame* frame = ffmpeg.av_frame_alloc();
                try {
                    while (!_stop) {
                        int ret = ffmpeg.av_read_frame(_fmt, pkt);
                        if (ret < 0) {
                            // EOF (or error) → loop back to start so the
                            // benchmark runs continuously.
                            ffmpeg.av_packet_unref(pkt);
                            ffmpeg.avcodec_flush_buffers(_ctx);
                            ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0,
                                                 ffmpeg.AVSEEK_FLAG_BACKWARD);
                            continue;
                        }
                        if (pkt->stream_index == _videoStreamIndex) {
                            int send = ffmpeg.avcodec_send_packet(_ctx, pkt);
                            if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                                _log?.LogDebug($"[libav-video] send_packet ret={AvStrError(send)}");
                            }
                            DrainFrames(frame);
                        }
                        ffmpeg.av_packet_unref(pkt);
                    }
                } finally {
                    AVPacket* pTmp = pkt; ffmpeg.av_packet_free(&pTmp);
                    AVFrame* fTmp = frame; ffmpeg.av_frame_free(&fTmp);
                }
            } catch (Exception ex) {
                _log?.LogError($"[libav-video] worker exception: {ex.Message}");
            }
        }

        private void DrainFrames(AVFrame* frame) {
            while (!_stop) {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) return;
                if (ret < 0) {
                    _log?.LogDebug($"[libav-video] receive_frame ret={AvStrError(ret)}");
                    return;
                }

                EnsureSws(frame);
                if (_sws == null) { ffmpeg.av_frame_unref(frame); continue; }

                fixed (byte* dst = _bgra) {
                    var dstData = new byte_ptrArray8();
                    dstData[0] = dst;
                    var dstLines = new int_array8();
                    dstLines[0] = _stride;
                    ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0,
                                     frame->height, dstData, dstLines);
                    Interlocked.Increment(ref _framesDecoded);
                    try {
                        OnFrame?.Invoke((IntPtr)dst, _stride, _width, _height);
                    } catch (Exception ex) {
                        _log?.LogError($"[libav-video] OnFrame threw: {ex.Message}");
                    }
                }
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void EnsureSws(AVFrame* frame) {
            if (_sws != null && frame->width == _width && frame->height == _height) return;

            // (Re)build the scaler for the frame's actual dimensions/format.
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }

            _width = frame->width;
            _height = frame->height;
            _stride = _width * 4;
            _bgra = new byte[_stride * _height];

            _sws = ffmpeg.sws_getContext(
                _width, _height, (AVPixelFormat)frame->format,
                _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null) {
                _log?.LogError("[libav-video] sws_getContext failed");
                return;
            }
            _log?.LogInfo($"[libav-video] sws ready: {_width}x{_height} " +
                          $"{(AVPixelFormat)frame->format} → BGRA");
            try { OnFormatReady?.Invoke(_width, _height); }
            catch (Exception ex) { _log?.LogError($"[libav-video] OnFormatReady threw: {ex.Message}"); }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            try { _worker?.Join(2000); } catch { }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_ctx != null) { AVCodecContext* t = _ctx; ffmpeg.avcodec_free_context(&t); _ctx = null; }
            if (_fmt != null) { AVFormatContext* t = _fmt; ffmpeg.avformat_close_input(&t); _fmt = null; }
            _log?.LogInfo("[libav-video] disposed");
        }

        private static string AvStrError(int ret) {
            const int BufSize = 256;
            byte* buf = stackalloc byte[BufSize];
            ffmpeg.av_strerror(ret, buf, (ulong)BufSize);
            return new string((sbyte*)buf);
        }
    }
}
