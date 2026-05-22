using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen; // Requires FFmpeg.AutoGen NuGet package

namespace M2TsHandling {
    /// <summary>
    /// Represents arguments for an event carrying raw MPEG-2 Transport Stream data extracted from RTP packets.
    /// </summary>
    public class TsDataEventArgs : EventArgs {
        /// <summary>
        /// Buffer containing the raw TS data chunk (one or more 188-byte packets).
        /// A copy is made to ensure the listener doesn't interfere with the receive buffer.
        /// </summary>
        public byte[] TsData { get; }

        /// <summary>
        /// The RTP Timestamp associated with this packet.
        /// </summary>
        public uint RtpTimestamp { get; }

        public TsDataEventArgs(byte[] data, int offset, int length, uint rtpTimestamp) {
            // Create a copy of the data to pass to the event handler
            TsData = new byte[length];
            Buffer.BlockCopy(data, offset, TsData, 0, length);
            RtpTimestamp = rtpTimestamp;
        }
    }

    /// <summary>
    /// Represents arguments for an event carrying a demuxed AVPacket from FFmpeg.
    /// WARNING: The AVPacket pointer is only valid during the event handler's execution.
    /// Copy the data if you need to process it asynchronously.
    /// </summary>
    public unsafe class AvPacketEventArgs : EventArgs // Added unsafe keyword
    {
        /// <summary>
        /// Pointer to the demuxed AVPacket. Valid only during event handling.
        /// </summary>
        public AVPacket* Packet { get; }

        /// <summary>
        /// The index of the stream this packet belongs to.
        /// </summary>
        public int StreamIndex { get; }

        public AvPacketEventArgs(AVPacket* packet) {
            Packet = packet;
            StreamIndex = packet->stream_index;
        }
    }


    /// <summary>
    /// Helper class to parse basic RTP packet headers.
    /// (Adapted from previous examples)
    /// </summary>
    public class RtpPacket {
        public int Version { get; private set; }
        public bool Padding { get; private set; }
        public bool Extension { get; private set; }
        public int CSRCCount { get; private set; }
        public bool Marker { get; private set; }
        public int PayloadType { get; private set; }
        public ushort SequenceNumber { get; private set; }
        public uint Timestamp { get; private set; }
        public uint SSRC { get; private set; }
        // public byte[] Payload { get; private set; } // Avoid allocation, use offset/length
        public int HeaderLength { get; private set; }
        public int PayloadOffset { get; private set; }
        public int PayloadLength { get; private set; }


        private RtpPacket() { } // Prevent direct instantiation

