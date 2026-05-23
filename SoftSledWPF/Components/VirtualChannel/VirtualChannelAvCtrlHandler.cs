using SoftSled.Components.Communication;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using SoftSled.Components.RTSP;
using SoftSled.Components.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SoftSled.Components.VirtualChannel {
    class VirtualChannelAvCtrlHandler {

        private Logger m_logger;

        public event EventHandler<VirtualChannelSendArgs> VirtualChannelSend;

        /// <summary>
        /// Fired (off the VC thread, on whichever thread RTSPClient is on)
        /// when the MPEG-ES video pipeline has been constructed and is ready
        /// to be opened by FFME. The MainWindow subscribes and dispatches the
        /// Media.Open call to the WPF thread.
        /// </summary>
        public event Action<Unosquare.FFME.Common.IMediaInputStream> VideoPipelineReady;

        /// <summary>
        /// Fired when an audio-only FFME pipeline is ready (currently the
        /// X-WMF-PF raw-PCM path). The MainWindow opens a *second* FFME
        /// MediaElement on this stream so audio + video render in parallel
        /// — same FFME backend for both, NAudio reserved for the WMC UI
        /// fast-path audio.
        /// </summary>
        public event Action<Unosquare.FFME.Common.IMediaInputStream> AudioPipelineReady;

        /// <summary>
        /// Fired when CloseMedia / Stop tears down the RTSP session — the
        /// host should close the FFME element so the producer (which has
        /// just been Completed by RTSPClient) can drain cleanly.
        /// </summary>
        public event Action VideoPipelineClosed;

        /// <summary>
        /// Fired when DMCT OpenMedia arrives, carrying the integer Surface
        /// ID from the request payload. In GDI mode the host can ignore
        /// the value (single surface); in RUI mode it's used to look the
        /// surface up in the Splash MS-RRSP2 registry so the video element
        /// can be positioned over the matching screen rectangle.
        /// </summary>
        public event Action<int /*surfaceId*/> VideoSurfaceRequested;

        /// <summary>
        /// Optional bridge to the actual playback engine (FFME wrapped in
        /// <see cref="FfmeMediaController"/>). When set, GetPosition /
        /// GetDuration / Play / Pause / Stop all defer to this controller
        /// for their data and side-effects instead of the legacy stopwatch
        /// estimate. Stays null on audio-only RFC-2250 MP3 sessions (no
        /// video pipeline → no FFME → stopwatch fallback path retained
        /// unchanged).
        ///
        /// Setting this property also subscribes our event handlers so
        /// FFME's BufferingEnded / MediaEnded / MediaFailed are translated
        /// to outbound DMCT <c>OnMediaEvent</c> messages
        /// (BUFFERING_STOP / END_OF_MEDIA / PTS_ERROR respectively), and
        /// the new RtspDisconnected / PtsError / UnrecoverableSkew events
        /// translate to RTSP_DISCONNECT / PTS_ERROR / UNRECOVERABLE_SKEW
        /// per [MS-DMCT] 2.2.2.1.2.3 / .4 / .5.
        /// </summary>
        public SoftSled.Components.AudioVisual.IMediaController MediaController {
            get => _mediaController;
            set {
                if (ReferenceEquals(_mediaController, value)) return;
                if (_mediaController != null) {
                    _mediaController.BufferingEnded     -= OnControllerBufferingEnded;
                    _mediaController.MediaEnded         -= OnControllerMediaEnded;
                    _mediaController.MediaFailed        -= OnControllerMediaFailed;
                    _mediaController.RtspDisconnected   -= OnControllerRtspDisconnected;
                    _mediaController.PtsError           -= OnControllerPtsError;
                    _mediaController.UnrecoverableSkew  -= OnControllerUnrecoverableSkew;
                }
                _mediaController = value;
                if (_mediaController != null) {
                    _mediaController.BufferingEnded     += OnControllerBufferingEnded;
                    _mediaController.MediaEnded         += OnControllerMediaEnded;
                    _mediaController.MediaFailed        += OnControllerMediaFailed;
                    _mediaController.RtspDisconnected   += OnControllerRtspDisconnected;
                    _mediaController.PtsError           += OnControllerPtsError;
                    _mediaController.UnrecoverableSkew  += OnControllerUnrecoverableSkew;
                }
            }
        }
        private SoftSled.Components.AudioVisual.IMediaController _mediaController;

        private void OnControllerBufferingEnded() {
            m_logger?.LogInfo("AVCTRL: BufferingEnded → emitting BUFFERING_STOP");
            OnMediaEvent(MediaEvent.BUFFERING_STOP, /*errorCode:*/ 0);
        }
        private void OnControllerMediaEnded() {
            m_logger?.LogInfo("AVCTRL: MediaEnded → emitting END_OF_MEDIA");
            OnMediaEvent(MediaEvent.END_OF_MEDIA, /*errorCode:*/ 0);
        }
        private void OnControllerMediaFailed(Exception ex) {
            // FFME pipeline crash (decoder error, demuxer failure, etc.).
            // Distinct from RTSP socket failure (handled below). Maps to
            // PTS_ERROR per the original behaviour — the spec doesn't
            // distinguish "decoder crashed" from "PTS-jumped" so PTS_ERROR
            // is the closest fit.
            int errorCode = unchecked((int)0x80004005);  // E_FAIL — generic
            m_logger?.LogError($"AVCTRL: MediaFailed → emitting PTS_ERROR " +
                               $"(errorCode=0x{errorCode:X8}): {ex?.Message}");
            OnMediaEvent(MediaEvent.PTS_ERROR, errorCode);
        }
        private void OnControllerRtspDisconnected(Exception ex) {
            // MS-DMCT 2.2.2.1.2.3 — RTSP socket dropped, server BYE'd a
            // dependent stream, or keepalive missed twice. WMC reads this
            // as "stream is gone, abandon playback".
            int hr = ex is System.Net.Sockets.SocketException
                     ? unchecked((int)0x80072746)  /* WSAECONNRESET-ish */
                     : unchecked((int)0x80004005);
            m_logger?.LogError($"AVCTRL: RtspDisconnected → emitting RTSP_DISCONNECT " +
                               $"(errorCode=0x{hr:X8}): {ex?.Message}");
            OnMediaEvent(MediaEvent.RTSP_DISCONNECT, hr);
        }
        private void OnControllerPtsError(SoftSled.Components.AudioVisual.PtsErrorInfo info) {
            // MS-DMCT 2.2.2.1.2.4 — per-stream PTS jumped outside the
            // -200ms..+2000ms tolerance window. Spec doesn't define a
            // payload error code; pass 0 and let the event itself signal
            // the threshold breach.
            m_logger?.LogDebug($"AVCTRL: PtsError → emitting PTS_ERROR " +
                               $"({(info.IsAudio?"audio":"video")} delta={info.DeltaMs}ms)");
            OnMediaEvent(MediaEvent.PTS_ERROR, 0);
        }
        private void OnControllerUnrecoverableSkew(SoftSled.Components.AudioVisual.SkewInfo info) {
            // MS-DMCT 2.2.2.1.2.5 — decoder open >500ms, or first-sample
            // A/V skew >3500ms after seek. Either is a hard "extender
            // can't keep up" signal.
            m_logger?.LogError($"AVCTRL: UnrecoverableSkew → emitting UNRECOVERABLE_SKEW " +
                               $"({info.TheCause}, value={info.ValueMs}ms)");
            OnMediaEvent(MediaEvent.UNRECOVERABLE_SKEW, 0);
        }

        private RTSPClient rtspClient;

        private int DMCTServiceHandle;
        private int DSPAServiceHandle;
        private int DRMRIServiceHandle;
        private int DMCTStubRequestCookie = 14724;
        private string DMCTOpenMediaURL;
        private int PreviousVolume = -1;
        private int StubRequestHandleIter = 1;
        private int StubServiceHandleIter = 1;
        private Dictionary<int, StubRequestType> StubRequestTypeDict = new Dictionary<int, StubRequestType>();
        private Dictionary<StubService, int> StubServiceHandleDict = new Dictionary<StubService, int>();
        private Dictionary<int, int> StubRequestCookieDict = new Dictionary<int, int>();
        private Dictionary<int, int> ProxyRequestHandleDict = new Dictionary<int, int>();
        private Dictionary<int, int> ProxyServiceHandleDict = new Dictionary<int, int>();

        // --- Playback position state ---
        //
        // [MS-DMCT] has TWO different time units to be careful about:
        //
        //   * Start Time (Start request input, §2.2.1.3.1) is in
        //     MILLISECONDS. A sentinel value of 0xFFFFFFFFFFFFFFFF means
        //     "resume from current position" / "I don't know".
        //
        //   * Duration (GetDuration response, §2.2.1.5) and Position
        //     (GetPosition response, §2.2.1.6) are in units of
        //     10 MILLISECONDS. So a 2-hour duration appears on the wire
        //     as 720,000, NOT 7,200,000. The unit mismatch made our
        //     previous reports run at 10× real time.
        //
        // We keep everything internally in milliseconds (TimeSpan-native);
        // the divide-by-10 happens only at the wire-packing call sites
        // (search for "/* /10 for 10ms units */" in this file).
        //
        // The legacy stopwatch fallback has been removed — position now
        // tracks the FFME MediaController's actual decoder clock. If the
        // controller isn't available yet (very early in the session),
        // we just report _playbackStartPositionMs unchanged.
        private long _playbackStartPositionMs;   // StartPayloadStartTime from WMC (0 for fresh play)
        private const long DefaultDurationMs = 2L * 60L * 60L * 1000L; // 2 h fallback = 7,200,000 ms

        // MS-DMCT sentinel: when the WMC Start request sets Start Time to
        // this value, it means "I don't know — resume from the current
        // playback position rather than seeking". We treat it as zero so
        // GetPosition just returns whatever the controller reports.
        private const long StartTimeSentinelResume = unchecked((long)0xFFFFFFFFFFFFFFFFUL);

        private static Guid RegisterTransmitterServiceClassID = new Guid("b707af79-ca99-42d1-8c60-469fe112001e");
        private static Guid RegisterTransmitterServiceServiceID = new Guid("acb96f70-e61f-45cb-9745-86c47dcbb156");

        public VirtualChannelAvCtrlHandler(Logger m_logger) {
            this.m_logger = m_logger;
        }


        public void ProcessData(byte[] incomingBuff) {

            // Convert the incoming data to bytes
            //byte[] incomingBuff = Encoding.Unicode.GetBytes(data);

            // Get DSLR Dispatcher Data
            int dispatchPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 0);
            int dispatchChildCount = DataUtilities.Get2ByteInt(incomingBuff, 4);
            int dispatchCallingConvention = DataUtilities.Get4ByteInt(incomingBuff, 6);
            int dispatchRequestHandle = DataUtilities.Get4ByteInt(incomingBuff, 10);
            byte[] dispatchRequestHandleArray = DataUtilities.GetByteSubArray(incomingBuff, 10, 4);

            // DEBUG PURPOSES ONLY
            string byteArray = "";
            foreach (byte b in incomingBuff) {
                byteArray += b.ToString("X2") + " ";
            }
            // DEBUG PURPOSES ONLY


            if (dispatchCallingConvention == 1) {

                int dispatchServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 14);
                int dispatchFunctionHandle = DataUtilities.Get4ByteInt(incomingBuff, 18);

                //Debug.WriteLine("AVCTRL: Bytes: " + BitConverter.ToString(incomingBuff));

                //// DEBUG PURPOSES ONLY
                //Debug.WriteLine("");
                //Debug.WriteLine("--------------------");
                //Debug.WriteLine($"AVCTRL ITER RECEIVED: {dispatchRequestHandle}");
                //Debug.WriteLine($"AVCTRL ITER BYTES RECEIVED: {dispatchRequestHandleArray[0]} {dispatchRequestHandleArray[1]} {dispatchRequestHandleArray[2]} {dispatchRequestHandleArray[3]}");
                //Debug.WriteLine($"ServiceHandle: {dispatchServiceHandle}");
                //Debug.WriteLine($"FunctionHandle: {dispatchFunctionHandle}");

                // Service Handle = Dispenser
                if (dispatchServiceHandle == 0) {

                    #region DSLR Service ##########################################

                    // CreateService Request
                    if (dispatchFunctionHandle == 0) {

                        // Get CreateService Data
                        int createServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int createServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid createServiceClassID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        Guid createServiceServiceID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);
                        int createServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16 + 16);

                        

                        switch (createServiceClassID.ToString().ToLower()) {
                            // DMCT ClassID
                            case "18c7c708-c529-4639-a846-5847f31b1e83":
                                DMCTServiceHandle = createServiceServiceHandle;
                                m_logger?.LogDebug($"AVCTRL: CreateService DMCT  ({DMCTServiceHandle})");
                                break;
                            // DSPA ClassID
                            case "077bfd3a-7028-4913-bd14-53963dc37754":
                                DSPAServiceHandle = createServiceServiceHandle;
                                m_logger?.LogDebug($"AVCTRL: CreateService DSPA ({DSPAServiceHandle})");
                                break;
                            // DRMRI ClassID
                            case "b707af79-ca99-42d1-8c60-469fe112001e":
                                DRMRIServiceHandle = createServiceServiceHandle;
                                m_logger?.LogDebug($"AVCTRL: CreateService DRMRI ({DRMRIServiceHandle})");
                                break;
                            default:
                                m_logger?.LogDebug($"AVCTRL: CreateService ClassID {createServiceClassID} with ServiceID {createServiceServiceID} not available");
                                break;
                        }

                        // Initialise CreateService Response
                        byte[] response = DSLRCommunication.CreateServiceResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // DeleteService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get DeleteService Data
                        int deleteServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int deleteServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int deleteServiceServiceHandle = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        // If this is the DMCT Service
                        if (deleteServiceServiceHandle == DMCTServiceHandle) {
                        m_logger?.LogDebug($"MCXSESS: DeleteService DMCT ({DMCTServiceHandle})");
                            // Clear the DMCT Service
                            DMCTServiceHandle = 0;
                        }
                        // If this is the DSPA Service
                        else if (deleteServiceServiceHandle == DSPAServiceHandle) {
                        m_logger?.LogDebug($"MCXSESS: DeleteService DSPA ({DSPAServiceHandle})");
                            // Clear the DSPA Service
                            DSPAServiceHandle = 0;
                        }
                        // If this is the DRMRI Service
                        else if (deleteServiceServiceHandle == DRMRIServiceHandle) {
                        m_logger?.LogDebug($"MCXSESS: DeleteService DRMRI ({DRMRIServiceHandle})");
                            // Clear the DRMRI Service
                            DRMRIServiceHandle = 0;
                        }

                        // Initialise DeleteService Response
                        byte[] response = DSLRCommunication.DeleteServiceResponse(
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CreateService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger?.LogDebug($"Unknown DSLR Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                }
                // DMCT Service Handle
                else if (dispatchServiceHandle == DMCTServiceHandle) {

                    #region DMCT Service ##########################################

                    // OpenMedia Request
                    if (dispatchFunctionHandle == 0) {

                        // Get OpenMedia Data
                        int OpenMediaPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int OpenMediaChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int OpenMediaPayloadURLLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string OpenMediaPayloadURL = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, OpenMediaPayloadURLLength);
                        int OpenMediaPayloadSurfaceID = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + OpenMediaPayloadURLLength);
                        int OpenMediaPayloadTimeOut = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + OpenMediaPayloadURLLength + 4);

                        m_logger?.LogDebug($"AVCTRL: OpenMedia ({OpenMediaPayloadURL})");

                        DMCTOpenMediaURL = OpenMediaPayloadURL;
                        Debug.WriteLine(DMCTOpenMediaURL);

                        rtspClient = new RTSPClient();
                        // Forward the wm-MPV video pipeline event to the host
                        // (MainWindow) so FFME can open the input stream once
                        // SDP processing identifies an MPEG-ES video PT.
                        rtspClient.VideoPipelineReady += stream => {
                            try { VideoPipelineReady?.Invoke(stream); }
                            catch (Exception ex) {
                                m_logger?.LogError($"AVCTRL: VideoPipelineReady handler threw: {ex.Message}");
                            }
                        };
                        rtspClient.AudioPipelineReady += stream => {
                            try { AudioPipelineReady?.Invoke(stream); }
                            catch (Exception ex) {
                                m_logger?.LogError($"AVCTRL: AudioPipelineReady handler threw: {ex.Message}");
                            }
                        };

                        // Bind the new RTSP session to the controller so
                        // SeekAsync / SetRateAsync / SetAvailableBandwidth
                        // forward to the right place, and so RTSP's spec-
                        // events propagate as controller events (and from
                        // there as DMCT OnMediaEvent codes).
                        try { _mediaController?.AttachRtspClient(rtspClient); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: AttachRtspClient(open) threw: {ex.Message}");
                        }

                        // Tell the host which surface WMC wants the video
                        // on. In GDI mode this is ignored (single surface);
                        // in RUI mode SurfaceRouter looks it up in the
                        // Splash registry and positions Media accordingly.
                        try { VideoSurfaceRequested?.Invoke(OpenMediaPayloadSurfaceID); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: VideoSurfaceRequested handler threw: {ex.Message}");
                        }

                        rtspClient.Connect(DMCTOpenMediaURL, RTSPClient.RTP_TRANSPORT.UDP, RTSPClient.MEDIA_REQUEST.VIDEO_AND_AUDIO);

                        // Initialise OpenMedia Response
                        byte[] response = DSLRCommunication.OpenMediaResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the SetDWORDProperty Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // CloseMedia Request
                    else if (dispatchFunctionHandle == 1) {

                        m_logger?.LogDebug("AVCTRL: CloseMedia");

                        // Reset playback position tracking on CloseMedia.
                        _playbackStartPositionMs = 0;

                        // Detach the live RTSP session from the controller
                        // BEFORE stopping it, so any final RTSP exceptions
                        // raised by Stop() don't propagate as
                        // RTSP_DISCONNECT (we're closing on purpose).
                        try { _mediaController?.AttachRtspClient(null); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: AttachRtspClient(close) threw: {ex.Message}");
                        }

                        // Signal the host to close FFME BEFORE we tear down
                        // RTSPClient — RTSPClient.Stop() disposes the producer
                        // which races with FFME's demux thread's Read.
                        try { VideoPipelineClosed?.Invoke(); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: VideoPipelineClosed handler threw: {ex.Message}");
                        }

                        rtspClient.Stop();

                        // Initialise CloseMedia Response
                        byte[] response = DSLRCommunication.CloseMediaResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the CloseMedia Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Start Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get Start Data
                        int StartPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int StartChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        long StartPayloadStartTime = DataUtilities.Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        long StartPayloadUseOptimisedPreroll = DataUtilities.Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8);
                        int StartPayloadRequestedPlayRate = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8 + 8);
                        long StartPayloadAvailableBandwidth = DataUtilities.Get8ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 8 + 8 + 4);

                        m_logger?.LogDebug($"AVCTRL: Start - StartTime ({StartPayloadStartTime}) UseOptimisedPreroll ({StartPayloadUseOptimisedPreroll}) PlayRate ({StartPayloadRequestedPlayRate}) AvailableBandwidth ({StartPayloadAvailableBandwidth})");

                        // Start Time is in milliseconds per MS-DMCT §2.2.1.3.1.
                        // The sentinel 0xFFFFFFFFFFFFFFFF means "resume from
                        // current position" (typical after Pause). Treat it
                        // as zero — GetPosition will return whatever the
                        // controller reports (which after resume is the
                        // pause-time position).
                        //
                        // NOTE: a Start with StartTime=0 immediately after a
                        // fresh OpenMedia is the default "play from the
                        // beginning" case, NOT an explicit seek. WMPNss is
                        // sensitive to redundant state changes during the
                        // initial RTSP handshake — if we push a Range header
                        // or a Scale change before any RTP has flowed, the
                        // server can interpret it as a pause/restart and
                        // never start the stream. We therefore only treat a
                        // *non-zero* StartTime as a real seek request.
                        bool isExplicitSeek =
                            (StartPayloadStartTime != StartTimeSentinelResume) &&
                            (StartPayloadStartTime > 0);
                        _playbackStartPositionMs =
                            (StartPayloadStartTime == StartTimeSentinelResume)
                                ? 0L : StartPayloadStartTime;

                        // Decode the WMC PlayRate. NOTE: the wire encoding
                        // is not 100% nailed down from existing captures —
                        // we treat the integer as 1/10000 units (so 10000
                        // = 1.0x) which is the common Microsoft fixed-
                        // point convention. The raw value is logged at
                        // INFO so we can refine this from real Start
                        // requests. Unknown / zero / negative → 1.0x.
                        double rate = DecodePlayRate(StartPayloadRequestedPlayRate);
                        bool isExplicitRate = System.Math.Abs(rate - 1.0) > 0.0001;

                        // Bandwidth / preroll hints — only push when WMC
                        // gave us a real value. Default-zero AvailableBandwidth
                        // and default-zero UseOptimisedPreroll mean "no
                        // preference"; we drop the hint to avoid emitting
                        // a SET_PARAMETER that WMPNss interprets as a
                        // mid-handshake state change.
                        if (StartPayloadAvailableBandwidth > 0) {
                            try { _mediaController?.SetAvailableBandwidth(StartPayloadAvailableBandwidth); }
                            catch (Exception ex) {
                                m_logger?.LogError($"AVCTRL: SetAvailableBandwidth failed: {ex.Message}");
                            }
                        }
                        if (StartPayloadUseOptimisedPreroll != 0) {
                            try { _mediaController?.SetOptimisedPreroll(true); }
                            catch (Exception ex) {
                                m_logger?.LogError($"AVCTRL: SetOptimisedPreroll failed: {ex.Message}");
                            }
                        }

                        // Only push an explicit seek when StartTime is
                        // genuinely non-zero AND meaningfully different
                        // from where we already are.
                        //
                        // Why the "meaningfully different" check: WMC
                        // periodically re-issues Start with
                        // StartTime=current-position as a heartbeat /
                        // position-report (NOT as a seek request). If we
                        // treat that as a seek we ship PLAY with
                        // Range:npt=<current>-, the server returns
                        // 200 OK + new RTP-Info, and WMPNss promptly
                        // stops streaming because the seek-to-where-we-
                        // already-are throws off its BFR pacing state.
                        // Observed empirically: a second PLAY with
                        // Range matching the just-played position broke
                        // the stream every time.
                        //
                        // Heuristic: ±2 s of the controller's reported
                        // position is "same place, no seek needed".
                        // Outside that window it's a real jump (chapter
                        // skip, scrub).
                        if (isExplicitSeek) {
                            // Same formula GetPosition uses on the way
                            // out — absolute media-timeline ms.
                            long currentMs = _playbackStartPositionMs;
                            try {
                                if (_mediaController != null && _mediaController.IsOpen) {
                                    currentMs += (long)_mediaController.Position.TotalMilliseconds;
                                }
                            } catch { /* controller not ready — treat as the cached offset */ }
                            long delta = System.Math.Abs(StartPayloadStartTime - currentMs);
                            if (delta <= 2000) {
                                m_logger?.LogInfo(
                                    $"AVCTRL: Start StartTime={StartPayloadStartTime}ms within " +
                                    $"±2s of current {currentMs}ms (delta={delta}ms) — treating " +
                                    $"as position heartbeat, NOT issuing seek");
                            } else {
                                try { _ = _mediaController?.SeekAsync(
                                            TimeSpan.FromMilliseconds(StartPayloadStartTime)); }
                                catch (Exception ex) {
                                    m_logger?.LogError($"AVCTRL: SeekAsync failed: {ex.Message}");
                                }
                            }
                        }

                        // Rate forwarding. Always invoke SetRateAsync — even
                        // for rate=1.0 — because WMC's "return from FF/RW
                        // to normal speed" arrives as a Start with rate=1.0,
                        // and we MUST flip the server out of trick-play
                        // mode (PLAY without Scale/Speed headers) when that
                        // happens. Skipping SetRateAsync at 1.0 would leave
                        // the server stuck at Scale=3 with audio dropped.
                        //
                        // Wire-level safety:
                        //   * RTSPClient.Play / SetRate are wire-idempotent
                        //     (skip the wire write if the cached state
                        //     already matches), so the very first 1.0
                        //     after session start (auto-PLAY already
                        //     emitted) is a free no-op.
                        //   * SetRateAsync also no-ops the FFME SpeedRatio
                        //     when it's already at the target value.
                        //
                        // The actual routing decision (client-side via
                        // SpeedRatio vs. server-side via RTSP Scale) lives
                        // inside SetRateAsync.
                        bool clientSide =
                            System.Math.Abs(rate) >= 0.5 && System.Math.Abs(rate) <= 2.0
                            && rate > 0;
                        m_logger?.LogInfo(
                            $"AVCTRL: Start RequestedPlayRate={rate}x — forwarding " +
                            $"({(clientSide ? "client-side via FFME SpeedRatio" : "server-side via RTSP PLAY-with-Scale")})" +
                            (isExplicitRate ? "" : " [normal 1× — also drives any required FF/RW exit]"));
                        try { _ = _mediaController?.SetRateAsync(rate); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: SetRateAsync failed: {ex.Message}");
                        }

                        // Drive FFME Play. Fire-and-forget — FFME may not
                        // have finished Open yet (WMC sends Start very
                        // shortly after OpenMedia), and PlayAsync is
                        // dispatcher-marshaled internally so we never
                        // block this VC handler. RTSP PLAY is emitted by
                        // the existing auto-PLAY trigger in RTSPClient
                        // (after the last SETUP response), picking up any
                        // Range/Scale/Speed cached above via SeekAsync/
                        // SetRateAsync.
                        try { _ = _mediaController?.PlayAsync(); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: MediaController.PlayAsync failed: {ex.Message}");
                        }

                        // Initialise Start Response
                        byte[] response = DSLRCommunication.StartResponse(
                            dispatchRequestHandleArray,
                            1
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the Start Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Pause Request
                    else if (dispatchFunctionHandle == 3) {

                        m_logger?.LogDebug("AVCTRL: Pause");

                        // No bookkeeping needed for position — FFME's
                        // MediaController.Position freezes automatically
                        // when the underlying playback pauses, so
                        // GetPosition naturally reports the pause-time
                        // position until Start resumes.

                        // Pause FFME so the decoder + audio output halt in
                        // sync with the RTSP server's pause.
                        try { _ = _mediaController?.PauseAsync(); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: MediaController.PauseAsync failed: {ex.Message}");
                        }

                        rtspClient.Pause();

                        // Initialise Pause Response
                        byte[] response = DSLRCommunication.PauseResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the Pause Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Stop Request
                    else if (dispatchFunctionHandle == 4) {

                        m_logger?.LogDebug("AVCTRL: Stop");

                        // Reset playback position tracking on Stop.
                        _playbackStartPositionMs = 0;

                        // Detach the live RTSP session from the controller
                        // BEFORE stopping it — same reason as CloseMedia.
                        try { _mediaController?.AttachRtspClient(null); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: AttachRtspClient(stop) threw: {ex.Message}");
                        }

                        // Pause FFME first so its decoder threads quiesce
                        // before VideoPipelineClosed tears down the
                        // producer's queue (avoids spurious "Read returned
                        // 0" exceptions in the FFME demux thread). Fire-
                        // and-forget — VideoPipelineClosed will follow.
                        try { _ = _mediaController?.PauseAsync(); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: MediaController.PauseAsync on Stop failed: {ex.Message}");
                        }

                        // Signal the host to close FFME before tearing down
                        // RTSPClient (which disposes the producer).
                        try { VideoPipelineClosed?.Invoke(); }
                        catch (Exception ex) {
                            m_logger?.LogError($"AVCTRL: VideoPipelineClosed handler threw: {ex.Message}");
                        }

                        rtspClient.Stop();

                        // Initialise Stop Response
                        byte[] response = DSLRCommunication.StopResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the Stop Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // GetDuration Request
                    else if (dispatchFunctionHandle == 5) {

                        // Prefer FFME's natural duration when the video
                        // pipeline is up and FFME has parsed enough of the
                        // stream to know it. Live / unbounded RTSP streams
                        // (recorded-TV with `a=range:npt=0-`) leave this
                        // null — we fall back to a 2-hour ceiling so WMC
                        // doesn't think the media is zero-length and stall.
                        long durationMs = DefaultDurationMs;
                        var ffmeDuration = _mediaController?.Duration;
                        if (ffmeDuration.HasValue && ffmeDuration.Value > TimeSpan.Zero) {
                            durationMs = (long)ffmeDuration.Value.TotalMilliseconds;
                        }

                        // MS-DMCT §2.2.1.5: Duration is delivered to WMC in
                        // units of 10 milliseconds. Convert at wire time.
                        long durationWire = durationMs / 10;  /* /10 for 10ms units */

                        m_logger?.LogDebug($"AVCTRL: GetDuration ({durationMs} ms = " +
                                          $"{TimeSpan.FromMilliseconds(durationMs):c}, " +
                                          $"wire={durationWire} ×10ms)");

                        // Initialise GetDuration Response
                        byte[] response = DSLRCommunication.GetDurationResponse(
                            dispatchRequestHandleArray,
                            durationWire
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the GetDuration Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // GetPosition Request
                    else if (dispatchFunctionHandle == 6) {

                        // Position comes from FFME's decoder clock — accurate
                        // to within one frame and stops advancing on Pause.
                        // Before the controller is set up (i.e. very early
                        // in the session) we simply report the StartTime
                        // WMC handed us, leaving the offset unchanged.
                        // Either way the result is in absolute media-
                        // timeline milliseconds.
                        long positionMs;
                        if (_mediaController != null && _mediaController.IsOpen) {
                            positionMs = _playbackStartPositionMs
                                         + (long)_mediaController.Position.TotalMilliseconds;
                        } else {
                            positionMs = _playbackStartPositionMs;
                        }

                        // MS-DMCT §2.2.1.6: Position is delivered to WMC in
                        // units of 10 milliseconds. Convert at wire time.
                        long positionWire = positionMs / 10;  /* /10 for 10ms units */

                        m_logger?.LogDebug($"AVCTRL: GetPosition ({positionMs} ms = " +
                                          $"{TimeSpan.FromMilliseconds(positionMs):c}, " +
                                          $"wire={positionWire} ×10ms)");

                        // Initialise GetPosition Response
                        byte[] response = DSLRCommunication.GetPositionResponse(
                            dispatchRequestHandleArray,
                            positionWire
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);
                        // Send the GetPosition Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // RegisterMediaEventCallback Request
                    else if (dispatchFunctionHandle == 8) {

                        // Get RegisterMediaEventCallback Data
                        int RegisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        Guid RegisterMediaEventCallbackClassID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        Guid RegisterMediaEventCallbackServiceID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16);

                        m_logger?.LogDebug("AVCTRL: RegisterMediaEventCallback");

                        
                        // Add Stub RequestType to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.RegisterMediaEventCallback);
                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);
                        // Add Proxy Service Handle to Dictionary
                        ProxyServiceHandleDict.Add(StubRequestHandleIter, StubServiceHandleIter);

                        // If the StubService Match Dictionary Contains this StubService
                        if (StubServiceHandleDict.ContainsKey(StubService.MediaEventCallback)) {
                            // Overwrite the StubService Entry in Match Dictionary
                            StubServiceHandleDict[StubService.MediaEventCallback] = StubServiceHandleIter;
                        } else {
                            // Add Stub Service Handle to StubService Match Dictionary
                            StubServiceHandleDict.Add(StubService.MediaEventCallback, StubServiceHandleIter);
                        }

                        // Initialise RegisterMediaEventCallback CreateService Request
                        byte[] response = DSLRCommunication.CreateServiceRequest(
                            StubRequestHandleIter,
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 16),
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 16, 16),
                            StubServiceHandleIter
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;
                        // Increment Stub ServiceHandle Iter
                        StubServiceHandleIter++;

                        // Send the RegisterMediaEventCallback CreateService Request
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // UnregisterMediaEventCallback Request
                    else if (dispatchFunctionHandle == 9) {

                        // Get UnregisterMediaEventCallback Data
                        int UnregisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterMediaEventCallbackPayloadCookie = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: UnregisterMediaEventCallback");

                        // Add Stub RequestHandle Iter to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.UnregisterMediaEventCallback);

                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);

                        // Initialise UnregisterMediaEventCallback DeleteService Request
                        byte[] response = DSLRCommunication.DeleteServiceRequest(
                            StubRequestHandleIter,
                            StubRequestCookieDict[UnregisterMediaEventCallbackPayloadCookie]
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;

                        // Send the UnregisterMediaEventCallback CreateService Request
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        // Bumped from Debug to Warning + payload preview so
                        // any DMCT function handle we don't yet implement
                        // (candidates: zoom mode, scale, source rect, etc.)
                        // stands out in the logs. The first ~32B of the
                        // dispatch payload usually carries the parameters
                        // we'd need to decode.
                        int previewLen = Math.Min(32, incomingBuff.Length);
                        var sb = new System.Text.StringBuilder(previewLen * 3);
                        for (int i = 0; i < previewLen; i++) {
                            if (i > 0) sb.Append('-');
                            sb.Append(incomingBuff[i].ToString("X2"));
                        }
                        m_logger?.LogError($"AVCTRL: UNKNOWN DMCT handle {dispatchFunctionHandle} " +
                                          $"(payloadSize={dispatchPayloadSize}, totalLen={incomingBuff.Length}) " +
                                          $"first{previewLen}B={sb}");

                    }

                    #endregion ####################################################

                }
                // DSPA Service Handle
                else if (dispatchServiceHandle == DSPAServiceHandle) {

                    #region DSPA Service ##########################################

                    // GetStringProperty Request
                    if (dispatchFunctionHandle == 0) {

                        // Get GetStringProperty Data
                        int GetStringPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetStringPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int GetStringPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string GetStringPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetStringPropertyPayloadLength);

                        m_logger?.LogDebug($"AVCTRL: GetStringProperty ({GetStringPropertyPayloadPropertyName})");

                        switch (GetStringPropertyPayloadPropertyName) {
                            // Property Bag Service
                            case "XspHostAddress":
                                // Initialise GetStringProperty Response
                                byte[] response = DSLRCommunication.GetStringPropertyResponse(
                                    dispatchRequestHandleArray,
                                    SoftSledConfigManager.ReadConfig().RdpLoginHost
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                                // Send the GetStringProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                                break;
                            default:
                                m_logger?.LogDebug($"AVCTRL: GetStringProperty ({GetStringPropertyPayloadPropertyName}) not available");
                                break;
                        }
                    }
                    // GetDWORDProperty Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get GetDWORDProperty Data
                        int GetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int GetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int GetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string GetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, GetDWORDPropertyPayloadLength);

                        m_logger?.LogDebug($"AVCTRL: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName})");

                        switch (GetDWORDPropertyPayloadPropertyName) {
                            case "IsMuted":
                                bool isMuted = SystemAudio.WindowsSystemAudio.GetMute();
                                // Initialise GetDWORDProperty Response
                                byte[] isMutedResponse = DSLRCommunication.GetDWORDPropertyResponse(
                                    dispatchRequestHandleArray,
                                    isMuted ? 1 : 0 
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedIsMutedResponse = DSLRCommunication.Encapsulate(isMutedResponse);

                                // Send the GetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedIsMutedResponse));

                                break;
                            case "Volume":
                                // Get the System Volume
                                decimal currentVolume = SystemAudio.WindowsSystemAudio.GetVolume();
                                int sendVolume = 0;

                                m_logger?.LogDebug($"AVCTRL: CurrentVolume ({currentVolume})");

                                // If the Volume is greater than 0
                                if (currentVolume > 0) {
                                    m_logger?.LogDebug($"AVCTRL: CurrentVolume Div ({(currentVolume / 100)})");
                                    sendVolume = (int)Math.Floor((65535 / (currentVolume / 100)) / 100);
                                }
                               
                                m_logger?.LogDebug($"AVCTRL: SendVolume ({sendVolume})");

                                // Initialise GetDWORDProperty Response
                                byte[] volumeResponse = DSLRCommunication.GetDWORDPropertyResponse(
                                    dispatchRequestHandleArray,
                                    sendVolume
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedVolumeResponse = DSLRCommunication.Encapsulate(volumeResponse);

                                // Send the GetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedVolumeResponse));
                                break;
                            case "WmvTrickModesSupported":
                                // Initialise GetDWORDProperty Response
                                byte[] trickModeResponse = DSLRCommunication.GetDWORDPropertyResponse(
                                    dispatchRequestHandleArray,
                                    0
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedTrickModeResponse = DSLRCommunication.Encapsulate(trickModeResponse);

                                // Send the GetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedTrickModeResponse));
                                break;
                            default:
                                m_logger?.LogDebug($"AVCTRL: GetDWORDProperty ({GetDWORDPropertyPayloadPropertyName}) not available");
                                break;
                        }
                    }
                    // SetDWORDProperty Request
                    else if (dispatchFunctionHandle == 3) {

                        // Set SetDWORDProperty Data
                        int SetDWORDPropertyPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int SetDWORDPropertyChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int SetDWORDPropertyPayloadLength = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);
                        string SetDWORDPropertyPayloadPropertyName = DataUtilities.GetByteArrayString(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4, SetDWORDPropertyPayloadLength);
                        int SetDWORDPropertyPayloadPropertyValue = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2 + 4 + SetDWORDPropertyPayloadLength);

                        m_logger?.LogDebug($"AVCTRL: SetDWORDProperty ({SetDWORDPropertyPayloadPropertyName})");

                        switch (SetDWORDPropertyPayloadPropertyName) {
                            case "IsMuted":

                                if (SetDWORDPropertyPayloadPropertyValue == 1) {
                                    //SystemAudio.WindowsSystemAudio.SetMute(true);
                                } else {
                                    //SystemAudio.WindowsSystemAudio.SetMute(false);
                                }

                                // Initialise SetDWORDProperty Response
                                byte[] response = DSLRCommunication.SetDWORDPropertyResponse(
                                    dispatchRequestHandleArray
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                                // Send the SetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                                break;
                            case "Volume":

                                int setVolume = (int)Math.Floor((SetDWORDPropertyPayloadPropertyValue / 65535.00) * 100);

                                m_logger?.LogDebug($"AVCTRL: SetVolume ({setVolume})");
                                //SystemAudio.WindowsSystemAudio.SetVolume(setVolume);

                                // Initialise SetDWORDProperty Response
                                byte[] volumeResponse = DSLRCommunication.SetDWORDPropertyResponse(
                                    dispatchRequestHandleArray
                                );
                                // Encapsulate the Response (Doesn't seem to work without this?)
                                byte[] encapsulatedVolumeResponse = DSLRCommunication.Encapsulate(volumeResponse);

                                // Send the SetDWORDProperty Response
                                VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedVolumeResponse));
                                break;
                            default:
                                m_logger?.LogDebug($"AVCTRL: SetDWORDProperty ({SetDWORDPropertyPayloadPropertyName}) not available");
                                break;
                        }
                    }
                    // Unknown Request
                    else {

                        // Bumped to Warning + payload preview for the same
                        // reason as the DMCT branch — DSPA SetDWORDProperty
                        // / GetDWORDProperty with an unfamiliar property
                        // name is a plausible carrier for zoom-mode info
                        // (DSPA defines ZOM/NLZ/RSZ as zoom-capability
                        // flags; an active set may show up here).
                        int dspaPrev = Math.Min(32, incomingBuff.Length);
                        var dspaHex = new System.Text.StringBuilder(dspaPrev * 3);
                        for (int i = 0; i < dspaPrev; i++) {
                            if (i > 0) dspaHex.Append('-');
                            dspaHex.Append(incomingBuff[i].ToString("X2"));
                        }
                        m_logger?.LogError($"AVCTRL: UNKNOWN DSPA handle {dispatchFunctionHandle} " +
                                          $"(totalLen={incomingBuff.Length}) first{dspaPrev}B={dspaHex}");

                    }

                    #endregion ####################################################

                }
                // DRMRI Service Handle
                else if (dispatchServiceHandle == DRMRIServiceHandle) {

                    #region DRMRI Service #########################################

                    // RegisterTransmitterService Request
                    if (dispatchFunctionHandle == 0) {

                        // Get RegisterTransmitterService Data
                        int RegisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        //Guid RegisterTransmitterServiceClassID = DataUtilities.GuidFromArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        byte[] RegisterTransmitterServiceClassIdBytes = DataUtilities.GuidToArray(RegisterTransmitterServiceClassID);
                        byte[] RegisterTransmitterServiceServiceIdBytes = DataUtilities.GuidToArray(RegisterTransmitterServiceServiceID);

                        m_logger?.LogDebug("AVCTRL: RegisterTransmitterService");

                        // Add Stub RequestType to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.RegisterTransmitterService);
                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);
                        // Add Proxy Service Handle to Dictionary
                        ProxyServiceHandleDict.Add(StubRequestHandleIter, StubServiceHandleIter);

                        // Initialise RegisterMediaEventCallback CreateService Request
                        byte[] response = DSLRCommunication.CreateServiceRequest(
                            StubRequestHandleIter,
                            RegisterTransmitterServiceClassIdBytes,
                            RegisterTransmitterServiceServiceIdBytes,
                            StubServiceHandleIter
                        );
                        
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;
                        // Increment Stub ServiceHandle Iter
                        StubServiceHandleIter++;

                        // Send the RegisterMediaEventCallback CreateService Request
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // UnregisterTransmitterService Request
                    else if (dispatchFunctionHandle == 1) {

                        // Get UnregisterTransmitterService Data
                        int UnregisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterTransmitterServicePayloadCookie = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: UnregisterTransmitterService");

                        // Add Stub RequestHandle Iter to Dictionary
                        StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.UnregisterTransmitterService);

                        // Add Proxy RequestHandle Iter to Stub RequestHandle Match Dictionary
                        ProxyRequestHandleDict.Add(StubRequestHandleIter, dispatchRequestHandle);

                        // Initialise UnregisterMediaEventCallback DeleteService Request
                        byte[] response = DSLRCommunication.DeleteServiceRequest(
                            StubRequestHandleIter,
                            0
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Increment Stub RequestHandle Iter
                        StubRequestHandleIter++;

                        // Send the UnregisterTransmitterService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // InitiateRegistration Request
                    else if (dispatchFunctionHandle == 2) {

                        // Get InitiateRegistration Data
                        int InitiateRegistrationPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int InitiateRegistrationChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);

                        m_logger?.LogDebug("AVCTRL: InitiateRegistration");

                        // Initialise InitiateRegistration Response
                        byte[] response = DSLRCommunication.InitiateRegistrationResponse(
                            dispatchRequestHandleArray
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the InitiateRegistration Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                    }
                    // Unknown Request
                    else {

                        m_logger?.LogDebug($"Unknown DRMRI Request {dispatchFunctionHandle} not implemented");

                    }

                    #endregion ####################################################

                } else {

                    // Catch-all for service handles we don't recognise at
                    // all (i.e. not DSLR/DMCT/DSPA/DRMRI). If WMC starts a
                    // new service for zoom/display-mode control, it will
                    // land here and we want to see it loudly.
                    int unkPrev = Math.Min(32, incomingBuff.Length);
                    var unkHex = new System.Text.StringBuilder(unkPrev * 3);
                    for (int i = 0; i < unkPrev; i++) {
                        if (i > 0) unkHex.Append('-');
                        unkHex.Append(incomingBuff[i].ToString("X2"));
                    }
                    m_logger?.LogError($"AVCTRL: UNKNOWN service {dispatchServiceHandle} " +
                                      $"handle {dispatchFunctionHandle} " +
                                      $"(totalLen={incomingBuff.Length}) first{unkPrev}B={unkHex}");

                }
            } else if (dispatchCallingConvention == 2) {

                byte[] response = null;
                byte[] encapsulatedResponse = null;

                switch (StubRequestTypeDict[dispatchRequestHandle]) {
                    // If the RequestHandle matches RegisterMediaEventCallback Request
                    case StubRequestType.RegisterMediaEventCallback:

                        // Get RegisterMediaEventCallback CreateService Response Data
                        int RegisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int RegisterMediaEventCallbackPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: RegisterMediaEventCallbackResponse");

                        if (RegisterMediaEventCallbackPayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger?.LogDebug("AVCTRL: RegisterMediaEventCallback Request Failed - 0x" + errorByteArray);
                        }

                        // Get Proxy Service Handle from Dictionary and Add with Cookie
                        StubRequestCookieDict.Add(DMCTStubRequestCookie, ProxyServiceHandleDict[dispatchRequestHandle]);

                        // Initialise RegisterMediaEventCallback Response
                        response = DSLRCommunication.RegisterMediaEventCallbackResponse(
                            ProxyRequestHandleDict[dispatchRequestHandle],
                            DMCTStubRequestCookie,
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the RegisterMediaEventCallback Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        // Increment Cookie
                        DMCTStubRequestCookie++;

                        break;
                    // If the RequestHandle matches UnregisterMediaEventCallback Request
                    case StubRequestType.UnregisterMediaEventCallback:

                        // Get UnregisterMediaEventCallback CreateService Response Data
                        int UnregisterMediaEventCallbackPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterMediaEventCallbackChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterMediaEventCallbackPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: UnregisterMediaEventCallbackResponse");

                        if (UnregisterMediaEventCallbackPayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger?.LogDebug("AVCTRL: UnregisterMediaEventCallback Request Failed - 0x" + errorByteArray);
                        }

                        // Initialise UnregisterMediaEventCallback Response
                        response = DSLRCommunication.UnregisterMediaEventCallbackResponse(
                            ProxyRequestHandleDict[dispatchRequestHandle],
                            DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the RegisterMediaEventCallback Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        break;
                    // If the RequestHandle matches RegisterTransmitterService Request
                    case StubRequestType.RegisterTransmitterService:

                        // Get RegisterMediaEventCallback CreateService Response Data
                        int RegisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int RegisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int RegisterTransmitterServicePayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: RegisterTransmitterServiceResponse");

                        if (RegisterTransmitterServicePayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + "";
                            }
                            m_logger?.LogDebug("AVCTRL: RegisterTransmitterService Request Failed - 0x" + errorByteArray);
                        }

                        // Initialise RegisterTransmitterService Response
                        response = DSLRCommunication.RegisterTransmitterServiceResponse(
                            DataUtilities.GetInt4Byte(ProxyRequestHandleDict[dispatchRequestHandle]),
                            RegisterTransmitterServicePayloadResult == 0
                        );
                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the RegisterTransmitterService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        break;
                    // If the RequestHandle matches UnregisterTransmitterService Request
                    case StubRequestType.UnregisterTransmitterService:

                        // Get UnregisterMediaEventCallback CreateService Response Data
                        int UnregisterTransmitterServicePayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int UnregisterTransmitterServiceChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int UnregisterTransmitterServicePayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: UnregisterTransmitterServiceResponse");

                        if (UnregisterTransmitterServicePayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger?.LogDebug("AVCTRL: UnregisterTransmitterService Request Failed - 0x" + errorByteArray);
                        }

                        // Initialise UnregisterTransmitterService Response
                        response = DSLRCommunication.UnregisterTransmitterServiceResponse(
                            DataUtilities.GetInt4Byte(ProxyRequestHandleDict[dispatchRequestHandle])
                        );

                        // Encapsulate the Response (Doesn't seem to work without this?)
                        encapsulatedResponse = DSLRCommunication.Encapsulate(response);

                        // Send the UnregisterTransmitterService Response
                        VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));

                        break;
                    // If the RequestHandle matches OnMediaEvent Request
                    case StubRequestType.OnMediaEvent:

                        // Get UnregisterMediaEventCallback CreateService Response Data
                        int OnMediaEventPayloadSize = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize);
                        int OnMediaEventChildCount = DataUtilities.Get2ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4);
                        int OnMediaEventPayloadResult = DataUtilities.Get4ByteInt(incomingBuff, 6 + dispatchPayloadSize + 4 + 2);

                        m_logger?.LogDebug("AVCTRL: OnMediaEventResponse");

                        if (OnMediaEventPayloadResult != 0) {
                            // Get Error in Hex Form
                            string errorByteArray = "";
                            foreach (byte b in DataUtilities.GetByteSubArray(incomingBuff, 6 + dispatchPayloadSize + 4 + 2, 4)) {
                                errorByteArray += b.ToString("X2") + " ";
                            }
                            m_logger?.LogDebug("AVCTRL: OnMediaEventResponse Request Failed - 0x" + errorByteArray);
                        }

                        break;
                    default:
                        m_logger?.LogDebug($"Unknown Response RequestHandle {dispatchRequestHandle} - No matching requests made");
                        break;
                }

            } else {

                m_logger?.LogDebug($"AVCTRL: Unknown CallingConvention {dispatchCallingConvention} not implemented");

            }
        }

        /// <summary>
        /// Decode the WMC <c>RequestedPlayRate</c> from the Start payload.
        ///
        /// MS-DMCT §2.2.1.3.1 describes this as a 4-byte "unsigned 32-bit
        /// integer" that is "specified as 1 for normal playback ... a value
        /// greater than 1 requests fast forward, and a negative value
        /// requests the server to rewind". The "unsigned integer ...
        /// negative value" wording is internally inconsistent — the actual
        /// on-wire encoding is IEEE 754 single-precision <c>float</c>,
        /// confirmed empirically: WMC sends <c>0x3F800000</c>
        /// (= 1065353216 if mis-read as int32) for normal speed, which is
        /// <c>1.0f</c>. <c>2.0f</c> fast-forward arrives as
        /// <c>0x40000000</c>; <c>-1.0f</c> rewind as <c>0xBF800000</c>
        /// (= -1082130432 as signed int).
        ///
        /// The caller hands us the 4 bytes already parsed as a signed
        /// int32 via <see cref="DataUtilities.Get4ByteInt"/>; we
        /// bit-cast those 4 bytes back into a float via
        /// <see cref="BitConverter"/>.
        ///
        /// Zero / non-finite / wildly-out-of-range maps to 1.0x as a
        /// safe fallback. The spec explicitly states the value MUST
        /// NOT be 0, but we tolerate it just in case.
        /// </summary>
        private double DecodePlayRate(int wire) {
            // Bit-cast int → float without changing the underlying bytes.
            // BitConverter.GetBytes(int) returns host-endian bytes; since
            // wire was Get4ByteInt'd from the same byte order, the round
            // trip preserves the original wire bytes.
            float rate = BitConverter.ToSingle(BitConverter.GetBytes(wire), 0);

            if (float.IsNaN(rate) || float.IsInfinity(rate)) {
                m_logger?.LogInfo($"AVCTRL: DecodePlayRate(0x{wire:X8}={wire}) → " +
                                  $"non-finite ({rate}), defaulting to 1.0x");
                return 1.0;
            }
            if (rate == 0.0f) {
                // MS-DMCT explicitly states "MUST NOT be 0". Treat as
                // normal speed if we ever see it.
                m_logger?.LogInfo($"AVCTRL: DecodePlayRate(0x{wire:X8}) → 0 " +
                                  $"(spec-prohibited), defaulting to 1.0x");
                return 1.0;
            }
            // Sanity-clamp to ±64x — DLNA negotiates speeds up to 20x in
            // the captured corpus; anything beyond that is almost
            // certainly a wire-format misinterpretation rather than a
            // legitimate trick-play request.
            if (rate < -64.0f || rate > 64.0f) {
                m_logger?.LogInfo($"AVCTRL: DecodePlayRate(0x{wire:X8}={rate}) → " +
                                  $"out of range, defaulting to 1.0x");
                return 1.0;
            }
            m_logger?.LogInfo($"AVCTRL: DecodePlayRate(0x{wire:X8}) → {rate}x");
            return rate;
        }

        public void OnMediaEvent(MediaEvent mediaEvent) {
            OnMediaEvent(mediaEvent, errorCode: 0);
        }

        /// <summary>
        /// Send a DMCT OnMediaEvent notification to WMC. The
        /// <paramref name="errorCode"/> is a 32-bit HRESULT-style status
        /// shipped alongside the event (0 = no error, used for normal
        /// state transitions like BUFFERING_STOP / END_OF_MEDIA).
        ///
        /// Silently no-ops if WMC hasn't registered the callback yet — that
        /// happens early in the session before handle 8 fires, and also
        /// when FFME raises events on audio-only sessions where the
        /// callback channel is never opened.
        /// </summary>
        public void OnMediaEvent(MediaEvent mediaEvent, int errorCode) {
            m_logger?.LogDebug($"AVCTRL: OnMediaEvent ({mediaEvent}, errorCode=0x{errorCode:X8})");

            if (!StubServiceHandleDict.ContainsKey(StubService.MediaEventCallback)) {
                m_logger?.LogDebug("AVCTRL: OnMediaEvent dropped — callback not registered yet");
                return;
            }

            // Initialise OnMediaEvent Request
            byte[] response = DSLRCommunication.OnMediaEventRequest(
                StubRequestHandleIter,
                StubServiceHandleDict[StubService.MediaEventCallback],
                DataUtilities.GetInt4Byte(errorCode),
                DataUtilities.GetInt4Byte((int)mediaEvent)
            );
            // Encapsulate the Response (Doesn't seem to work without this?)
            byte[] encapsulatedResponse = DSLRCommunication.Encapsulate(response);

            // Add Stub RequestType to Dictionary
            StubRequestTypeDict.Add(StubRequestHandleIter, StubRequestType.OnMediaEvent);

            // Increment Stub RequestHandle Iter
            StubRequestHandleIter++;


            // Send the OnMediaEvent Request
            VirtualChannelSend(this, new VirtualChannelSendArgs("avctrl", encapsulatedResponse));
        }
    }

    enum StubService {
        MediaEventCallback
    }

    enum StubRequestType {
        RegisterMediaEventCallback,
        UnregisterMediaEventCallback,
        RegisterTransmitterService,
        UnregisterTransmitterService,
        OnMediaEvent,
    }

    enum MediaEvent {
        BUFFERING_STOP = 1,
        END_OF_MEDIA = 2,
        RTSP_DISCONNECT = 3,
        PTS_ERROR = 5,
        UNRECOVERABLE_SKEW = 6,
        DRM_LICENSE_ERROR = 11,
        DRM_LICENSE_CLEAR = 14,
        DRM_HDCP_ERROR = 15,
        FIRMWARE_UPDATE = 17
    }
}
