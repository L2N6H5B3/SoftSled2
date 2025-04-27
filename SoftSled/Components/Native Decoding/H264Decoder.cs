using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen; // Requires FFmpeg.AutoGen NuGet package

namespace SoftSled.Components.NativeDecoding {
    /// <summary>
    /// Represents event arguments for a decoded video frame.
    /// Contains pointers and information needed for rendering.
    /// WARNING: The AVFrame pointer and its data buffers are only valid
    /// during the event handler execution unless explicitly copied.
    /// </summary>
    public unsafe class DecodedFrameEventArgs : EventArgs {
        public AVFrame* Frame { get; }
        public int Width => Frame->width;
        public int Height => Frame->height;
        public AVPixelFormat PixelFormat => (AVPixelFormat)Frame->format;
        public long Pts => Frame->pts; // Presentation Timestamp

        public DecodedFrameEventArgs(AVFrame* frame) {
            Frame = frame;
        }
    }

    /// <summary>
    /// Decodes H.264 NAL units using FFmpeg's libavcodec via FFmpeg.AutoGen.
    /// Assumes input NAL units are complete and prefixed with Annex B start codes.
    /// Requires compiling with /unsafe option.
    /// </summary>
    public unsafe class H264Decoder : IDisposable {
        private AVCodecContext* _codecContext = null;
        private AVCodecParserContext* _parserContext = null;
        private AVFrame* _decodedFrame = null;
        private AVPacket* _packet = null; // Packet used by the parser
        private bool _disposed = false;

        /// <summary>
        /// Event raised when a complete video frame has been decoded.
        /// </summary>
        public event EventHandler<DecodedFrameEventArgs> FrameDecoded;

        /// <summary>
        /// Initializes the H.264 decoder.
        /// </summary>
        /// <returns>True on success, false otherwise.</returns>
        public bool Initialize() {
            if (_codecContext != null) return true; // Already initialized

            Trace.WriteLine("Initializing H.264 Decoder...");
            try {
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG); // Enable FFmpeg internal logging

                // Find H.264 decoder
                AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
                if (codec == null) throw new ApplicationException("H.264 decoder not found.");
                Trace.WriteLine($"Found H.264 decoder: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}");

                // Initialize Parser (helps handle Annex B stream format)
                _parserContext = ffmpeg.av_parser_init((int)AVCodecID.AV_CODEC_ID_H264);
                if (_parserContext == null) throw new ApplicationException("Failed to initialize H.264 parser.");
                Trace.WriteLine("H.264 parser initialized.");

                // Allocate Codec Context
                _codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecContext == null) throw new ApplicationException("Failed to allocate codec context.");
                Trace.WriteLine("Codec context allocated.");

                // Set context options if known (e.g., low delay, thread count)
                // _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                // _codecContext->thread_count = Math.Max(1, Environment.ProcessorCount / 2);

                // Open the codec
                int ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
                if (ret < 0) throw new ApplicationException($"Failed to open codec: {GetErrorMessage(ret)}");
                Trace.WriteLine("Codec opened successfully.");

                // Allocate frame and packet structures
                _decodedFrame = ffmpeg.av_frame_alloc();
                if (_decodedFrame == null) throw new ApplicationException("Failed to allocate frame.");
                _packet = ffmpeg.av_packet_alloc();
                if (_packet == null) throw new ApplicationException("Failed to allocate packet.");
                Trace.WriteLine("Frame and packet allocated.");