        /// <summary>
        /// Parses an RTP packet from a byte buffer.
        /// NOTE: This is a basic parser. Production code should be more robust.
        /// </summary>
        /// <param name="buffer">Buffer containing the RTP packet.</param>
        /// <param name="length">Length of the data in the buffer.</param>
        /// <returns>Parsed RtpPacket or null if parsing fails.</returns>
        public static RtpPacket Parse(byte[] buffer, int length) {
            if (buffer == null || length < 12) return null; // Minimum RTP header size

            var packet = new RtpPacket();
            try {
                packet.Version = (buffer[0] >> 6) & 0x03;
                if (packet.Version != 2) { Trace.WriteLine("Error: Invalid RTP version."); return null; }

                packet.Padding = ((buffer[0] >> 5) & 0x01) != 0;
                packet.Extension = ((buffer[0] >> 4) & 0x01) != 0;
                packet.CSRCCount = buffer[0] & 0x0F;
                packet.Marker = ((buffer[1] >> 7) & 0x01) != 0;
                packet.PayloadType = buffer[1] & 0x7F;
                packet.SequenceNumber = (ushort)((buffer[2] << 8) | buffer[3]);
                packet.Timestamp = (uint)((buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7]);
                packet.SSRC = (uint)((buffer[8] << 24) | (buffer[9] << 16) | (buffer[10] << 8) | buffer[11]);

                packet.HeaderLength = 12 + packet.CSRCCount * 4;
                if (length < packet.HeaderLength) { Trace.WriteLine("Error: Packet too short for header."); return null; }

                packet.PayloadOffset = packet.HeaderLength;
                int payloadLengthWithoutPadding = length - packet.PayloadOffset;

                // Handle Extension Header (rudimentary - just calculates length to find payload start)
                if (packet.Extension) {
                    if (length < packet.PayloadOffset + 4) { Trace.WriteLine("Error: Packet too short for extension header length."); return null; }
                    // Defined By Profile field (2 bytes) + Length field (2 bytes)
                    int extensionHeaderLengthInWords = (buffer[packet.PayloadOffset + 2] << 8) | buffer[packet.PayloadOffset + 3];
                    int extensionHeaderLengthInBytes = (extensionHeaderLengthInWords * 4) + 4; // Include profile + length fields
                    if (length < packet.PayloadOffset + extensionHeaderLengthInBytes) { Trace.WriteLine("Error: Packet too short for extension header data."); return null; }
                    packet.PayloadOffset += extensionHeaderLengthInBytes;
                    payloadLengthWithoutPadding = length - packet.PayloadOffset;
                }

                // Handle Padding
                if (packet.Padding) {
                    if (payloadLengthWithoutPadding <= 0) { Trace.WriteLine("Error: Invalid padding - no payload or only padding."); return null; }
                    int paddingLength = buffer[length - 1];
                    if (paddingLength == 0 || paddingLength > payloadLengthWithoutPadding) { Trace.WriteLine($"Error: Invalid padding length {paddingLength} for payload size {payloadLengthWithoutPadding}."); return null; }
                    payloadLengthWithoutPadding -= paddingLength;
                }

                if (payloadLengthWithoutPadding < 0) { Trace.WriteLine("Error: Negative payload length after parsing."); return null; }

                packet.PayloadLength = payloadLengthWithoutPadding;

                return packet;
            } catch (Exception ex) {
                Trace.WriteLine($"Exception during RTP packet parsing: {ex.Message}");
                return null;
            }
        }
    }


    /// <summary>
    /// Handles RTP packets containing MPEG-2 Transport Stream data,
    /// queues the TS data, and uses FFmpeg (via FFmpeg.AutoGen)
    /// to demux it into elementary stream packets (AVPacket).
    /// Requires compiling with /unsafe option.
    /// </summary>
    public unsafe class MpegTsRtpHandler : IDisposable // Added unsafe keyword
    {
        // Configuration
        private readonly int _mpegTsPayloadType;
        private const byte MpegTsSyncByte = 0x47;
        private const int AvioBufferSize = 8192; // Internal buffer size for FFmpeg custom IO

        // State
        private bool _processing = false;
        private Thread _processingThread;
        private bool _disposed = false;

        // FFmpeg related fields
        private AVFormatContext* _avFormatContext = null;
        private AVIOContext* _avioContext = null;
        private byte[] _avioBuffer = null; // Managed buffer for AVIOContext
        private GCHandle _avioBufferHandle; // Handle to pin the managed buffer
        private GCHandle _thisHandle;       // Handle to pass 'this' instance to native callback

        // Use the delegate type directly from FFmpeg.AutoGen
        private avio_alloc_context_read_packet_func _readCallbackInstance; // Keep delegate alive

        // Data Queue & Signaling
        private readonly ConcurrentQueue<TsDataEventArgs> _tsDataQueue = new ConcurrentQueue<TsDataEventArgs>();
        private readonly AutoResetEvent _dataAvailableSignal = new AutoResetEvent(false);

        // Stream Info
        public int VideoStreamIndex { get; private set; } = -1;
        public int AudioStreamIndex { get; private set; } = -1;
        public AVCodecParameters* VideoCodecParameters { get; private set; } = null; // Pointer, valid until Cleanup
        public AVCodecParameters* AudioCodecParameters { get; private set; } = null; // Pointer, valid until Cleanup

