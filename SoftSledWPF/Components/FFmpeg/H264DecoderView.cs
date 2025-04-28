//using System;
//using System.Collections.Concurrent;
//using System.Runtime.InteropServices;
//using System.Threading;
//using System.Windows;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;
//using System.Windows.Threading; // Required for Dispatcher
//using FFmpeg.AutoGen; // Make sure you have the using statement
//using System.Collections.Generic; // For List


///// <summary>
///// Decodes raw H.264 NAL units using FFmpeg (attempting hardware acceleration)
///// and displays them on a WriteableBitmap.
///// Assumes it receives full NAL units. Ignores timestamps and synchronization.
///// Targets C# 7.3 compatibility.
///// </summary>
//public unsafe class H264DecoderView : IDisposable {
//    private AVCodec* _pCodec = null;
//    private AVCodecContext* _pCodecContext = null;
//    private AVFrame* _pDecodedFrame = null;     // Stores the frame from the decoder (HW or SW)
//    private AVFrame* _pSwFrame = null;          // Stores the frame transferred to system memory if HW decoding
//    private AVFrame* _pConvertedFrame = null;   // Stores the frame converted to BGRA for WPF
//    private AVPacket* _pPacket = null;          // Stores the input NAL unit
//    private SwsContext* _pSwsContext = null;      // For color space conversion (e.g., YUV -> BGRA)
//    private byte* _convertedFrameBufferPtr = null; // Pointer to the buffer allocated for _pConvertedFrame

//    // HW Acceleration specific fields
//    private AVBufferRef* _pHWDeviceContext = null; // Reference to the HW device context
//    private AVPixelFormat _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE; // The target HW pixel format

//    // Threading and Queueing
//    private ConcurrentQueue<byte[]> _nalQueue = new ConcurrentQueue<byte[]>();
//    private Thread _decodingThread = null;
//    private CancellationTokenSource _cancellationTokenSource = null;

//    // WPF Rendering
//    private WriteableBitmap _videoSurface = null;
//    private int _width;
//    private int _height;
//    private readonly Dispatcher _uiDispatcher;

//    public ImageSource VideoSource => _videoSurface;

//    // Static delegate field to prevent GC from collecting the callback delegate
//    private static AVCodecContext_get_format _getFormatDelegate;

//    public H264DecoderView(Dispatcher uiDispatcher) {
//        if (uiDispatcher == null) throw new ArgumentNullException(nameof(uiDispatcher));
//        _uiDispatcher = uiDispatcher;
//        // Ensure FFmpeg binaries are loaded/path is set *before* calling Initialize
//        // e.g., ffmpeg.RootPath = @"C:\ffmpeg\bin";
//    }

//    /// <summary>
//    /// Tries to initialize a hardware device context for the given type.
//    /// </summary>
//    private bool TryInitializeHardwareDevice(AVHWDeviceType hwDeviceType) {

//        AVBufferRef* localHWDeviceContext = null; // Declare a local pointer variable

//        Console.WriteLine($"Attempting to initialize HW device type: {hwDeviceType}");
//        int ret = ffmpeg.av_hwdevice_ctx_create(&localHWDeviceContext, hwDeviceType, null, null, 0);

//        if (ret < 0) {
//            Console.WriteLine($"Failed to create HW device context {hwDeviceType}: {GetErrorMessage(ret)}");
//            // _pHWDeviceContext remains null (its default)
//            return false;
//        } else {
//            // Success! Assign the result from the local variable to the class field.
//            _pHWDeviceContext = localHWDeviceContext;
//            Console.WriteLine($"Successfully created HW device context for {hwDeviceType}.");
//        }

//        // Determine the corresponding hardware pixel format for this device type
//        switch (hwDeviceType) {
//            case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA:
//                _hwPixFmt = AVPixelFormat.AV_PIX_FMT_D3D11; // Note: Frame format is AV_PIX_FMT_D3D11, surface might be NV12 inside
//                // Or sometimes AV_PIX_FMT_NV12 might be directly involved depending on context setup
//                break;
//            case AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2:
//                _hwPixFmt = AVPixelFormat.AV_PIX_FMT_DXVA2_VLD;
//                break;
//            default:
//                Console.WriteLine($"Warning: Hardware pixel format not explicitly set for {hwDeviceType}");
//                // Attempt common formats? Or rely on get_format entirely.
//                _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE; // Let get_format decide
//                break;
//        }
//        Console.WriteLine($"Successfully created HW device context for {hwDeviceType}. Target HW format likely: {_hwPixFmt}");
//        return true;
//    }


