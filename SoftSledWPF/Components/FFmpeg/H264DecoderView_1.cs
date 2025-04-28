//using System;
//using System.Collections.Concurrent;
//using System.Runtime.InteropServices;
//using System.Threading;
//// using System.Threading.Tasks; // Not strictly needed for this version
//using System.Windows;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;
//using System.Windows.Threading; // Required for Dispatcher
//using FFmpeg.AutoGen; // Make sure you have the using statement

///// <summary>
///// Decodes raw H.264 NAL units using FFmpeg and displays them on a WriteableBitmap.
///// Assumes it receives full NAL units. Ignores timestamps and synchronization.
///// Targets C# 7.3 compatibility (removes nullable reference type annotations).
///// </summary>
//public unsafe class H264DecoderView : IDisposable {
//    private AVCodec* _pCodec = null;
//    private AVCodecContext* _pCodecContext = null;
//    private AVFrame* _pDecodedFrame = null;    // Stores the raw decoded frame (e.g., YUV)
//    private AVFrame* _pConvertedFrame = null;   // Stores the frame converted to BGRA for WPF
//    private AVPacket* _pPacket = null;         // Stores the input NAL unit
//    private SwsContext* _pSwsContext = null;     // For color space conversion (e.g., YUV -> BGRA)
//    private byte* _convertedFrameBufferPtr = null; // Pointer to the buffer allocated for _pConvertedFrame

//    // Threading and Queueing
//    private ConcurrentQueue<byte[]> _nalQueue = new ConcurrentQueue<byte[]>();
//    private Thread _decodingThread = null; // Changed from Thread?
//    private CancellationTokenSource _cancellationTokenSource = null; // Changed from CancellationTokenSource?

//    // WPF Rendering
//    private WriteableBitmap _videoSurface = null; // Changed from WriteableBitmap?
//    private int _width;
//    private int _height;
//    private readonly Dispatcher _uiDispatcher; // UI thread dispatcher, required for WriteableBitmap updates

//    /// <summary>
//    /// The ImageSource property to bind to an Image control in WPF.
//    /// </summary>
//    public ImageSource VideoSource => _videoSurface; // Changed from ImageSource?

//    /// <summary>
//    /// Creates an instance of the H264DecoderView.
//    /// </summary>
//    /// <param name="uiDispatcher">The dispatcher for the UI thread (e.g., Application.Current.Dispatcher).</param>
//    public H264DecoderView(Dispatcher uiDispatcher) {
//        if (uiDispatcher == null) throw new ArgumentNullException(nameof(uiDispatcher));
//        _uiDispatcher = uiDispatcher;
//        // Consider setting ffmpeg.RootPath here if necessary
//    }

//    /// <summary>
//    /// Initializes the FFmpeg decoder and rendering surface. Must be called before Start().
//    /// </summary>
//    /// <param name="width">Video width (obtain from SPS NAL unit).</param>
//    /// <param name="height">Video height (obtain from SPS NAL unit).</param>
//    /// <returns>True if initialization succeeded, false otherwise.</returns>
//    public bool Initialize(int width, int height) {
//        if (width <= 0 || height <= 0)
//            throw new ArgumentOutOfRangeException("Width and height must be positive.");

//        // Clean up previous instance if any
//        Dispose(); // Ensure clean state if Initialize is called multiple times

//        _width = width;
//        _height = height;

//        try {
//            // 1. Find H.264 Decoder
//            _pCodec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
//            if (_pCodec == null) throw new InvalidOperationException("H.264 decoder not found. Ensure FFmpeg binaries are accessible.");

//            // 2. Allocate Codec Context
//            _pCodecContext = ffmpeg.avcodec_alloc_context3(_pCodec);
//            if (_pCodecContext == null) throw new InvalidOperationException("Could not allocate codec context.");

//            // Set context properties if needed (e.g., dimensions, though decoder might override)
//            _pCodecContext->width = width;
//            _pCodecContext->height = height;
//            // Optional: _pCodecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

//            // 3. Open Codec
//            int ret = ffmpeg.avcodec_open2(_pCodecContext, _pCodec, null);
//            if (ret < 0) throw new InvalidOperationException($"Could not open codec: {GetErrorMessage(ret)}");

//            // 4. Allocate Packet and Frames
//            _pPacket = ffmpeg.av_packet_alloc();
//            if (_pPacket == null) throw new InvalidOperationException("Could not allocate packet.");

//            _pDecodedFrame = ffmpeg.av_frame_alloc();
//            if (_pDecodedFrame == null) throw new InvalidOperationException("Could not allocate decoded frame.");

//            _pConvertedFrame = ffmpeg.av_frame_alloc();
//            if (_pConvertedFrame == null) throw new InvalidOperationException("Could not allocate converted frame.");