        /// <summary>
        /// Event raised when a raw chunk of MPEG-TS data is extracted from an RTP packet.
        /// Primarily for internal use to feed the queue.
        /// </summary>
        private event EventHandler<TsDataEventArgs> TsPacketReceived;

        /// <summary>
        /// Event raised when an AVPacket containing video data is demuxed.
        /// WARNING: The AVPacket pointer is only valid during event handler execution.
        /// </summary>
        public event EventHandler<AvPacketEventArgs> VideoPacketDemuxed;

        /// <summary>
        /// Event raised when an AVPacket containing audio data is demuxed.
        /// WARNING: The AVPacket pointer is only valid during event handler execution.
        /// </summary>
        public event EventHandler<AvPacketEventArgs> AudioPacketDemuxed;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="mpegTsPayloadType">The RTP payload type number for MPEG-TS (e.g., 33 or the one from SDP for 'vnd.ms.wm-MPV').</param>
        public MpegTsRtpHandler(int mpegTsPayloadType) {
            _mpegTsPayloadType = mpegTsPayloadType;
            Trace.WriteLine($"MpegTsRtpHandler configured for MPEG-TS Payload Type: {mpegTsPayloadType}");

            // Subscribe internal event handler
            this.TsPacketReceived += OnTsPacketReceivedInternal;

            // Assign the callback delegate instance here using the method group directly.
            _readCallbackInstance = ReadPacketDataCallback;

            // Allocate GCHandle to pass 'this' to native code
            _thisHandle = GCHandle.Alloc(this);
        }

        /// <summary>
        /// Initializes the FFmpeg demuxer components. Must be called before starting processing.
        /// </summary>
        /// <returns>True if initialization was successful, false otherwise.</returns>
        public bool InitializeDemuxer() {
            if (_avFormatContext != null) {
                Trace.WriteLine("Demuxer already initialized.");
                return true; // Already initialized
            }

            Trace.WriteLine("Initializing FFmpeg demuxer...");

            try {
                // Allocate Format Context
                _avFormatContext = ffmpeg.avformat_alloc_context();
                if (_avFormatContext == null) throw new ApplicationException("Failed to allocate AVFormatContext.");

                // Create Custom IO Context
                _avioContext = CreateCustomIoContext(); // Can throw if allocation fails
                _avFormatContext->pb = _avioContext;
                _avFormatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO | ffmpeg.AVFMT_FLAG_NOBUFFER;

                // Hint input format
                AVInputFormat* inputFormat = ffmpeg.av_find_input_format("mpegts");
                if (inputFormat == null) throw new ApplicationException("Failed to find mpegts input format.");

                // Open Input Stream (using custom IO)
                AVDictionary* options = null;
                // Increase probe size and analysis duration for potentially sparse TS streams from RTP
                ffmpeg.av_dict_set(&options, "probesize", "2000000", 0); // Probe up to 2MB
                ffmpeg.av_dict_set(&options, "analyzeduration", "5000000", 0); // Analyze up to 5 seconds

                int ret = ffmpeg.avformat_open_input(&_avFormatContext, null, inputFormat, &options);
                ffmpeg.av_dict_free(&options); // Free options dictionary regardless of success
                if (ret < 0) throw new ApplicationException($"avformat_open_input failed: {GetErrorMessage(ret)}");

                // Find Stream Information (reads initial data via callback)
                ret = ffmpeg.avformat_find_stream_info(_avFormatContext, null);
                if (ret < 0) throw new ApplicationException($"avformat_find_stream_info failed: {GetErrorMessage(ret)}");

                // Dump format info for debugging
                ffmpeg.av_dump_format(_avFormatContext, 0, "RTP-MPEGTS-Input", 0);

                // Find streams
                FindStreamsInternal();

                Trace.WriteLine("FFmpeg demuxer initialized successfully.");
                return true;
            } catch (Exception ex) {
                Trace.WriteLine($"Error initializing FFmpeg demuxer: {ex.Message}");
                CleanupResources(); // Clean up partially initialized resources
                return false;
            }
        }

