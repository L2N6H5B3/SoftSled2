using NAudio.Wave;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SoftSled.Components.AudioVisual.ExternalSync {

    /// <summary>
    /// NAudio output renderer that doubles as the master clock for
    /// external-sync mode. PCM samples are written via
    /// <see cref="WritePcm"/>; the audio device plays them at its
    /// native rate; <see cref="GetMediaTimeMs"/> reports the exact
    /// number of bytes the device has actually played, divided by
    /// the wave format's average-bytes-per-second.
    ///
    /// <para>This is the "audio render clock as master" position that
    /// the sync controller will use to chase video — but for Phase 1
    /// the audio-only MP3 path just needs accurate position
    /// reporting so AvCtrlHandler's GetPosition returns the right
    /// thing.</para>
    ///
    /// <para>Format is hard-coded to int16 stereo at the rate
    /// supplied at construction (typically
    /// <see cref="LibAvAudioDecoder.OutSampleRate"/> = 48 kHz) so
    /// the decoder's swr_convert output drops in without further
    /// reformatting. <see cref="BufferedWaveProvider"/> tolerates a
    /// few seconds of buffered audio before reporting overflow,
    /// which is enough headroom for any reasonable network jitter.</para>
    /// </summary>
    internal sealed class NAudioMasterRenderer : IDisposable {

        // Buffer depth. 5 seconds is generous but matters: if the
        // wire goes quiet (e.g. trick play, server-side mute), we
        // want playback to continue until the buffer drains. After
        // it drains, GetMediaTimeMs stops advancing — which is
        // correct (silence at the device = no media-time progress).
        private const double BufferSeconds = 5.0;

        // Device latency target. Lower = less buffered in the driver,
        // but if it goes too low you get underrun stutter. 100 ms is
        // a safe default; matches WaveOutEvent's recommended range.
        private const int DesiredLatencyMs = 100;

        private readonly Logger _log;
        private readonly WaveFormat _format;
        private readonly BufferedWaveProvider _provider;
        private readonly WaveOutEvent _device;
        private readonly object _gate = new object();

        // PTS of the first PCM byte written. GetMediaTimeMs reports
        // (basePtsMs + samples_played_in_ms). This makes the
        // controller's Position absolute-to-the-stream rather than
        // 0-relative, so seeks and resumes report sensible values.
        private long _basePtsMs;
        private bool _baseSet;
        private long _bytesWritten;
        private bool _disposed;

        // Diagnostics — how many under/overrun events have we seen
        // in steady state. Cumulative since construction.
        private int _overflowEvents;

        // Phase 2.6: hold mode + skip-on-release.
        //
        // When _holding is true, WritePcm enqueues PCM byte arrays
        // into _holdQueue instead of writing to _provider, and the
        // audio device is not started. Once the caller knows where
        // audio should be aligned (typically when the video element
        // opens and we can compute audio-vs-video first-MAU PTS
        // skew), ReleaseHold(skipMs) is called: any held samples
        // matching the first `skipMs` of audio are discarded; the
        // rest are drained into the provider; _device.Play() runs.
        //
        // This eliminates the visible "audio plays first for ~25
        // seconds while the sync controller slowly nudges video
        // forward" pattern when the server sent video from a later
        // wire-PTS than audio. Instead, audio starts already at the
        // correct wire-PTS for video's first frame.
        //
        // _pendingSkipBytes lets a partial skip carry over to the
        // next WritePcm if the held queue ran out before the skip
        // budget was consumed (e.g. video opened sooner than 1769
        // ms of audio had been decoded yet).
        private bool _holding;
        private Queue<byte[]> _holdQueue;
        private long _pendingSkipBytes;

        public NAudioMasterRenderer(int sampleRate, int channels, int bitsPerSample, Logger log)
            : this(sampleRate, channels, bitsPerSample, log, startHeld: false) { }

        public NAudioMasterRenderer(int sampleRate, int channels, int bitsPerSample,
                                    Logger log, bool startHeld) {
            _log = log;
            _format = new WaveFormat(sampleRate, bitsPerSample, channels);
            _provider = new BufferedWaveProvider(_format) {
                BufferDuration = TimeSpan.FromSeconds(BufferSeconds),
                // false: AddSamples throws if the buffer is full. We
                // catch and log the throw rather than silently
                // discarding. Lets us notice if the upstream pipeline
                // is producing faster than the device can drain
                // (shouldn't happen in normal playback).
                DiscardOnBufferOverflow = false,
                ReadFully = true,    // pad with silence on underrun (no clicks)
            };
            _device = new WaveOutEvent {
                DesiredLatency = DesiredLatencyMs,
            };
            _device.Init(_provider);
            _holding = startHeld;
            if (startHeld) {
                _holdQueue = new Queue<byte[]>();
            }
            _log?.LogInfo($"[naudio-master] device ready: {sampleRate}Hz/{channels}ch/{bitsPerSample}bit, " +
                          $"bufferDuration={BufferSeconds:F1}s, latency={DesiredLatencyMs}ms, " +
                          $"startHeld={startHeld}");
        }

        // ----- Playback control -----

        public void Play() {
            if (_disposed) return;
            try { _device.Play(); }
            catch (Exception ex) { _log?.LogError($"[naudio-master] Play failed: {ex.Message}"); }
        }

        public void Pause() {
            if (_disposed) return;
            try { _device.Pause(); }
            catch (Exception ex) { _log?.LogError($"[naudio-master] Pause failed: {ex.Message}"); }
        }

        public void Stop() {
            if (_disposed) return;
            try { _device.Stop(); }
            catch { /* device may already be stopped */ }
        }

        public PlaybackState PlaybackState => _disposed ? PlaybackState.Stopped : _device.PlaybackState;

        // ----- Sample input -----

        /// <summary>
        /// Hand decoded PCM bytes to the device. <paramref name="ptsMs"/>
        /// is the engine-time PTS of the FIRST sample in the buffer;
        /// the first call's value becomes the base for
        /// <see cref="GetMediaTimeMs"/>. Subsequent calls are
        /// expected to be contiguous (no PTS gaps) — we don't
        /// reset the base on later writes.
        /// </summary>
        public void WritePcm(byte[] pcm, int len, long ptsMs) {
            if (_disposed || pcm == null || len <= 0) return;
            lock (_gate) {
                if (!_baseSet) {
                    _basePtsMs = ptsMs;
                    _baseSet = true;
                    _log?.LogInfo($"[naudio-master] first sample written, basePts={ptsMs}ms");
                }
                if (_holding) {
                    // Take a private copy — the caller (decoder) reuses
                    // its scratch buffer between frames, so we can't
                    // hold a reference to the supplied array.
                    byte[] copy = new byte[len];
                    Buffer.BlockCopy(pcm, 0, copy, 0, len);
                    _holdQueue.Enqueue(copy);
                    return;
                }
            }
            FeedPcmInternal(pcm, 0, len);
        }

        /// <summary>
        /// Internal feed path used by both <see cref="WritePcm"/>
        /// (after hold released) and <see cref="ReleaseHold"/>
        /// (draining the held queue). Applies any
        /// <c>_pendingSkipBytes</c> first — bytes inside the skip
        /// budget are discarded; only bytes past the budget reach
        /// the audio device. <paramref name="offset"/> +
        /// <paramref name="len"/> must be within <paramref name="pcm"/>.
        /// </summary>
        private void FeedPcmInternal(byte[] pcm, int offset, int len) {
            if (len <= 0) return;
            // Apply skip budget under the gate so a concurrent
            // ReleaseHold update doesn't half-skip.
            int writeOffset = offset;
            int writeLen = len;
            lock (_gate) {
                if (_pendingSkipBytes > 0) {
                    if (writeLen <= _pendingSkipBytes) {
                        _pendingSkipBytes -= writeLen;
                        return;  // entire chunk skipped
                    }
                    int eat = (int)_pendingSkipBytes;
                    writeOffset += eat;
                    writeLen -= eat;
                    _pendingSkipBytes = 0;
                }
            }
            try {
                _provider.AddSamples(pcm, writeOffset, writeLen);
                Interlocked.Add(ref _bytesWritten, writeLen);
            } catch (InvalidOperationException) {
                // BufferedWaveProvider throws when full. Drop the
                // chunk and count the event. In practice this
                // shouldn't fire under normal playback because the
                // device drains at real-time rate; firing means
                // the device is paused or the upstream is bursting.
                int n = Interlocked.Increment(ref _overflowEvents);
                if ((n & 0xF) == 1) {
                    _log?.LogError($"[naudio-master] buffer overflow (total {n}) — " +
                                   $"dropping {writeLen}B; bufferedBytes={_provider.BufferedBytes}");
                }
            }
        }

        /// <summary>
        /// Release the hold (Phase 2.6). The first
        /// <paramref name="skipMs"/> of held audio is discarded;
        /// the remainder is drained into the device, then playback
        /// starts. If the held queue runs out before the skip
        /// budget is consumed, the remaining skip carries over to
        /// the next <see cref="WritePcm"/> call (so it still works
        /// when video opens earlier than we have buffered audio for).
        ///
        /// <para>Returns silently if the renderer wasn't started
        /// held, or has already been released.</para>
        /// </summary>
        public void ReleaseHold(int skipMs) {
            if (_disposed) return;
            byte[][] heldCopy = null;
            long skipBytes = 0;
            lock (_gate) {
                if (!_holding) return;
                _holding = false;
                long bps = _format.AverageBytesPerSecond;
                skipBytes = skipMs > 0 ? skipMs * bps / 1000L : 0L;
                _pendingSkipBytes = skipBytes;
                if (_holdQueue != null) {
                    heldCopy = _holdQueue.ToArray();
                    _holdQueue.Clear();
                    _holdQueue = null;
                }
            }
            long heldBytes = 0;
            if (heldCopy != null) {
                foreach (var b in heldCopy) heldBytes += b.LongLength;
                foreach (var b in heldCopy) FeedPcmInternal(b, 0, b.Length);
            }
            long stillPending = Interlocked.Read(ref _bytesWritten);   // for log only
            _log?.LogInfo($"[naudio-master] hold released: skipMs={skipMs} (skipBytes={skipBytes}) " +
                          $"heldBytes={heldBytes} bytesQueued={stillPending} " +
                          $"pendingSkipCarryover={_pendingSkipBytes}");
            Play();
        }

        /// <summary>True if the renderer is still holding the audio
        /// queue waiting for <see cref="ReleaseHold"/>. False once
        /// released or if it was never started held.</summary>
        public bool IsHeld {
            get { lock (_gate) { return _holding; } }
        }

        // ----- Position reporting (the "master clock") -----

        /// <summary>
        /// Current media time, in milliseconds, derived from the
        /// audio device's actual playback position. This is the
        /// authoritative "now" for the external-sync mode — what
        /// AvCtrlHandler.GetPosition will report to WMC and what
        /// the sync controller compares video against.
        ///
        /// <para>0-relative: returns 0 before any PCM is played
        /// and advances at real-time rate as samples drain. The
        /// wire-side base PTS (<see cref="_basePtsMs"/>) is
        /// deliberately NOT added because:</para>
        /// <list type="bullet">
        ///   <item>WMC's AvCtrlHandler.GetPosition adds its own
        ///   StartPayloadStartTime — combining that with a wire-PTS
        ///   prefix would double-count the offset.</item>
        ///   <item>FFME's <c>MediaElement.Position</c> is also
        ///   0-relative to MediaOpened — sync controller can compare
        ///   directly without scaling.</item>
        ///   <item>The wire PTS is just "where in the server's
        ///   stream we happened to join" — meaningless to anything
        ///   downstream that cares about playback progress rather
        ///   than wall-clock position in the source asset.</item>
        /// </list>
        /// </summary>
        public long GetMediaTimeMs() {
            if (_disposed || !_baseSet) return 0;
            try {
                // GetPosition: bytes actually played by the device
                // (cumulative since device.Play). Sub-millisecond
                // precision.
                long bytesPlayed = _device.GetPosition();
                if (bytesPlayed < 0) bytesPlayed = 0;
                double sec = bytesPlayed / (double)_format.AverageBytesPerSecond;
                return (long)(sec * 1000.0);
            } catch {
                return 0;
            }
        }

        /// <summary>Wire-side PTS of the first sample written, in ms.
        /// Diagnostic only — not added to <see cref="GetMediaTimeMs"/>.
        /// Useful for log correlation with RTP-Info / RTCP SR
        /// timestamps from the server side.</summary>
        public long FirstSampleWirePtsMs => _baseSet ? _basePtsMs : 0;

        /// <summary>Diagnostics: how many bytes the device has played
        /// vs how many bytes we've written. Useful for logging
        /// drain progress.</summary>
        public long BytesPlayed {
            get {
                if (_disposed) return 0;
                try { return _device.GetPosition(); } catch { return 0; }
            }
        }

        public long BytesWritten => Interlocked.Read(ref _bytesWritten);
        public int BufferedBytes {
            get {
                if (_disposed) return 0;
                try { return _provider.BufferedBytes; } catch { return 0; }
            }
        }

        // ----- Teardown -----

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _device.Stop(); } catch { }
            try { _device.Dispose(); } catch { }
            _log?.LogInfo($"[naudio-master] disposed (bytesWritten={_bytesWritten}, " +
                          $"bytesPlayed={BytesPlayed}, overflowEvents={_overflowEvents})");
        }
    }
}
