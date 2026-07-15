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
        private bool _paused;   // true between PauseAsync and the next PlayAsync (server RTSP-paused)
        private volatile bool _disposed;


        // Smoothed master clock. The raw audio-position clock (min bytes
        // played/written) JUMPS when delivery is bursty (live TV swings ~57-164%
        // of real-time), and the slaved video pacer whipsaws to track it →
        // "video starting and stopping". SmoothedMasterMs advances at wall-clock
        // real-time, never past the raw available-content position (so a genuine
        // underrun still stalls it) and never backwards, with a bounded catch-up
        // — turning the jumpy raw clock into a steady one. No-op for smooth
        // (recorded-TV) delivery where raw already tracks real-time.
        private readonly object _smoothLock = new object();
        private readonly System.Diagnostics.Stopwatch _smoothWall = System.Diagnostics.Stopwatch.StartNew();
        private double _smoothMasterMs = -1;
        private long _smoothLastWallMs;
        private const int MaxClockLagMs = 1000;
        // Above-real-time catch-up rate used to reconverge the smoothed clock to
        // raw when it has fallen behind (see SmoothedMasterMs). Without this the
        // smoother has NO way to close a gap — it only advances at real-time — so
        // after the startup preroll burst raced raw ahead it parked a full
        // MaxClockLagMs behind for the entire session, pacing all video ~1s off
        // the true audio clock even on perfectly smooth recorded delivery. Capped
        // at the same ≤15% momentary video-speed nudge the offset slew uses
        // (PtsFramePacer.OffsetSlewMsPerSec), so a sustained live-TV over-delivery
        // burst still can't be fully chased (smoother falls back toward the floor,
        // preserving the original jitter rejection) while smooth delivery
        // reconverges to raw — a true no-op, as originally intended.
        private const int MaxClockCatchUpMsPerSec = 150;

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

        // [av-timing] diagnostic: 1 Hz snapshot correlating the audio master
        // clock (and its underlying played/written byte positions vs real
        // wall-clock) against the video content the pacer has released and the
        // present-layer backlog. Purpose: surface the residual A/V skew that the
        // pacer's own drift accounting cannot see — the master→audible-audio and
        // release→on-screen latencies that leave video visibly ahead of audio
        // even when [pacer] drift ≈ 0. Diagnostic-only; never affects the clock.
        private System.Threading.Timer _avTimingTimer;
        private int _avTimingTick;

        // First audio + video MAU wire PTS (ms). Their difference is the wire
        // A/V offset the pacer needs. Each is converted from the stream's RTP
        // timestamp using that stream's RTP clock (90 kHz for wm-MPA/MPV,
        // 1 kHz for x-wmf-pf)
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
        // CONTENT-clock reference per stream: the B57/NPT content-ms of the first
        // (post-gate) MAU that carried one, PAIRED with that same MAU's wire ms.
        // The pair lets UpdateSyncOffset re-express the content time at ANY other
        // wire position (content and wire clocks advance 1:1 in ms within a
        // stream): contentAt(x) = RefContentMs + (x − RefWireMs). This is what
        // fixes the arrived-vs-decoded mismatch — the video sync origin is the
        // first DECODED frame (pacer pts0), which on decode-skip files is LATER
        // than the first arrived MAU the content value was read from. Latched
        // here (gated, per position) rather than read from RTSPClient's latch so
        // the pairing is exact and seek re-latching follows the anchor gate.
        private long _audContentRefMs = -1;      // content-ms of the audio reference MAU
        private long _audContentRefWireMs = -1;  // that MAU's wire pts (ms)
        private long _vidContentRefMs = -1;      // content-ms of the video reference MAU
        private long _vidContentRefWireMs = -1;  // that MAU's wire pts (ms)
        // After a seek, gate (re-)anchoring until the post-seek RTP-Info arrives.
        // _anchorMinRtpInfoGen = the RTSPClient.RtpInfoGeneration we must reach
        // before a MAU may anchor (0 = no gate, e.g. initial play). Without this,
        // a pre-seek in-flight MAU re-anchors ~9ms after the seek using the OLD
        // RTP-Info → wrong offset → A/V out of sync after a scrub.
        private long _anchorMinRtpInfoGen;
        private int  _seekGateTick;
        private const int SeekGateTimeoutMs = 3000; // fallback if RTP-Info never advances
        // No genuine A/V startup skew exceeds a few seconds; beyond this the
        // RTP-Info cross-stream offset is treated as bogus (Live TV npt=now).
        private const int MaxPlausibleOffsetMs = 5000;
        // The B57/NPT CONTENT clock is authoritative (verified shared presentation
        // timeline), so a large offset there is a legitimate long-GOP video I-frame
        // lead-in (e.g. 6.2s), NOT garbage — use a far higher bound so it isn't
        // wrongly clamped to 0 (which desyncs by seconds). Still bounded so a truly
        // absurd value can't make the pacer buffer unbounded decoded frames.
        private const int MaxPlausibleContentOffsetMs = 15000;
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
            // Refactor step 1/2: surface the pure offset-policy self-test (fixture
            // status) once at startup. Diagnostic only — proves the module loads
            // and shows which fixtures still need ground truth.
            try { _log?.LogInfo("[av-policy] " + AvSyncPolicy.SelfTest().Replace("\n", " | ")); } catch { }
        }

        // DEBUG: epoch-invariant sync mode (see SoftSledConfig.EpochInvariantSync).
        // Settable from the session on construction. When on, a seek's offset is
        // computed from the once-measured session RTP epoch + the fresh first-frame
        // origins, instead of the seek's own (sometimes inconsistent) RTP-Info.
        public bool EpochInvariantSync { get; set; }
        private long _sessionEpochMs;    // measured once per media at the initial play
        private bool _sessionEpochSet;   // reset per media in ResetPipelineForNewMedia

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
                _rtsp.EndOfStream       -= OnRtspEndOfStream;
                try { _rtsp.SetExternalAudioConsumer(null, null); } catch { }
                try { _rtsp.SetExternalVideoConsumer(null, null); } catch { }
                // Tear the decode pipeline down on every detach so the NEXT media rebuilds decoders for its own codec.
                ResetPipelineForNewMedia();
            }
            _rtsp = client;
            if (_rtsp != null) {
                _rtsp.Disconnected      += OnRtspDisconnected;
                _rtsp.PtsError          += OnRtspPtsError;
                _rtsp.UnrecoverableSkew += OnRtspUnrecoverableSkew;
                _rtsp.EndOfStream       += OnRtspEndOfStream;
                // NOTE: we deliberately do NOT subscribe CorrespondenceOffsetReady.
                // The Xbox reference capture proved the cross-stream RTP-Info offset
                // is rock-constant (video−audio = 3385 across the whole session), so
                // the Correspondence estimator's growing value was a least-squares
                // extrapolation ARTIFACT. Chasing it slewed the pacer and drained
                // the jitter buffer (judder). Sync is now locked to the stable
                // RTP-Info anchor (UpdateSyncOffset) + the user trim. The estimator
                // still runs in RTSPClient for diagnostics; we just ignore it.
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
            // x-wmf-pf audio uses a 1 kHz RTP wire clock; WMA uses its rtpmap
            // clock (wma/1000/2 → 1 kHz too); wm-MPA / AC-3 use 90 kHz. This
            // must match the divisor used in OnAudioMau so the wire A/V offset
            // is computed in real milliseconds.
            _audioClockHz = (wc == "X-WMF-PF" || wc == "WMA") ? 1000 : 90000;
            switch (wc) {
                case "MPA":
                case "VND.MS.WM-MPA":
                    codecId = (fmt.MpegLayer == 1 || fmt.MpegLayer == 2)
                        ? AVCodecID.AV_CODEC_ID_MP2 : AVCodecID.AV_CODEC_ID_MP3;
                    break;
                case "VND.MS.WM-AC3":
                    codecId = AVCodecID.AV_CODEC_ID_AC3;
                    break;
                case "WMA":
                    // Windows Media Audio. profile=1 / version=STD is the
                    // standard WMA (WMAV2); the SDP config= blob carries the
                    // codec-private extradata libav needs, and block_align /
                    // bit_rate / sample_rate / channels come from the fmtp.
                    codecId = AVCodecID.AV_CODEC_ID_WMAV2;
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
                bool isWma = wc == "WMA";
                if (isPcm) {
                    hintRate = fmt.SampleRateHint > 0 ? fmt.SampleRateHint : 48000;
                    hintChannels = fmt.ChannelsHint > 0 ? fmt.ChannelsHint : 2;
                }
                if (isWma) {
                    // WMA cannot probe rate/channels from the bitstream alone —
                    // they MUST be set on the codec context before open, along
                    // with block_align + the config= extradata. Pull them from
                    // the SDP fmtp (samplerate=44100, blocksize=5945, etc).
                    hintRate = fmt.SampleRateHint > 0 ? fmt.SampleRateHint : 44100;
                    hintChannels = fmt.ChannelsHint > 0 ? fmt.ChannelsHint : 2;
                    _decoder = new LibAvAudioDecoder(codecId, _log, hintRate, hintChannels,
                                                     fmt.BlockAlign, fmt.BitRate, fmt.ExtraData);
                } else {
                    _decoder = new LibAvAudioDecoder(codecId, _log, hintRate, hintChannels);
                }
                _decoder.OnFormatReady += OnDecoderFormatReady;
                _decoder.OnPcm         += OnDecodedPcm;
                // Backpressure: decode at the device's drain rate, not WMPNss's
                // ~121% audio flood. Reads the field at call time, so it safely
                // returns 0 (no backpressure) until the renderer is constructed.
                _decoder.PcmBufferedMsProvider = () => _renderer?.BufferedMs ?? 0;
                _decoder.Start();
                _log?.LogInfo($"[ext-sync] audio decoder started: codec={codecId} ({fmt})" +
                              (isPcm ? $" PCM hints: rate={hintRate}Hz ch={hintChannels}" : ""));
            } catch (Exception ex) {
                _log?.LogError($"[ext-sync] decoder init failed: {ex.Message}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        /// <summary>True once it's safe to anchor on incoming MAUs. After a seek
        /// this stays false until the post-seek RTP-Info has been parsed (so we
        /// discard pre-seek in-flight MAUs), with a timeout fallback so we never
        /// stall forever if the server doesn't re-send RTP-Info.</summary>
        private bool AnchorGateOpen() {
            long min = Interlocked.Read(ref _anchorMinRtpInfoGen);
            if (min <= 0) return true;                                  // no seek gate (initial play)
            if ((_rtsp?.RtpInfoGeneration ?? long.MaxValue) >= min) return true; // post-seek RTP-Info arrived
            if (unchecked(Environment.TickCount - _seekGateTick) > SeekGateTimeoutMs) return true; // fallback
            return false;
        }

        private void OnAudioMau(byte[] data, uint rtpTs, long contentMs) {
            if (_decoder == null) return;
            // Discard pre-seek in-flight MAUs until the post-seek RTP-Info lands.
            if (Interlocked.Read(ref _firstAudioMauRtpRaw) < 0 && !AnchorGateOpen()) return;
            long ptsMs = (long)rtpTs * 1000L / _audioClockHz;
            // Latch the content-clock reference (content-ms paired with THIS
            // MAU's wire ms) from the first post-gate MAU carrying one. Wire ms
            // is written first so a reader that sees content ≥ 0 sees both.
            if (contentMs >= 0 && Interlocked.Read(ref _audContentRefMs) < 0) {
                Interlocked.Exchange(ref _audContentRefWireMs, ptsMs);
                Interlocked.Exchange(ref _audContentRefMs, contentMs);
            }
            if (Interlocked.CompareExchange(ref _firstAudioMauWirePtsMs, ptsMs, -1L) == -1L) {
                Interlocked.Exchange(ref _firstAudioMauRtpRaw, rtpTs);
                _log?.LogDebug($"[ext-sync-anchor] first audio MAU rtpTs={rtpTs} ({ptsMs}ms, clk={_audioClockHz})");
                UpdateSyncOffset();
            }
            _decoder.SubmitPacket(data, ptsMs);
        }

        private void OnDecoderFormatReady() {
            try {
                // Start HELD so the renderer pre-rolls ~1s of audio before
                // playing — the device then starts with a cushion instead of
                // stuttering through the bursty first second of delivery (the
                // startup stutter the user reported on recorded TV / live).
                // The renderer auto-releases at its pre-roll target (or a
                // timeout). Video naturally pre-rolls too: it's slaved to the
                // audio master clock, which stays 0 while audio is held, so the
                // pacer just accumulates frames and releases when audio starts.
                _renderer = new NAudioMasterRenderer(
                    LibAvAudioDecoder.OutSampleRate,
                    LibAvAudioDecoder.OutChannels,
                    LibAvAudioDecoder.OutBitsPerSample,
                    _log,
                    startHeld: true);
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

        /// <summary>Rate-limited view of the audio master clock for video pacing.
        /// Advances at wall-clock real-time but is clamped to the raw
        /// available-content position (so a real underrun stalls it) and is
        /// monotonic. This removes the jump/stutter the raw clock shows under
        /// bursty delivery (which the slaved pacer would otherwise turn into
        /// visible video start/stop), while staying a no-op when raw is already
        /// smooth. Reset (resync to raw) on seek/pause via ResetMasterSmoothing.</summary>
        private long SmoothedMasterMs() {
            long raw = _renderer?.GetMediaTimeMs() ?? 0L;
            lock (_smoothLock) {
                long wallNow = _smoothWall.ElapsedMilliseconds;
                if (_smoothMasterMs < 0) {
                    _smoothMasterMs = raw; _smoothLastWallMs = wallNow; return raw;
                }
                long dt = wallNow - _smoothLastWallMs;
                if (dt < 0) dt = 0;
                _smoothLastWallMs = wallNow;
                double adv = _smoothMasterMs + dt;     // advance at real-time
                // Reconverge toward raw when behind, at a bounded (≤15%) rate.
                // This is the fix for the "parked a full second behind" bug: the
                // old code had no above-real-time term, so once the startup burst
                // opened a gap it never closed. Bounded so a live-TV over-delivery
                // swing still can't be fully chased (falls back toward the floor
                // below), but smooth recorded delivery converges to raw.
                double lag = raw - adv;
                if (lag > 0) {
                    double catchUp = dt * (MaxClockCatchUpMsPerSec / 1000.0);
                    if (catchUp > lag) catchUp = lag;  // converge exactly, don't overshoot
                    adv += catchUp;
                }
                if (adv > raw) adv = raw;              // never past available content (stall on underrun)
                if (raw - adv > MaxClockLagMs) adv = raw - MaxClockLagMs; // hard safety floor (kept)
                if (adv < _smoothMasterMs) adv = _smoothMasterMs;         // monotonic
                _smoothMasterMs = adv;
                return (long)adv;
            }
        }

        private void ResetMasterSmoothing() {
            lock (_smoothLock) { _smoothMasterMs = -1; }
        }

        /// <summary>Current smoothed-clock value WITHOUT advancing it (diagnostic
        /// only — the real advance happens in <see cref="SmoothedMasterMs"/>).
        /// The [av-timing] snapshot logs this next to raw so the smoother's
        /// convergence (or lag) is visible directly.</summary>
        private long SmoothedMasterPeekMs() {
            lock (_smoothLock) { return _smoothMasterMs < 0 ? 0 : (long)_smoothMasterMs; }
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
                // Codec-specific A/V trim. H.264: the RTP-Info play-point method
                // counts the video first-I-frame lead-in (vPad) ONCE, but WMPNss
                // needs it counted TWICE — so UpdateSyncOffset derives an extra
                // vPad per-file below; H264ExtraSyncOffsetMs is only a small
                // residual constant on top. MPEG-2 stays 0 (working sync untouched).
                _videoIsH264 = (id == AVCodecID.AV_CODEC_ID_H264);
                Interlocked.Exchange(ref _videoCodecTrimMs,
                    _videoIsH264 ? H264ExtraSyncOffsetMs : 0L);   // refined with derived vPad in UpdateSyncOffset
                _log?.LogInfo($"[ext-sync] video codec trim (pre-vPad) = {Interlocked.Read(ref _videoCodecTrimMs)}ms " +
                              $"(codec={id})");

                _pacer = new PtsFramePacer(
                    (ptr, stride, w, h) => _presenter?.SubmitFrame(ptr, stride, w, h),
                    prerollMs: _videoJitterBufferMs, _log);
                // Slave video presentation to the audio device clock.
                _pacer.SetMasterClock(SmoothedMasterMs);
                // Feed the video RTCP BFR W3 the pacer's REAL buffered span so
                // WMPNss sees the jitter buffer draining and speeds up to refill
                // — without this, video delivery settles ~3% under real-time and
                // the buffer slowly starves mid-playback (mirrors the audio fix).
                try {
                    // Report TOTAL video buffering = pacer's decoded-frame span +
                    // the decoder's undecoded backlog (held by backpressure). If we
                    // reported only the pacer span, the backlog would be invisible
                    // to WMPNss → it floods a post-seek burst, the backlog balloons,
                    // then it over-corrects and stalls (delivery oscillates → the
                    // video freezes and A/V drifts). Fields read at call time so the
                    // decoder (created just below) is picked up once it exists.
                    _rtsp?.SetVideoBufferOccupancyProvider(
                        () => (_pacer?.BufferedMs ?? 0) + (_videoDecoder?.InputQueuedMs ?? 0));
                } catch (Exception ex) {
                    _log?.LogError($"[ext-sync] SetVideoBufferOccupancyProvider failed: {ex.Message}");
                }
                UpdateSyncOffset();
                StartAvTimingDiag();

                var d = new LibAvVideoPushDecoder(id, clockHz, _log);
                // Backpressure: decode at the pacer's (audio-slaved) drain rate,
                // not H.264's bursty ~100fps delivery. The threshold tracks the
                // pacer's offset-dependent drop cap (BackpressureTargetMs) so it
                // engages at every offset — a fixed threshold missed small offsets
                // (maxBuffer < threshold) and the burst overflowed → frozen video.
                d.ShouldBackpressure = () => {
                    var p = _pacer;
                    return p != null && p.BufferedMs >= p.BackpressureTargetMs;
                };
                d.OnFrame += (ptr, stride, w, h, ptsMs) => {
                    // Capture the VIDEO sync origin from the first DECODED frame,
                    // NOT the first ARRIVED MAU (that was done in OnVideoMau). The
                    // pacer anchors pts0 on the first decoded frame; post-seek the
                    // first arrived MAU is usually a non-decodable P/B frame, so the
                    // decoder skips ~1s to the next I-frame. Computing the offset
                    // from the arrived MAU (an earlier, different origin than pts0)
                    // made it erratic (-2465/-873/+4066) and raced the video.
                    // Anchoring the offset to the SAME frame keeps them consistent.
                    if (Interlocked.Read(ref _firstVideoMauRtpRaw) < 0) {
                        // Post-seek, until the anchor gate opens (new RTP-Info has
                        // arrived) frames are STALE pre-seek frames still draining
                        // the decode pipeline. DROP them — do NOT submit to the
                        // pacer, or it anchors pts0 on a stale frame that differs
                        // from the offset origin (seen on a rapid drag-seek:
                        // pts0=109039 vs origin=101458 → 7.6s desync). Dropping
                        // makes the pacer's first frame == the offset origin.
                        if (!AnchorGateOpen()) return;
                        if (Interlocked.CompareExchange(ref _firstVideoMauWirePtsMs, ptsMs, -1L) == -1L) {
                            long rtp = ptsMs * _videoClockHz / 1000L;
                            Interlocked.Exchange(ref _firstVideoMauRtpRaw, rtp);
                            _log?.LogDebug($"[ext-sync-anchor] first DECODED video frame ptsMs={ptsMs} " +
                                           $"(rtp≈{rtp}, clk={_videoClockHz})");
                            if (!_syncFinalized) UpdateSyncOffset();
                        }
                    }
                    _pacer?.Submit(ptr, stride, w, h, ptsMs);
                };
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

        private void OnVideoMau(byte[] data, uint rtpTs, long contentMs) {
            // Discard pre-seek in-flight MAUs until the post-seek RTP-Info lands.
            // NOTE: the video sync origin (_firstVideoMauRtpRaw / WirePtsMs) is now
            // captured in the decoder's OnFrame handler (first DECODED frame =
            // the pacer's pts0 anchor), NOT here — the first ARRIVED MAU is often
            // a non-decodable P/B frame that the decoder skips, so anchoring the
            // offset here used a different origin than the pacer and skewed sync.
            if (Interlocked.Read(ref _firstVideoMauRtpRaw) < 0 && !AnchorGateOpen()) return;
            // Latch the content-clock reference from the first post-gate MAU
            // carrying one (paired with this MAU's wire ms). UpdateSyncOffset
            // re-expresses it at the first DECODED frame's wire pts, which
            // corrects the content offset on decode-skip lead-ins.
            if (contentMs >= 0 && Interlocked.Read(ref _vidContentRefMs) < 0) {
                Interlocked.Exchange(ref _vidContentRefWireMs, (long)rtpTs * 1000L / _videoClockHz);
                Interlocked.Exchange(ref _vidContentRefMs, contentMs);
            }
            // DIAGNOSTIC (temporary): log the first video MAUs' arriving rtpTs so we
            // can tell whether a late first DECODED frame (seen: 12096ms while the
            // play point was 530ms → offset clamped to 0 → seconds out) is a
            // DELIVERY gap (arriving rtpTs jumps) or a DECODE skip (arriving rtpTs
            // contiguous from the play point but no decodable frame until later).
            int n = Interlocked.Increment(ref _videoMauDiagCount);
            if (n <= 20) {
                long ms = (long)rtpTs * 1000L / _videoClockHz;
                _log?.LogDebug($"[ext-sync-diag] video MAU #{n} rtpTs={rtpTs} ({ms}ms) len={data?.Length ?? 0}");
            }
            _videoDecoder?.SubmitPacket(data, rtpTs);
        }
        private int _videoMauDiagCount;

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
            // NOTE (2026-07-06): a codec-split "MPEG-2 = direct first-MAU diff"
            // path was tried (32nd fix) on the premise that MPEG-2 audio/video
            // share one RTP epoch. Disproven by log softsled-20260706-202311: the
            // first MAUs were 2920ms apart (A0=589,V0=3509) but the play points
            // only ~249ms apart (aInfo=48938,vInfo=26517) — the 2920ms is almost
            // all ASYMMETRIC LEAD-IN PADDING (WMC skips a variable number of
            // lead-in MAUs), NOT content skew. So the raw first-MAU diff is
            // corrupted; the RTP-Info play-point method (below) correctly gave
            // -282ms. RTP-Info is the right method for BOTH codecs — reverted.

            // WMPNss doesn't send RTCP Sender Reports, so the cross-stream
            // offset comes from the PLAY response's RTP-Info (each stream's
            // play-point RTP timestamp) — epoch-free and available immediately.
            if (_rtsp != null && _rtsp.TryGetRtpInfoAvOffsetMs((uint)aRaw, (uint)vRaw, out long offsetMs, out long videoPadMs)) {
                long rtpInfoCandidate = offsetMs;   // raw RTP-Info value, pre epoch-invariant/clamp (candidate logging)

                // PREFERRED: the B57532D6 / NPT file-global CONTENT clock. Both streams'
                // content times are on the shared ASF presentation timeline (not the
                // per-stream Send-Time RTP epoch), so their difference is the true,
                // epoch-free cross-stream offset — no vPad doubling, no direct-vs-
                // rtpinfo selection, no per-session drift. When available it REPLACES the
                // RTP-Info method entirely; the codec trim drops to just the residual.
                //
                // Each stream's content time is re-expressed AT ITS SYNC ORIGIN using
                // the latched (content, wire-ms) reference pair — content and wire
                // clocks advance 1:1 in ms within a stream. For video the origin is
                // the first DECODED frame (the pacer's pts0), which on decode-skip
                // lead-ins is LATER than the arrived MAU the content value came from;
                // using the arrived MAU's content directly mis-aligned the offset by
                // the skipped span (same arrived-vs-decoded class as the old RTP
                // anchor bug). For audio origin == reference MAU, so the delta is 0.
                long aRefC = Interlocked.Read(ref _audContentRefMs);
                long vRefC = Interlocked.Read(ref _vidContentRefMs);
                long a0ms  = Interlocked.Read(ref _firstAudioMauWirePtsMs);
                long v0ms  = Interlocked.Read(ref _firstVideoMauWirePtsMs);
                bool haveContent = aRefC >= 0 && vRefC >= 0 && a0ms >= 0 && v0ms >= 0;
                if (haveContent) {
                    long aContentMs = aRefC + (a0ms - Interlocked.Read(ref _audContentRefWireMs));
                    long vSkipMs    = v0ms - Interlocked.Read(ref _vidContentRefWireMs);
                    long vContentMs = vRefC + vSkipMs;
                    long contentOffset = aContentMs - vContentMs;
                    _log?.LogInfo($"[ext-sync] CONTENT offset (B57/NPT) = {contentOffset}ms " +
                                  $"(audioContent={aContentMs}ms videoContent={vContentMs}ms" +
                                  (vSkipMs != 0 ? $" [decode-skip corrected +{vSkipMs}ms]" : "") +
                                  $"; rtpinfo was {rtpInfoCandidate}ms; Δ={contentOffset - rtpInfoCandidate}ms)");
                    offsetMs = contentOffset;
                    if (_videoIsH264) Interlocked.Exchange(ref _videoCodecTrimMs, H264ExtraSyncOffsetMs);
                }

                // FALLBACK (no content clock from the server): the RTP-Info method.
                // H.264 derives the codec trim = video first-I-frame lead-in pad (vPad),
                // picking min-magnitude of {direct, 2·vPad}. MPEG-2 keeps codec trim 0.
                if (!haveContent && _videoIsH264) {
                    // H.264 cross-stream offset selection. Two candidates:
                    //   • 2·vPad = rtpinfo + vPad (RTP-Info play-point method,
                    //     doubled — right when the A/V RTP epochs are independent).
                    //   • direct = A0 − V0 (first-MAU diff — right when the epochs
                    //     are comparable, in which case it comes out SMALL; when
                    //     they're independent it blows up huge = obviously garbage).
                    // Rule (validated on 3 precisely-tuned files: Play2 direct 3ms,
                    // File2 2·vPad 280ms, Play7 2·vPad 28ms): take whichever has the
                    // SMALLER magnitude — direct when it's plausibly small, else the
                    // RTP-Info fallback. Derivable, no B-frame/fps dependence, no
                    // manual trim. codecTrim carries (chosen − rtpinfo) so the base
                    // (_baseOffsetMs=rtpinfo) + codecTrim lands on the chosen value.
                    long twoVpad = offsetMs + videoPadMs;
                    var dcand = AvSyncPolicy.ComputeDirect(aRaw, _audioClockHz, vRaw, _videoClockHz);
                    long chosen; string src;
                    if (dcand.Valid && Math.Abs(dcand.OffsetMs) < Math.Abs(twoVpad)) {
                        chosen = dcand.OffsetMs; src = "direct";
                    } else {
                        chosen = twoVpad; src = "2·vPad";
                    }
                    Interlocked.Exchange(ref _videoCodecTrimMs, (chosen - offsetMs) + H264ExtraSyncOffsetMs);
                    _log?.LogInfo($"[ext-sync] H.264 offset select: direct={(dcand.Valid ? dcand.OffsetMs.ToString() : "n/a")} " +
                                  $"2·vPad={twoVpad} → chose {src}={chosen}ms " +
                                  $"(codecTrim {chosen - offsetMs} + residual {H264ExtraSyncOffsetMs})");
                }
                // Capture the session RTP epoch ONCE (initial play, before any
                // seek — the play points are reliable there). Reused by the
                // epoch-invariant mode across seeks. Cheap; harmless when the mode
                // is off.
                if (!_sessionEpochSet && _rtsp.TryGetRtpInfoEpochMs(out long epMs)) {
                    _sessionEpochMs = epMs;
                    _sessionEpochSet = true;
                    _log?.LogInfo($"[ext-sync] session RTP epoch captured = {epMs}ms");
                }
                // DEBUG epoch-invariant mode: recompute the offset from the stored
                // epoch + the FRESH first-frame origins, rather than this seek's
                // own RTP-Info pad diff (which WMPNss can report inconsistently).
                // At the initial play this equals the per-seek value; it only
                // diverges when a seek's play points drift from the true epoch.
                if (EpochInvariantSync && _sessionEpochSet && !haveContent) {
                    // a0ms/v0ms: the first-MAU wire origins already read above
                    // for the content path (identical values).
                    long epOffset = _sessionEpochMs - (v0ms - a0ms);
                    _log?.LogInfo($"[ext-sync] epoch-invariant offset {epOffset}ms " +
                                  $"(per-seek would be {offsetMs}ms; epoch {_sessionEpochMs}ms − (v0 {v0ms} − a0 {a0ms}))");
                    offsetMs = epOffset;
                }
                // Refactor step 2: compute + log ALL offset candidates side-by-side
                // via the pure AvSyncPolicy, and cross-check the legacy selection.
                // DIAGNOSTIC ONLY — the applied offset below is unchanged
                // (behaviour-preserving). This yields the direct-vs-rtpinfo-vs-corr
                // data the discriminator needs, per file.
                var cand = new AvSyncPolicy.Candidates(
                    AvSyncPolicy.ComputeDirect(aRaw, _audioClockHz, vRaw, _videoClockHz),
                    new AvSyncPolicy.Candidate(true, rtpInfoCandidate, "rtpinfo"),
                    (_rtsp != null && _rtsp.TryGetCorrespondenceOffsetMs(out long corrMs))
                        ? new AvSyncPolicy.Candidate(true, corrMs, "correspondence")
                        : AvSyncPolicy.Candidate.None);
                var legacy = AvSyncPolicy.ChooseLegacy(cand);
                _log?.LogInfo($"[av-candidates] {cand} | legacy→{legacy.Source}={legacy.OffsetMs}ms" +
                              (legacy.Clamped ? " (clamped)" : "") +
                              $" (audioRtp={aRaw} videoRtp={vRaw} aClk={_audioClockHz} vClk={_videoClockHz})");

                // Sanity gate: no real A/V startup skew exceeds a few seconds.
                // A larger value means the RTP-Info / first-MAU relationship
                // didn't follow the prior-IDR-padding model — e.g. Live TV
                // (npt=now), where the audio RTP-Info play point can sit tens
                // of seconds from the first delivered audio MAU and inflates
                // the offset. In that case the streams' first MAUs actually
                // arrive together (the live edge), so 0 is the right answer.
                int plausibleBound = haveContent ? MaxPlausibleContentOffsetMs : MaxPlausibleOffsetMs;
                if (Math.Abs(offsetMs) > plausibleBound) {
                    _log?.LogInfo($"[ext-sync] {(haveContent ? "content" : "RTP-Info")} offset {offsetMs}ms implausible " +
                                  $"(>{plausibleBound}ms) — falling back to 0 (streams assumed to start together)");
                    offsetMs = 0;
                    // Offset rejected → drop the H.264 codec trim back to just the residual.
                    if (_videoIsH264) Interlocked.Exchange(ref _videoCodecTrimMs, H264ExtraSyncOffsetMs);
                }
                Interlocked.Exchange(ref _baseOffsetMs, offsetMs);
                long offset = offsetMs + EffectiveTrimMs();
                _pacer?.SetSyncOffsetMs(offset);
                _syncFinalized = true;
                _log?.LogInfo($"[ext-sync] A/V offset = {offsetMs}ms + trim {Interlocked.Read(ref _liveTrimMs)}ms" +
                              (Interlocked.Read(ref _videoCodecTrimMs) != 0 ? $" + codecTrim {Interlocked.Read(ref _videoCodecTrimMs)}ms" : "") +
                              $" → {offset}ms (audioRtp={aRaw} videoRtp={vRaw})");
            } else {
                long now = _srWaitClock.ElapsedMilliseconds;
                if (now - _lastSrWaitLogMs >= 2000) {
                    _lastSrWaitLogMs = now;
                    _log?.LogInfo($"[ext-sync] waiting for RTP-Info (aRtp={aRaw} vRtp={vRaw})");
                }
            }
        }

        // Live A/V trim: the computed cross-stream offset (_baseOffsetMs)
        // aligns the streams' content time, but a residual remains that
        // software can't measure — only the eye can. _liveTrimMs is added on
        // top and can be nudged during playback (Ctrl+]/Ctrl+[) to dial that
        // residual out in real time. Seeded from config's AudioSyncOffsetMs
        // (the user's persistent baseline, e.g. display/AVR latency) in the
        // ctor AND re-seeded to it on every new media — the residual proved
        // per-file, so nudges are per-media tuning and must not carry over.
        private long _baseOffsetMs;
        private long _liveTrimMs;

        // Extra A/V trim applied ONLY for the current video codec. H.264 needs a
        // small positive (video-forward) trim for its larger present / B-frame
        // reorder latency that the content-time offset can't measure; MPEG-2 = 0
        // (leaves the working MPEG-2 config untouched). Combined with the user's
        // _liveTrimMs at every point the pacer offset is set. Set from config's
        // H264ExtraSyncOffsetMs when the H.264 video codec commits.
        private long _videoCodecTrimMs;
        private bool _videoIsH264;   // set at codec commit; gates the derived-vPad H.264 offset
        public int H264ExtraSyncOffsetMs { get; set; }
        private long EffectiveTrimMs() =>
            Interlocked.Read(ref _liveTrimMs) + Interlocked.Read(ref _videoCodecTrimMs);

        /// <summary>Current user A/V trim in ms (positive = video earlier /
        /// less lag). Persist this back to config so it survives the session.</summary>
        public int CurrentAudioSyncTrimMs => (int)Interlocked.Read(ref _liveTrimMs);

        /// <summary>Adjust the A/V sync trim live and re-apply it to the pacer
        /// immediately. Positive deltas advance video (reduce video-lags-audio);
        /// negative delay it. Returns the new total trim (ms).</summary>
        public int NudgeAudioSyncTrim(int deltaMs) {
            long trim = Interlocked.Add(ref _liveTrimMs, deltaMs);
            long offset = Interlocked.Read(ref _baseOffsetMs) + trim + Interlocked.Read(ref _videoCodecTrimMs);
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
            bool resuming = _paused;

            // Un-pause the LOCAL pipeline so the decoders + pacer are live again,
            // and resume the audio device from its RETAINED buffer (instant).
            lock (_videoGate) {
                _videoDecoder?.Resume();
                _pacer?.SetPaused(false);
            }
            // The AVCTRL Start handler calls SetRateAsync THEN PlayAsync, so a FF/RW
            // Start lands here right after SetRateAsync silenced audio for trick
            // play — do NOT resume audio while in trick play (rate ≠ 1×), or it
            // plays out the reservoir for several seconds. WMC stops sending audio
            // during trick play; the return-to-1× Start (via SetRateAsync) resumes.
            bool trickPlay = Math.Abs(_lastRequestedRate - 1.0) >= 0.0001;
            if (!trickPlay) {
                _decoder?.Resume();
                _renderer?.Play();
            }

            if (resuming) {
                _paused = false;
                // Resume EXACTLY like the reference Xbox extender: a bare PLAY
                // with NO Range (and "Scale: 1.000", which RTSPClient now always
                // sends). WMPNss resumes at the pause point and CONTINUES the same
                // RTP timeline — so the retained audio buffer + video queue + sync
                // anchors stay valid. No Range, no re-baseline, no buffer flush,
                // no position bookkeeping. (The earlier +30s jump was purely from
                // omitting Scale on the resume PLAY.)
                // RTSPClient.Pause() armed _resumeNextPlay so this PLAY hits the
                // wire even though (startMs,rate) match the pre-pause values.
                ResetMasterSmoothing();   // re-seed the smoothed clock across the pause gap
                try { _rtsp?.Play(-1L, _lastRequestedRate); }
                catch (Exception ex) { _log?.LogError($"[ext-sync] resume PLAY failed: {ex.Message}"); }
                _log?.LogInfo("[ext-sync] resume from pause — bare PLAY (Scale, no Range); timeline continues, buffers kept");
            }
            return Task.CompletedTask;
        }

        public Task PauseAsync() {
            _playRequested = false;
            _paused = true;

            // Freeze the LOCAL pipeline FIRST, retaining every buffer so the
            // resume is instant. Pausing the audio device stalls the master
            // clock → the pacer holds the last video frame; explicitly pausing
            // the decoders + pacer stops any further decode/release so the
            // already-decoded PCM (in NAudio's BufferedWaveProvider) and the
            // queued video frames stay intact.
            lock (_videoGate) {
                _videoDecoder?.Pause();
                _pacer?.SetPaused(true);
            }
            _decoder?.Pause();
            _renderer?.Pause();  // master clock stalls → video holds on its last frame

            // THEN PAUSE the RTSP server so it stops sending RTP — otherwise it
            // keeps streaming into a now-frozen (non-draining) buffer, which
            // overflows on a long pause, the honest BFR W3 goes full, and the
            // server throttles/stops on its own (messy resume).
            try { _rtsp?.Pause(); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] RTSP PAUSE failed: {ex.Message}"); }
            _log?.LogInfo("[ext-sync] pause — local pipeline frozen (buffers retained), RTSP PAUSE");
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position) {
            try {
                // Seeking a PLAYING WMPNss stream: a bare PLAY-with-Range on the
                // live session makes the server ACK (200 OK + new RTP-Info) but
                // then STOP sending RTP — audio/video MAUs cease at the seek and
                // A/V freeze; only a full stop/restart recovers (confirmed in
                // logs: libav-audio stats stop at the seek instant, RTCP w3→0,
                // highest-seq frozen). The standard RTSP seek for a playing
                // stream is PAUSE → PLAY(Range): the PAUSE resets the server's
                // streaming/BFR state so it resumes cleanly from the new point.
                // Pause() arms _resumeNextPlay so the following Play hits the
                // wire even though the Range differs.
                //
                // ONLY pause when the stream is ALREADY playing (a true
                // mid-session seek). A "seek" with StartTime>0 also arrives at
                // OPEN as a resume-from-last-position, BEFORE the stream is
                // established (renderer not ready, _isOpen=false). PAUSE→PLAY
                // there disrupts WMPNss's initial handshake/pacing — observed:
                // audio delivery thrashes 30%↔172%, the min(played,written)
                // master clock stutters, and the video pacer wedges and never
                // recovers. At open/resume there's nothing to pause; just PLAY
                // with the start position (the server seeks on the initial PLAY).
                if (_isOpen) {
                    _rtsp?.Pause();
                    // Freeze the audio device clock too, so ReBaselineSync's
                    // ClearBuffer captures a CLEAN segment-start (the frozen
                    // playhead). Without this the still-playing device outputs
                    // silence through the seek's brief underrun, drifting the
                    // playhead past the true segment start → the re-anchor
                    // over-catches-up and the seek lands slightly out of sync.
                    // FF already freezes the renderer (trick-play audio stop),
                    // which is why FF is clean and a seek wasn't.
                    try { _renderer?.Pause(); } catch { }
                }
                _rtsp?.Play(startMs: (long)position.TotalMilliseconds, rate: _lastRequestedRate);
            } catch (Exception ex) { _log?.LogError($"[ext-sync] RTSP seek failed: {ex.Message}"); }

            ReBaselineSync($"seek to {(long)position.TotalMilliseconds}ms");
            // Resume the device now that the clean segment-start is captured.
            if (_isOpen && _playRequested) { try { _renderer?.Play(); } catch { } }
            return Task.CompletedTask;
        }

        /// <summary>Re-baseline the A/V sync after a position change (seek, or
        /// returning to 1× from server-side trick play). Both streams restart
        /// from a new position with a NEW RTP-Info and new first-MAU timestamps,
        /// so the prior cross-stream offset no longer applies — keeping it leaves
        /// audio/video out of sync. Clears the sync anchors + finalized flag
        /// (WITHOUT tearing down decoders/renderer — playback continues; same
        /// media so codec/clock stay intact), gates re-anchoring until the new
        /// RTP-Info arrives (discards pre-change in-flight MAUs), resets the
        /// Correspondence estimator, and re-anchors the pacer. The user trim
        /// (_liveTrimMs) persists as the fixed residual.</summary>
        /// <summary>Clear the per-position content-clock reference pairs so the
        /// next position's first MAUs re-latch them. Must accompany every anchor
        /// reset — stale refs would make the recomputed offset reuse the PREVIOUS
        /// position's content times.</summary>
        private void ResetContentRefs() {
            Interlocked.Exchange(ref _audContentRefMs, -1L);
            Interlocked.Exchange(ref _audContentRefWireMs, -1L);
            Interlocked.Exchange(ref _vidContentRefMs, -1L);
            Interlocked.Exchange(ref _vidContentRefWireMs, -1L);
        }

        private void ReBaselineSync(string reason) {
            // A seek RECOMPUTES the offset (unlike trick-play, which keeps it):
            // each seek lands at a new point with new lead-in, so the offset
            // genuinely differs. Correctness now relies on the video origin being
            // captured from the first DECODED frame (see the decoder OnFrame
            // handler) so it matches the pacer's pts0 anchor — the earlier erratic
            // -2465/-873/+4066 values came from using the first ARRIVED MAU.
            _syncFinalized = false;
            Interlocked.Exchange(ref _firstAudioMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstVideoMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstAudioMauWirePtsMs, -1L);
            Interlocked.Exchange(ref _firstVideoMauWirePtsMs, -1L);
            ResetContentRefs();   // new position = new content anchors
            Interlocked.Exchange(ref _anchorMinRtpInfoGen, (_rtsp?.RtpInfoGeneration ?? 0) + 1);
            _seekGateTick = Environment.TickCount;
            // The estimator still runs for diagnostics; reset it so its logged
            // fit restarts cleanly at the new position (we don't act on it).
            try { _rtsp?.ResetCorrespondenceEstimator(); } catch { }
            // Drop stale pre-seek audio: the deep coded reservoir (input queue)
            // plus the renderer's PCM backlog would otherwise play the OLD
            // position's audio at the NEW one. Flush both so audio restarts clean.
            // Flush video too so no stale pre-seek frame reaches the pacer and
            // becomes its pts0 anchor (which would skew the recomputed offset).
            try { _decoder?.Flush(); } catch { }
            try { _videoDecoder?.Flush(); } catch { }
            try { _renderer?.ClearBuffer(); } catch { }
            // Anchor the re-anchored video to where the new audio segment ACTUALLY
            // starts playing (not the later video-frame-arrival master), so video
            // catches up instead of baking in its decode latency as audio-ahead.
            try { long seg = _renderer?.SegmentStartMasterMs ?? -1L; if (seg >= 0) _pacer?.SetReanchorMasterOverride(seg); } catch { }
            _pacer?.Reanchor();
            ResetMasterSmoothing();
            _log?.LogInfo($"[ext-sync] {reason} — re-baselining A/V sync");
        }

        /// <summary>
        /// Re-anchor the timeline after a trick-play (FF/RW) return to 1×.
        /// Deliberately SEPARATE from <see cref="ReBaselineSync"/> (used by
        /// seek/resume) so the two can evolve independently — in particular the
        /// pending pause/resume rework must not disturb trick-play, and vice
        /// versa.
        ///
        /// <para>Like a seek it re-anchors the pacer, re-arms the anchor gate to
        /// discard the trick-play frame backlog until the new (1×) RTP-Info
        /// arrives, and RECOMPUTES the cross-stream offset. Trick-play exit lands
        /// at a new position whose A/V lead-in differs from the initial play, so
        /// the offset is NOT constant (keeping the old value left audio ~3.4s out
        /// of sync). Recompute is now reliable because the video origin comes from
        /// the first DECODED frame (matching the pacer's pts0 anchor) — the old
        /// "recompute → 0ms, raced the video" failure was that origin mismatch
        /// (first ARRIVED MAU), since fixed. Kept SEPARATE from ReBaselineSync so
        /// the two can still evolve independently.</para>
        /// </summary>
        private void ReanchorAfterTrickPlay() {
            _syncFinalized = false;
            Interlocked.Exchange(ref _firstAudioMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstVideoMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstAudioMauWirePtsMs, -1L);
            Interlocked.Exchange(ref _firstVideoMauWirePtsMs, -1L);
            ResetContentRefs();   // new position = new content anchors
            Interlocked.Exchange(ref _anchorMinRtpInfoGen, (_rtsp?.RtpInfoGeneration ?? 0) + 1);
            _seekGateTick = Environment.TickCount;
            try { _rtsp?.ResetCorrespondenceEstimator(); } catch { }
            // Drop the trick-play audio backlog (deep coded queue + PCM buffer)
            // and stale video frames so 1× resumes from the new position without
            // replaying stale audio or anchoring the pacer on a stale frame.
            try { _decoder?.Flush(); } catch { }
            try { _videoDecoder?.Flush(); } catch { }
            try { _renderer?.ClearBuffer(); } catch { }
            // Anchor the re-anchored video to where the new audio segment ACTUALLY
            // starts playing (not the later video-frame-arrival master), so video
            // catches up instead of baking in its decode latency as audio-ahead.
            try { long seg = _renderer?.SegmentStartMasterMs ?? -1L; if (seg >= 0) _pacer?.SetReanchorMasterOverride(seg); } catch { }
            _pacer?.Reanchor();
            ResetMasterSmoothing();
            _log?.LogInfo("[ext-sync] trick-play exit → 1× — re-anchored, recomputing offset");
        }

        public Task SetRateAsync(double rate) {
            // NO-OP when the rate isn't actually changing. WMC's AVCTRL handler
            // calls SetRateAsync on EVERY Start for rate-forwarding — including
            // the StartTime=0 at open and every resume/seek Start (all at 1×).
            // Acting on those (sending an RTSP SetRate→PLAY and re-baselining)
            // fires a redundant PLAY during the open handshake / alongside the
            // seek's own PLAY — a double-PLAY that disrupts WMPNss and yields NO
            // playback on resume, plus a spurious re-baseline that arms the
            // anchor gate before anything is streaming. Only a genuine rate
            // CHANGE (entering or exiting trick play) should do anything.
            double prevRate = _lastRequestedRate;
            if (Math.Abs(rate - prevRate) < 0.0001) return Task.CompletedTask;
            _lastRequestedRate = rate;
            bool normal = Math.Abs(rate - 1.0) < 0.0001;
            // Non-1x = server-side trick play: audio drops, so the master clock
            // stalls. Switch the pacer to free-run so the (fast-PTS) video keeps
            // updating; return to audio-slaved at 1x.
            _pacer?.SetFreeRun(!normal);
            if (!normal) {
                // Entering trick play: WMC stops sending audio, but our decode
                // reservoir (the deep backpressure queue) still holds buffered
                // audio that would keep playing through the FF/RW. Silence audio
                // NOW — pause the device + hold the decoder. Returning to 1× below
                // resumes them, and ReanchorAfterTrickPlay flushes the stale
                // reservoir + clears the PCM buffer so audio restarts clean.
                try { _renderer?.Pause(); } catch { }
                try { _decoder?.Pause(); } catch { }
            }
            try { _rtsp?.SetRate(rate); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] RTSP SetRate({rate}) failed: {ex.Message}"); }
            // Reaching here with normal=true means we were in trick play (prev
            // rate ≠ 1×) and are returning to 1× — that lands at a NEW position
            // with NEW RTP-Info, so re-anchor the timeline and RECOMPUTE the
            // offset (the exit position's A/V lead-in differs from the initial
            // play's, so the old offset no longer applies). Kept on a dedicated
            // path (NOT ReBaselineSync) so it can evolve independently of
            // seek/resume. Entering trick play (rate≠1) needs nothing — the
            // pacer is free-run and ignores the offset.
            if (normal) {
                // Returning to 1×: re-anchor (flushes the stale reservoir + clears
                // the PCM buffer), then resume audio decode + playback so the new
                // 1× audio streams in fresh.
                ReanchorAfterTrickPlay();
                try { _decoder?.Resume(); } catch { }
                if (_playRequested) { try { _renderer?.Play(); } catch { } }
            }
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
        // Server pushed an RTSP ANNOUNCE end-of-stream event (DLNA 2000).
        // Surface it as MediaEnded; the AVCTRL handler maps that to the
        // MS-DMCT END_OF_MEDIA event so WMC tears down / advances.
        private void OnRtspEndOfStream() {
            _log?.LogInfo("[ext-sync] RTSP EndOfStream → MediaEnded");
            try { MediaEnded?.Invoke(); } catch { }
        }

        // ============================================================
        //  Teardown
        // ============================================================

        /// <summary>
        /// Stop all audio/video output RIGHT NOW. Used on CloseMedia/Stop so
        /// playback ceases the instant WMC asks, rather than continuing through
        /// the decode-pipeline teardown below (whose Dispose Joins, and the
        /// audio device's buffered PCM, would otherwise keep sound playing for a
        /// noticeable tail). <see cref="NAudioMasterRenderer.Stop"/> halts the
        /// WaveOut device and discards its queued buffers immediately;
        /// ClearBuffer drops the provider's backlog; freezing the pacer stops
        /// any further video frame from being presented. The on-screen surface
        /// is blanked separately by the session (VideoPipelineClosed). Safe to
        /// call repeatedly / from any thread.
        /// </summary>
        public void HaltPlaybackNow() {
            try { _renderer?.Stop(); } catch { }
            try { _renderer?.ClearBuffer(); } catch { }
            lock (_videoGate) { try { _pacer?.SetPaused(true); } catch { } }
        }

        /// <summary>Tear down the per-media decode pipeline (video decoder +
        /// pacer, audio decoder + renderer) and reset the sync-anchor state so
        /// the next media starts clean and rebuilds decoders for ITS codec.
        /// Preserves the session-owned presenter. The live A/V trim is reset to
        /// the config baseline (<see cref="AudioSyncOffsetMs"/>): the residual
        /// turned out to be per-file, so carrying one file's tuned trim into the
        /// next contaminated its sync (and any subsequent tuning). Idempotent.</summary>
        private void ResetPipelineForNewMedia() {
            // Silence the device + freeze video BEFORE the dispose Joins below,
            // so a media switch (or any teardown) doesn't leak the old media's
            // buffered audio while we wait on the decode threads to exit.
            HaltPlaybackNow();
            StopAvTimingDiag();
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
            // Reset the smoothed master clock. It's monotonic (never decreases)
            // WITHIN a video, but the new media's renderer restarts its byte
            // position at 0 — without this reset, _smoothMasterMs stays pinned at
            // the PREVIOUS video's end (e.g. 21371ms), so every frame of the next
            // video is instantly "due" and the pacer races it forward instead of
            // matching audio. (Manifested as 2nd/3rd plays speeding up; a restart
            // "fixed" it only because that cleared the stale value.)
            ResetMasterSmoothing();

            // Reset sync anchors / per-media clocks so the new stream's first
            // MAUs re-anchor and the offset is recomputed from scratch.
            _isOpen = false;
            _syncFinalized = false;
            _sessionEpochSet = false;      // new media = new RTP session = new epoch
            _videoMauDiagCount = 0;        // re-arm the first-MAU arrival diagnostic
            Interlocked.Exchange(ref _videoCodecTrimMs, 0L);  // re-set on next codec commit
            _audioClockHz = 90000;
            _videoClockHz = 90000;
            Interlocked.Exchange(ref _firstAudioMauRtpRaw, -1L);
            Interlocked.Exchange(ref _firstVideoMauRtpRaw, -1L);
            // BOTH WirePts capture-gates must reset, not just video. OnAudioMau
            // gates its first-MAU anchor capture on _firstAudioMauWirePtsMs; if
            // it isn't reset, the 2nd media in a session never re-captures the
            // audio anchor → _firstAudioMauRtpRaw stays -1 → UpdateSyncOffset
            // bails (aRaw<0) → the offset is never applied → pacer runs at
            // off=0 → 2nd recording plays out of sync. (Was missing here.)
            Interlocked.Exchange(ref _firstAudioMauWirePtsMs, -1L);
            Interlocked.Exchange(ref _firstVideoMauWirePtsMs, -1L);
            ResetContentRefs();
            Interlocked.Exchange(ref _baseOffsetMs, 0L);
            // Reset the live trim to the config baseline. The trim was designed
            // to carry a FIXED pipeline/display residual across media, but the
            // residual is per-file — carrying one file's tuned value poisons the
            // next file's sync and any tuning done from it (seen repeatedly:
            // a trim dialled for one recording applied to the next).
            long prevTrim = Interlocked.Exchange(ref _liveTrimMs, AudioSyncOffsetMs);
            if (prevTrim != AudioSyncOffsetMs) {
                _log?.LogInfo($"[ext-sync] live trim reset for new media: {prevTrim}ms → " +
                              $"{AudioSyncOffsetMs}ms (per-media nudges don't carry over)");
            }
            // New media gets a fresh RTSPClient (RtpInfoGeneration restarts at 0),
            // so clear the seek anchor-gate or it would block the new media.
            Interlocked.Exchange(ref _anchorMinRtpInfoGen, 0L);
            // Reset rate so a prior media that ended in trick play doesn't make
            // the new media's first 1× Start look like a rate change (→ spurious
            // SetRate/re-baseline during the new open).
            _lastRequestedRate = 1.0;
            _paused = false;
            // Fresh open gate so the next media's OpenAsync waits for the new
            // renderer rather than returning the previous media's result.
            if (!_disposed) _openTcs = new TaskCompletionSource<bool>();
            _log?.LogInfo("[ext-sync] pipeline reset for new media");
        }

        // ----- [av-timing] pipeline-latency diagnostic -----
        //
        // The cross-stream RTP-Info offset SoftSled applies is content-correct
        // (verified against the Xbox, which syncs the same file with the same
        // offset). Yet video still renders visibly ahead of audio, scaling with
        // content complexity. That residual lives in SoftSled's OWN pipeline —
        // the gaps the pacer's drift accounting is blind to:
        //   • master→audible: the master clock is min(GetPosition, written); does
        //     GetPosition (bytes the device "played") lead the audible audio, and
        //     is the device chronically underrunning (played > written → silence)?
        //   • release→on-screen: SubmitFrame coalesces on the UI thread, so
        //     on-screen video lags the pacer's released frame by (submitted −
        //     presented) frames.
        // This snapshot logs both, 1 Hz, so the residual becomes a measurable
        // number instead of a visual estimate. It reads only; it never feeds back
        // into the clock or the offset.
        private void StartAvTimingDiag() {
            if (_avTimingTimer != null) return;
            _avTimingTick = 0;
            try {
                _avTimingTimer = new System.Threading.Timer(
                    _ => { try { LogAvTimingSnapshot(); } catch { } },
                    null, 1000, 1000);
            } catch { _avTimingTimer = null; }
        }

        private void StopAvTimingDiag() {
            var t = _avTimingTimer;
            _avTimingTimer = null;
            if (t != null) { try { t.Dispose(); } catch { } }
        }

        private void LogAvTimingSnapshot() {
            var r = _renderer;
            var pc = _pacer;
            var pr = _presenter;
            if (r == null || pc == null) return;

            long master   = r.GetMediaTimeMs();   // min(played, written) — the RAW clock
            long smoothed = SmoothedMasterPeekMs();// what the pacer is actually slaved to
            long smoothLag = master - smoothed;    // >0 ⇒ pacer clock lags true audio (the residual)
            long played   = r.AudioPlayedMs;      // uncapped device GetPosition
            long written  = r.AudioWrittenMs;     // real content handed to device
            long wall     = r.WallSincePlayMs;    // real elapsed since Play()
            long devSilence = played - written;   // >0 ⇒ device playing silence (underrun)
            long padSil   = r.SilencePaddedMs;    // cumulative rendered silence (clock subtracts it)
            long gapFill  = r.GapFilledMs;        // cumulative content-gap silence (played as dead air)
            long masterVsWall = master - wall;    // clock drift from real-time

            long vidRel   = pc.LastReleasedElapsedMs;  // video content the pacer released
            long offset   = pc.CurrentSyncOffsetMs;
            // Video content the pacer BELIEVES is on screen ≈ master + offset. The
            // actual content presented lags that by the coalescing backlog.
            long relLag   = vidRel - (master + offset); // pacer's own drift (≈0 normally)

            long submitted = pr?.FramesSubmitted ?? 0;
            long presented = pr?.FramesPresented ?? 0;
            long presentBacklog = submitted - presented; // frames released but not on screen

            _log?.LogInfo(
                $"[av-timing] t={++_avTimingTick}s master={master} smoothed={smoothed} " +
                $"smoothLag={smoothLag} wall={wall} (masterVsWall={masterVsWall}) " +
                $"played={played} written={written} devSilence={devSilence} padSil={padSil} gapFill={gapFill} | " +
                $"vidRelEl={vidRel} offset={offset} pacerDrift={relLag} | " +
                $"present sub={submitted} pres={presented} backlog={presentBacklog}");
        }

        public void Dispose() {
            _disposed = true;
            StopAvTimingDiag();
            AttachRtspClient(null);   // detaches + resets the pipeline
            ResetPipelineForNewMedia();
            // _presenter is owned by the session; it disposes it.
            _log?.LogInfo("[ext-sync] controller disposed");
        }
    }
}