//            // Get pointers to the fields within the AVFrame struct
//            byte_ptrArray8* pData = &_pConvertedFrame->data;
//            int_array8* pLinesize = &_pConvertedFrame->linesize;

//            // Cast these pointers to pointers of the type expected by the function (array of 4)
//            byte_ptrArray4* pDataAs4 = (byte_ptrArray4*)pData;
//            int_array4* pLinesizeAs4 = (int_array4*)pLinesize;


//            // 5. Setup BGRA Frame Buffer for WPF
//            var dstPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
//            int bufferSize = ffmpeg.av_image_get_buffer_size(dstPixelFormat, width, height, 1); // Use alignment 1
//            _convertedFrameBufferPtr = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
//            if (_convertedFrameBufferPtr == null) throw new InvalidOperationException("Could not allocate buffer for converted frame.");

//            // Associate the buffer with the frame structure
//            ret = ffmpeg.av_image_fill_arrays(ref *pDataAs4, ref *pLinesizeAs4, _convertedFrameBufferPtr, dstPixelFormat, width, height, 1); _pConvertedFrame->width = width;
//            _pConvertedFrame->height = height;
//            _pConvertedFrame->format = (int)dstPixelFormat;

//            // 6. Setup Software Scaler (SWS) Context for Color Conversion (deferred until first frame)
//            _pSwsContext = null;

//            // 7. Create WriteableBitmap on UI thread
//            _uiDispatcher.Invoke(() => // Use Invoke to ensure creation before Initialize returns
//            {
//                _videoSurface = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
//            });

//            // 8. Setup Threading
//            _cancellationTokenSource = new CancellationTokenSource();
//            // Pass token to thread start method parameter instead of capturing closure (slightly cleaner)
//            _decodingThread = new Thread(DecodingLoop) { IsBackground = true, Name = "H264DecodingThread" };

//            Console.WriteLine($"H264DecoderView Initialized ({width}x{height})");
//            return true;
//        } catch (Exception ex) {
//            Console.WriteLine($"Decoder Initialization failed: {ex.Message}");
//            Cleanup(); // Clean up partially allocated resources
//            return false;
//        }
//    }

//    /// <summary>
//    /// Starts the background decoding thread.
//    /// </summary>
//    public void Start() {
//        if (_decodingThread == null || _cancellationTokenSource == null) {
//            throw new InvalidOperationException("Initialize() must be called successfully before starting.");
//        }
//        // Check IsAlive before starting (though ThreadStartException would occur anyway)
//        if (!_decodingThread.IsAlive) {
//            _decodingThread.Start(_cancellationTokenSource.Token); // Pass token as parameter
//            Console.WriteLine("Decoding thread started.");
//        }
//    }

//    /// <summary>
//    /// Adds a H.264 NAL unit to the decoding queue. Can be called from any thread.
//    /// </summary>
//    /// <param name="nalData">The byte array containing the NAL unit.</param>
//    public void ReceiveNalUnit(byte[] nalData) {
//        // Keep runtime null check
//        if (nalData == null || nalData.Length == 0 || (_cancellationTokenSource != null && _cancellationTokenSource.IsCancellationRequested))
//            return;

//        // Create a copy for the queue, as the original buffer might be reused by the caller
//        byte[] nalCopy = new byte[nalData.Length];
//        Buffer.BlockCopy(nalData, 0, nalCopy, 0, nalData.Length);
//        _nalQueue.Enqueue(nalCopy);
//    }

//    /// <summary>
//    /// The main loop running on the background thread that dequeues NALs, decodes, and triggers rendering.
//    /// </summary>
//    /// <param name="cancellationTokenObj">The CancellationToken passed via Thread.Start.</param>
//    private void DecodingLoop(object cancellationTokenObj) // Changed from object?
//    {
//        // Keep runtime null check for parameter
//        if (cancellationTokenObj == null) return;
//        var cancellationToken = (CancellationToken)cancellationTokenObj;

//        Console.WriteLine("Decoding loop starting...");
//        try {
//            while (!cancellationToken.IsCancellationRequested) {
//                // Use C# 7.0 'out var' syntax
//                if (_nalQueue.TryDequeue(out byte[] nalData)) // Changed from byte[]?
//                {
//                    DecodeNalUnit(nalData, cancellationToken);
//                } else {
//                    // Queue is empty, wait a short time
//                    Thread.Sleep(5); // Adjust as needed
//                }
//            }

//            // Cancellation requested, try to flush the decoder
//            Console.WriteLine("Cancellation requested. Flushing decoder...");
//            DecodeNalUnit(null, cancellationToken); // Send null data to flush
//            Console.WriteLine("Decoder flushed.");
//        } catch (Exception ex) when (!(ex is OperationCanceledException)) {
//            Console.WriteLine($"Error in decoding loop: {ex}");
//            // Consider signaling an error state
//        } finally {
//            Console.WriteLine("Decoding loop finished.");
//        }
//    }