//    /// <summary>
//    /// Initializes the FFmpeg decoder (trying HW accel) and rendering surface.
//    /// </summary>
//    public bool Initialize(int width, int height) {
//        if (width <= 0 || height <= 0)
//            throw new ArgumentOutOfRangeException("Width and height must be positive.");

//        Dispose(); // Ensure clean state

//        _width = width;
//        _height = height;
//        bool useHardware = false;
//        AVHWDeviceType selectedHwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

//        // --- Attempt to find HW decoder and initialize HW device ---
//        // Try D3D11VA first
//        _pCodec = ffmpeg.avcodec_find_decoder_by_name("h264_d3d11va");
//        if (_pCodec != null && TryInitializeHardwareDevice(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)) {
//            useHardware = true;
//            selectedHwType = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
//            Console.WriteLine("Using D3D11VA hardware decoder.");
//        } else {
//            // Try DXVA2 next
//            _pCodec = ffmpeg.avcodec_find_decoder_by_name("h264_dxva2");
//            if (_pCodec != null && TryInitializeHardwareDevice(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2)) {
//                useHardware = true;
//                selectedHwType = AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
//                Console.WriteLine("Using DXVA2 hardware decoder.");
//            }
//        }

//        // Fallback to CPU decoder if HW initialization failed
//        if (!useHardware || _pCodec == null) {
//            Console.WriteLine("Hardware decoder initialization failed or not found. Falling back to CPU decoder.");
//            _pCodec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
//            if (_pCodec == null) throw new InvalidOperationException("H.264 CPU decoder not found.");
//            useHardware = false; // Ensure flag is false
//            if (_pHWDeviceContext != null) // Clean up potentially partially created context
//            {
//                AVBufferRef* ctxRef = _pHWDeviceContext;
//                ffmpeg.av_buffer_unref(&ctxRef);
//                _pHWDeviceContext = null;
//            }
//            _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
//        }
//        // --- End HW Decoder Init Attempt ---

//        try {
//            _pCodecContext = ffmpeg.avcodec_alloc_context3(_pCodec);
//            if (_pCodecContext == null) throw new InvalidOperationException("Could not allocate codec context.");

//            _pCodecContext->width = width;
//            _pCodecContext->height = height;

//            // Inside Initialize method:
//            if (useHardware) {
//                // Assign the get_format callback delegate instance directly
//                // Store the delegate in a static field first to prevent garbage collection
//                _getFormatDelegate = new AVCodecContext_get_format(CodecGetFormatCallback);
//                // Corrected Line ~146
//                _pCodecContext->get_format = _getFormatDelegate; // Assign delegate directly

//                // Assign the hardware device context to the codec context
//                _pCodecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_pHWDeviceContext);
//                if (_pCodecContext->hw_device_ctx == null)
//                    throw new InvalidOperationException("Could not reference hardware device context.");
//            }

//            int ret = ffmpeg.avcodec_open2(_pCodecContext, _pCodec, null);
//            if (ret < 0) throw new InvalidOperationException($"Could not open codec: {GetErrorMessage(ret)}");

//            _pPacket = ffmpeg.av_packet_alloc();
//            _pDecodedFrame = ffmpeg.av_frame_alloc();
//            _pConvertedFrame = ffmpeg.av_frame_alloc();
//            _pSwFrame = ffmpeg.av_frame_alloc(); // Allocate frame for SW copy if using HW

//            if (_pPacket == null || _pDecodedFrame == null || _pConvertedFrame == null || _pSwFrame == null)
//                throw new InvalidOperationException("Could not allocate packet or frames.");


//            // Setup BGRA frame buffer (same as before)
//            var dstPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
//            int bufferSize = ffmpeg.av_image_get_buffer_size(dstPixelFormat, width, height, 1);
//            _convertedFrameBufferPtr = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
//            if (_convertedFrameBufferPtr == null) throw new InvalidOperationException("Could not allocate buffer for converted frame.");

