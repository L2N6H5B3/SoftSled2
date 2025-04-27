using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen; // Requires FFmpeg.AutoGen NuGet package

namespace SoftSled.Components.NativeDecoding {
    /// <summary>
    /// Represents event arguments for a decoded audio frame.
    /// Contains pointers and information needed for playback.
    /// WARNING: The AVFrame pointer and its data buffers are only valid
    /// during the event handler execution unless explicitly copied.
    /// </summary>
    public unsafe class DecodedAudioFrameEventArgs : EventArgs // Changed class name
    {
        public AVFrame* Frame { get; }
        public int SampleRate => Frame->sample_rate;
        public int Channels => Frame->ch_layout.nb_channels; // Use channel layout
        public AVSampleFormat SampleFormat => (AVSampleFormat)Frame->format;
        public int NumberOfSamples => Frame->nb_samples;
        public long Pts => Frame->pts; // Presentation Timestamp

        public DecodedAudioFrameEventArgs(AVFrame* frame) {
            Frame = frame;
        }
    }

    /// <summary>
    /// Decodes raw PCM audio data (specifically s16le) using FFmpeg's libavcodec via FFmpeg.AutoGen.
    /// It essentially wraps the raw PCM data into AVFrames with correct parameters.
    /// Requires compiling with /unsafe option.
    /// </summary>
    public unsafe class PcmAudioDecoder : IDisposable // Changed class name
    {
        private AVCodecContext* _codecContext = null;
        // No parser needed for raw PCM
        private AVFrame* _decodedFrame = null;
        private AVPacket* _packet = null; // Packet to hold the raw input data
        private bool _disposed = false;

        // Expected input format details
        private const AVCodecID InputCodecId = AVCodecID.AV_CODEC_ID_PCM_S16LE;
        private const AVSampleFormat InputSampleFormat = AVSampleFormat.AV_SAMPLE_FMT_S16; // s16le uses AV_SAMPLE_FMT_S16
        private const int InputSampleRate = 48000; // From your WAVEFORMATEX
        private const int InputChannels = 2;       // From your WAVEFORMATEX
        // Removed static readonly AVChannelLayout - will get default layout dynamically

        /// <summary>
        /// Event raised when a complete audio frame has been processed/decoded.
        /// </summary>
        public event EventHandler<DecodedAudioFrameEventArgs> AudioFrameDecoded; // Changed event name

        /// <summary>
        /// Initializes the PCM audio decoder context.
        /// </summary>
        /// <returns>True on success, false otherwise.</returns>
        public bool Initialize() {
            if (_codecContext != null) return true; // Already initialized

            Trace.WriteLine("Initializing PCM Audio Decoder...");
            try {
                // Find the PCM S16LE decoder (it mainly handles format info)
                AVCodec* codec = ffmpeg.avcodec_find_decoder(InputCodecId);
                if (codec == null) throw new ApplicationException("PCM S16LE decoder not found.");

                // Allocate Codec Context
                _codecContext = ffmpeg.avcodec_alloc_context3(codec);
                if (_codecContext == null) throw new ApplicationException("Failed to allocate codec context.");

                // *** Set the INPUT parameters explicitly ***
                _codecContext->sample_fmt = InputSampleFormat;
                _codecContext->sample_rate = InputSampleRate;

                // *** Use av_get_default_channel_layout to set the channel layout ***
                AVChannelLayout defaultLayout = default; // Create an instance
                ffmpeg.av_channel_layout_default(&defaultLayout, InputChannels); // Get default layout for channel count
                if (defaultLayout.nb_channels == 0) // Check if getting default layout failed
                {
                    // Fallback or error - For stereo, we can often assume AV_CH_LAYOUT_STEREO's bitmask
                    Trace.WriteLine("Warning: Could not get default channel layout, assuming stereo bitmask.");
                    _codecContext->ch_layout.u.mask = ffmpeg.AV_CH_LAYOUT_STEREO; // Use the constant directly under ffmpeg
                    _codecContext->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC; // Important for mask usage
                    _codecContext->ch_layout.nb_channels = InputChannels; // Ensure channel count is set
                } else {
                    // Copy the obtained default layout to the context
                    ffmpeg.av_channel_layout_copy(&_codecContext->ch_layout, &defaultLayout);
                }
                // *** End Channel Layout Setting ***


                // Optional: Set a timebase if known, otherwise ffmpeg might guess
                // _codecContext->time_base = new AVRational { num = 1, den = InputSampleRate };

                // Open the codec (for PCM, this mainly validates parameters)
                int ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
                if (ret < 0) throw new ApplicationException($"Failed to open codec: {GetErrorMessage(ret)}");

                // Allocate frame and packet structures
                _decodedFrame = ffmpeg.av_frame_alloc();
                if (_decodedFrame == null) throw new ApplicationException("Failed to allocate frame.");
                _packet = ffmpeg.av_packet_alloc();
                if (_packet == null) throw new ApplicationException("Failed to allocate packet.");

                Trace.WriteLine("PCM Audio Decoder Initialized Successfully.");
                return true;
            } catch (Exception ex) {
                Trace.WriteLine($"Error initializing PCM Audio Decoder: {ex.Message}");
                Dispose(); // Clean up partially initialized resources
                return false;
            }
        }

