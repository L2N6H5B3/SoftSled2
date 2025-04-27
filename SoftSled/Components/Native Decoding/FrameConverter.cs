using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SoftSled.Components.NativeDecoding {
    /// <summary>
    /// Uses FFmpeg's libswscale to convert AVFrames from one pixel format (e.g., YUV)
    /// to another suitable for display (e.g., BGRA).
    /// Requires compiling with /unsafe option.
    /// </summary>
    public unsafe class FrameConverter : IDisposable {
        private SwsContext* _swsContext = null;
        private AVFrame* _destFrame = null;
        private byte* _destBuffer = null;
        private int _srcWidth = 0;
        private int _srcHeight = 0;
        private AVPixelFormat _srcPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _destWidth = 0;
        private int _destHeight = 0;
        private AVPixelFormat _destPixFmt = AVPixelFormat.AV_PIX_FMT_BGRA; // Target format for Bitmap
        private bool _disposed = false;

        /// <summary>
        /// Converts a source AVFrame to the destination format (BGRA).
        /// Creates or reuses the conversion context and destination frame buffer as needed.
        /// </summary>
        /// <param name="sourceFrame">The decoded frame from the decoder.</param>
        /// <returns>An AVFrame containing the converted image data in BGRA format, or null on failure.</returns>
        public AVFrame* ConvertFrame(AVFrame* sourceFrame) {
            if (sourceFrame == null || _disposed) return null;

            int currentWidth = sourceFrame->width;
            int currentHeight = sourceFrame->height;
            AVPixelFormat currentPixFmt = (AVPixelFormat)sourceFrame->format;

            // Check if context needs to be recreated (input format/size changed)
            if (_swsContext == null || _srcWidth != currentWidth || _srcHeight != currentHeight || _srcPixFmt != currentPixFmt || _destWidth != currentWidth || _destHeight != currentHeight) {
                Trace.WriteLine($"Recreating SwsContext: {currentWidth}x{currentHeight} {currentPixFmt} -> {_destPixFmt}");
                ffmpeg.sws_freeContext(_swsContext); // Safe to call on null pointer

                _swsContext = ffmpeg.sws_getContext(
                    currentWidth, currentHeight, currentPixFmt, // Source
                    currentWidth, currentHeight, _destPixFmt,  // Destination (same size, BGRA format)
                    ffmpeg.SWS_BILINEAR, // Scaling algorithm (relevant if resizing)
                    null, null, null);

                if (_swsContext == null) {
                    Trace.WriteLine("Failed to get SwsContext.");
                    return null;
                }

                // Free previous destination frame/buffer if dimensions changed
                FreeDestFrame();

                // Allocate destination frame and buffer
                _destFrame = ffmpeg.av_frame_alloc();
                if (_destFrame == null) throw new ApplicationException("Failed to allocate destination frame.");

                _destFrame->width = currentWidth;
                _destFrame->height = currentHeight;
                _destFrame->format = (int)_destPixFmt;

                int bufferSize = ffmpeg.av_image_get_buffer_size(_destPixFmt, currentWidth, currentHeight, 1); // Alignment = 1
                if (bufferSize < 0) throw new ApplicationException("Failed to calculate destination buffer size.");

                _destBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize); // Allocate buffer
                if (_destBuffer == null) throw new ApplicationException("Failed to allocate destination buffer.");

                // Assign buffer to frame
                // *** FIXED: Cast pointers to expected array types before passing by ref ***
                int ret = ffmpeg.av_image_fill_arrays(
                    ref *(byte_ptrArray4*)&_destFrame->data,  // Cast data pointer array
                    ref *(int_array4*)&_destFrame->linesize, // Cast linesize array
                    _destBuffer,
                    _destPixFmt,
                    currentWidth,
                    currentHeight,
                    1); // Alignment
                if (ret < 0) throw new ApplicationException($"Failed to fill destination frame arrays: {GetErrorMessage(ret)}");


                _srcWidth = currentWidth;
                _srcHeight = currentHeight;
                _srcPixFmt = currentPixFmt;
                _destWidth = currentWidth;
                _destHeight = currentHeight;
                Trace.WriteLine("SwsContext and destination frame/buffer created/recreated.");
            }

            // Perform the conversion
            int outputSliceHeight = ffmpeg.sws_scale(
                _swsContext,
                sourceFrame->data, // Input data pointers
                sourceFrame->linesize, // Input linesizes
                0, // Input slice Y start
                sourceFrame->height, // Input slice height
                _destFrame->data, // Output data pointers
                _destFrame->linesize // Output linesizes
            );

            if (outputSliceHeight <= 0) {
                Trace.WriteLine("sws_scale failed or returned 0 height.");
                return null;
            }

            // Copy PTS from source frame
            _destFrame->pts = sourceFrame->pts;

            return _destFrame; // Return the frame containing BGRA data
        }

        private void FreeDestFrame() {
            if (_destFrame != null) {
                // Free the buffer allocated with av_malloc
                if (_destBuffer != null) {
                    ffmpeg.av_free(_destBuffer);
                    _destBuffer = null;
                }
                // Free the frame structure itself
                fixed (AVFrame** ppFrame = &_destFrame) {
                    ffmpeg.av_frame_free(ppFrame);
                }
                _destFrame = null;
            }
        }

        /// <summary>
        /// Helper to get FFmpeg error messages.
        /// </summary>
        private static string GetErrorMessage(int error) {
            byte* errbuf = stackalloc byte[256];
            ffmpeg.av_strerror(error, errbuf, 256);
            return Marshal.PtrToStringAnsi((IntPtr)errbuf);
        }

        public void Dispose() {
            if (_disposed) return;
            Trace.WriteLine("Disposing FrameConverter...");
            ffmpeg.sws_freeContext(_swsContext);
            FreeDestFrame();
            _swsContext = null;
            _disposed = true;
            Trace.WriteLine("FrameConverter Disposed.");
            GC.SuppressFinalize(this);
        }

        ~FrameConverter() { Dispose(); }
    }
}