//            byte_ptrArray8* pData = &_pConvertedFrame->data;
//            int_array8* pLinesize = &_pConvertedFrame->linesize;
//            byte_ptrArray4* pDataAs4 = (byte_ptrArray4*)pData;
//            int_array4* pLinesizeAs4 = (int_array4*)pLinesize;
//            ret = ffmpeg.av_image_fill_arrays(ref *pDataAs4, ref *pLinesizeAs4, _convertedFrameBufferPtr, dstPixelFormat, width, height, 1);
//            if (ret < 0) throw new InvalidOperationException($"Could not fill image arrays: {GetErrorMessage(ret)}");

//            _pConvertedFrame->width = width;
//            _pConvertedFrame->height = height;
//            _pConvertedFrame->format = (int)dstPixelFormat;

//            _pSwsContext = null; // Initialize SwsContext later based on input frame format

//            _uiDispatcher.Invoke(() => {
//                _videoSurface = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
//            });

//            _cancellationTokenSource = new CancellationTokenSource();
//            _decodingThread = new Thread(DecodingLoop) { IsBackground = true, Name = "H264DecodingThread" };

//            Console.WriteLine($"H264DecoderView Initialized ({width}x{height}). Hardware Acceleration: {useHardware} ({selectedHwType})");
//            return true;
//        } catch (Exception ex) {
//            Console.WriteLine($"Decoder Initialization failed: {ex.Message}");
//            Cleanup();
//            return false;
//        }
//    }

//    /// <summary>
//    /// Callback function for the decoder to select a pixel format.
//    /// Prioritizes the hardware format associated with the configured device.
//    /// </summary>
//    //[AOT.MonoPInvokeCallback(typeof(AVCodecContext_get_format))] // Important for AOT/IL2CPP if used, good practice anyway
//    private static AVPixelFormat CodecGetFormatCallback(AVCodecContext* s, AVPixelFormat* fmt) {
//        // We need to find the desired HW format (_hwPixFmt) from the instance.
//        // Accessing instance members from a static callback is tricky without passing 'this' via opaque.
//        // Workaround: Iterate and find the *first* supported HW format matching known types.
//        // A more robust solution might involve AVCodecContext->opaque pointer.

//        Console.WriteLine("get_format called by decoder.");
//        AVPixelFormat targetHwFormat = AVPixelFormat.AV_PIX_FMT_NONE;

//        // Determine potential target HW format based on device context if available
//        // This is still indirect, ideally we'd get _hwPixFmt from the instance
//        if (s->hw_device_ctx != null) {
//            AVHWDeviceContext* hwCtx = (AVHWDeviceContext*)s->hw_device_ctx->data;
//            if (hwCtx->type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
//                targetHwFormat = AVPixelFormat.AV_PIX_FMT_D3D11; // Match D3D11VA
//            else if (hwCtx->type == AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2)
//                targetHwFormat = AVPixelFormat.AV_PIX_FMT_DXVA2_VLD; // Match DXVA2
//                                                                     // Add other types if needed (QSV, CUDA etc)
//            Console.WriteLine($"  Targeting HW format based on context: {targetHwFormat}");
//        }


//        for (AVPixelFormat* p = fmt; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++) {
//            Console.WriteLine($"  Decoder supports format: {*p}");
//            // Check if this format matches our desired hardware format
//            if (*p == targetHwFormat && targetHwFormat != AVPixelFormat.AV_PIX_FMT_NONE) {
//                Console.WriteLine($"  Found matching HW format: {*p}. Selecting it.");
//                return *p; // Found the preferred HW format
//            }
//             // Additional check for common HW formats if target wasn't determined precisely
//             else if (targetHwFormat == AVPixelFormat.AV_PIX_FMT_NONE) // If context check failed
//             {
//                if (*p == AVPixelFormat.AV_PIX_FMT_D3D11 || *p == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD) {
//                    Console.WriteLine($"  Found suitable general HW format: {*p}. Selecting it.");
//                    return *p;
//                }
//            }
//        }

//        Console.WriteLine("  No suitable HW format found. Falling back to default (likely first software format).");
//        // If no hardware format found, FFmpeg usually defaults to the first format in the list
//        return fmt[0];
//    }


//    // Start() remains the same
//    public void Start() {
//        if (_decodingThread == null || _cancellationTokenSource == null) {
//            throw new InvalidOperationException("Initialize() must be called successfully before starting.");
//        }
//        if (!_decodingThread.IsAlive) {
//            _decodingThread.Start(_cancellationTokenSource.Token);
//            Console.WriteLine("Decoding thread started.");
//        }
//    }

