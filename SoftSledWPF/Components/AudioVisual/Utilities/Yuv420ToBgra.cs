using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.AudioVisual.Utilities {

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
    /// <para>Input is always plain <c>yuv420p</c>. The decoder normalises any
    /// non-420p frame to yuv420p before packing, and passes the source frame's
    /// <em>colorspace</em> and <em>range</em> through so the YUV→BGRA matrix is
    /// chosen correctly (BT.709 for HD, BT.601 for SD) via
    /// <c>sws_setColorspaceDetails</c>. Without this, swscale's BT.601 default
    /// mis-converts HD H.264 (BT.709) and colours shift.</para>
    /// </summary>
    internal sealed unsafe class Yuv420ToBgra : IDisposable {

        private readonly Logger _log;
        private SwsContext* _sws;
        private IntPtr _bgra;      // native BGRA scratch, _w*4 * _h bytes
        private int _w, _h;
        private int _colorspace = -1, _range = -1;   // last applied; -1 = unset
        private bool _disposed;

        public Yuv420ToBgra(Logger log) { _log = log; }

        /// <summary>BGRA stride of the last converted frame (width × 4).</summary>
        public int Stride => _w * 4;

        /// <summary>Convert a packed <c>yuv420p</c> frame (Y plane w×h, then U and
        /// V planes cw×ch with cw=(w+1)/2, ch=(h+1)/2, tight strides) to BGRA.
        /// <paramref name="colorspace"/> / <paramref name="range"/> are the source
        /// frame's AVColorSpace / AVColorRange (as ints); they select the YUV→RGB
        /// matrix and input range. Returns a pointer to the reused native BGRA
        /// buffer, valid until the next call, or <see cref="IntPtr.Zero"/> on
        /// failure.</summary>
        public IntPtr Convert(IntPtr yuvPacked, int w, int h, int colorspace, int range) {
            if (_disposed || yuvPacked == IntPtr.Zero || w <= 0 || h <= 0) return IntPtr.Zero;

            if (_sws == null || _w != w || _h != h) {
                if (!Reinit(w, h)) return IntPtr.Zero;
                _colorspace = -1; _range = -1;   // force colour re-apply on the new context
            }
            if (colorspace != _colorspace || range != _range) {
                ApplyColorspace(w, h, colorspace, range);
                _colorspace = colorspace; _range = range;
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

        /// <summary>Apply the YUV→RGB matrix + input range to the sws context so
        /// HD (BT.709) and SD (BT.601) each convert correctly, rather than
        /// swscale's fixed BT.601 default. An UNSPECIFIED/RGB colorspace is
        /// resolved by resolution (≥720 lines → BT.709, else BT.601), the
        /// standard convention.</summary>
        private void ApplyColorspace(int w, int h, int colorspace, int range) {
            if (_sws == null) return;
            var cs = (AVColorSpace)colorspace;
            if (cs == AVColorSpace.AVCOL_SPC_UNSPECIFIED
                || cs == AVColorSpace.AVCOL_SPC_RGB
                || cs == AVColorSpace.AVCOL_SPC_RESERVED)
                cs = h >= 720 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_BT470BG;

            int swsCs;
            switch (cs) {
                case AVColorSpace.AVCOL_SPC_BT709:      swsCs = ffmpeg.SWS_CS_ITU709;   break;
                case AVColorSpace.AVCOL_SPC_BT2020_NCL:
                case AVColorSpace.AVCOL_SPC_BT2020_CL:  swsCs = ffmpeg.SWS_CS_BT2020;   break;
                case AVColorSpace.AVCOL_SPC_SMPTE240M:  swsCs = ffmpeg.SWS_CS_SMPTE240M; break;
                case AVColorSpace.AVCOL_SPC_FCC:        swsCs = ffmpeg.SWS_CS_FCC;      break;
                default:                                swsCs = ffmpeg.SWS_CS_ITU601;   break; // 601 family
            }
            // Source range: JPEG/full = 1, else limited = 0. Destination is
            // full-range RGB (BGRA), so dstRange = 1 (matches swscale's default
            // for RGB output).
            int srcRange = ((AVColorRange)range == AVColorRange.AVCOL_RANGE_JPEG) ? 1 : 0;

            int* coeff = ffmpeg.sws_getCoefficients(swsCs);
            if (coeff == null) return;
            var table = new int_array4();
            for (uint i = 0; i < 4; i++) table[i] = coeff[i];
            int ret = ffmpeg.sws_setColorspaceDetails(_sws, table, srcRange, table, 1,
                                                       0, 1 << 16, 1 << 16);
            if (ret < 0) {
                _log?.LogError($"[yuv2bgra] sws_setColorspaceDetails failed (cs={cs}, ret={ret})");
            } else {
                _log?.LogInfo($"[yuv2bgra] colour matrix: cs={cs} sws_cs={swsCs} " +
                              $"srcRange={(srcRange == 1 ? "full" : "limited")} ({w}x{h})");
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_bgra != IntPtr.Zero) { Marshal.FreeHGlobal(_bgra); _bgra = IntPtr.Zero; }
        }
    }
}