                Trace.WriteLine("H.264 Decoder Initialized Successfully.");
                return true;
            } catch (Exception ex) {
                Trace.WriteLine($"Error initializing H.264 Decoder: {ex.Message}");
                Dispose(); // Clean up partially initialized resources
                return false;
            }
        }

        /// <summary>
        /// Decodes a buffer containing one or more Annex B formatted NAL units.
        /// </summary>
        /// <param name="nalUnitDataWithStartCode">Byte array containing NAL unit(s), each prefixed with Annex B start code.</param>
        /// <param name="dataLength">Length of the valid data in the array.</param>
        /// <param name="pts">Presentation timestamp for this data (optional)  = ffmpeg.AV_NOPTS_VALUE.</param>
        public void DecodeNalUnits(byte[] nalUnitDataWithStartCode, int dataLength, long pts) {
            if (_disposed || _codecContext == null || _parserContext == null || nalUnitDataWithStartCode == null || dataLength == 0) {
                return;
            }

            fixed (byte* pData = nalUnitDataWithStartCode) {
                byte* currentPtr = pData;
                int remainingBytes = dataLength;

                while (remainingBytes > 0) {
                    // Ensure packet is clean before parser potentially writes to it
                    // (av_parser_parse2 documentation implies it handles packet allocation/reallocation if needed,
                    // but starting clean is safer)
                    // ffmpeg.av_packet_unref(_packet); // Let parser manage packet data buffer

                    // Use parser to extract packets from the Annex B stream
                    int bytesParsed = ffmpeg.av_parser_parse2(
                        _parserContext,
                        _codecContext,
                        &_packet->data,      // Pointer to output buffer pointer (parser allocates/reallocates)
                        &_packet->size,      // Pointer to output buffer size
                        currentPtr,          // Current position in input buffer
                        remainingBytes,      // Bytes remaining in input buffer
                        pts,                 // Use provided PTS if available for the *start* of this buffer chunk
                        ffmpeg.AV_NOPTS_VALUE, // DTS (usually not needed for decoder)
                        0                    // Position (not usually needed)
                    );

                    if (bytesParsed < 0) {
                        Trace.WriteLine($"Error parsing NAL unit data: {GetErrorMessage(bytesParsed)}");
                        break; // Error during parsing
                    }

                    currentPtr += bytesParsed;       // Advance input pointer
                    remainingBytes -= bytesParsed; // Decrease remaining bytes

                    // If the parser output a packet (_packet->size > 0), send it to the decoder
                    if (_packet->size > 0) {
                        // Assign PTS from parser output if available (parser might refine it)
                        if (_parserContext->pts != ffmpeg.AV_NOPTS_VALUE) {
                            _packet->pts = _parserContext->pts;
                        } else if (_packet->pts == ffmpeg.AV_NOPTS_VALUE && pts != ffmpeg.AV_NOPTS_VALUE) {
                            _packet->pts = pts; // Use original PTS if parser didn't provide one
                        }
                        // Assign other relevant fields if needed (DTS, duration, etc.)
                        _packet->dts = _parserContext->dts;
                        _packet->duration = _parserContext->duration;
                        // _packet->pos = _parserContext->pos; // File position, less relevant here

                        SendPacketToDecoder(_packet);
                    }

                    // If we've processed all bytes, break the loop
                    // Note: remainingBytes might be 0, but parser might still have buffered data
                    // that produces a packet in the next iteration with remainingBytes=0.
                    // The check for _packet->size > 0 handles this.
                }
            }

            // After processing input, try to receive any remaining buffered frames
            ReceiveDecodedFrames();
        }


        /// <summary>
        /// Sends a packet (obtained from parser) to the decoder.
        /// </summary>
        private void SendPacketToDecoder(AVPacket* packet) {
            if (_disposed || _codecContext == null) return;

            int ret = ffmpeg.avcodec_send_packet(_codecContext, packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                // Decoder needs output read before accepting more input
                // Trace.WriteLine("Decoder needs output before accepting more input (EAGAIN).");
                ReceiveDecodedFrames(); // Try to receive frames
                // Retry sending the same packet
                ret = ffmpeg.avcodec_send_packet(_codecContext, packet);
            }

            if (ret < 0 && ret != ffmpeg.AVERROR_EOF) // EOF is ok, just means stream ended
            {
                Trace.WriteLine($"Error sending packet to decoder: {GetErrorMessage(ret)}");
            }
            // Packet data buffer is managed by the parser or FFmpeg internals after send_packet.
            // Do not unref here within the send loop.
        }

        /// <summary>
        /// Attempts to receive decoded frames from the decoder.
        /// </summary>
        private void ReceiveDecodedFrames() {
            if (_disposed || _codecContext == null || _decodedFrame == null) return;

            int ret;
            do {
                // Ensure frame is clean before receiving into it
                ffmpeg.av_frame_unref(_decodedFrame);
                ret = ffmpeg.avcodec_receive_frame(_codecContext, _decodedFrame);

                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) {
                    // Need more input or end of stream reached
                    return;
                } else if (ret < 0) {
                    // Genuine decoding error
                    Trace.WriteLine($"Error receiving frame from decoder: {GetErrorMessage(ret)}");
                    return; // Stop trying on error
                } else // Success! Got a frame
                  {
                    // Raise event with the decoded frame
                    OnFrameDecoded(_decodedFrame);
                    // Frame is unreferenced at the start of the next loop iteration or before next receive call
                }
            } while (ret >= 0); // Continue looping as long as frames are available
        }


        /// <summary>
        /// Safely raises the FrameDecoded event.
        /// </summary>
        protected virtual void OnFrameDecoded(AVFrame* frame) {
            FrameDecoded?.Invoke(this, new DecodedFrameEventArgs(frame));
        }

        /// <summary>
        /// Helper to get FFmpeg error messages.
        /// </summary>
        private static string GetErrorMessage(int error) {
            byte* errbuf = stackalloc byte[256];
            ffmpeg.av_strerror(error, errbuf, 256);
            return Marshal.PtrToStringAnsi((IntPtr)errbuf);
        }

        /// <summary>
        /// Cleans up native FFmpeg resources.
        /// </summary>
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (_disposed) return;

            Trace.WriteLine("Disposing H.264 Decoder...");
            if (_codecContext != null) {
                // Flush decoder by sending null packet
                try {
                    AVPacket* flushPacket = null; // Send null packet to flush
                    SendPacketToDecoder(flushPacket);
                    // Receive any remaining frames after flush
                    ReceiveDecodedFrames();
                } catch (Exception ex) { Trace.WriteLine($"Exception during decoder flush: {ex.Message}"); }


                fixed (AVCodecContext** ppCodecContext = &_codecContext) {
                    ffmpeg.avcodec_free_context(ppCodecContext);
                }
                _codecContext = null;
            }

            if (_parserContext != null) {
                ffmpeg.av_parser_close(_parserContext);
                _parserContext = null;
            }

            if (_decodedFrame != null) {
                fixed (AVFrame** ppFrame = &_decodedFrame) {
                    ffmpeg.av_frame_free(ppFrame);
                }
                _decodedFrame = null;
            }
            if (_packet != null) {
                fixed (AVPacket** ppPacket = &_packet) {
                    ffmpeg.av_packet_free(ppPacket);
                }
                _packet = null;
            }

            _disposed = true;
            Trace.WriteLine("H.264 Decoder Disposed.");
        }

        ~H264Decoder() {
            Dispose(false);
        }
    }
}
