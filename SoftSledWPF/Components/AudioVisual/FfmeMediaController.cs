using SoftSled.Components.Diagnostics;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Unosquare.FFME;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// <see cref="IMediaController"/> implementation that adapts FFME's
    /// <see cref="MediaElement"/> to the AvCtrl handler's needs. Owned by
    /// MainWindow; passed to <c>VirtualChannelAvCtrlHandler.MediaController</c>
    /// for the lifetime of the application.
    ///
    /// Threading:
    /// <list type="bullet">
    ///   <item><description><see cref="Position"/> / <see cref="Duration"/> /
    ///     <see cref="IsOpen"/> are property reads. Called from the VC
    ///     worker thread when WMC polls GetPosition/GetDuration. FFME's
    ///     properties are safe to read off-thread.</description></item>
    ///   <item><description><see cref="PlayAsync"/> / <see cref="PauseAsync"/>
    ///     marshal to the FFME element's dispatcher before invoking, since
    ///     <c>Play()</c>/<c>Pause()</c> are dispatcher-affine.</description></item>
    ///   <item><description>FFME events arrive on its worker thread; we
    ///     forward them directly to <see cref="IMediaController"/>'s events
    ///     without dispatcher-marshaling, because the consumer
    ///     (AvCtrlHandler → OnMediaEvent → VC send) doesn't need WPF.</description></item>
    /// </list>
    ///
    /// Composition with RTSPClient: the controller doesn't *own* an
    /// RTSPClient — the AvCtrlHandler creates one per OpenMedia and
    /// calls <see cref="AttachRtspClient"/> immediately after. When the
    /// session ends (CloseMedia / Stop), AvCtrlHandler calls the same
    /// method with null. This lets the controller forward Seek/SetRate/
    /// SetBufferInfo to the live RTSPClient and re-raise its spec-events
    /// (RtspDisconnected, PtsError, UnrecoverableSkew) without the
    /// controller needing to know how the RTSPClient was constructed.
    /// </summary>
    internal sealed class FfmeMediaController : IMediaController, IDisposable {

        private readonly MediaElement _media;
        private readonly Dispatcher _dispatcher;
        private readonly Logger _log;
        private bool _disposed;

        // FFME exposes Position / NaturalDuration / IsOpen as WPF
        // DependencyProperty values. Reading a DP from any thread other
        // than the dispatcher that owns it throws InvalidOperationException
        // ("The calling thread cannot access this object because a
        // different thread owns it"). The VC handler runs on a worker
        // thread and polls GetPosition / GetDuration constantly, so we
        // CANNOT read DPs from those calls.
        //
        // Fix: subscribe to FFME's events (PositionChanged, MediaOpened,
        // MediaClosed) which fire on the dispatcher thread; cache the
        // values in volatile / interlocked fields; the property getters
        // return those cached values to any caller from any thread.
        private long _cachedPositionTicks;   // Interlocked-updated
        private long _cachedDurationTicks;   // 0 = unknown / unbounded
        private volatile bool _isOpen;

        // Pending seek that arrived before MediaOpened. Applied at open.
        private long _pendingSeekTicks = -1;
        // Pending rate that arrived before MediaOpened.
        private double _pendingRate = double.NaN;

        // The live RTSP session, when one is attached. Owned by AvCtrlHandler
        // — we just hold a weak-by-convention reference so we can forward
        // Seek/SetRate/SetBufferInfo and subscribe to its three spec-events.
        private SoftSled.Components.RTSP.RTSPClient _rtsp;

        public FfmeMediaController(MediaElement media, Logger log) {
            _media      = media ?? throw new ArgumentNullException(nameof(media));
            _dispatcher = media.Dispatcher;
            _log        = log;

            _media.MediaEnded       += MediaEndedHandler;
            _media.MediaFailed      += MediaFailedHandler;
            _media.BufferingEnded   += BufferingEndedHandler;
            // BufferingStarted has no DMCT counterpart (only BUFFERING_STOP
            // is in the enum). We still log it for diagnostics.
            _media.BufferingStarted += (s, e) =>
                _log?.LogDebug("[ffme-mc] BufferingStarted");
            _media.MediaOpened      += MediaOpenedHandler;
            _media.MediaClosed      += MediaClosedHandler;
            _media.PositionChanged  += PositionChangedHandler;
        }

        /// <summary>
        /// Attach (or detach, with null) the live RTSP session. Called by
        /// AvCtrlHandler in OpenMedia / CloseMedia. Subscribing here means
        /// the spec-events (RtspDisconnected/PtsError/UnrecoverableSkew)
        /// flow without AvCtrl having to know about FfmeMediaController's
        /// internals.
        /// </summary>
        public void AttachRtspClient(SoftSled.Components.RTSP.RTSPClient client) {
            if (ReferenceEquals(_rtsp, client)) return;
            if (_rtsp != null) {
                _rtsp.Disconnected      -= RtspDisconnectedHandler;
                _rtsp.PtsError          -= RtspPtsErrorHandler;
                _rtsp.UnrecoverableSkew -= RtspUnrecoverableSkewHandler;
            }
            _rtsp = client;
            if (_rtsp != null) {
                _rtsp.Disconnected      += RtspDisconnectedHandler;
                _rtsp.PtsError          += RtspPtsErrorHandler;
                _rtsp.UnrecoverableSkew += RtspUnrecoverableSkewHandler;
            }
        }

        // ----- IMediaController properties --------------------------------
        // All read off the cached fields; safe from any thread.

        public TimeSpan Position => new TimeSpan(System.Threading.Interlocked.Read(ref _cachedPositionTicks));

        public TimeSpan? Duration {
            get {
                long t = System.Threading.Interlocked.Read(ref _cachedDurationTicks);
                return t > 0 ? new TimeSpan(t) : (TimeSpan?)null;
            }
        }

        public bool IsOpen => !_disposed && _isOpen;

        // ----- IMediaController actions -----------------------------------

        public async Task PlayAsync() {
            if (_disposed) return;
            // FFME's Play()/Pause() return ConfiguredTaskAwaitable<bool>
            // rather than Task — fine to await directly. Dispatcher
            // marshaling: if we're already on the dispatcher thread,
            // await directly; otherwise post the work and bridge via a
            // TaskCompletionSource so callers see a normal Task.
            if (_dispatcher.CheckAccess()) {
                await _media.Play();
                return;
            }
            var tcs = new TaskCompletionSource<bool>();
            _dispatcher.BeginInvoke(new Action(async () => {
                try { await _media.Play(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
            await tcs.Task;
        }

        public async Task PauseAsync() {
            if (_disposed) return;
            if (_dispatcher.CheckAccess()) {
                await _media.Pause();
                return;
            }
            var tcs = new TaskCompletionSource<bool>();
            _dispatcher.BeginInvoke(new Action(async () => {
                try { await _media.Pause(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
            await tcs.Task;
        }

        public async Task SeekAsync(TimeSpan position) {
            if (_disposed) return;

            // RTSP-side seek (the real one, since live unicast streams
            // can't be repositioned inside FFME's demuxer queue) — issue
            // a fresh PLAY with Range. The rate stays whatever was last
            // requested. Fire-and-forget at the RTSP layer; the server
            // response is handled by the existing message-routing.
            try {
                _rtsp?.Play(startMs: (long)position.TotalMilliseconds,
                            rate: double.IsNaN(_pendingRate) ? 1.0 : _pendingRate);
            } catch (Exception ex) {
                _log?.LogError($"[ffme-mc] RTSP seek failed: {ex.Message}");
            }

            // FFME-side seek — best-effort. For seekable streams (file-
            // backed, finite-duration RTSP with npt range) this snaps
            // FFME's playhead so the display catches up immediately
            // without waiting for the next sample. For non-seekable
            // (live) streams FFME ignores the call.
            if (!_isOpen) {
                // Open hasn't fired yet. Stash and apply at MediaOpened.
                System.Threading.Interlocked.Exchange(ref _pendingSeekTicks, position.Ticks);
                return;
            }
            try {
                if (_dispatcher.CheckAccess()) {
                    await _media.Seek(position);
                } else {
                    var tcs = new TaskCompletionSource<bool>();
                    _dispatcher.BeginInvoke(new Action(async () => {
                        try { await _media.Seek(position); tcs.SetResult(true); }
                        catch (Exception ex) { tcs.SetException(ex); }
                    }));
                    await tcs.Task;
                }
            } catch (Exception ex) {
                _log?.LogDebug($"[ffme-mc] FFME Seek failed (expected for live streams): {ex.Message}");
            }
        }

        public async Task SetRateAsync(double rate) {
            if (_disposed) return;
            _pendingRate = rate;

            // Rate-split per the trick-play plan:
            //
            //   * Rates in [0.5, 2.0] → CLIENT-SIDE via FFME SpeedRatio
            //     (atempo resampler keeps audio intelligible). The
            //     server stays at 1× so audio + video continue
            //     normally. We MUST also reset the RTSP rate to 1.0
            //     in case we're stepping DOWN from a higher rate
            //     (e.g. 3× → 1.5×) — otherwise the server stays in
            //     trick-play mode with no audio and we end up with
            //     compound scaling that under-runs the FFME audio
            //     buffer.
            //
            //   * Anything outside [0.5, 2.0] (incl. zero / negative
            //     for reverse) → SERVER-SIDE via RTSP PLAY-with-Scale.
            //     FFME runs at SpeedRatio=1.0 (otherwise the already-
            //     rate-adjusted server stream gets double-scaled).
            //     Audio is expected to be silent during this — DLNA
            //     servers drop audio at non-1× rates. IsTimeSyncDisabled
            //     (set in AsfFfmeInputStream.OnInitializing) keeps the
            //     video element advancing despite the audio dropout.
            //
            // Negative rates always go server-side because FFME's
            // SpeedRatio cannot be negative.
            bool clientSideEligible = rate >= 0.5 && rate <= 2.0;

            if (clientSideEligible) {
                _log?.LogDebug($"[ffme-mc] SetRateAsync({rate}) — CLIENT-SIDE " +
                              "(SpeedRatio + atempo, server stays at 1×)");

                // Reset server to 1× regardless of prior state — RTSPClient.SetRate
                // is wire-idempotent so this is a no-op if we were already at 1×.
                try { _rtsp?.SetRate(1.0); }
                catch (Exception ex) {
                    _log?.LogError($"[ffme-mc] RTSP SetRate(1.0) for client-side rate failed: {ex.Message}");
                }

                await ApplySpeedRatioAsync(rate);
            } else {
                _log?.LogDebug($"[ffme-mc] SetRateAsync({rate}) — SERVER-SIDE " +
                              "(RTSP PLAY-with-Scale, audio drop expected)");

                // FFME at 1× — the server-delivered stream is already
                // rate-adjusted, applying SpeedRatio again would
                // double-scale.
                await ApplySpeedRatioAsync(1.0);

                try { _rtsp?.SetRate(rate); }
                catch (Exception ex) {
                    _log?.LogError($"[ffme-mc] RTSP SetRate({rate}) failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Apply a SpeedRatio value to the FFME element, marshaling
        /// onto its dispatcher if necessary. Swallows + logs errors
        /// rather than propagating so a transient SpeedRatio failure
        /// doesn't cascade into the RTSP rate path.
        /// </summary>
        private async Task ApplySpeedRatioAsync(double rate) {
            if (_dispatcher.CheckAccess()) {
                try { _media.SpeedRatio = rate; }
                catch (Exception ex) {
                    _log?.LogError($"[ffme-mc] SpeedRatio={rate} set failed: {ex.Message}");
                }
                return;
            }
            var tcs = new TaskCompletionSource<bool>();
            _dispatcher.BeginInvoke(new Action(() => {
                try { _media.SpeedRatio = rate; tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
            try { await tcs.Task; }
            catch (Exception ex) {
                _log?.LogError($"[ffme-mc] SpeedRatio={rate} dispatcher set failed: {ex.Message}");
            }
        }

        public void SetAvailableBandwidth(long bitsPerSecond) {
            if (_disposed) return;
            try { _rtsp?.SetBufferInfo(bitsPerSecond, _lastOptimisedPreroll); }
            catch (Exception ex) {
                _log?.LogError($"[ffme-mc] SetBufferInfo (bandwidth) failed: {ex.Message}");
            }
            _lastBandwidthBps = bitsPerSecond;
        }

        public void SetOptimisedPreroll(bool optimised) {
            if (_disposed) return;
            try { _rtsp?.SetBufferInfo(_lastBandwidthBps, optimised); }
            catch (Exception ex) {
                _log?.LogError($"[ffme-mc] SetBufferInfo (preroll) failed: {ex.Message}");
            }
            _lastOptimisedPreroll = optimised;
        }

        private long _lastBandwidthBps = -1;
        private bool _lastOptimisedPreroll;

        // ----- IMediaController events ------------------------------------

        public event Action BufferingEnded;
        public event Action MediaEnded;
        public event Action<Exception> MediaFailed;
        public event Action<Exception> RtspDisconnected;
        public event Action<PtsErrorInfo> PtsError;
        public event Action<SkewInfo> UnrecoverableSkew;

        private void BufferingEndedHandler(object sender, EventArgs e) {
            _log?.LogInfo("[ffme-mc] BufferingEnded → BUFFERING_STOP");
            try { BufferingEnded?.Invoke(); }
            catch (Exception ex) { _log?.LogError($"[ffme-mc] BufferingEnded subscriber threw: {ex.Message}"); }
        }

        private void MediaEndedHandler(object sender, EventArgs e) {
            _log?.LogInfo("[ffme-mc] MediaEnded → END_OF_MEDIA");
            try { MediaEnded?.Invoke(); }
            catch (Exception ex) { _log?.LogError($"[ffme-mc] MediaEnded subscriber threw: {ex.Message}"); }
        }

        private void MediaFailedHandler(object sender, Unosquare.FFME.Common.MediaFailedEventArgs e) {
            var ex = e.ErrorException;
            _log?.LogError($"[ffme-mc] MediaFailed: {ex?.Message ?? "(no message)"}");
            try { MediaFailed?.Invoke(ex); }
            catch (Exception ex2) { _log?.LogError($"[ffme-mc] MediaFailed subscriber threw: {ex2.Message}"); }
        }

        private void RtspDisconnectedHandler(Exception ex) {
            _log?.LogError($"[ffme-mc] RTSP Disconnected: {ex?.Message ?? "(no message)"}");
            try { RtspDisconnected?.Invoke(ex); }
            catch (Exception ex2) { _log?.LogError($"[ffme-mc] RtspDisconnected subscriber threw: {ex2.Message}"); }
        }

        private void RtspPtsErrorHandler(PtsErrorInfo info) {
            _log?.LogDebug($"[ffme-mc] PtsError: stream={(info.IsAudio?"audio":"video")} " +
                          $"prev={info.PreviousPtsMs} cur={info.CurrentPtsMs} delta={info.DeltaMs}");
            try { PtsError?.Invoke(info); }
            catch (Exception ex2) { _log?.LogError($"[ffme-mc] PtsError subscriber threw: {ex2.Message}"); }
        }

        private void RtspUnrecoverableSkewHandler(SkewInfo info) {
            _log?.LogError($"[ffme-mc] UnrecoverableSkew: {info.TheCause} value={info.ValueMs}ms");
            try { UnrecoverableSkew?.Invoke(info); }
            catch (Exception ex2) { _log?.LogError($"[ffme-mc] UnrecoverableSkew subscriber threw: {ex2.Message}"); }
        }

        // ----- Cache-maintaining handlers (run on dispatcher thread) -----

        private void MediaOpenedHandler(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e) {
            // MediaOpenedEventArgs.Info.Duration is a TimeSpan (not TimeSpan?).
            // Zero / negative = unbounded / unknown; positive = finite.
            // We mirror that into _cachedDurationTicks (0 = unknown).
            long durTicks = 0;
            try {
                var d = e.Info.Duration;
                if (d > TimeSpan.Zero) durTicks = d.Ticks;
            } catch { /* be lenient — Info might not be fully populated */ }
            System.Threading.Interlocked.Exchange(ref _cachedDurationTicks, durTicks);
            System.Threading.Interlocked.Exchange(ref _cachedPositionTicks, 0L);
            _isOpen = true;
            _log?.LogInfo($"[ffme-mc] MediaOpened — duration: " +
                          (durTicks > 0 ? new TimeSpan(durTicks).ToString() : "live / unbounded"));

            // Notify RTSPClient so its decoder-open stopwatch (UNRECOVERABLE_SKEW
            // cause = DecoderOpenTooSlow, Layer 4g(a)) can complete.
            try { _rtsp?.NotifyDecoderOpened(); } catch { }

            // Apply any seek that arrived before MediaOpened.
            long stashed = System.Threading.Interlocked.Exchange(ref _pendingSeekTicks, -1);
            if (stashed >= 0) {
                var pos = new TimeSpan(stashed);
                _log?.LogInfo($"[ffme-mc] applying deferred seek to {pos}");
                _ = SeekAsync(pos);
            }
            // Apply any rate that arrived before MediaOpened.
            if (!double.IsNaN(_pendingRate)) {
                double r = _pendingRate;
                _pendingRate = double.NaN;
                _ = SetRateAsync(r);
            }
        }

        private void MediaClosedHandler(object sender, EventArgs e) {
            _isOpen = false;
            System.Threading.Interlocked.Exchange(ref _cachedPositionTicks, 0L);
            System.Threading.Interlocked.Exchange(ref _cachedDurationTicks, 0L);
            System.Threading.Interlocked.Exchange(ref _pendingSeekTicks, -1L);
            _pendingRate = double.NaN;
            _log?.LogInfo("[ffme-mc] MediaClosed");
        }

        private void PositionChangedHandler(object sender,
            Unosquare.FFME.Common.PositionChangedEventArgs e) {
            // PositionChanged fires on the dispatcher thread. Cache the
            // value so the VC worker thread can read it lock-free via
            // Interlocked.Read.
            System.Threading.Interlocked.Exchange(ref _cachedPositionTicks, e.Position.Ticks);
        }

        // ----- Lifecycle --------------------------------------------------

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            AttachRtspClient(null);
            try { _media.MediaEnded       -= MediaEndedHandler;       } catch { }
            try { _media.MediaFailed      -= MediaFailedHandler;      } catch { }
            try { _media.BufferingEnded   -= BufferingEndedHandler;   } catch { }
            try { _media.MediaOpened      -= MediaOpenedHandler;      } catch { }
            try { _media.MediaClosed      -= MediaClosedHandler;      } catch { }
            try { _media.PositionChanged  -= PositionChangedHandler;  } catch { }
        }
    }
}
