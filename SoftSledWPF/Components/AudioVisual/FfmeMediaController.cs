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

        // ----- IMediaController events ------------------------------------

        public event Action BufferingEnded;
        public event Action MediaEnded;
        public event Action<Exception> MediaFailed;

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
        }

        private void MediaClosedHandler(object sender, EventArgs e) {
            _isOpen = false;
            System.Threading.Interlocked.Exchange(ref _cachedPositionTicks, 0L);
            System.Threading.Interlocked.Exchange(ref _cachedDurationTicks, 0L);
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
            try { _media.MediaEnded       -= MediaEndedHandler;       } catch { }
            try { _media.MediaFailed      -= MediaFailedHandler;      } catch { }
            try { _media.BufferingEnded   -= BufferingEndedHandler;   } catch { }
            try { _media.MediaOpened      -= MediaOpenedHandler;      } catch { }
            try { _media.MediaClosed      -= MediaClosedHandler;      } catch { }
            try { _media.PositionChanged  -= PositionChangedHandler;  } catch { }
        }
    }
}
