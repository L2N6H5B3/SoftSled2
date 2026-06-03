using FFmpeg.AutoGen;
using SoftSled.Components.AudioVisual;
using SoftSled.Components.AudioVisual.VideoFpsLab;
using SoftSled.Components.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoftSled.Components.AudioVisual.ExternalSync {

    /// <summary>
    /// Sole playback controller. Audio is decoded with libav and rendered
    /// through <see cref="NAudioMasterRenderer"/>, whose device playback
    /// position is the master clock. Video is decoded with libav
    /// (<see cref="LibAvVideoPushDecoder"/>) and presented on a GPU surface
    /// (<see cref="D3DImagePresenter"/>); a <see cref="PtsFramePacer"/> slaves
    /// video presentation to the audio master clock for lip-sync. FFME is no
    /// longer involved.
    ///
    /// <para>The session owns the <see cref="D3DImagePresenter"/> (it needs the
    /// window handle) and hands it to the controller via
    /// <see cref="AttachVideoPresenter"/>. RTSP media is routed in through the
    /// RTSPClient's external audio + video consumers.</para>
    /// </summary>
    internal sealed class ExternalSyncMediaController : IMediaController, IDisposable {

        private readonly Logger _log;
        private LibAvAudioDecoder _decoder;
        private NAudioMasterRenderer _renderer;

        // Manual audio-sync trim (ms) from config — added to the wire A/V
        // offset so the user can nudge lip-sync for downstream HDMI/AVR delay.
        public int AudioSyncOffsetMs { get; }

        // Video jitter-buffer depth (ms) from config — feeds the pacer's
        // pre-roll / max-buffer to absorb bursty RTSP delivery.
        private readonly int _videoJitterBufferMs;

        private bool _playRequested = true;
        private bool _isOpen;
        private volatile bool _disposed;

        private SoftSled.Components.RTSP.RTSPClient _rtsp;
        private long _lastBandwidthBps = -1;
        private bool _lastOptimisedPreroll;
        private double _lastRequestedRate = 1.0;

        // Video pipeline (libav + D3DImage, audio-slaved). The presenter is
        // created and owned by the session; the decoder + pacer are owned here.
        private D3DImagePresenter _presenter;
        private LibAvVideoPushDecoder _videoDecoder;
        private PtsFramePacer _pacer;
        private readonly object _videoGate = new object();

        // First audio + video MAU wire PTS (ms). Their difference is the wire
        // A/V offset the pacer needs. Each is converted from the stream's RTP
        // timestamp using that stream's RTP clock (90 kHz for wm-MPA/MPV,
        // 1 kHz for x-wmf-pf) — getting the clock wrong scales the offset and
        // mis-aligns video (the x-wmf-pf 1 kHz case was lagging because both
        // were divided by 90 as if 90 kHz).
        private long _firstAudioMauWirePtsMs = -1;
        private long _firstVideoMauWirePtsMs = -1;
        private int _audioClockHz = 90000;
        private int _videoClockHz = 90000;

        // Raw first-MAU RTP timestamps (per stream) for the RTCP-SR cross-stream
        // offset, plus a latch so we stop recomputing once both SRs have
        // anchored and the offset is finalised.
        private long _firstAudioMauRtpRaw = -1;
        private long _firstVideoMauRtpRaw = -1;
        private bool _syncFinalized;
        // No genuine A/V startup skew exceeds a few seconds; beyond this the
        // RTP-Info cross-stream offset is treated as bogus (Live TV npt=now).
        private const int MaxPlausibleOffsetMs = 5000;
        private readonly System.Diagnostics.Stopwatch _srWaitClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastSrWaitLogMs = -100000;

        private TaskCompletionSource<bool> _openTcs =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExternalSyncMediaController(Logger log) : this(log, 0, 250) { }

        public ExternalSyncMediaController(Logger log, int audioSyncOffsetMs)
            : this(log, audioSyncOffsetMs, 250) { }

        public ExternalSyncMediaController(Logger log, int audioSyncOffsetMs, int videoJitterBufferMs) {
            _log = log;
            AudioSyncOffsetMs = audioSyncOffsetMs;
            _liveTrimMs = audioSyncOffsetMs;   // live-nudgeable starting point
            _videoJitterBufferMs = videoJitterBufferMs > 0 ? videoJitterBufferMs : 250;
            _log?.LogInfo($"[ext-sync] controller constructed (audioSyncOffset={audioSyncOffsetMs}ms, " +
                          $"videoJitterBuffer={_videoJitterBufferMs}ms)");
        }

        /// <summary>Bind the session-owned GPU presenter. Must be called before
        /// video starts (the session does this right after construction).</summary>
        public void AttachVideoPresenter(D3DImagePresenter presenter) {
            _presenter = presenter;
        }

        // ============================================================
        //  RTSPClient hookup
        // ============================================================

        public void AttachRtspClient(SoftSled.Components.RTSP.RTSPClient client) {
            if (ReferenceEquals(_rtsp, client)) return;
            if (_rtsp != null) {
                _rtsp.Disconnected      -= OnRtspDisconnected;
                _rtsp.PtsError          -= OnRtspPtsError;
                _rtsp.UnrecoverableSkew -= OnRtspUnrecoverableSkew;
                try { _rtsp.SetExternalAudioConsumer(null, null); } catch { }
                try { _rtsp.SetExternalVideoConsumer(null, null); } catch { }
                // The controller is session-scoped and outlives individual
                // media (WMC reuses it across OpenMedia/CloseMedia, creating a
                // fresh RTSPClient each time). Tear the decode pipeline down on
                // every detach so the NEXT media rebuilds decoders for its own
                // codec. Without this, switching e.g. H.264/PCM → MPEG-2/MP2
                // left the old H.264 + PCM decoders in place (the codec-commit
                // handlers bail when a decoder already exists) → the H.264
                // decoder chokes on MPEG-2 ("Invalid data") = no video, and the
                // PCM decoder renders MP2 bytes as raw samples = static.
                ResetPipelineForNewMedia();
            }
            _rtsp = client;
            if (_rtsp != null) {
                _rtsp.Disconnected      += OnRtspDisconnected;
                _rtsp.PtsError          += OnRtspPtsError;
                _rtsp.UnrecoverableSkew += OnRtspUnrecoverableSkew;
                try {
                    _rtsp.SetExternalAudioConsumer(OnAudioCodecCommitted, OnAudioMau);
                } catch (Exception ex) {
                    _log?.LogError($"[ext-sync] SetExternalAudioConsumer threw: {ex.Message}");
                }
                try {
                    _rtsp.SetExternalVideoConsumer(OnVideoCodecCommitted, OnVideoMau);
                } catch (Exception ex) {
                    _log?.LogError($"[ext-sync] SetExternalVideoConsumer threw: {ex.Message}");
                }
            }
        }

        // ============================================================
        //  Audio
        // ============================================================

        private void OnAudioCodecCommitted(ExternalAudioFormat fmt) {
            if (_decoder != null) return;
            if (fmt == null) {
                _log?.LogError("[ext-sync] OnAudioCodecCommitted called with null fmt");
                return;
            }
            AVCodecID codecId;
            string wc = (fmt.WireCodec ?? "").ToUpperInvariant();
            // x-wmf-pf audio uses a 1 kHz RTP wire clock; wm-MPA / AC-3 use
            // 90 kHz. This must match the divisor used in OnAudioMau so the
            // wire A/V offset is computed in real milliseconds.
            _audioClockHz = (wc == "X-WMF-PF") ? 1000 : 90000;
            switch (wc) {
                case "MPA":
                case "VND.MS.WM-MPA":
                    codecId = (fmt.MpegLayer == 1 || fmt.MpegLayer == 2)
                        ? AVCodecID.AV_CODEC_ID_MP2 : AVCodecID.AV_CODEC_ID_MP3;
                    break;
                case "VND.MS.WM-AC3":
                    codecId = AVCodecID.AV_CODEC_ID_AC3;
                    break;
                case "X-WMF-PF":
                    switch (fmt.BitsPerSampleHint) {
                        case 8:  codecId = AVCodecID.AV_CODEC_ID_PCM_U8; break;
                        case 16: codecId = fmt.PcmBigEndian
                                          ? AVCodecID.AV_CODEC_ID_PCM_S16BE
                                          : AVCodecID.AV_CODEC_ID_PCM_S16LE; break;
                        case 24: codecId = fmt.PcmBigEndian
                                          ? AVCodecID.AV_CODEC_ID_PCM_S24BE
                                          : AVCodecID.AV_CODEC_ID_PCM_S24LE; break;
                        case 32: codecId = fmt.PcmBigEndian
                                          ? AVCodecID.AV_CODEC_ID_PCM_S32BE
                                          : AVCodecID.AV_CODEC_ID_PCM_S32LE; break;
                        default:
                            codecId = AVCodecID.AV_CODEC_ID_PCM_S16LE;
                            _log?.LogError($"[ext-sync] X-WMF-PF audio has no bitspersample hint " +
                                           $"({fmt}); defaulting to PCM_S16LE");
                            break;
                    }
                    break;
                default:
                    _log?.LogError($"[ext-sync] unsupported wire codec '{fmt.WireCodec}' — " +
                                   $"audio will not play.");
                    return;
            }
            try {
                int hintRate = 0, hintChannels = 0;
                bool isPcm = wc == "X-WMF-PF";
                if (isPcm) {
                    hintRate = fmt.SampleRateHint > 0 ? fmt.SampleRateHint : 48000;
                    hintChannels = fmt.ChannelsHint > 0 ? fmt.ChannelsHint : 2;
                }
                _decoder = new LibAvAudioDecoder(codecId, _log, hintRate, hintChannels);
                _decoder.OnFormatReady += OnDecoderFormatReady;
                _decoder.OnPcm         += OnDecodedPcm;
                _decoder.Start();
                _log?.LogInfo($"[ext-sync] audio decoder started: codec={codecId} ({fmt})" +
                              (isPcm ? $" PCM hints: rate={hintRate}Hz ch={hintChannels}" : ""));
            } catch (Exception ex) {
                _log?.LogError($"[ext-sync] decoder init failed: {ex.Message}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        private void OnAudioMau(byte[] data, uint rtpTs) {
            if (_decoder == null) return;
            long ptsMs = (long)rtpTs * 1000L / _audioClockHz;
            if (Interlocked.CompareExchange(ref _firstAudioMauWirePtsMs, ptsMs, -1L) == -1L) {
                Interlocked.Exchange(ref _firstAudioMauRtpRaw, rtpTs);
                _log?.LogDebug($"[ext-sync-anchor] first audio MAU rtpTs={rtpTs} ({ptsMs}ms, clk={_audioClockHz})");
                UpdateSyncOffset();
            }
            _decoder.SubmitPacket(data, ptsMs);
        }

        private void OnDecoderFormatReady() {
            try {
                // No video element to hold for — audio plays immediately and
                // video slaves to its clock.
                _renderer = new NAudioMasterRenderer(
                    LibAvAudioDecoder.OutSampleRate,
                    LibAvAudioDecoder.OutChannels,
                    LibAvAudioDecoder.OutBitsPerSample,
                    _log,
                    startHeld: false);
                // Feed the RTCP BFR W3 field the renderer's REAL audio buffer
                // occupancy so a drain (server under-delivery) is visible to
                // WMPNss and it speeds up to refill — see ComputeBfrW3Fill.
                try {
                    var r = _renderer;
                    _rtsp?.SetAudioBufferOccupancyProvider(() => r?.BufferedMs ?? 0);
                } catch (Exception ex) {
                    _log?.LogError($"[ext-sync] SetAudioBufferOccupancyProvider failed: {ex.Message}");
                }
                _isOpen = true;
                _log?.LogInfo("[ext-sync] renderer ready, signalling open");
                if (_playRequested) _renderer.Play();
                try { _openTcs.TrySetResult(true); } catch { }
                try { BufferingEnded?.Invoke(); } catch { }
            } catch (Exception ex) {
                _log?.LogError($"[ext-sync] renderer init failed: {ex.Message}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        private void OnDecodedPcm(byte[] pcm, int len, long ptsMs) {
            _renderer?.WritePcm(pcm, len, ptsMs);
        }

        // ============================================================
        //  Video
        // ============================================================

        private void OnVideoCodecCommitted(string wireCodec) {
            lock (_videoGate) {
                if (_videoDecoder != null) return;
                if (_presenter == null) {
                    _log?.LogError("[ext-sync] video committed but no presenter attached — " +
                                   "video will not render.");
                    return;
                }
                AVCodecID id = MapWireCodec(wireCodec);
                int clockHz = MapClockHz(wireCodec);
                _videoClockHz = clockHz;

                _pacer = new PtsFramePacer(
                    (ptr, stride, w, h) => _presenter?.SubmitFrame(ptr, stride, w, h),
                    prerollMs: _videoJitterBufferMs, _log);
                // Slave video presentation to the audio device clock.
                _pacer.SetMasterClock(() => _renderer?.GetMediaTimeMs() ?? 0L);
                UpdateSyncOffset();

                var d = new LibAvVideoPushDecoder(id, clockHz, _log);
                d.OnFrame += (ptr, stride, w, h, ptsMs) => _pacer?.Submit(ptr, stride, w, h, ptsMs);
                try {
                    d.Start();
                    _videoDecoder = d;
                    _log?.LogInfo($"[ext-sync] video decoder started: wireCodec={wireCodec} " +
                                  $"→ {id} clockHz={clockHz}");
                } catch (Exception ex) {
                    _log?.LogError($"[ext-sync] video decoder start failed: {ex.Message}");
                    try { _pacer?.Dispose(); } catch { }
                    _pacer = null;
                }
            }
        }

        private void OnVideoMau(byte[] data, uint rtpTs) {
            long ptsMs = (long)rtpTs * 1000L / _videoClockHz;
            if (Interlocked.CompareExchange(ref _firstVideoMauWirePtsMs, ptsMs, -1L) == -1L) {
                Interlocked.Exchange(ref _firstVideoMauRtpRaw, rtpTs);
                _log?.LogDebug($"[ext-sync-anchor] first video MAU rtpTs={rtpTs} ({ptsMs}ms, clk={_videoClockHz})");
            }
            // Retry the SR-based offset until both streams' Sender Reports have
            // anchored (they arrive a little after the first MAUs). Cheap once
            // finalised. Runs on the depacketizer thread.
            if (!_syncFinalized) UpdateSyncOffset();
            _videoDecoder?.SubmitPacket(data, rtpTs);
        }

        // A/V offset via the RTCP Sender Report NTP↔RTP mapping — the canonical
        // cross-stream sync. Each stream's SR ties its RTP clock to absolute
        // NTP wall time, so we can compute the true wall-time gap between the
        // first audio sample (at the play point) and the first video frame
        // (delivered earlier as prior-IDR padding) = how much video to skip.
        // This is epoch-free and server-semantics-free, unlike the earlier
        // first-MAU-PTS-difference (independent epochs → x-wmf-pf raced ahead)
        // and RTP-Info-rtptime (WMPNss reports the first-packet ts, not the
        // play point → no padding skipped → video lagged). Retried per video
        // MAU until both SRs anchor (a few seconds in), then latched.
        private void UpdateSyncOffset() {
            long aRaw = Interlocked.Read(ref _firstAudioMauRtpRaw);
            long vRaw = Interlocked.Read(ref _firstVideoMauRtpRaw);
            if (aRaw < 0 || vRaw < 0) return;
            // WMPNss doesn't send RTCP Sender Reports, so the cross-stream
            // offset comes from the PLAY response's RTP-Info (each stream's
            // play-point RTP timestamp) — epoch-free and available immediately.
            if (_rtsp != null && _rtsp.TryGetRtpInfoAvOffsetMs((uint)aRaw, (uint)vRaw, out long offsetMs)) {
                // Sanity gate: no real A/V startup skew exceeds a few seconds.
                // A larger value means the RTP-Info / first-MAU relationship
                // didn't follow the prior-IDR-padding model — e.g. Live TV
                // (npt=now), where the audio RTP-Info play point can sit tens
                // of seconds from the first delivered audio MAU and inflates
                // the offset. In that case the streams' first MAUs actually
                // arrive together (the live edge), so 0 is the right answer.
                if (Math.Abs(offsetMs) > MaxPlausibleOffsetMs) {
                    _log?.LogInfo($"[ext-sync] RTP-Info offset {offsetMs}ms implausible (>{MaxPlausibleOffsetMs}ms) " +
                                  $"— falling back to 0 (streams assumed to start together, e.g. Live TV)");
                    offsetMs = 0;
                }
                Interlocked.Exchange(ref _baseOffsetMs, offsetMs);
                long offset = offsetMs + Interlocked.Read(ref _liveTrimMs);
                _pacer?.SetSyncOffsetMs(offset);
                _syncFinalized = true;
                _log?.LogInfo($"[ext-sync] A/V offset = {offsetMs}ms + trim {Interlocked.Read(ref _liveTrimMs)}ms " +
                              $"→ {offset}ms (audioRtp={aRaw} videoRtp={vRaw})");
            } else {
                long now = _srWaitClock.ElapsedMilliseconds;
                if (now - _lastSrWaitLogMs >= 2000) {
                    _lastSrWaitLogMs = now;
                    _log?.LogInfo($"[ext-sync] waiting for RTP-Info (aRtp={aRaw} vRtp={vRaw})");
                }
            }
        }

        // Live A/V trim: the computed cross-stream RTP-Info offset
        // (_baseOffsetMs) aligns the streams' content time, but a fixed
        // residual remains from physical pipeline latency (video present
        // chain vs audio device output) that software can't measure — only
        // the eye can. _liveTrimMs is added on top and can be nudged during
        // playback so the user can dial out that residual in real time.
        // Initialised from config's AudioSyncOffsetMs in the ctor.
        private long _baseOffsetMs;
        private long _liveTrimMs;

        /// <summary>Current user A/V trim in ms (positive = video earlier /
        /// less lag). Persist this back to config so it survives the session.</summary>
        public int CurrentAudioSyncTrimMs => (int)Interlocked.Read(ref _liveTrimMs);

        /// <summary>Adjust the A/V sync trim live and re-apply it to the pacer
        /// immediately. Positive deltas advance video (reduce video-lags-audio);
        /// negative delay it. Returns the new total trim (ms).</summary>
        public int NudgeAudioSyncTrim(int deltaMs) {
            long trim = Interlocked.Add(ref _liveTrimMs, deltaMs);
            long offset = Interlocked.Read(ref _baseOffsetMs) + trim;
            _pacer?.SetSyncOffsetMs(offset);
            _log?.LogInfo($"[ext-sync] A/V trim nudged {(deltaMs >= 0 ? "+" : "")}{deltaMs}ms " +
                          $"→ trim {trim}ms (base {Interlocked.Read(ref _baseOffsetMs)}ms → offset {offset}ms)");
            return (int)trim;
        }

        private static AVCodecID MapWireCodec(string wireCodec) {
            switch ((wireCodec ?? "").ToUpperInvariant()) {
                case "VND.MS.WM-MPV": return AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
                case "H264":
                case "X-WMF-PF":      return AVCodecID.AV_CODEC_ID_H264;
                default:              return AVCodecID.AV_CODEC_ID_H264;
            }
        }

        private static int MapClockHz(string wireCodec) {
            switch ((wireCodec ?? "").ToUpperInvariant()) {
                case "VND.MS.WM-MPV": return 90000;
                case "X-WMF-PF":      return 1000;
                default:              return 90000;
            }
        }

        // ============================================================
        //  IMediaController surface
        // ============================================================

        public TimeSpan Position {
            get {
                if (_renderer == null) return TimeSpan.Zero;
                return TimeSpan.FromMilliseconds(_renderer.GetMediaTimeMs());
            }
        }

        public TimeSpan? Duration => null;

        public bool IsOpen => _isOpen && _renderer != null;

        public Task WaitUntilOpenAsync(int timeoutMs) {
            if (IsOpen) return Task.CompletedTask;
            var tcs = _openTcs;
            if (timeoutMs < 0) return tcs.Task;
            if (timeoutMs == 0) return Task.CompletedTask;
            return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }

        public Task PlayAsync() {
            _playRequested = true;
            _renderer?.Play();   // video resumes automatically as the clock advances
            return Task.CompletedTask;
        }

        public Task PauseAsync() {
            _playRequested = false;
            _renderer?.Pause();  // master clock stalls → video holds on its last frame
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position) {
            try { _rtsp?.Play(startMs: (long)position.TotalMilliseconds, rate: _lastRequestedRate); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] RTSP seek failed: {ex.Message}"); }
            // Both streams jump — re-anchor the pacer so it re-aligns video to
            // the audio clock at the new position (the wire offset is unchanged
            // because the server re-sends prior-IDR padding on each PLAY).
            _pacer?.Reanchor();
            return Task.CompletedTask;
        }

        public Task SetRateAsync(double rate) {
            _lastRequestedRate = rate;
            bool normal = Math.Abs(rate - 1.0) < 0.0001;
            // Non-1x = server-side trick play: audio drops, so the master clock
            // stalls. Switch the pacer to free-run so the (fast-PTS) video keeps
            // updating; return to audio-slaved at 1x.
            _pacer?.SetFreeRun(!normal);
            if (normal) _pacer?.Reanchor();
            try { _rtsp?.SetRate(rate); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] RTSP SetRate({rate}) failed: {ex.Message}"); }
            return Task.CompletedTask;
        }

        public void SetAvailableBandwidth(long bitsPerSecond) {
            try { _rtsp?.SetBufferInfo(bitsPerSecond, _lastOptimisedPreroll); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] SetBufferInfo (bandwidth) failed: {ex.Message}"); }
            _lastBandwidthBps = bitsPerSecond;
        }

        public void SetOptimisedPreroll(bool optimised) {
            try { _rtsp?.SetBufferInfo(_lastBandwidthBps, optimised); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] SetBufferInfo (preroll) failed: {ex.Message}"); }
            _lastOptimisedPreroll = optimised;
        }

        // ============================================================
        //  Events
        // ============================================================

        public event Action BufferingEnded;
        public event Action MediaEnded;
        public event Action<Exception> MediaFailed;
        public event Action<Exception> RtspDisconnected;
        public event Action<PtsErrorInfo> PtsError;
        public event Action<SkewInfo> UnrecoverableSkew;

        private void OnRtspDisconnected(Exception ex) { try { RtspDisconnected?.Invoke(ex); } catch { } }
        private void OnRtspPtsError(PtsErrorInfo info) { try { PtsError?.Invoke(info); } catch { } }
        private void OnRtspUnrecoverableSkew(SkewInfo info) { try { UnrecoverableSkew?.Invoke(info); } catch { } }

        // ============================================================
        //  Teardown
        // ============================================================

        /// <summary>Tear down the per-media decode pipeline (video decoder +
        /// pacer, audio decoder + renderer) and reset the sync-anchor state so
        /// the next media starts clean and rebuilds decoders for ITS codec.
        /// Preserves session-scoped state: the presenter (session-owned) and
        /// the user's live A/V trim (<see cref="_liveTrimMs"/>). Idempotent.</summary>
        private void ResetPipelineForNewMedia() {
            lock (_videoGate) {
                try { _videoDecoder?.Complete(); } catch { }
                try { _videoDecoder?.Dispose(); } catch { }
                _videoDecoder = null;
                try { _pacer?.Dispose(); } catch { }
                _pacer = null;
            }
            try { _decoder?.Complete(); } catch { }
            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
            try { _renderer?.Dispose(); } catch { }
            _renderer = null;

            // Reset sync anchors / per-media clocks so the new stream's first
            // MAUs re-anchor and the offset is recomputed from scratch.
            _isOpen = false;
            _syncFinalized = false;
            _audioClockHz = 90000;
            _videoClockHz = 90000;
            Interlocked.Exchange(ref _firstAudioMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstVideoMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstVideoMauWirePtsMs, -1L);
            Interlocked.Exchange(ref _baseOffsetMs, 0L);
            // Fresh open gate so the next media's OpenAsync waits for the new
            // renderer rather than returning the previous media's result.
            if (!_disposed) _openTcs = new TaskCompletionSource<bool>();
            _log?.LogInfo("[ext-sync] pipeline reset for new media");
        }

        public void Dispose() {
            _disposed = true;
            AttachRtspClient(null);   // detaches + resets the pipeline
            ResetPipelineForNewMedia();
            // _presenter is owned by the session; it disposes it.
            _log?.LogInfo("[ext-sync] controller disposed");
        }
    }
}
