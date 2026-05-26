using FFmpeg.AutoGen;
using SoftSled.Components.AudioVisual;
using SoftSled.Components.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Unosquare.FFME;

namespace SoftSled.Components.AudioVisual.ExternalSync {

    /// <summary>
    /// <see cref="IMediaController"/> implementation that owns its own
    /// audio decoder (<see cref="LibAvAudioDecoder"/>) + renderer
    /// (<see cref="NAudioMasterRenderer"/>) rather than going through
    /// FFME's audio path. The audio render clock becomes the
    /// authoritative "Position"; FFME continues to decode + render
    /// video, but the controller chases NAudio's master clock via
    /// per-tick <see cref="MediaElement.SpeedRatio"/> nudges (the
    /// sync controller, Phase 2).
    ///
    /// <para><b>Phase 1 scope:</b> audio-only, MP3-only (no video,
    /// no sync controller).</para>
    /// <para><b>Phase 2 scope:</b> adds MP2 support (recorded-TV
    /// VND.MS.WM-MPA), video via FFME (attached through
    /// <see cref="AttachVideoMediaElement"/>), and the drift-
    /// correction sync loop. Video stream is fed raw MPEG-VS bytes
    /// through the existing <c>TrySetupMpvVideoProducer</c> path —
    /// no muxer, no audio in the FFME input.</para>
    ///
    /// <para>Sync loop:
    /// <list type="bullet">
    ///   <item>Tick every 250 ms on a thread-pool timer; marshal to
    ///   the FFME dispatcher to read <c>Media.Position</c> + write
    ///   <c>Media.SpeedRatio</c>.</item>
    ///   <item>Drift = audioMasterMs - videoPositionMs (after
    ///   applying user's manual <c>AudioSyncOffsetMs</c>).</item>
    ///   <item>Deadband ±50 ms: no nudge, keep SpeedRatio at 1.0.</item>
    ///   <item>Proportional: SpeedRatio = clamp(1 + drift/2000, 0.95, 1.05).
    ///   Aims to close 1 s of drift in ~2 s of wall-time.</item>
    ///   <item>Catastrophic (>2 s): log loudly, leave the deadband
    ///   open so the proportional control nudges with full ±5%
    ///   until back inside ±50 ms. We do NOT seek the FFME element
    ///   (live RTSP — seek is a no-op anyway).</item>
    /// </list></para>
    /// </summary>
    internal sealed class ExternalSyncMediaController : IMediaController, IDisposable {

        private readonly Logger _log;
        private LibAvAudioDecoder _decoder;
        private NAudioMasterRenderer _renderer;

        // Snapshot of the manual audio-sync offset from
        // SoftSledConfig at construction time. Captured once so the
        // controller stays stable for the session even if the user
        // re-opens the config page mid-session.
        // Phase 2 USES this: the sync loop computes drift as
        // (audioRenderTime - offsetMs) - videoPosition, so positive
        // offset shifts the apparent audio time earlier from the
        // sync engine's perspective — making video lag the speaker
        // output by the offset, compensating for downstream display
        // latency (HDMI / AVR).
        public int AudioSyncOffsetMs { get; }

        // Pending Play state. WMC may call PlayAsync() before the
        // renderer is constructed (first decoded frame hasn't fired
        // OnFormatReady yet). Capture the intent and apply once
        // the renderer exists.
        private bool _playRequested = true;       // default: play on attach
        private bool _isOpen;

        // RTSP wiring.
        private SoftSled.Components.RTSP.RTSPClient _rtsp;
        private long _lastBandwidthBps = -1;
        private bool _lastOptimisedPreroll;
        private double _lastRequestedRate = 1.0;

        // Readiness TCS — completes when the renderer is alive
        // (i.e. the decoder has emitted at least one frame and we
        // know the output format). Lets AvCtrlHandler defer the
        // first Start ack against the same WaitUntilOpenAsync hook
        // as the FFME path.
        private TaskCompletionSource<bool> _openTcs =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Phase 2: video sync state.
        private MediaElement _videoMedia;
        private Dispatcher _videoDispatcher;
        private Timer _syncTimer;
        private volatile bool _videoMediaOpen;
        private long _lastLoggedDriftMs;
        private long _syncLogIntervalTicks;
        private System.Diagnostics.Stopwatch _syncLogClock;