//    /// <summary>
//    /// Decodes a single NAL unit (or flushes if nalData is null).
//    /// </summary>
//    private void DecodeNalUnit(byte[] nalData, CancellationToken cancellationToken) // Changed from byte[]?
//    {
//        if (cancellationToken.IsCancellationRequested) return;

//        int ret;

//        // nalData can be null for flushing
//        fixed (byte* pNalData = nalData) {
//            // Reset packet for new data
//            ffmpeg.av_packet_unref(_pPacket);
//            _pPacket->data = pNalData;
//            _pPacket->size = nalData != null ? nalData.Length : 0; // Check for null
//            // Timestamps ignored for now
//            _pPacket->pts = ffmpeg.AV_NOPTS_VALUE;
//            _pPacket->dts = ffmpeg.AV_NOPTS_VALUE;

//            // Send packet to decoder
//            ret = ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket);
//            if (ret < 0 && ret != ffmpeg.AVERROR_EOF) // EOF is expected when flushing
//            {
//                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
//                    Console.WriteLine($"Error sending packet: {GetErrorMessage(ret)}");
//                }
//            }
//        }

//        // Receive and render any available frames
//        ReceiveAndRenderFrames(cancellationToken);
//    }

//    /// <summary>
//    /// Attempts to receive decoded frames from FFmpeg and triggers rendering.
//    /// </summary>
//    private void ReceiveAndRenderFrames(CancellationToken cancellationToken) {
//        // Keep runtime null check
//        if (_pCodecContext == null) return;
//        int ret;

//        // Loop to get all available frames
//        while ((ret = ffmpeg.avcodec_receive_frame(_pCodecContext, _pDecodedFrame)) >= 0) {
//            if (cancellationToken.IsCancellationRequested) break;

//            // Frame decoded successfully (usually YUV format)

//            var sourcePixelFormat = (AVPixelFormat)_pDecodedFrame->format;
//            if (_pSwsContext == null || _pDecodedFrame->width != _width || _pDecodedFrame->height != _height) {
//                if (_pSwsContext != null) ffmpeg.sws_freeContext(_pSwsContext);

//                _pSwsContext = ffmpeg.sws_getContext(
//                    _pDecodedFrame->width, _pDecodedFrame->height, sourcePixelFormat,
//                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
//                    ffmpeg.SWS_BILINEAR, null, null, null);

//                if (_pSwsContext == null) {
//                    Console.WriteLine("Could not create SWS context for format conversion.");
//                    continue;
//                }
//                Console.WriteLine($"SWS Context Initialized: {sourcePixelFormat} -> BGRA");
//            }

//            ret = ffmpeg.sws_scale(_pSwsContext,
//                                  _pDecodedFrame->data, _pDecodedFrame->linesize,
//                                  0, _pDecodedFrame->height,
//                                  _pConvertedFrame->data, _pConvertedFrame->linesize);

//            if (ret < 0) {
//                Console.WriteLine($"Error during sws_scale: {GetErrorMessage(ret)}");
//            } else {
//                RenderFrameOnUIThread(_pConvertedFrame);
//            }

//            ffmpeg.av_frame_unref(_pDecodedFrame);
//        }

//        if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF) {
//            Console.WriteLine($"Error receiving frame: {GetErrorMessage(ret)}");
//        }
//    }

//    /// <summary>
//    /// Schedules the rendering of a BGRA frame onto the WriteableBitmap via the UI dispatcher.
//    /// </summary>
//    private void RenderFrameOnUIThread(AVFrame* pBgraFrame) {
//        // Keep runtime null check
//        if (pBgraFrame == null || (_cancellationTokenSource != null && _cancellationTokenSource.IsCancellationRequested)) return;

//        IntPtr dataPtr = (IntPtr)pBgraFrame->data[0];
//        int lineSize = pBgraFrame->linesize[0];

//        _uiDispatcher.BeginInvoke(DispatcherPriority.Render, (Action)(() => {
//            // Keep runtime null checks
//            if (_videoSurface == null || (_cancellationTokenSource != null && _cancellationTokenSource.IsCancellationRequested)) return;

//            try {
//                _videoSurface.Lock();

//                IntPtr backBuffer = _videoSurface.BackBuffer;
//                int backBufferStride = _videoSurface.BackBufferStride;