        /// <summary>
        /// Starts the background thread that demuxes the queued TS data.
        /// Requires InitializeDemuxer() to have been called successfully first.
        /// </summary>
        public void StartProcessing() {
            if (_processing) {
                Trace.WriteLine("Processing already started.");
                return;
            }
            if (_avFormatContext == null) {
                Trace.WriteLine("Error: Cannot start processing, demuxer not initialized.");
                return;
            }
            if (_disposed) {
                Trace.WriteLine("Error: Cannot start processing, handler has been disposed.");
                return;
            }

            Trace.WriteLine("Starting processing thread...");
            _processing = true;
            _processingThread = new Thread(ProcessingLoop);
            _processingThread.IsBackground = true;
            _processingThread.Name = "MpegTsDemuxerThread";
            _processingThread.Start();
        }

        /// <summary>
        /// Stops the background processing thread and waits for it to finish.
        /// </summary>
        public void StopProcessing() {
            if (!_processing) return;

            Trace.WriteLine("Stopping processing thread...");
            _processing = false;
            _dataAvailableSignal?.Set(); // Signal loop to wake up and check _processing flag
            _processingThread?.Join(); // Wait for thread to complete
            _processingThread = null;
            Trace.WriteLine("Processing thread stopped.");
        }

        /// <summary>
        /// Call this method from your RTP receiving loop whenever a UDP packet arrives.
        /// It parses the RTP header and, if it's the target payload type, queues the TS data.
        /// </summary>
        /// <param name="packetBuffer">Raw buffer containing the UDP datagram (RTP packet).</param>
        /// <param name="packetLength">Length of the data in the buffer.</param>
        public void ProcessRtpPacket(byte[] packetBuffer, int packetLength) {
            if (_disposed) return;

            // 1. Parse the RTP packet header
            RtpPacket rtpPacket = RtpPacket.Parse(packetBuffer, packetLength);
            if (rtpPacket == null) {
                // Warning logged within Parse method
                return;
            }

            // 2. Check if the payload type matches our configured MPEG-TS type
            if (rtpPacket.PayloadType == _mpegTsPayloadType) {
                // 3. Basic Validation: Check if payload has data
                if (rtpPacket.PayloadLength > 0) {
                    // Optional: Deeper check for payload length multiple of 188 or sync bytes
                    if (packetBuffer[rtpPacket.PayloadOffset] != MpegTsSyncByte) {
                        Trace.WriteLine($"Warning: RTP SN {rtpPacket.SequenceNumber} PT {_mpegTsPayloadType} payload does not start with TS Sync Byte 0x47.");
                        // Decide whether to still process it or discard
                        // return; // Example: Discard if first byte isn't sync byte
                    }
                    if (rtpPacket.PayloadLength % 188 != 0) {
                        Trace.WriteLine($"Warning: RTP SN {rtpPacket.SequenceNumber} PT {_mpegTsPayloadType} payload length {rtpPacket.PayloadLength} is not a multiple of 188.");
                        // Decide whether to still process it or discard
                    }

                    // 4. Raise the internal event to queue the data
                    // Pass the original buffer and offset/length to avoid immediate copy
                    OnTsPacketReceivedInternal(new TsDataEventArgs(packetBuffer, rtpPacket.PayloadOffset, rtpPacket.PayloadLength, rtpPacket.Timestamp));
                }
                // else: Empty payload for the TS type? Log or ignore.
            }
            // else: Packet is for a different payload type, ignore it here.
        }


        // --- Internal Methods ---

        /// <summary>
        /// Internal handler that queues the received TS data.
        /// </summary>
        private void OnTsPacketReceivedInternal(object sender, TsDataEventArgs e) {
            // The constructor of TsDataEventArgs already made a copy, so just queue it.
            _tsDataQueue.Enqueue(e);
            _dataAvailableSignal.Set(); // Signal that new data is ready for FFmpeg
        }