//    // ReceiveNalUnit() remains the same
//    public void ReceiveNalUnit(byte[] nalData) {
//        if (nalData == null || nalData.Length == 0 || (_cancellationTokenSource != null && _cancellationTokenSource.IsCancellationRequested))
//            return;
//        byte[] nalCopy = new byte[nalData.Length];
//        Buffer.BlockCopy(nalData, 0, nalCopy, 0, nalData.Length);
//        _nalQueue.Enqueue(nalCopy);
//    }

//    // DecodingLoop() remains the same structure
//    private void DecodingLoop(object cancellationTokenObj) {
//        if (cancellationTokenObj == null) return;
//        var cancellationToken = (CancellationToken)cancellationTokenObj;
//        Console.WriteLine("Decoding loop starting...");
//        try { /* ... same loop structure ... */ } catch (Exception ex) when (!(ex is OperationCanceledException)) { /* ... */ } finally { Console.WriteLine("Decoding loop finished."); }
//    }


//    // DecodeNalUnit() remains the same structure
//    private void DecodeNalUnit(byte[] nalData, CancellationToken cancellationToken) {
//        /* ... same structure sending packet ... */
//        ReceiveAndRenderFrames(cancellationToken);
//    }


//    /// <summary>
//    /// Attempts to receive decoded frames from FFmpeg, handles HW frame transfer,
//    /// converts to BGRA, and triggers rendering.
//    /// </summary>
//    private void ReceiveAndRenderFrames(CancellationToken cancellationToken) {
//        if (_pCodecContext == null) return;
//        int ret;

//        while ((ret = ffmpeg.avcodec_receive_frame(_pCodecContext, _pDecodedFrame)) >= 0) {
//            if (cancellationToken.IsCancellationRequested) break;

//            AVFrame* pFrameToConvert = null; // This will point to the frame in system memory (YUV/NV12 etc.)

//            // Check if the decoded frame is a hardware frame
//            if (_pDecodedFrame->format == (int)_hwPixFmt && _hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE) {
//                Console.WriteLine($"Received HW Frame (Format: {(AVPixelFormat)_pDecodedFrame->format})");
//                // Transfer data from HW surface to SW frame (_pSwFrame)
//                ret = ffmpeg.av_hwframe_transfer_data(_pSwFrame, _pDecodedFrame, 0);
//                if (ret < 0) {
//                    Console.WriteLine($"Error transferring HW frame data to system memory: {GetErrorMessage(ret)}");
//                    ffmpeg.av_frame_unref(_pDecodedFrame); // Unref the HW frame
//                    continue; // Skip this frame
//                }
//                // Data is now in _pSwFrame in a software format (e.g., NV12 or YUV420P)
//                pFrameToConvert = _pSwFrame;
//                // _pSwFrame->format should now be a software format
//                Console.WriteLine($"  HW Frame transferred to SW Frame (Format: {(AVPixelFormat)_pSwFrame->format})");
//            } else {
//                Console.WriteLine($"Received SW Frame (Format: {(AVPixelFormat)_pDecodedFrame->format})");
//                // It's already a software frame (or HW accel failed/not used)
//                pFrameToConvert = _pDecodedFrame;
//            }

//            // Ensure we have a frame to convert
//            if (pFrameToConvert == null || pFrameToConvert->data[0] == null) {
//                Console.WriteLine("Frame to convert is null or has no data.");
//                ffmpeg.av_frame_unref(_pDecodedFrame); // Unref original frame if not used
//                                                       // pFrameToConvert might be _pSwFrame which doesn't need unref here yet
//                continue;
//            }


//            // -- Initialize or update SwsContext based on the *software* frame format --
//            var sourcePixelFormat = (AVPixelFormat)pFrameToConvert->format;
//            // Check if width/height have changed (unlikely here but good practice)
//            int currentWidth = pFrameToConvert->width;
//            int currentHeight = pFrameToConvert->height;


//            if (_pSwsContext == null || currentWidth != _width || currentHeight != _height) {
//                if (_pSwsContext != null) ffmpeg.sws_freeContext(_pSwsContext);

//                // Update dimensions if they changed (relevant if resolution changes mid-stream)
//                _width = currentWidth;
//                _height = currentHeight;

//                _pSwsContext = ffmpeg.sws_getContext(
//                    _width, _height, sourcePixelFormat, // Source format from SW frame
//                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA, // Destination BGRA
//                    ffmpeg.SWS_BILINEAR, null, null, null);