        /// <summary>
        /// Decodes a buffer containing raw PCM audio data.
        /// </summary>
        /// <param name="pcmData">Byte array containing raw PCM samples.</param>
        /// <param name="dataLength">Length of the valid data in the array.</param>
        /// <param name="pts">Presentation timestamp for this data (optional)  = ffmpeg.AV_NOPTS_VALUE.</param>
        public void DecodeAudioData(byte[] pcmData, int dataLength, long pts) {
            if (_disposed || _codecContext == null || pcmData == null || dataLength == 0) {
                return;
            }

            // For raw PCM, we wrap the input data directly in an AVPacket
            // The data needs to be accessible while the packet is being processed.
            GCHandle handle = GCHandle.Alloc(pcmData, GCHandleType.Pinned);
            try {
                // Ensure packet is clean before reuse
                ffmpeg.av_packet_unref(_packet);
                // Allocate buffer within the packet
                int ret = ffmpeg.av_new_packet(_packet, dataLength);
                if (ret < 0) {
                    Trace.WriteLine($"Error allocating packet buffer: {GetErrorMessage(ret)}");
                    return;
                }
                Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(), _packet->data, dataLength, dataLength);
                _packet->pts = pts;
                // _packet->dts = ffmpeg.AV_NOPTS_VALUE; // DTS often not needed for PCM

                SendPacketToDecoder(_packet);
            } catch (Exception ex) {
                Trace.WriteLine($"Error processing audio data: {ex.Message}");
            } finally {
                // Unref packet data buffer, but packet struct itself is reused
                ffmpeg.av_packet_unref(_packet);
                if (handle.IsAllocated) handle.Free();
            }

            // Try to receive any frames produced
            ReceiveDecodedFrames();
        }


        /// <summary>
        /// Sends a packet to the decoder.
        /// </summary>
        private void SendPacketToDecoder(AVPacket* packet) {
            if (_disposed || _codecContext == null) return;

            int ret = ffmpeg.avcodec_send_packet(_codecContext, packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                // Decoder needs output read before accepting more input
                // Trace.WriteLine("Audio Decoder needs output before accepting more input (EAGAIN).");
                ReceiveDecodedFrames(); // Try to receive frames
                // Retry sending the same packet
                ret = ffmpeg.avcodec_send_packet(_codecContext, packet);
            }

            if (ret < 0 && ret != ffmpeg.AVERROR_EOF) // EOF is ok
            {
                Trace.WriteLine($"Error sending audio packet to decoder: {GetErrorMessage(ret)}");
            }
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
                    Trace.WriteLine($"Error receiving audio frame from decoder: {GetErrorMessage(ret)}");
                    return; // Stop trying on error
                } else // Success! Got a frame
                  {
                    // Raise event with the decoded frame
                    OnAudioFrameDecoded(_decodedFrame);
                    // Frame is unreferenced at the start of the next loop iteration or before next receive call
                }
            } while (ret >= 0); // Continue looping as long as frames are available
        }


        /// <summary>
        /// Safely raises the AudioFrameDecoded event.
        /// </summary>
        protected virtual void OnAudioFrameDecoded(AVFrame* frame) // Renamed method
        {
            AudioFrameDecoded?.Invoke(this, new DecodedAudioFrameEventArgs(frame)); // Use renamed EventArgs
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

            Trace.WriteLine("Disposing PCM Audio Decoder...");
            if (_codecContext != null) {
                // Flush decoder
                try {
                    AVPacket* flushPacket = null; // Send null packet to flush
                    SendPacketToDecoder(flushPacket);
                    // Receive any remaining frames after flush
                    ReceiveDecodedFrames();
                } catch (Exception ex) { Trace.WriteLine($"Exception during audio decoder flush: {ex.Message}"); }


                fixed (AVCodecContext** ppCodecContext = &_codecContext) {
                    ffmpeg.avcodec_free_context(ppCodecContext);
                }
                _codecContext = null;
            }

            // No parser context for PCM

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
            Trace.WriteLine("PCM Audio Decoder Disposed.");
        }

        ~PcmAudioDecoder() {
            Dispose(false);
        }
    }
}