        /// <summary>
        /// Creates the custom AVIOContext for FFmpeg.
        /// </summary>
        private AVIOContext* CreateCustomIoContext() {
            // Allocate buffer for FFmpeg I/O context
            _avioBuffer = new byte[AvioBufferSize];
            // Pin the buffer in memory so the GC doesn't move it while FFmpeg uses it
            _avioBufferHandle = GCHandle.Alloc(_avioBuffer, GCHandleType.Pinned);

            // Assign the callback function instance (already assigned in constructor)
            // _readCallbackInstance = ReadPacketDataCallback; // Done in constructor

            AVIOContext* avioCtx;
            // Use fixed statement to get pointer safely for avio_alloc_context
            fixed (byte* pAvioBuffer = _avioBuffer) {
                // Allocate the AVIOContext using the buffer and callback
                // Pass the delegate instance directly.
                avioCtx = ffmpeg.avio_alloc_context(
                    pAvioBuffer, // Use the pointer obtained via fixed
                    AvioBufferSize,
                    0, // write_flag = 0 (false) because we are reading
                    (void*)GCHandle.ToIntPtr(_thisHandle), // Pass handle to 'this' as opaque pointer
                    _readCallbackInstance, // Pass the delegate instance directly
                    null, // write_packet callback (not needed)
                    null // seek callback (not needed for non-seekable stream)
                );
            } // pAvioBuffer goes out of scope here, but _avioBufferHandle keeps it pinned

            if (avioCtx == null) {
                if (_avioBufferHandle.IsAllocated) _avioBufferHandle.Free(); // Unpin buffer if context allocation failed
                // Also free the GCHandle for 'this' if context allocation failed early
                if (_thisHandle.IsAllocated) _thisHandle.Free();
                throw new ApplicationException("Failed to allocate AVIOContext.");
            }
            return avioCtx;
        }

        /// <summary>
        /// The custom read callback passed to FFmpeg. Called when FFmpeg needs input data.
        /// Signature must match FFmpeg.AutoGen.avio_alloc_context_read_packet_func:
        /// int Func(void* opaque, byte* buf, int buf_size)
        /// This is an INSTANCE method.
        /// </summary>
        // [UnmanagedFunctionPointer(CallingConvention.Cdecl)] // Removed this attribute
        private int ReadPacketDataCallback(void* opaque, byte* buf, int buf_size) {
            // Retrieve the instance from the opaque pointer
            GCHandle handle = GCHandle.FromIntPtr((IntPtr)opaque);
            MpegTsRtpHandler instance = (MpegTsRtpHandler)handle.Target;

            if (instance == null) // Should not happen if handle is valid
            {
                Trace.WriteLine("Error in ReadPacketDataCallback: Failed to retrieve instance from opaque handle.");
                return ffmpeg.AVERROR_EOF;
            }

            try {
                TsDataEventArgs tsDataEvent;
                // Try to get data immediately from the instance's queue
                if (!instance._tsDataQueue.TryDequeue(out tsDataEvent)) {
                    // No data currently available, wait for signal or timeout
                    // Only wait if processing is still active
                    if (instance._processing && !instance._dataAvailableSignal.WaitOne(TimeSpan.FromMilliseconds(100))) {
                        // Timeout waiting for data
                        return ffmpeg.AVERROR(ffmpeg.EAGAIN); // Signal temporary unavailability
                    }

                    // Check again after waiting or if processing was stopped
                    if (!instance._processing || !instance._tsDataQueue.TryDequeue(out tsDataEvent)) {
                        // Still no data, or processing was stopped
                        return ffmpeg.AVERROR_EOF; // Signal EOF or stop request
                    }
                }

                // We got a chunk (tsDataEvent.TsData)
                int bytesToCopy = Math.Min(tsDataEvent.TsData.Length, buf_size);
                Marshal.Copy(tsDataEvent.TsData, 0, (IntPtr)buf, bytesToCopy);

                // TODO: Handle case where tsDataEvent.TsData.Length > buf_size
                // This simple version truncates, which is bad for TS streams.
                // A better approach would buffer the remainder internally.
                if (tsDataEvent.TsData.Length > bytesToCopy) {
                    Trace.WriteLine($"Warning: TS Chunk size ({tsDataEvent.TsData.Length}) > AVIO buffer size ({buf_size}). Data truncated.");
                }

                return bytesToCopy; // Return number of bytes actually copied
            } catch (Exception ex) {
                Trace.WriteLine($"Error in ReadPacketData callback: {ex.Message}");
                return ffmpeg.AVERROR_EOF; // Signal error / EOF
            }
        }