//                if (_pSwsContext == null) {
//                    Console.WriteLine($"Could not create SWS context for format {sourcePixelFormat}.");
//                    ffmpeg.av_frame_unref(_pDecodedFrame);
//                    // No need to unref _pSwFrame specifically here as it's reused
//                    continue;
//                }
//                Console.WriteLine($"SWS Context Initialized/Updated: {sourcePixelFormat} -> BGRA");
//            }
//            // -- End SwsContext Update --

//            // Perform color conversion using the software frame
//            ret = ffmpeg.sws_scale(_pSwsContext,
//                                  pFrameToConvert->data, pFrameToConvert->linesize,
//                                  0, currentHeight, // Use currentHeight from frame
//                                  _pConvertedFrame->data, _pConvertedFrame->linesize);

//            if (ret < 0) {
//                Console.WriteLine($"Error during sws_scale: {GetErrorMessage(ret)}");
//            } else {
//                // Render the converted BGRA frame
//                RenderFrameOnUIThread(_pConvertedFrame);
//            }

//            // Unreference the *original* decoded frame (whether HW or SW)
//            ffmpeg.av_frame_unref(_pDecodedFrame);
//            // We don't unref _pSwFrame here because it gets reused after av_hwframe_transfer_data
//        }

//        // Check for errors other than EAGAIN/EOF
//        if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF) {
//            Console.WriteLine($"Error receiving frame: {GetErrorMessage(ret)}");
//        }
//    }


//    // RenderFrameOnUIThread() remains the same
//    private void RenderFrameOnUIThread(AVFrame* pBgraFrame) { /* ... same implementation ... */ }

//    // Stop() remains the same
//    public void Stop() { /* ... same implementation ... */ }

//    // Dispose() remains the same
//    public void Dispose() { /* ... same implementation ... */ }

//    // Finalizer remains the same
//    ~H264DecoderView() { /* ... same implementation ... */ }


//    /// <summary>
//    /// Performs the actual cleanup of FFmpeg resources, including HW context.
//    /// </summary>
//    private void Cleanup() {
//        Console.WriteLine("Cleaning up FFmpeg resources...");
//        // Order: Free contexts/converters first, then frames/packets, then codec context, then HW context.

//        if (_pSwsContext != null) { ffmpeg.sws_freeContext(_pSwsContext); _pSwsContext = null; }
//        if (_convertedFrameBufferPtr != null) { ffmpeg.av_free(_convertedFrameBufferPtr); _convertedFrameBufferPtr = null; }

//        // Free the frame structures
//        if (_pConvertedFrame != null) { AVFrame* frameToFree = _pConvertedFrame; ffmpeg.av_frame_free(&frameToFree); _pConvertedFrame = null; }
//        if (_pSwFrame != null) { AVFrame* frameToFree = _pSwFrame; ffmpeg.av_frame_free(&frameToFree); _pSwFrame = null; } // Clean up SW frame
//        if (_pDecodedFrame != null) { AVFrame* frameToFree = _pDecodedFrame; ffmpeg.av_frame_free(&frameToFree); _pDecodedFrame = null; }

//        // Free the packet structure
//        if (_pPacket != null) { AVPacket* packetToFree = _pPacket; ffmpeg.av_packet_free(&packetToFree); _pPacket = null; }

//        // Close and free the codec context (this should internally unref hw_device_ctx)
//        if (_pCodecContext != null) {
//            ffmpeg.avcodec_close(_pCodecContext);
//            AVCodecContext* contextToFree = _pCodecContext;
//            ffmpeg.avcodec_free_context(&contextToFree);
//            _pCodecContext = null; // Important to null check before freeing HW device context
//        }

//        // Unreference and free the HW device context *after* freeing the codec context that references it
//        if (_pHWDeviceContext != null) {
//            AVBufferRef* ctxRef = _pHWDeviceContext;
//            ffmpeg.av_buffer_unref(&ctxRef); // This decrements ref count, freeing if it reaches 0
//            _pHWDeviceContext = null;
//        }


//        _pCodec = null;
//        _nalQueue = new ConcurrentQueue<byte[]>();

//        Console.WriteLine("FFmpeg cleanup complete.");
//    }


//    // GetErrorMessage() remains the same
//    private static string GetErrorMessage(int error) {
//        int bufferSize = 1024;
//        byte* buffer = stackalloc byte[bufferSize];
//        ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
//        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Unknown error code {error}";
//    }
//}