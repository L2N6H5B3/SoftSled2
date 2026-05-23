using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Video specialization of <see cref="LibAvDecoder"/>. Each decoded
    /// frame is converted from whatever libav pixel format the decoder
    /// produces (YUV420P, NV12, etc) to BGRA32 via <c>sws_scale</c>, then
    /// handed to the renderer through the supplied callback.
    ///
    /// The <c>SwsContext</c> is lazily allocated on the first frame so
    /// we know the actual source dimensions / pixel format (codec
    /// reporting from extradata isn't always accurate before a frame
    /// arrives — especially with MPEG-2 video that probes the sequence
    /// header in-band).
    /// </summary>
    internal sealed unsafe class VideoDecoder : LibAvDecoder {

        private readonly Action<VideoFrameSample> _onFrame;
        private SwsContext* _sws;
        private int _swsSrcW, _swsSrcH;
        private AVPixelFormat _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;

        public VideoDecoder(AVCodecID codecId, byte[] extradata,
                            Action<VideoFrameSample> onFrame, Logger log)
            : base(codecId, extradata, "video", log) {
            _onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
        }

        protected override void OnFrameDecoded(AVFrame* frame, long ptsMs) {
            int srcW = frame->width;
            int srcH = frame->height;
            AVPixelFormat srcFmt = (AVPixelFormat)frame->format;
            if (srcW <= 0 || srcH <= 0 || srcFmt == AVPixelFormat.AV_PIX_FMT_NONE) return;

            // (Re)allocate sws context if format or size changed. Common
            // case: never (single-resolution stream).
            //
            // SWS_POINT is intentional, not a quality compromise: source and
            // destination dimensions are identical (we don't scale), so
            // sws_scale here is just a pixel-format converter (YUV420P /
            // NV12 / etc → BGRA). With same-size conversion the
            // interpolation choice has no effect on output quality but a
            // ~3× effect on throughput — POINT is essentially the fast
            // path for "memcpy with format unpack" and is the right
            // choice when no resampling is taking place. (Output will
            // get stretched by the WPF renderer if the Image element is
            // sized differently, but that's a GPU-side bilinear stretch
            // and free.)
            if (_sws == null || srcW != _swsSrcW || srcH != _swsSrcH || srcFmt != _swsSrcFmt) {
                if (_sws != null) {
                    ffmpeg.sws_freeContext(_sws);
                    _sws = null;
                }
                _sws = ffmpeg.sws_getContext(
                    srcW, srcH, srcFmt,
                    srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_POINT, null, null, null);
                if (_sws == null) {
                    Log?.LogError($"[libav-video] sws_getContext failed for " +
                                  $"{srcW}x{srcH} {srcFmt} → BGRA");
                    return;
                }
                _swsSrcW = srcW;
                _swsSrcH = srcH;
                _swsSrcFmt = srcFmt;
            }

            int dstStride = srcW * 4;       // BGRA32 = 4 bytes/pixel, tightly packed
            int dstBytes = dstStride * srcH;

            // Rent a buffer from the pool. At 1080p that's ~8 MB which
            // would otherwise hit the LOH and trigger frequent Gen-2
            // collections at 60 fps. The renderer returns the buffer to
            // the pool via VideoFrameSample.Release() once the bitmap
            // blit is complete, so steady-state same-resolution streams
            // cycle through a small fixed set of buffers indefinitely.
            byte[] dstBuf = BufferPool.Rent(dstBytes);

            // sws_scale writes into a uint8_t** plane array. For BGRA (packed)
            // there's only one plane; pass dst as plane 0. Writing
            // straight into the pooled (eventually-rendered) buffer
            // skips the previous Buffer.BlockCopy step entirely — at
            // 1080p that was 8 MB of memory bandwidth per frame.
            fixed (byte* dst = dstBuf) {
                byte_ptrArray4 dstPlanes = new byte_ptrArray4();
                dstPlanes[0] = dst;
                int_array4 dstStrides = new int_array4();
                dstStrides[0] = dstStride;
                int scaled = ffmpeg.sws_scale(_sws,
                    frame->data, frame->linesize, 0, srcH,
                    dstPlanes, dstStrides);
                if (scaled <= 0) {
                    Log?.LogError($"[libav-video] sws_scale returned {scaled}");
                    BufferPool.Return(dstBuf);
                    return;
                }
            }

            VideoFrameSample sample = new VideoFrameSample {
                PresentationMs = ptsMs,
                Width = srcW,
                Height = srcH,
                Stride = dstStride,
                Bgra32 = dstBuf,
            };
            _onFrame(sample);
        }

        public override void Dispose() {
            if (_sws != null) {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
            base.Dispose();
        }
    }
}