        // Sync baselines. Captured on the first tick where BOTH
        // audio and video report valid (non-zero) times.
        // Phase 2.5: also captures wireOffset so the controller
        // targets the server's intended sync (per wire-side RTP
        // timestamps) rather than just holding whatever skew the
        // streams happened to have at startup.
        //
        //   wireOffset = audioFirstWirePtsMs - videoFirstWirePtsMs
        //
        // A typical recorded-TV stream has video PTS earlier than
        // audio PTS (server sends prior IDR before the play
        // position, ~1.5s typical), so wireOffset is positive.
        // Drift formula:
        //   drift = (audioNow - audioBaseline)
        //         - (videoNow - videoBaseline)
        //         + wireOffset
        //         - AudioSyncOffsetMs
        // Positive drift = audio ahead of intended → speed up video.
        private long _audioBaselineMs = -1;
        private long _videoBaselineMs = -1;
        private long _firstAudioMauWirePtsMs = -1;
        private long _wireOffsetMs;

        // SpeedRatio-honour diagnostic. We record each tick's video
        // elapsed; the next tick computes (videoNowElapsed - prev) /
        // wallDelta and compares to the rate we last requested. If
        // FFME is honouring SpeedRatio on raw mpegvideo streams the
        // ratio should track; if not, this surfaces it. (Note: the
        // existing _lastRequestedRate field tracks the user-facing
        // RATE from AvCtrl Start payloads — different concept; this
        // one tracks the sync-controller's nudges.)
        private long _prevVideoMsForDiag = -1;
        private long _prevTickWallMs = -1;
        private double _lastNudgedRate = 1.0;

        public ExternalSyncMediaController(Logger log)
            : this(log, audioSyncOffsetMs: 0) { }

        public ExternalSyncMediaController(Logger log, int audioSyncOffsetMs) {
            _log = log;
            AudioSyncOffsetMs = audioSyncOffsetMs;
            _log?.LogInfo($"[ext-sync] controller constructed " +
                          $"(audioSyncOffset={audioSyncOffsetMs}ms)");
        }

        // ============================================================
        //  RTSPClient hookup — receives raw audio MAUs
        // ============================================================

        public void AttachRtspClient(SoftSled.Components.RTSP.RTSPClient client) {
            if (ReferenceEquals(_rtsp, client)) return;
            if (_rtsp != null) {
                _rtsp.Disconnected      -= OnRtspDisconnected;
                _rtsp.PtsError          -= OnRtspPtsError;
                _rtsp.UnrecoverableSkew -= OnRtspUnrecoverableSkew;
                try { _rtsp.SetExternalAudioConsumer(null, null); } catch { }
            }
            _rtsp = client;
            if (_rtsp != null) {
                _rtsp.Disconnected      += OnRtspDisconnected;
                _rtsp.PtsError          += OnRtspPtsError;
                _rtsp.UnrecoverableSkew += OnRtspUnrecoverableSkew;
                try {
                    _rtsp.SetExternalAudioConsumer(
                        codecCommit: OnAudioCodecCommitted,
                        mauArrived:  OnAudioMau);
                } catch (Exception ex) {
                    _log?.LogError($"[ext-sync] SetExternalAudioConsumer threw: {ex.Message}");
                }
            }
        }

