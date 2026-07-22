using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// One-frame YUV420p → BGRA colour converter, used at PRESENT time so the
    /// pacer's jitter buffer can store decoded frames as planar YUV420p
    /// (1.5 bytes/pixel) instead of BGRA (4 bytes/pixel) — a 2.67× cut on the
    /// dominant playback buffer. The decoder emits packed YUV420p; this converts
    /// each frame back to BGRA only when it is actually released to the screen
    /// (so dropped frames cost no conversion at all).
    ///
    /// <para>Owns a persistent native BGRA scratch buffer whose pointer stays
    /// valid until the next <see cref="Convert"/> or <see cref="Dispose"/>. The
    /// present callback copies the result synchronously (the D3D presenter's
    /// SubmitFrame copies into its staging buffer before returning), so a single
    /// reused scratch is safe. Not thread-safe: call from one thread only.</para>
    ///
    /// <para>Input is always plain <c>yuv420p</c> (limited range). The decoder
    /// normalises any full-range / non-420p frame to yuv420p before packing, so
    /// this converter never has to special-case pixel format or colour range.</para>
    /// </summary>
    internal sealed unsafe class Yuv420ToBgra : IDisposable {

        private readonly Logger _log;
        private SwsContext* _sws;
        private IntPtr _bgra;      // native BGRA scratch, _w*4 * _h bytes
        private int _w, _h;
        private bool _disposed;

        public Yuv420ToBgra(Logger log) { _log = log; }

        /// <summary>BGRA stride of the last converted frame (width × 4).</summary>
        public int Stride => _w * 4;

        /// <summary>Convert a packed <c>yuv420p</c> frame (Y plane w×h, then U and
        /// V planes cw×ch with cw=(w+1)/2, ch=(h+1)/2, tight strides) to BGRA.
        /// Returns a pointer to the reused native BGRA buffer, valid until the
        /// next call. Returns <see cref="IntPtr.Zero"/> on failure.</summary>
        public IntPtr Convert(IntPtr yuvPacked, int w, int h) {
            if (_disposed || yuvPacked == IntPtr.Zero || w <= 0 || h <= 0) return IntPtr.Zero;

            if (_sws == null || _w != w || _h != h) {
                if (!Reinit(w, h)) return IntPtr.Zero;
            }

            byte* p = (byte*)yuvPacked;
            int ySize = w * h;
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            int cSize = cw * ch;

            var srcData = new byte_ptrArray8();
            srcData[0] = p;
            srcData[1] = p + ySize;
            srcData[2] = p + ySize + cSize;
            var srcLines = new int_array8();
            srcLines[0] = w;
            srcLines[1] = cw;
            srcLines[2] = cw;

            var dstData = new byte_ptrArray8();
            dstData[0] = (byte*)_bgra;
            var dstLines = new int_array8();
            dstLines[0] = w * 4;

            ffmpeg.sws_scale(_sws, srcData, srcLines, 0, h, dstData, dstLines);
            return _bgra;
        }

        private bool Reinit(int w, int h) {
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_bgra != IntPtr.Zero) { Marshal.FreeHGlobal(_bgra); _bgra = IntPtr.Zero; }

            _sws = ffmpeg.sws_getContext(
                w, h, AVPixelFormat.AV_PIX_FMT_YUV420P,
                w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null) {
                _log?.LogError($"[yuv2bgra] sws_getContext failed ({w}x{h})");
                _w = _h = 0;
                return false;
            }
            _bgra = Marshal.AllocHGlobal(w * 4 * h);
            _w = w; _h = h;
            _log?.LogInfo($"[yuv2bgra] converter ready {w}x{h} (yuv420p → BGRA)");
            return true;
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_bgra != IntPtr.Zero) { Marshal.FreeHGlobal(_bgra); _bgra = IntPtr.Zero; }
        }
    }
}