        /// <summary>
        /// Finds video and audio stream indices after avformat_find_stream_info.
        /// </summary>
        private void FindStreamsInternal() {
            if (_avFormatContext == null) return;
            VideoStreamIndex = -1;
            AudioStreamIndex = -1;
            VideoCodecParameters = null;
            AudioCodecParameters = null;

            for (int i = 0; i < _avFormatContext->nb_streams; i++) {
                AVStream* stream = _avFormatContext->streams[i];
                AVCodecParameters* codecParams = stream->codecpar;

                // Prioritize finding the first video stream
                if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && VideoStreamIndex == -1) {
                    VideoStreamIndex = i;
                    VideoCodecParameters = codecParams; // Store pointer
                    Trace.WriteLine($"Found Video Stream Index: {i}, Codec: {ffmpeg.avcodec_get_name(codecParams->codec_id)}, Res: {codecParams->width}x{codecParams->height}");
                }
                // Prioritize finding the first audio stream
                else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && AudioStreamIndex == -1) {
                    AudioStreamIndex = i;
                    AudioCodecParameters = codecParams; // Store pointer
                    // Use ch_layout.nb_channels as requested (assuming it worked)
                    int channels = codecParams->ch_layout.nb_channels;
                    // Fallback or alternative if needed: int channels = codecParams->channels;
                    Trace.WriteLine($"Found Audio Stream Index: {i}, Codec: {ffmpeg.avcodec_get_name(codecParams->codec_id)}, SampleRate: {codecParams->sample_rate}, Channels: {channels}");
                }
            }