//                if (lineSize == backBufferStride) {
//                    int bufferSize = lineSize * _height;
//                    Buffer.MemoryCopy(dataPtr.ToPointer(), backBuffer.ToPointer(), bufferSize, bufferSize);
//                } else {
//                    IntPtr sourceRowPtr = dataPtr;
//                    IntPtr destRowPtr = backBuffer;
//                    int bytesPerRowToCopy = Math.Min(lineSize, backBufferStride);

//                    for (int y = 0; y < _height; y++) {
//                        Buffer.MemoryCopy(sourceRowPtr.ToPointer(), destRowPtr.ToPointer(), bytesPerRowToCopy, bytesPerRowToCopy);
//                        sourceRowPtr += lineSize;
//                        destRowPtr += backBufferStride;
//                    }
//                }

//                _videoSurface.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
//            } catch (Exception ex) {
//                Console.WriteLine($"Error updating WriteableBitmap: {ex.Message}");
//            } finally {
//                _videoSurface.Unlock();
//            }
//        }));
//    }

//    /// <summary>
//    /// Stops the decoding thread and releases resources.
//    /// </summary>
//    public void Stop() {
//        Console.WriteLine("Stopping H264DecoderView...");
//        if (_cancellationTokenSource != null) {
//            _cancellationTokenSource.Cancel();
//        }

//        if (_decodingThread != null && _decodingThread.IsAlive) {
//            Console.WriteLine("Waiting for decoding thread to join...");
//            if (!_decodingThread.Join(TimeSpan.FromSeconds(2))) {
//                Console.WriteLine("Decoding thread did not join gracefully.");
//            } else {
//                Console.WriteLine("Decoding thread joined.");
//            }
//        }
//        _decodingThread = null; // Set thread reference to null after join/timeout

//        // Cleanup is called by Dispose, which Stop should ensure happens
//        Console.WriteLine("H264DecoderView stopped.");
//    }

//    /// <summary>
//    /// Releases all managed and unmanaged resources.
//    /// </summary>
//    public void Dispose() {
//        // Check cancellation source state before calling Stop again
//        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested) {
//            Stop();
//        }
//        Cleanup(); // Perform actual FFmpeg resource cleanup

//        // Dispose managed resources like CancellationTokenSource
//        if (_cancellationTokenSource != null) {
//            _cancellationTokenSource.Dispose();
//            _cancellationTokenSource = null;
//        }
//        GC.SuppressFinalize(this); // Prevent finalizer from running
//    }

//    /// <summary>
//    /// Finalizer as a safety net, though Dispose should always be called.
//    /// </summary>
//    ~H264DecoderView() {
//        Console.WriteLine("H264DecoderView finalizer called. Dispose() was likely not called.");
//        Cleanup();
//    }

//    /// <summary>
//    /// Performs the actual cleanup of FFmpeg resources.
//    /// </summary>
//    private void Cleanup() {
//        Console.WriteLine("Cleaning up FFmpeg resources...");

//        // Ensure thread isn't running if cleanup is called unexpectedly
//        // This might happen if Dispose isn't called and finalizer runs.
//        // Direct cancellation here might be needed if Stop wasn't effective.
//        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested) {
//            _cancellationTokenSource.Cancel();
//            // Don't join thread here - finalizer thread cannot reliably join other threads.
//        }

//        // Actual FFmpeg cleanup (order matters)
//        if (_pSwsContext != null) { ffmpeg.sws_freeContext(_pSwsContext); _pSwsContext = null; }
//        if (_convertedFrameBufferPtr != null) { ffmpeg.av_free(_convertedFrameBufferPtr); _convertedFrameBufferPtr = null; }

//        // Use temporary variable for freeing pointers passed by ref in C# interop
//        if (_pConvertedFrame != null) { AVFrame* frameToFree = _pConvertedFrame; ffmpeg.av_frame_free(&frameToFree); _pConvertedFrame = null; }
//        if (_pDecodedFrame != null) { AVFrame* frameToFree = _pDecodedFrame; ffmpeg.av_frame_free(&frameToFree); _pDecodedFrame = null; }
//        if (_pPacket != null) { AVPacket* packetToFree = _pPacket; ffmpeg.av_packet_free(&packetToFree); _pPacket = null; }
//        if (_pCodecContext != null) { ffmpeg.avcodec_close(_pCodecContext); AVCodecContext* contextToFree = _pCodecContext; ffmpeg.avcodec_free_context(&contextToFree); _pCodecContext = null; }

//        _pCodec = null;
//        _nalQueue = new ConcurrentQueue<byte[]>();

//        Console.WriteLine("FFmpeg cleanup complete.");
//    }

//    /// <summary>
//    /// Helper to get FFmpeg error messages.
//    /// </summary>
//    private static string GetErrorMessage(int error) {
//        int bufferSize = 1024;
//        byte* buffer = stackalloc byte[bufferSize];
//        ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
//        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Unknown error code {error}";
//    }
//}