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
        private byte[] _rentBuffer;     // reused per-frame BGRA buffer when sizes match

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
            if (_sws == null || srcW != _swsSrcW || srcH != _swsSrcH || srcFmt != _swsSrcFmt) {
                if (_sws != null) {
                    ffmpeg.sws_freeContext(_sws);
                    _sws = null;
                }
                _sws = ffmpeg.sws_getContext(
                    srcW, srcH, srcFmt,
                    srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BILINEAR, null, null, null);
                if (_sws == null) {
                    Log?.LogError($"[libav-video] sws_getContext failed for " +
                                  $"{srcW}x{srcH} {srcFmt} → BGRA");
                    return;
                }
                _swsSrcW = srcW;
                _swsSrcH = srcH;
                _swsSrcFmt = srcFmt;
                _rentBuffer = null;     // force re-alloc on size change
            }

            int dstStride = srcW * 4;       // BGRA32 = 4 bytes/pixel, tightly packed
            int dstBytes = dstStride * srcH;
            if (_rentBuffer == null || _rentBuffer.Length < dstBytes) {
                _rentBuffer = new byte[dstBytes];
            }

            // sws_scale writes into a uint8_t** plane array. For BGRA (packed)
            // there's only one plane; pass dst as plane 0.
            fixed (byte* dst = _rentBuffer) {
                byte_ptrArray4 dstPlanes = new byte_ptrArray4();
                dstPlanes[0] = dst;
                int_array4 dstStrides = new int_array4();
                dstStrides[0] = dstStride;
                int scaled = ffmpeg.sws_scale(_sws,
                    frame->data, frame->linesize, 0, srcH,
                    dstPlanes, dstStrides);
                if (scaled <= 0) {
                    Log?.LogError($"[libav-video] sws_scale returned {scaled}");
                    return;
                }
            }

            // Copy out of the rent buffer so the next frame can reuse it
            // without racing the renderer. (We could pool VideoFrameSamples
            // too, but a fresh byte[] per frame at ~24 fps for 720p is only
            // ~85 MB/s of GC pressure — fine for a desktop app.)
            byte[] copy = new byte[dstBytes];
            Buffer.BlockCopy(_rentBuffer, 0, copy, 0, dstBytes);

            VideoFrameSample sample = new VideoFrameSample {
                PresentationMs = ptsMs,
                Width = srcW,
                Height = srcH,
                Stride = dstStride,
                Bgra32 = copy,
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