            if (VideoStreamIndex == -1) Trace.WriteLine("Warning: No video stream found.");
            if (AudioStreamIndex == -1) Trace.WriteLine("Warning: No audio stream found.");
        }


        /// <summary>
        /// The main loop running on a background thread that calls av_read_frame.
        /// </summary>
        private void ProcessingLoop() {
            Trace.WriteLine("Demuxing processing loop started.");
            AVPacket* packet = ffmpeg.av_packet_alloc(); // Allocate packet once

            try {
                while (_processing) {
                    // Reset packet data before reading next frame
                    ffmpeg.av_packet_unref(packet);

                    int ret = ffmpeg.av_read_frame(_avFormatContext, packet);

                    if (ret == ffmpeg.AVERROR_EOF) {
                        Trace.WriteLine("av_read_frame returned EOF.");
                        // End of stream reached according to FFmpeg
                        break; // Exit loop
                    } else if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                        // Temporarily no data available, wait and continue
                        Thread.Sleep(10); // Small sleep to avoid busy-waiting excessively
                        continue;
                    } else if (ret < 0) {
                        // Actual error
                        Trace.WriteLine($"Error during av_read_frame: {GetErrorMessage(ret)}");
                        break; // Exit loop on error
                    }

                    // Successfully read a packet - check which stream it belongs to
                    if (packet->stream_index == VideoStreamIndex) {
                        // Raise event for video packet
                        OnVideoPacketDemuxed(packet);
                    } else if (packet->stream_index == AudioStreamIndex) {
                        // Raise event for audio packet
                        OnAudioPacketDemuxed(packet);
                    }
                    // else: Packet for stream we are not interested in, ignore
                }
            } catch (Exception ex) {
                // Catch exceptions during the loop (e.g., from event handlers if not caught there)
                Trace.WriteLine($"Exception in processing loop: {ex.Message}");
            } finally {
                ffmpeg.av_packet_free(&packet); // Free the packet allocation
                _processing = false; // Ensure processing flag is false on exit
                Trace.WriteLine("Demuxing processing loop finished.");
                // Consider raising an event here to signal completion or error state
            }
        }

        // --- Event Raisers ---

        protected virtual void OnTsPacketReceivedInternal(TsDataEventArgs e) {
            TsPacketReceived?.Invoke(this, e);
        }

        protected virtual void OnVideoPacketDemuxed(AVPacket* packet) {
            try {
                VideoPacketDemuxed?.Invoke(this, new AvPacketEventArgs(packet));
            } catch (Exception ex) {
                Trace.WriteLine($"Error in VideoPacketDemuxed event handler: {ex.Message}");
            }
        }

        protected virtual void OnAudioPacketDemuxed(AVPacket* packet) {
            try {
                AudioPacketDemuxed?.Invoke(this, new AvPacketEventArgs(packet));
            } catch (Exception ex) {
                Trace.WriteLine($"Error in AudioPacketDemuxed event handler: {ex.Message}");
            }
        }


        // --- Helper & Cleanup ---

        /// <summary>
        /// Helper to get FFmpeg error messages.
        /// </summary>
        private static string GetErrorMessage(int error) {
            byte* errbuf = stackalloc byte[256]; // Allocate on stack
            ffmpeg.av_strerror(error, errbuf, 256);
            return Marshal.PtrToStringAnsi((IntPtr)errbuf);
        }

        /// <summary>
        /// Cleans up FFmpeg resources.
        /// </summary>
        private void CleanupResources() {
            Trace.WriteLine("Cleaning up MpegTsRtpHandler resources...");

            // Close input and free format context
            // avformat_close_input also frees the AVFormatContext itself
            if (_avFormatContext != null) {
                // Corrected call: pass address of the pointer field
                ffmpeg.avformat_close_input(&_avFormatContext); // Pass address of pointer
                                                                // avformat_close_input sets the pointer to NULL on success
                                                                // _avFormatContext = null; // No need to explicitly null, close_input does it via pointer.
                Trace.WriteLine("AVFormatContext closed and freed.");
            }
            _avFormatContext = null; // Ensure it's null after potential close

            // Free the AVIOContext *struct* if it wasn't freed by avformat_close_input
            // (It's safer to assume it might need separate freeing if allocated manually,
            // although avformat_close_input *should* free ->pb if AVFMT_FLAG_CUSTOM_IO is set)
            // However, avio_context_free expects a pointer-to-pointer.
            // Let's rely on avformat_close_input freeing the attached pb.
            // We MUST free the buffer we pinned, though.

            _avioContext = null; // AVIOContext struct itself is freed by avformat_close_input

            // Unpin and allow GC of the IO buffer
            if (_avioBufferHandle.IsAllocated) {
                _avioBufferHandle.Free();
                Trace.WriteLine("AVIO buffer unpinned.");
            }
            _avioBuffer = null;
            _readCallbackInstance = null; // Allow delegate GC

            // Free the GCHandle for 'this'
            if (_thisHandle.IsAllocated) {
                _thisHandle.Free();
                Trace.WriteLine("GCHandle for 'this' freed.");
            }


            Trace.WriteLine("MpegTsRtpHandler resources cleaned up.");
        }

        /// <summary>
        /// Dispose pattern implementation.
        /// </summary>
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose pattern implementation.
        /// </summary>
        protected virtual void Dispose(bool disposing) {
            if (_disposed) return;

            if (disposing) {
                // Dispose managed state (managed objects).
                StopProcessing(); // Stop thread before cleaning up native resources
                _dataAvailableSignal?.Dispose();
            }

            // Free unmanaged resources (unmanaged objects) and override finalizer
            CleanupResources(); // CleanupResources now also frees _thisHandle

            _disposed = true;
        }

        // Finalizer (just in case Dispose is not called)
        ~MpegTsRtpHandler() {
            Dispose(false);
        }
    }
}
