using SoftSled.Components.AudioVisual.FormatStructures;
using SoftSled.Components.Configuration;
using SoftSled.Components.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Top-level orchestrator for the direct-libav playback path. Owns:
    /// <list type="bullet">
    ///   <item>the <see cref="PlaybackClock"/> master clock,</item>
    ///   <item>per-stream <see cref="VideoDecoder"/> / <see cref="AudioDecoder"/>
    ///         instances (created lazily when codecs commit),</item>
    ///   <item>the <see cref="WpfVideoRenderer"/> targeting the host's
    ///         <c>Image</c> element and the <see cref="NAudioRenderer"/>
    ///         targeting the system audio output.</item>
    /// </list>
    ///
    /// Implements <see cref="IMediaController"/> so
    /// <c>VirtualChannelAvCtrlHandler</c> can drive it via the same
    /// surface the previous FFME-based controller used — no change above
    /// this layer.
    ///
    /// PTS conversion: depacketizer events carry the wire-side RTP
    /// timestamp (uint32 at the stream's RTP clock rate). The engine
    /// converts to engine-canonical media-milliseconds at submit time,
    /// anchored to the first MAU of each stream. The result is per-
    /// stream PTS that starts at 0 and advances at 1 ms per ms of media —
    /// directly usable by the renderers without further clock math.
    /// </summary>
    internal sealed class SoftSledPlaybackEngine : IMediaController, IDisposable {

        private readonly Image _videoTarget;
        private readonly Logger _log;
        private readonly PlaybackClock _clock = new PlaybackClock();

        // RTSP wiring — relayed Disconnected/PtsError/UnrecoverableSkew.
        private SoftSled.Components.RTSP.RTSPClient _rtsp;

        // Per-stream decoder + renderer state. Both null until the
        // corresponding codec commits. The renderer is held through
        // IVideoRenderer so the engine doesn't care whether it's the
        // WriteableBitmap path (WpfVideoRenderer) or the D3DImage
        // GPU-backed path (D3DImageVideoRenderer) — the choice is
        // driven by SoftSledConfig.EnableD3DImage and made at the
        // point of codec commit.
        private VideoDecoder _videoDecoder;
        private IVideoRenderer _videoRenderer;
        private AudioDecoder _audioDecoder;
        private NAudioRenderer _audioRenderer;

        // PTS anchoring per stream. The base is the first received MAU's
        // RTP timestamp; we convert subsequent timestamps to media-ms
        // relative to that base. Clock-Hz comes from the SDP rtpmap
        // (handed in at codec commit time).
        private uint _videoRtpBase;
        private uint _videoRtpClockHz = 90000;
        private bool _videoBaseAnchored;
        private uint _audioRtpBase;
        private uint _audioRtpClockHz = 90000;
        private bool _audioBaseAnchored;

        // Buffering-stop tracker: one BufferingEnded event per session,
        // fired once both streams (or the single stream we have) have
        // produced their first decoded sample.
        private int _bufferingEndedFired;
        private bool _videoFirstFrameSeen;
        private bool _audioFirstSampleSeen;
        private bool _isVideoSession;  // false on audio-only

        // Started flag controls IsOpen + PlayAsync semantics.
        private volatile bool _isOpen;
        private bool _disposed;

        public SoftSledPlaybackEngine(Image videoTarget, Logger log) {
            _videoTarget = videoTarget ?? throw new ArgumentNullException(nameof(videoTarget));
            _log = log;
            // Engine clock runs from the moment the session opens. Pause /
            // rate-changes update it; the engine doesn't gate its clock
            // on the first MAU arrival because GetPosition is polled
            // before any data arrives.
            _clock.Start(startMediaMs: 0);
            _isOpen = true;
        }

        // ============== IMediaController surface ==============

        public TimeSpan Position => TimeSpan.FromMilliseconds(_clock.CurrentMediaTimeMs);
        public TimeSpan? Duration { get; private set; }
        public bool IsOpen => _isOpen && !_disposed;

        public Task PlayAsync() { _clock.Resume(); return Task.CompletedTask; }
        public Task PauseAsync() { _clock.Pause(); return Task.CompletedTask; }

        public Task SeekAsync(TimeSpan position) {
            // The wire-side seek goes through the RTSP layer; the engine
            // clock just jumps. Decoders / renderers will flush their
            // queues as new packets arrive (Reanchor pattern from the
            // old plan is now implicit — first new MAU re-anchors the
            // per-stream RTP base).
            _clock.Seek((long)position.TotalMilliseconds);
            try {
                _rtsp?.Play(startMs: (long)position.TotalMilliseconds,
                            rate: _lastRequestedRate);
            } catch (Exception ex) {
                _log?.LogError($"[engine] RTSP seek failed: {ex.Message}");
            }
            // A real seek invalidates the per-stream anchors — next MAUs
            // re-anchor naturally.
            _videoBaseAnchored = false;
            _audioBaseAnchored = false;
            return Task.CompletedTask;
        }

        private double _lastRequestedRate = 1.0;

        public Task SetRateAsync(double rate) {
            _lastRequestedRate = rate;

            bool clientSideEligible = rate >= 0.5 && rate <= 2.0;

            // The engine clock always tracks the EFFECTIVE rate at which
            // media advances:
            //   * client-side: server stays at 1× so the stream arrives
            //     normally; we run the clock at <rate> so the renderers
            //     drain their queues at the right pace (and audio swr
            //     would time-stretch — for v1 we just let NAudio play
            //     at native pitch since the [0.5, 2.0] window is small
            //     enough not to be jarring),
            //   * server-side: server pre-rates the stream so PTS values
            //     advance at <rate> per wall second; we run the clock at
            //     <rate> so each frame's PresentationMs is reached at
            //     the right wall instant.
            // Result: same SetRate call for both branches, decision only
            // affects which side gets the wire request.
            _clock.SetRate(System.Math.Abs(rate));

            if (clientSideEligible) {
                _log?.LogDebug($"[engine] SetRateAsync({rate}) — CLIENT-SIDE " +
                              "(engine clock + server at 1×)");
                try { _rtsp?.SetRate(1.0); }
                catch (Exception ex) {
                    _log?.LogError($"[engine] RTSP SetRate(1.0) failed: {ex.Message}");
                }
            } else {
                _log?.LogDebug($"[engine] SetRateAsync({rate}) — SERVER-SIDE " +
                              "(engine clock + RTSP PLAY-with-Scale)");
                try { _rtsp?.SetRate(rate); }
                catch (Exception ex) {
                    _log?.LogError($"[engine] RTSP SetRate({rate}) failed: {ex.Message}");
                }
            }
            return Task.CompletedTask;
        }

        public void SetAvailableBandwidth(long bitsPerSecond) {
            try { _rtsp?.SetBufferInfo(bitsPerSecond, _lastOptimisedPreroll); }
            catch (Exception ex) {
                _log?.LogError($"[engine] SetBufferInfo (bandwidth) failed: {ex.Message}");
            }
            _lastBandwidthBps = bitsPerSecond;
        }

        public void SetOptimisedPreroll(bool optimised) {
            try { _rtsp?.SetBufferInfo(_lastBandwidthBps, optimised); }
            catch (Exception ex) {
                _log?.LogError($"[engine] SetBufferInfo (preroll) failed: {ex.Message}");
            }
            _lastOptimisedPreroll = optimised;
        }

        private long _lastBandwidthBps = -1;
        private bool _lastOptimisedPreroll;

        public void AttachRtspClient(SoftSled.Components.RTSP.RTSPClient client) {
            if (ReferenceEquals(_rtsp, client)) return;
            if (_rtsp != null) {
                _rtsp.Disconnected      -= OnRtspDisconnected;
                _rtsp.PtsError          -= OnRtspPtsError;
                _rtsp.UnrecoverableSkew -= OnRtspUnrecoverableSkew;
                // Detach the engine from the prior session so its
                // depacketizer events stop routing here.
                try { _rtsp.SetPlaybackEngine(null); } catch { }
            }
            _rtsp = client;
            if (_rtsp != null) {
                _rtsp.Disconnected      += OnRtspDisconnected;
                _rtsp.PtsError          += OnRtspPtsError;
                _rtsp.UnrecoverableSkew += OnRtspUnrecoverableSkew;
                // Tell the new RTSP session to route depacketizer MAUs
                // here so the decoders get fed.
                try { _rtsp.SetPlaybackEngine(this); } catch { }
            }
        }

        // ============== IMediaController events ==============

        public event Action BufferingEnded;
        public event Action MediaEnded;
        public event Action<Exception> MediaFailed;
        public event Action<Exception> RtspDisconnected;
        public event Action<PtsErrorInfo> PtsError;
        public event Action<SkewInfo> UnrecoverableSkew;

        private void OnRtspDisconnected(Exception ex) {
            try { RtspDisconnected?.Invoke(ex); } catch { }
        }
        private void OnRtspPtsError(PtsErrorInfo info) {
            try { PtsError?.Invoke(info); } catch { }
        }
        private void OnRtspUnrecoverableSkew(SkewInfo info) {
            try { UnrecoverableSkew?.Invoke(info); } catch { }
        }

        // ============== RTSPClient hooks ==============
        // Called by the depacketizer event handlers in RTSPClient.

        /// <summary>Called once on the first video-codec packet observed.</summary>
        public void OnVideoCodecCommitted(string wireCodec, AM_Media_Format fmt,
                                          string fmtpFormatParameter, uint rtpClockHz) {
            if (_videoDecoder != null) return;     // already committed
            var plan = CodecRegistry.ForVideo(wireCodec, fmt, fmtpFormatParameter);
            if (plan == null) {
                _log?.LogError($"[engine] no video codec plan for wire codec '{wireCodec}'");
                return;
            }
            _videoRtpClockHz = rtpClockHz > 0 ? rtpClockHz : 90000u;
            _isVideoSession = true;
            try {
                // Renderer selection is read live at codec-commit time
                // (not at engine construction) so a config flip between
                // sessions takes effect on the next Start without
                // restarting the app. Failing to read the config
                // (corrupt file, unwriteable settings dir) falls back
                // to the safe WriteableBitmap path.
                bool useD3DImage = false;
                try { useD3DImage = SoftSledConfigManager.ReadConfig().EnableD3DImage; }
                catch (Exception ex) {
                    _log?.LogError($"[engine] EnableD3DImage read failed, defaulting to " +
                                   $"WriteableBitmap path: {ex.Message}");
                }
                if (useD3DImage) {
                    _videoRenderer = new D3DImageVideoRenderer(_videoTarget, _clock, _log);
                    _log?.LogInfo("[engine] video renderer: D3DImageVideoRenderer (config: EnableD3DImage=true)");
                } else {
                    _videoRenderer = new WpfVideoRenderer(_videoTarget, _clock, _log);
                    _log?.LogInfo("[engine] video renderer: WpfVideoRenderer (config: EnableD3DImage=false)");
                }
                _videoDecoder = new VideoDecoder(plan.CodecId, plan.Extradata,
                    OnVideoFrameDecoded, _log);
                _videoDecoder.Start();
                _log?.LogInfo($"[engine] video pipeline up: {plan}");
            } catch (Exception ex) {
                _log?.LogError($"[engine] video pipeline init failed: {ex.Message}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        /// <summary>Called once on the first audio-codec packet observed.</summary>
        public void OnAudioCodecCommitted(string wireCodec, AM_Media_Format fmt,
                                          string fmtpFormatParameter, uint rtpClockHz) {
            if (_audioDecoder != null) return;     // already committed
            var plan = CodecRegistry.ForAudio(wireCodec, fmt, fmtpFormatParameter);
            if (plan == null) {
                _log?.LogError($"[engine] no audio codec plan for wire codec '{wireCodec}'");
                return;
            }
            _audioRtpClockHz = rtpClockHz > 0 ? rtpClockHz : 90000u;
            // Renderer output format: 48 kHz stereo for muxed sources (the
            // captures all advertise 48 kHz), 44.1 kHz when WAVEFORMATEX
            // says so. AudioDecoder.swr will resample as needed.
            int targetRate = plan.SrcSampleRate > 0 ? plan.SrcSampleRate : 48000;
            int targetCh   = plan.SrcChannels   > 0 ? plan.SrcChannels   : 2;
            // Cap channels at 2 (stereo) — multichannel AC3 etc. will
            // downmix in swr.
            if (targetCh > 2) targetCh = 2;
            try {
                _audioRenderer = new NAudioRenderer(_clock, targetRate, targetCh, _log);
                _audioDecoder = new AudioDecoder(plan.CodecId, plan.Extradata,
                    plan.SrcSampleRate, plan.SrcChannels, plan.SrcSampleFmt,
                    targetRate, targetCh,
                    OnAudioSampleDecoded, _log);
                _audioDecoder.Start();
                _log?.LogInfo($"[engine] audio pipeline up: {plan}, target={targetRate}Hz/{targetCh}ch");
            } catch (Exception ex) {
                _log?.LogError($"[engine] audio pipeline init failed: {ex.Message}");
                try { MediaFailed?.Invoke(ex); } catch { }
            }
        }

        /// <summary>Called for every video MAU from the depacketizer.</summary>
        public void OnDepacketizedVideo(byte[] mau, uint rtpTs, bool isKeyframe) {
            if (_videoDecoder == null || mau == null || mau.Length == 0) return;
            if (!_videoBaseAnchored) {
                _videoRtpBase = rtpTs;
                _videoBaseAnchored = true;
            }
            long ptsMs = RtpToMediaMs(rtpTs, _videoRtpBase, _videoRtpClockHz);
            _videoDecoder.SubmitPacket(mau, ptsMs);
        }

        /// <summary>Called for every audio MAU from the depacketizer.</summary>
        public void OnDepacketizedAudio(byte[] mau, uint rtpTs) {
            if (_audioDecoder == null || mau == null || mau.Length == 0) return;
            if (!_audioBaseAnchored) {
                _audioRtpBase = rtpTs;
                _audioBaseAnchored = true;
            }
            long ptsMs = RtpToMediaMs(rtpTs, _audioRtpBase, _audioRtpClockHz);
            _audioDecoder.SubmitPacket(mau, ptsMs);
        }

        /// <summary>Called by RTSPClient on TEARDOWN / CloseMedia.</summary>
        public void Complete() {
            try { _videoDecoder?.Complete(); } catch { }
            try { _audioDecoder?.Complete(); } catch { }
            try { MediaEnded?.Invoke(); } catch { }
        }

        // ============== Internal frame-arrived callbacks ==============

        private void OnVideoFrameDecoded(VideoFrameSample sample) {
            _videoRenderer?.EnqueueFrame(sample);
            if (!_videoFirstFrameSeen) {
                _videoFirstFrameSeen = true;
                TryFireBufferingEnded();
            }
        }

        private void OnAudioSampleDecoded(AudioFrameSample sample) {
            _audioRenderer?.EnqueueSample(sample);
            if (!_audioFirstSampleSeen) {
                _audioFirstSampleSeen = true;
                TryFireBufferingEnded();
            }
        }

        private void TryFireBufferingEnded() {
            // Fire once when both available streams have produced their
            // first sample (or the single stream we have, on audio-only
            // sessions where _isVideoSession stays false).
            bool ready = _isVideoSession
                ? (_videoFirstFrameSeen && _audioFirstSampleSeen)
                : _audioFirstSampleSeen;
            if (!ready) return;
            if (Interlocked.Exchange(ref _bufferingEndedFired, 1) != 0) return;
            try { BufferingEnded?.Invoke(); } catch { }
        }

        private static long RtpToMediaMs(uint rtpTs, uint baseTs, uint clockHz) {
            // SIGNED delta tolerates out-of-order delivery near the base.
            int delta = unchecked((int)(rtpTs - baseTs));
            if (clockHz == 0) clockHz = 90000;
            return (long)delta * 1000L / clockHz;
        }

        // ============== Lifecycle ==============

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _isOpen = false;
            AttachRtspClient(null);
            try { _videoDecoder?.Dispose(); } catch { }
            try { _audioDecoder?.Dispose(); } catch { }
            try { _videoRenderer?.Dispose(); } catch { }
            try { _audioRenderer?.Dispose(); } catch { }
            _videoDecoder = null;
            _audioDecoder = null;
            _videoRenderer = null;
            _audioRenderer = null;
        }
    }
}