        /// <summary>Called by RTSPClient on the first audio packet
        /// of a stream, once the SDP codec is known.</summary>
        private void OnAudioCodecCommitted(ExternalAudioFormat fmt) {
            if (_decoder != null) return;     // already committed
            if (fmt == null) {
                _log?.LogError("[ext-sync] OnAudioCodecCommitted called with null fmt");
                return;
            }
            AVCodecID codecId;
            string wc = (fmt.WireCodec ?? "").ToUpperInvariant();
            switch (wc) {
                case "MPA":
                case "VND.MS.WM-MPA":
                    // MPEG-1/2 audio. Layer 3 = MP3, Layer 1/2 = MP2
                    // (libav's MP2 decoder handles both layers).
                    // Default to MP3 when fmtp doesn't tell us
                    // (plain .mp3 source case from a WMC music
                    // library will always advertise layer=3).
                    if (fmt.MpegLayer == 1 || fmt.MpegLayer == 2) {
                        codecId = AVCodecID.AV_CODEC_ID_MP2;
                    } else {
                        codecId = AVCodecID.AV_CODEC_ID_MP3;
                    }
                    break;
                case "VND.MS.WM-AC3":
                    // Dolby Digital. libav's AC3 decoder handles all
                    // common channel layouts (2.0 / 5.1 / 7.1);
                    // swr_convert will downmix to stereo on the way
                    // to the canonical NAudio format. WMC's
                    // recorded-TV path commonly uses this for HD
                    // content (NTSC_XAC3 profile etc.).
                    codecId = AVCodecID.AV_CODEC_ID_AC3;
                    break;
                case "X-WMF-PF":
                    // Raw PCM (Windows Media Format Payload Frame).
                    // The SDP fmtp tells us bit-depth + endianness:
                    //   bitspersample=16, codec=pcm_s16le (typical)
                    //   bitspersample=24 → s24le
                    //   bitspersample=32 → s32le
                    // 8-bit is unsigned per WAVEFORMATEX convention.
                    // BE variants are rare on WMC servers (Intel
                    // byte order is the norm) but we honour the
                    // hint if it's present.
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
                            // Default to s16le — the safest assumption
                            // for X-WMF-PF audio when fmtp didn't
                            // include bitspersample. If wrong, the
                            // decoder will produce noise rather than
                            // silence — visibly wrong but recoverable
                            // (the user notices, we can refine).
                            codecId = AVCodecID.AV_CODEC_ID_PCM_S16LE;
                            _log?.LogError($"[ext-sync] X-WMF-PF audio has no bitspersample hint " +
                                           $"({fmt}); defaulting to PCM_S16LE");
                            break;
                    }
                    break;
                default:
                    _log?.LogError($"[ext-sync] unsupported wire codec '{fmt.WireCodec}' — " +
                                   $"audio will not play. Add a case in OnAudioCodecCommitted " +
                                   $"to extend coverage.");
                    return;
            }
            try {
                // PCM decoders need sample_rate + channels set on the
                // codec context BEFORE avcodec_open2 — there's no
                // bitstream header to read them from. Other codecs
                // happily auto-detect from the first frame.
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

        /// <summary>Called by RTSPClient for every audio MAU.</summary>
        private void OnAudioMau(byte[] data, uint rtpTs) {
            if (_decoder == null) return;
            // MPA / VND.MS.WM-MPA: RTP clock is 90 kHz per the
            // rtpmap. Convert to ms for consistent PTS arithmetic
            // downstream. (The decoded frame's libav-side PTS is
            // ignored; we honour the wire-side timestamp.)
            long ptsMs = rtpTs / 90L;
            // Phase 2.5: capture the first audio MAU's wire-side PTS
            // for use by the sync controller's wireOffset calc.
            // Volatile-ish via Interlocked since this runs on the
            // depacketizer thread while the sync tick reads it on
            // the dispatcher thread.
            if (Interlocked.CompareExchange(ref _firstAudioMauWirePtsMs, ptsMs, -1L) == -1L) {
                _log?.LogDebug($"[ext-sync-anchor] first audio MAU rtpTs={rtpTs} ({ptsMs}ms)");
            }
            _decoder.SubmitPacket(data, ptsMs);
        }

        private void OnDecoderFormatReady() {
            try {
                // Phase 2.6: when a video element is attached we
                // expect to perform A/V sync. Start the renderer
                // HELD — decoded PCM accumulates in a software
                // queue, NAudio doesn't start playing yet. We
                // release on the video's MediaOpened event with the
                // appropriate skip to align audio's first audible
                // sample with video's first visible frame. No video
                // attached → start unheld (Phase 1 behaviour for
                // audio-only sessions).
                bool startHeld = _videoMedia != null;
                _renderer = new NAudioMasterRenderer(
                    LibAvAudioDecoder.OutSampleRate,
                    LibAvAudioDecoder.OutChannels,
                    LibAvAudioDecoder.OutBitsPerSample,
                    _log,
                    startHeld);
                _isOpen = true;
                _log?.LogInfo($"[ext-sync] renderer ready (startHeld={startHeld}), signalling open");
                if (_playRequested) _renderer.Play();
                try { _openTcs.TrySetResult(true); } catch { }
                try { BufferingEnded?.Invoke(); } catch { }

                // If video already opened before the renderer was
                // ready, apply the deferred release now.
                if (startHeld && _videoMediaOpen) {
                    ApplyHoldRelease();
                }
                // Audio-only fallback: even with no video attached
                // we shouldn't sit held forever — covers the case
                // where video was supposed to come but never did.
                // 3 s is enough for FFME open + the AvCtrl first-
                // Start ack flow to settle.
                if (startHeld) {
                    System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => {
                        try {
                            if (!_disposed && _renderer != null && _renderer.IsHeld) {
                                _log?.LogInfo("[ext-sync] hold-release watchdog: 3s elapsed without " +
                                              "video MediaOpened, releasing with skipMs=0");
                                _renderer.ReleaseHold(0);
                            }
                        } catch { }
                    });
                }
            } catch (Exception ex) {
                _log?.LogError($"[ext-sync] renderer init failed: {ex.Message}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        /// <summary>
        /// Compute the audio-vs-video pre-roll skip from the
        /// captured first-MAU wire PTSes and release the renderer's
        /// hold. Safe to call when the renderer isn't held (no-op
        /// in <see cref="NAudioMasterRenderer.ReleaseHold"/>).
        ///
        /// <para>Positive skipMs (video PTS > audio PTS, today's
        /// recorded-TV case) drops the first skipMs of buffered
        /// audio so audio's first audible sample aligns with
        /// video's first visible frame.</para>
        ///
        /// <para>Negative or zero (audio PTS >= video PTS) → can't
        /// skip backwards, release with skipMs=0; the sync
        /// controller closes any residual drift via SpeedRatio.</para>
        /// </summary>
        private void ApplyHoldRelease() {
            if (_renderer == null) return;
            long aWire = Interlocked.Read(ref _firstAudioMauWirePtsMs);
            long vWire = _rtsp?.FirstVideoMauWirePtsMs ?? -1L;
            int skipMs = 0;
            if (aWire >= 0 && vWire >= 0) {
                long delta = vWire - aWire;   // positive = audio is N ms ahead in stream-time
                if (delta > 0 && delta < 30000) skipMs = (int)delta;
                _log?.LogInfo($"[ext-sync] computing pre-roll skip: " +
                              $"audWirePts={aWire}ms vidWirePts={vWire}ms → skipMs={skipMs} " +
                              $"({(delta < 0 ? "audio behind video — no skip possible" : (delta == 0 ? "perfectly aligned" : "audio ahead — skip"))})");
            } else {
                _log?.LogInfo($"[ext-sync] computing pre-roll skip: wire PTS not available " +
                              $"(aWire={aWire}, vWire={vWire}), releasing with skipMs=0");
            }
            try { _renderer.ReleaseHold(skipMs); }
            catch (Exception ex) { _log?.LogError($"[ext-sync] ReleaseHold threw: {ex.Message}"); }
        }

        private void OnDecodedPcm(byte[] pcm, int len, long ptsMs) {
            _renderer?.WritePcm(pcm, len, ptsMs);
        }

        // ============================================================
        //  Phase 2: Video sync controller
        // ============================================================

        /// <summary>
        /// Attach the FFME MediaElement that's rendering the
        /// video-only stream. The controller subscribes to its
        /// open/close events and starts the sync timer that nudges
        /// SpeedRatio to chase NAudio's master clock.
        /// Pass <c>null</c> to detach (e.g. on Dispose).
        /// </summary>
        public void AttachVideoMediaElement(MediaElement media) {
            if (ReferenceEquals(_videoMedia, media)) return;
            if (_videoMedia != null) {
                _videoMedia.MediaOpened -= OnVideoMediaOpened;
                _videoMedia.MediaClosed -= OnVideoMediaClosed;
                _videoMedia.MediaFailed -= OnVideoMediaFailed;
                StopSyncTimer();
            }
            _videoMedia = media;
            _videoDispatcher = media?.Dispatcher;
            if (_videoMedia != null) {
                _videoMedia.MediaOpened += OnVideoMediaOpened;
                _videoMedia.MediaClosed += OnVideoMediaClosed;
                _videoMedia.MediaFailed += OnVideoMediaFailed;
                _log?.LogInfo("[ext-sync] video MediaElement attached; sync controller armed");
            }
        }

        private void OnVideoMediaOpened(object sender, EventArgs e) {
            _videoMediaOpen = true;
            _syncLogClock = System.Diagnostics.Stopwatch.StartNew();
            _syncLogIntervalTicks = 0;
            // Re-arm baselines so the first tick captures fresh
            // values (in case the controller is being re-used across
            // a CloseMedia → OpenMedia cycle).
            _audioBaselineMs = -1;
            _videoBaselineMs = -1;
            _wireOffsetMs = 0;
            _prevVideoMsForDiag = -1;
            _prevTickWallMs = -1;
            _syncInDeadband = true;
            _lastNudgedRate = 1.0;
            // Phase 2.6: release the audio hold (if active) with the
            // computed pre-roll skip so audio's first audible sample
            // aligns with video's first frame. Safe no-op when the
            // renderer was started unheld or has already been
            // released.
            ApplyHoldRelease();
            StartSyncTimer();
            _log?.LogInfo("[ext-sync] video MediaOpened; sync timer started, baselines re-armed");
        }

        private void OnVideoMediaClosed(object sender, EventArgs e) {
            _videoMediaOpen = false;
            StopSyncTimer();
            // Restore neutral playback rate so a subsequent re-open
            // doesn't inherit a nudged value.
            if (_videoMedia != null && _videoDispatcher != null) {
                try {
                    _videoDispatcher.BeginInvoke(new Action(() => {
                        try { if (_videoMedia != null) _videoMedia.SpeedRatio = 1.0; }
                        catch { }
                    }));
                } catch { }
            }
            _log?.LogInfo("[ext-sync] video MediaClosed; sync timer stopped");
        }

        private void OnVideoMediaFailed(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e) {
            _log?.LogError($"[ext-sync] video MediaFailed: {e.ErrorException?.Message ?? "(no message)"}");
            try { MediaFailed?.Invoke(e.ErrorException ?? new Exception("video MediaFailed")); } catch { }
        }

        // ---- Sync loop ----

        // 250 ms tick: fast enough to keep drift under control, slow
        // enough that SpeedRatio updates don't thrash. Each tick the
        // loop reads audio + video clocks and writes at most one new
        // SpeedRatio value.
        private const int SyncTickPeriodMs = 250;
        // Deadband hysteresis. When already inside the deadband, we
        // only EXIT it once drift crosses SyncDeadbandExitMs.
        // Conversely from outside, we only ENTER the deadband once
        // drift drops below SyncDeadbandEnterMs. Both well under
        // human lip-sync perception threshold (~75-100 ms). The
        // hysteresis prevents the controller from oscillating
        // between "neutral" and "edge-of-nudge" when drift sits
        // close to one threshold.
        private const int SyncDeadbandEnterMs = 35;
        private const int SyncDeadbandExitMs  = 75;
        // Drift over which we apply max speed nudge. Phase 2.5:
        // tightened from 2000ms to 1000ms — 1s of drift = full
        // ±5% nudge, closes the gap in ~1 wall-second instead of 2.
        // Faster but still well under perceptible video-rate-change
        // territory.
        private const int SyncRecoveryTargetMs = 1000;
        private const double SyncMaxSpeedDelta = 0.05;
        // Catastrophic threshold — log loudly but keep nudging.
        private const int SyncCatastrophicMs = 2000;
        // Track whether we're currently in the deadband (for hysteresis).
        private bool _syncInDeadband = true;

        private void StartSyncTimer() {
            if (_syncTimer != null) return;
            _syncTimer = new Timer(OnSyncTick, null, SyncTickPeriodMs, SyncTickPeriodMs);
        }

        private void StopSyncTimer() {
            try { _syncTimer?.Dispose(); } catch { }
            _syncTimer = null;
        }

        private int _syncTickPending;
        private void OnSyncTick(object state) {
            // Re-entrancy guard — if the previous tick is still
            // queued on the dispatcher, skip this one. The dispatcher
            // tick is short (one read + at most one write) so this
            // only triggers if the UI thread is genuinely stalled.
            if (Interlocked.Exchange(ref _syncTickPending, 1) != 0) return;
            var dispatcher = _videoDispatcher;
            if (dispatcher == null) {
                Interlocked.Exchange(ref _syncTickPending, 0);
                return;
            }
            try {
                dispatcher.BeginInvoke(new Action(DoSyncTick), DispatcherPriority.Background);
            } catch {
                Interlocked.Exchange(ref _syncTickPending, 0);
            }
        }

        private void DoSyncTick() {
            try {
                if (!_videoMediaOpen || _videoMedia == null || _renderer == null) return;

                long audioMs = _renderer.GetMediaTimeMs();
                long videoMs;
                try { videoMs = (long)_videoMedia.Position.TotalMilliseconds; }
                catch { return; }

                // Wait for both clocks to start advancing. NAudio
                // GetMediaTimeMs returns 0 before first sample drains
                // through the device; FFME.Position returns 0 before
                // first frame presents.
                if (audioMs <= 0 || videoMs <= 0) return;

                long nowWallMs = _syncLogClock.ElapsedMilliseconds;

                // First valid tick — capture baselines AND the
                // wire-PTS offset. The wireOffset represents the
                // server's intended sync: e.g. if the server sends
                // a prior IDR 1.5 s before the play position, the
                // first video MAU's PTS is 1.5 s earlier than the
                // first audio MAU's PTS — meaning at any given wall
                // moment, audio is "1.5 s ahead" of video in pure
                // elapsed terms but actually IN-SYNC per the
                // server's PTS truth. Adding wireOffset to the drift
                // formula corrects for this.
                if (_audioBaselineMs < 0) {
                    _audioBaselineMs = audioMs;
                    _videoBaselineMs = videoMs;

                    // wireOffset = audioFirstWirePts - videoFirstWirePts
                    // Read both from authoritative sources (controller's
                    // own audio capture + RTSPClient's video capture).
                    long aWire = Interlocked.Read(ref _firstAudioMauWirePtsMs);
                    long vWire = _rtsp?.FirstVideoMauWirePtsMs ?? -1L;
                    if (aWire >= 0 && vWire >= 0) {
                        _wireOffsetMs = aWire - vWire;
                        _log?.LogInfo($"[ext-sync] sync baselines captured: " +
                                      $"aud={audioMs}ms vid={videoMs}ms " +
                                      $"(initial elapsed offset {audioMs - videoMs:+#;-#;0}ms) " +
                                      $"wireOffset={_wireOffsetMs:+#;-#;0}ms " +
                                      $"(audWirePts={aWire}ms vidWirePts={vWire}ms)");
                    } else {
                        _wireOffsetMs = 0;
                        _log?.LogInfo($"[ext-sync] sync baselines captured: " +
                                      $"aud={audioMs}ms vid={videoMs}ms — wire PTS not " +
                                      $"yet available (aWire={aWire}, vWire={vWire}), " +
                                      $"using elapsed-only sync (initial skew will be " +
                                      $"held as-is; user can fine-tune via AudioSyncOffsetMs)");
                    }
                    _prevTickWallMs = nowWallMs;
                    _prevVideoMsForDiag = videoMs;
                    return;
                }

                long audioElapsed = audioMs - _audioBaselineMs;
                long videoElapsed = videoMs - _videoBaselineMs;

                // Drift: how far has audio gone relative to video
                // (in elapsed terms), corrected for wire-side intended
                // sync (+wireOffset) and the user's manual delay
                // (-AudioSyncOffsetMs). Zero = perfectly in sync.
                //
                // Sign of wireOffset matters: when video PTS starts
                // earlier than audio PTS, wireOffset is positive
                // (audWire - vidWire = positive). Adding it to the
                // formula means: for the SAME real-time moment,
                // audio's "elapsed" naturally lags video's "elapsed"
                // by wireOffset ms (because video had a head-start
                // in wire-time). Drift = 0 means that natural
                // relationship holds. Drift > 0 means audio has
                // pulled ahead beyond that — video needs to speed
                // up. Drift < 0 means video has pulled ahead.
                long drift = audioElapsed - videoElapsed + _wireOffsetMs - AudioSyncOffsetMs;
                bool catastrophic = System.Math.Abs(drift) > SyncCatastrophicMs;

                // Deadband hysteresis: stay in / out of the deadband
                // based on which side of the relevant threshold we
                // cross. Prevents oscillation when drift hovers near
                // a single threshold.
                bool wasInDeadband = _syncInDeadband;
                if (_syncInDeadband) {
                    if (System.Math.Abs(drift) > SyncDeadbandExitMs) _syncInDeadband = false;
                } else {
                    if (System.Math.Abs(drift) < SyncDeadbandEnterMs) _syncInDeadband = true;
                }

                double newRate;
                if (_syncInDeadband) {
                    newRate = 1.0;
                } else {
                    double frac = (double)drift / SyncRecoveryTargetMs;
                    if (frac >  SyncMaxSpeedDelta) frac =  SyncMaxSpeedDelta;
                    if (frac < -SyncMaxSpeedDelta) frac = -SyncMaxSpeedDelta;
                    newRate = 1.0 + frac;
                }

                // SpeedRatio-honour diagnostic. Compare actual video
                // advance over wall time to the rate we previously
                // requested. If FFME honours SpeedRatio on raw
                // elementary streams these should track; if FFME
                // ignores it, actualRate will hover near 1.0
                // regardless of what we set.
                double actualRate = double.NaN;
                if (_prevVideoMsForDiag >= 0 && _prevTickWallMs >= 0) {
                    long dVid = videoMs - _prevVideoMsForDiag;
                    long dWall = nowWallMs - _prevTickWallMs;
                    if (dWall > 0) actualRate = (double)dVid / dWall;
                }
                _prevVideoMsForDiag = videoMs;
                _prevTickWallMs = nowWallMs;

                // Apply only if different from current (avoid
                // pointless DependencyProperty writes that re-trigger
                // FFME's internal change handlers).
                double currentRate;
                try { currentRate = _videoMedia.SpeedRatio; }
                catch { currentRate = 1.0; }
                if (System.Math.Abs(currentRate - newRate) > 0.001) {
                    try {
                        _videoMedia.SpeedRatio = newRate;
                        _lastNudgedRate = newRate;
                    } catch (Exception ex) {
                        _log?.LogError($"[ext-sync] SpeedRatio={newRate:F3} failed: {ex.Message}");
                    }
                }

                // Log throttling: one line per ~2 seconds of wall
                // time, OR immediately when we transition to /
                // from catastrophic drift OR cross the deadband
                // boundary (state change is interesting).
                bool transitionCatastrophic =
                    System.Math.Abs(_lastLoggedDriftMs) <= SyncCatastrophicMs && catastrophic;
                bool deadbandTransition = wasInDeadband != _syncInDeadband;
                if (nowWallMs - _syncLogIntervalTicks >= 2000 ||
                    transitionCatastrophic || deadbandTransition) {
                    _syncLogIntervalTicks = nowWallMs;
                    _lastLoggedDriftMs = drift;
                    string actualStr = double.IsNaN(actualRate) ? "n/a" : actualRate.ToString("F3");
                    _log?.LogInfo($"[ext-sync] sync: audE={audioElapsed}ms vidE={videoElapsed}ms " +
                                  $"drift={drift:+#;-#;0}ms req={newRate:F3} actual={actualStr}" +
                                  (_syncInDeadband ? " DEAD" : "") +
                                  (catastrophic ? " CATASTROPHIC" : ""));
                }
            } catch (Exception ex) {
                _log?.LogError($"[ext-sync] DoSyncTick threw: {ex.Message}");
            } finally {
                Interlocked.Exchange(ref _syncTickPending, 0);
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
            _renderer?.Play();
            // Also drive FFME play if a video element is attached.
            if (_videoMedia != null && _videoDispatcher != null) {
                try {
                    _videoDispatcher.BeginInvoke(new Action(async () => {
                        try { await _videoMedia.Play(); }
                        catch (Exception ex) { _log?.LogError($"[ext-sync] video Play failed: {ex.Message}"); }
                    }));
                } catch { }
            }
            return Task.CompletedTask;
        }

        public Task PauseAsync() {
            _playRequested = false;
            _renderer?.Pause();
            if (_videoMedia != null && _videoDispatcher != null) {
                try {
                    _videoDispatcher.BeginInvoke(new Action(async () => {
                        try { await _videoMedia.Pause(); }
                        catch (Exception ex) { _log?.LogError($"[ext-sync] video Pause failed: {ex.Message}"); }
                    }));
                } catch { }
            }
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position) {
            try { _rtsp?.Play(startMs: (long)position.TotalMilliseconds, rate: _lastRequestedRate); }
            catch (Exception ex) {
                _log?.LogError($"[ext-sync] RTSP seek failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task SetRateAsync(double rate) {
            _lastRequestedRate = rate;
            bool isNormalRate = System.Math.Abs(rate - 1.0) < 0.0001;

            // External-sync trick play. For ANY non-1.0 rate WMPNss does
            // server-side trick play (drops audio entirely, sends video
            // with PTS spacing × rate). The sync controller's SpeedRatio
            // nudging (which assumes 1.0 = nominal) steps aside, and
            // FFME's SpeedRatio is set to the requested rate so the video
            // element renders the fast-PTS frames at matching wall speed.
            //
            // <para>Visual presentation is best-effort and limited by FFME:
            // high forward rates above ~4× exceed FFME's sustainable
            // presentation pipeline (frames pile up in decode queue), and
            // any reverse rate's backward-PTS frames are silently dropped
            // by FFME's video element. The wire-side server trick play
            // and progress-bar tracking are unaffected — the user can
            // still navigate via the timeline indicator even when the
            // image isn't updating. Capping SpeedRatio at
            // <see cref="MaxFfmeSpeedRatio"/> (in ApplySpeedRatioToFfme)
            // keeps FFME stable on the forward path; documented limitation
            // for high rates and all RW.</para>
            if (!isNormalRate) {
                StopSyncTimer();
                ApplySpeedRatioToFfme(rate, reason: $"trick play {rate:F2}×");
            } else {
                // Returning to 1.0×. Restore SpeedRatio first so the
                // sync timer's first tick reads a stable rate, then
                // restart the timer with fresh baselines (the prior
                // baselines are stale — video PTS jumped during trick
                // play, so audio-vs-video drift won't be comparable
                // with pre-trick baselines).
                ApplySpeedRatioToFfme(1.0, reason: "return-to-1×");
                _audioBaselineMs = -1;
                _videoBaselineMs = -1;
                _wireOffsetMs = 0;
                _prevVideoMsForDiag = -1;
                _prevTickWallMs = -1;
                _syncInDeadband = true;
                _lastNudgedRate = 1.0;
                if (_videoMediaOpen) StartSyncTimer();
            }

            try { _rtsp?.SetRate(rate); }
            catch (Exception ex) {
                _log?.LogError($"[ext-sync] RTSP SetRate({rate}) failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        /// <summary>Apply a SpeedRatio to the attached FFME video
        /// element, dispatcher-marshalled. Safe to call when no video
        /// element is attached (no-op).
        ///
        /// <para>FFME's <c>SpeedRatio</c> is positive-only — negative
        /// values either throw or silently leave the element in an
        /// unrecoverable state (May 2026 test: setting
        /// SpeedRatio=-10 during RW broke the element such that
        /// subsequent return-to-1× couldn't resume playback). We pass
        /// the absolute value here. The wire-side direction is
        /// already handled by RTSP PLAY-with-Scale=-N which tells
        /// WMPNss to deliver reverse-PTS frames; FFME then plays
        /// those frames at <c>|rate|</c> wall speed and the user
        /// sees rewound content at the requested speed.</para>
        ///
        /// <para>Speed is also clamped to <see cref="MaxFfmeSpeedRatio"/>
        /// — above ~4× FFME's internal presentation pipeline can't
        /// sustain the rate and frames pile up in the decode queue
        /// until rate-change releases them, producing the "image
        /// frozen during FF then blazes through to current position
        /// on Play" symptom (May 2026 test: SpeedRatio=10 and =100
        /// both exhibited this). Wire-side server-driven rate still
        /// goes out at the requested value, so trick play continues
        /// to traverse media at the user's requested rate; only the
        /// visual update is capped. Users see "stuttery N×" video
        /// playback for any requested rate above the cap, which is
        /// still informative for navigation.</para></summary>
        private void ApplySpeedRatioToFfme(double rate, string reason) {
            if (_videoMedia == null || _videoDispatcher == null) return;
            double ffmeRate = System.Math.Abs(rate);
            // Floor: FFME refuses SpeedRatio < ~0.1 in practice; cap at
            // a safe minimum even though our trick-play ladder never
            // emits values that small.
            if (ffmeRate < 0.1) ffmeRate = 0.1;
            // Ceiling: FFME's video presentation can't sustain very
            // high SpeedRatio values without backing up the decoder
            // queue. Cap visually while still letting the wire-side
            // server trick play run at the user's requested rate.
            if (ffmeRate > MaxFfmeSpeedRatio) ffmeRate = MaxFfmeSpeedRatio;
            try {
                _videoDispatcher.BeginInvoke(new Action(() => {
                    try {
                        if (_videoMedia != null) {
                            _videoMedia.SpeedRatio = ffmeRate;
                            _log?.LogInfo($"[ext-sync] FFME SpeedRatio = {ffmeRate:F2} " +
                                          $"(requested={rate:F2}, {reason})");
                        }
                    } catch (Exception ex) {
                        _log?.LogError($"[ext-sync] SpeedRatio={ffmeRate:F2} apply failed: {ex.Message}");
                    }
                }));
            } catch (Exception ex) {
                _log?.LogError($"[ext-sync] dispatcher.BeginInvoke threw: {ex.Message}");
            }
        }

        // Practical max FFME SpeedRatio. Above 4×, the visual update
        // stalls because FFME can't sustain the presentation rate (see
        // ApplySpeedRatioToFfme doc). Pick 4.0 — keeps the visual
        // movement obvious enough to navigate while staying well under
        // the failure threshold. Wire-side server trick play (Scale: N)
        // is unaffected; the server still walks the timeline at the
        // requested rate regardless of what FFME displays.
        private const double MaxFfmeSpeedRatio = 4.0;

        public void SetAvailableBandwidth(long bitsPerSecond) {
            try { _rtsp?.SetBufferInfo(bitsPerSecond, _lastOptimisedPreroll); }
            catch (Exception ex) {
                _log?.LogError($"[ext-sync] SetBufferInfo (bandwidth) failed: {ex.Message}");
            }
            _lastBandwidthBps = bitsPerSecond;
        }

        public void SetOptimisedPreroll(bool optimised) {
            try { _rtsp?.SetBufferInfo(_lastBandwidthBps, optimised); }
            catch (Exception ex) {
                _log?.LogError($"[ext-sync] SetBufferInfo (preroll) failed: {ex.Message}");
            }
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

        // Set once on Dispose so the hold-release watchdog (scheduled
        // via Task.Delay in OnDecoderFormatReady) can skip safely if
        // the session ended before its 3 s timer fired.
        private volatile bool _disposed;

        public void Dispose() {
            _disposed = true;
            AttachRtspClient(null);
            AttachVideoMediaElement(null);
            StopSyncTimer();
            try { _decoder?.Complete(); } catch { }
            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
            try { _renderer?.Dispose(); } catch { }
            _renderer = null;
            _isOpen = false;
            _log?.LogInfo("[ext-sync] controller disposed");
        }
    }
}
