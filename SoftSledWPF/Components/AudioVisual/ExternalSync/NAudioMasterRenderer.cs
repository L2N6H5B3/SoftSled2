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

        /// <summary>
        /// Pass-through wave provider that pads underruns with silence (like
        /// BufferedWaveProvider's ReadFully) but COUNTS every padded byte.
        /// The device's GetPosition() counts rendered silence as "played", so
        /// without this count each delivery gap permanently inflated the
        /// master clock by the gap's length — after the gap, video was paced
        /// that far ahead of the audible audio for the rest of the media,
        /// compounding with every subsequent gap (observed: a ~1s wire stall
        /// embedded 243ms of silence → video ~240ms early from then on).
        /// Counting at the consume point is exact — no sampling heuristics.
        /// </summary>
        private sealed class SilenceCountingProvider : IWaveProvider {
            private readonly BufferedWaveProvider _inner;   // ReadFully = false
            private long _silenceBytes;
            public SilenceCountingProvider(BufferedWaveProvider inner) { _inner = inner; }
            public WaveFormat WaveFormat => _inner.WaveFormat;
            public long SilenceBytes => Interlocked.Read(ref _silenceBytes);
            public int Read(byte[] buffer, int offset, int count) {
                int got;
                try { got = _inner.Read(buffer, offset, count); }
                catch { got = 0; }
                if (got < count) {
                    Array.Clear(buffer, offset + got, count - got);
                    Interlocked.Add(ref _silenceBytes, count - got);
                }
                return count;   // always satisfy the device — no auto-stop, no clicks
            }
        }

        // Buffer depth. Must hold the normal delivery cushion (~2s) PLUS a
        // maximal content-gap fill (GapFillMaxMs of silence injected in one
        // write — see WritePcm's gap fill) without overflowing, because
        // DiscardOnBufferOverflow=false makes AddSamples throw on overflow
        // and the dropped chunk would itself desync the clock. 20s of 16-bit
        // 48k stereo is ~3.8MB — cheap. If the wire goes quiet (trick play,
        // server-side mute) playback continues until the buffer drains; after
        // that GetMediaTimeMs stops advancing — which is correct.
        private const double BufferSeconds = 20.0;

        // Device latency target. Lower = less buffered in the driver,
        // but if it goes too low you get underrun stutter. 100 ms is
        // a safe default; matches WaveOutEvent's recommended range.
        private const int DesiredLatencyMs = 100;

        private readonly Logger _log;
        private readonly WaveFormat _format;
        private readonly BufferedWaveProvider _provider;
        private readonly SilenceCountingProvider _silenceProvider;
        private readonly WaveOutEvent _device;
        private readonly object _gate = new object();
        // Monotonic floor for GetMediaTimeMs: the silence subtraction can
        // transiently dip (silence is counted at consume time, ~device-latency
        // ahead of GetPosition's render time), and the clock must never run
        // backwards under the pacer.
        private long _lastMediaTimeMs;

        // PTS of the first PCM byte written. GetMediaTimeMs reports
        // (basePtsMs + samples_played_in_ms). This makes the
        // controller's Position absolute-to-the-stream rather than
        // 0-relative, so seeks and resumes report sensible values.
        private long _basePtsMs;
        private bool _baseSet;
        private long _bytesWritten;
        // Bytes written to the provider but DISCARDED unplayed by ClearBuffer
        // (seek / trick-play exit / media halt). The master clock caps at real
        // playable content = _bytesWritten − _bytesDiscarded; without the
        // subtraction each clear permanently loosened the silence-stall cap by
        // the cleared amount (cumulative over seeks), letting the clock run
        // through that much underrun silence and drag slaved video ahead.
        private long _bytesDiscarded;
        private long _segmentStartMasterMs = -1; // see ClearBuffer / SegmentStartMasterMs
        private bool _disposed;

        // Diagnostic (av-timing): wall-clock since the device first started
        // playing. Lets the controller compare the master clock (which is
        // derived from GetPosition, i.e. the audio the device THINKS it has
        // played) against real elapsed time — surfacing whether the audio
        // clock leads/lags real-time and by how much.
        private readonly System.Diagnostics.Stopwatch _playWall = new System.Diagnostics.Stopwatch();

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

        // Startup PRE-ROLL: when started held, accumulate this much audio before
        // releasing playback, so the device starts with a cushion and doesn't
        // stutter through the bursty first second of delivery. Auto-released by
        // WritePcm once the held audio reaches PrerollTargetMs (or the timeout
        // elapses, so slow/short startup delivery can't hang the hold forever).
        private const int PrerollTargetMs  = 1000;
        private const int PrerollTimeoutMs = 3000;
        private long _heldBytes;
        private readonly System.Diagnostics.Stopwatch _holdWall = new System.Diagnostics.Stopwatch();

        // ----- Audio content-gap fill (timestamp-aware feeding) -----
        //
        // The wire timestamps are the content timeline, but the master clock
        // is byte-derived, and writing PCM byte-contiguously ERASES content
        // gaps (recording defects / lost packets): post-gap audio plays early
        // by the gap length, while video — paced by its own honestly-jumped
        // PTS — correctly waits, ending up behind by exactly the gap for the
        // rest of the media (observed: a recording missing ~5s of both
        // streams). WMC/Xbox play both streams by timestamp, so a shared gap
        // collapses for both together. Filling the gap with silence makes the
        // byte clock isomorphic to the content timeline: audio goes quiet for
        // the missing span, video holds its last frame to its jumped
        // schedule, and both resume IN SYNC.
        //
        // Expected pts is derived from a (pts, authored-bytes) reference pair
        // — never per-write accumulation, whose integer rounding (21.333ms
        // PCM frames) would drift into false gaps. The reference re-seeds on
        // ClearBuffer (seek / trick-play: the new position's pts jump is a
        // legitimate discontinuity, not a gap) and on implausible jumps.
        // Backwards jumps are the mirror image: the recording's audio timeline
        // FOLDS BACK on itself (observed: −1914ms in one broadcast recording —
        // a capture hiccup wrote overlapping audio). Replaying the overlap
        // shifts the byte clock the other way (video ends up behind by the
        // fold). Fix: TRIM the overlapped prefix of each incoming chunk until
        // the timeline catches back up to where authored content already
        // reached — the duplicate samples are dropped, the clock stays on the
        // content timeline. Stateless per chunk: overlap = expected − pts; eat
        // min(len, overlap); the ref pair is deliberately NOT re-based, so the
        // deficit shrinks chunk by chunk until pts passes expected again.
        private const int GapFillMinMs = 60;         // |gap| below: rtpTs quantisation / multi-frame packets — ignore
        private const int GapDiscontinuityMs = 10000; // |gap| above: discontinuity — re-seed, no fill/trim
        private long _gapRefPtsMs = long.MinValue;   // pts↔bytes reference; long.MinValue = re-seed on next write
        private long _gapRefAuthoredBytes;
        private long _authoredBytes;                 // content bytes accepted via WritePcm (data + gap fills)
        private long _gapFilledBytes;                // cumulative gap-fill silence (diagnostic)
        private long _overlapTrimmedBytes;           // cumulative overlap-trimmed audio (diagnostic)
        private int  _lastTrimLogTick;               // rate-limit for per-chunk trim logging
        private static readonly byte[] ZeroChunk = new byte[32768];

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
                // Underrun silence is padded (and COUNTED) by the
                // SilenceCountingProvider wrapper below, not here — with
                // ReadFully=true the provider pads internally and the padded
                // amount is unobservable, which is how underrun silence got
                // silently baked into the master clock (see _silenceProvider).
                ReadFully = false,
            };
            _silenceProvider = new SilenceCountingProvider(_provider);
            _device = new WaveOutEvent {
                DesiredLatency = DesiredLatencyMs,
            };
            _device.Init(_silenceProvider);
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
            try { _device.Play(); if (!_playWall.IsRunning) _playWall.Start(); }
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

        /// <summary>
        /// Discard any buffered-but-unplayed PCM. Used on seek / trick-play
        /// exit: the server re-streams FROM the new point, so the audio we'd
        /// buffered ahead must be dropped or it would replay (an audible
        /// repeat). <c>_bytesWritten</c> stays cumulative (monotonic clock);
        /// the discarded amount is tracked in <c>_bytesDiscarded</c> so the
        /// clock's content cap (written − discarded) stays tight — it stalls
        /// at real playable content until fresh PCM arrives.
        /// </summary>
        public void ClearBuffer() {
            if (_disposed) return;
            lock (_gate) {
                // Account for the unplayed bytes we're about to discard so the
                // master clock's content cap (written − discarded) stays tight
                // (see _bytesDiscarded).
                try { Interlocked.Add(ref _bytesDiscarded, _provider.BufferedBytes); } catch { }
                try { _provider.ClearBuffer(); } catch { }
                // Re-seed the content-gap tracker: the next segment's pts jump
                // (seek / trick-play exit) is a legitimate discontinuity, not
                // missing content — it must not trigger a silence fill.
                _gapRefPtsMs = long.MinValue;
                // Record the master-clock value at which the audio written AFTER
                // this clear begins playing. The clock is min(bytesPlayed,
                // bytesWritten)/rate. The buffered-but-now-cleared audio between
                // bytesPlayed and bytesWritten is DROPPED (not played) — the new
                // segment's first sample plays right after the current PLAYHEAD,
                // so the segment-start ≈ the current clock (bytesPlayed-based),
                // NOT bytesWritten/rate (which counts the cleared audio and would
                // anchor the video too LATE → audio ahead — observed). The pacer
                // uses this as the re-anchor masterAtAnchor so video pairs with
                // the audio's TRUE restart point instead of the (later)
                // video-frame-arrival time (which baked video decode latency in
                // as an audio-ahead lag).
                long abps = _format.AverageBytesPerSecond;
                long played = 0; try { played = _device.GetPosition(); } catch { }
                if (played < 0) played = 0;
                long content = Interlocked.Read(ref _bytesWritten) - Interlocked.Read(ref _bytesDiscarded);
                // Same basis as GetMediaTimeMs: subtract rendered underrun
                // silence so the pacer re-anchors to the true audible playhead.
                long audible = played - _silenceProvider.SilenceBytes;
                long realBytes = Math.Min(audible, content);
                if (realBytes < 0) realBytes = 0;
                Interlocked.Exchange(ref _segmentStartMasterMs, abps > 0 ? realBytes * 1000L / abps : -1L);
            }
        }

        /// <summary>Master-clock ms at which the segment written after the most
        /// recent <see cref="ClearBuffer"/> starts playing (see there). -1 until
        /// the first ClearBuffer.</summary>
        public long SegmentStartMasterMs => Interlocked.Read(ref _segmentStartMasterMs);

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
            bool releaseAfterUnlock = false;
            long fillBytes = 0;
            int pcmOffset = 0;
            lock (_gate) {
                if (!_baseSet) {
                    _basePtsMs = ptsMs;
                    _baseSet = true;
                    _log?.LogInfo($"[naudio-master] first sample written, basePts={ptsMs}ms");
                }
                // Content-gap detection (see the field block above). Compare
                // this chunk's pts against where the authored bytes say the
                // timeline should be; a forward jump is missing content and
                // gets filled with silence so the byte clock stays on the
                // content timeline.
                long abps = _format.AverageBytesPerSecond;
                if (_gapRefPtsMs == long.MinValue || abps <= 0) {
                    _gapRefPtsMs = ptsMs;
                    _gapRefAuthoredBytes = _authoredBytes;
                } else {
                    long expected = _gapRefPtsMs + (_authoredBytes - _gapRefAuthoredBytes) * 1000L / abps;
                    long gapMs = ptsMs - expected;
                    if (gapMs >= GapFillMinMs && gapMs <= GapDiscontinuityMs) {
                        long fb = gapMs * abps / 1000L;
                        fillBytes = fb - (fb % _format.BlockAlign);
                        Interlocked.Add(ref _gapFilledBytes, fillBytes);
                        _log?.LogInfo($"[naudio-master] audio content gap {gapMs}ms (pts={ptsMs} " +
                                      $"expected={expected}) — filling with silence to hold the content timeline");
                    } else if (gapMs <= -GapFillMinMs && gapMs >= -GapDiscontinuityMs) {
                        // Timeline fold-back: drop the overlapped prefix (see
                        // the field block above). Whole-chunk drops just return.
                        long ob = (-gapMs) * abps / 1000L;
                        long eat = Math.Min(len, ob - (ob % _format.BlockAlign));
                        if (eat > 0) {
                            pcmOffset = (int)eat;
                            Interlocked.Add(ref _overlapTrimmedBytes, eat);
                            int now = Environment.TickCount;
                            if (unchecked(now - _lastTrimLogTick) > 1000) {
                                _lastTrimLogTick = now;
                                _log?.LogInfo($"[naudio-master] audio timeline fold {gapMs}ms (pts={ptsMs} " +
                                              $"expected={expected}) — trimming overlapped audio " +
                                              $"(cumulative {Interlocked.Read(ref _overlapTrimmedBytes) * 1000L / abps}ms)");
                            }
                        }
                    } else if (Math.Abs(gapMs) > GapDiscontinuityMs) {
                        _log?.LogInfo($"[naudio-master] audio pts discontinuity {gapMs}ms (pts={ptsMs} " +
                                      $"expected={expected}) — re-seeding gap tracking, no fill/trim");
                        _gapRefPtsMs = ptsMs;
                        _gapRefAuthoredBytes = _authoredBytes;
                    }
                    // |gap| under GapFillMinMs (multi-frame packets sharing one
                    // pts, rtpTs quantisation) is expected — ignore.
                }
                len -= pcmOffset;
                if (len <= 0 && fillBytes == 0) return;   // whole chunk was overlap
                _authoredBytes += fillBytes + len;

                if (_holding) {
                    // Take private copies — the caller (decoder) reuses its
                    // scratch buffer between frames, so we can't hold a
                    // reference to the supplied array. Fill goes FIRST so the
                    // gap sits where the content is actually missing.
                    for (long rem = fillBytes; rem > 0; rem -= ZeroChunk.Length) {
                        _holdQueue.Enqueue(new byte[Math.Min(rem, ZeroChunk.Length)]);
                    }
                    byte[] copy = new byte[len];
                    Buffer.BlockCopy(pcm, pcmOffset, copy, 0, len);
                    _holdQueue.Enqueue(copy);
                    _heldBytes += fillBytes + len;
                    if (!_holdWall.IsRunning) _holdWall.Restart();
                    long heldMs = abps > 0 ? _heldBytes * 1000L / abps : 0;
                    // Release once we've buffered the pre-roll target — or the
                    // timeout fires (slow/short startup delivery), so the hold
                    // can't stall playback indefinitely. skip=0: KEEP the held
                    // audio as the startup cushion (don't discard it).
                    if (heldMs >= PrerollTargetMs || _holdWall.ElapsedMilliseconds >= PrerollTimeoutMs) {
                        _log?.LogInfo($"[naudio-master] pre-roll complete: heldMs={heldMs} " +
                                      $"(target={PrerollTargetMs}, waited={_holdWall.ElapsedMilliseconds}ms) — releasing");
                        releaseAfterUnlock = true;
                    } else {
                        return;  // keep holding
                    }
                }
            }
            if (releaseAfterUnlock) { ReleaseHold(0); return; }
            for (long rem = fillBytes; rem > 0; rem -= ZeroChunk.Length) {
                FeedPcmInternal(ZeroChunk, 0, (int)Math.Min(rem, ZeroChunk.Length));
            }
            if (len > 0) FeedPcmInternal(pcm, pcmOffset, len);
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
                long abps = _format.AverageBytesPerSecond;
                if (abps <= 0) return 0;
                // GetPosition: bytes the device has played (cumulative since
                // device.Play). With DiscardOnBufferOverflow=false + ReadFully
                // the provider pads SILENCE when the buffer underruns, so the
                // device position keeps advancing through that silence — but no
                // real audio content has been heard. Cap the clock at the real
                // audio actually written (_bytesWritten) so it only advances
                // with genuine content. Without this, when the server delivers
                // audio slowly (e.g. the start-up buffering ramp, ~20% of
                // real-time), the clock races through silence and the
                // video pacer slaved to it runs seconds ahead of the audio.
                long bytesPlayed = _device.GetPosition();
                if (bytesPlayed < 0) bytesPlayed = 0;
                // Playable content excludes bytes ClearBuffer discarded unplayed
                // — they were written but will never sound, so counting them
                // would let the clock run through that much silence first.
                long written = Interlocked.Read(ref _bytesWritten)
                             - Interlocked.Read(ref _bytesDiscarded);
                // Subtract underrun silence the device rendered (counted
                // exactly by SilenceCountingProvider): played − silence =
                // real audio content actually audible. Without this, every
                // delivery gap permanently advanced the clock by the gap
                // length and video paced ahead of audio from then on.
                long audible = bytesPlayed - _silenceProvider.SilenceBytes;
                long realBytes = Math.Min(audible, written);
                if (realBytes < 0) realBytes = 0;
                long ms = realBytes * 1000L / abps;
                // Monotonic floor (see _lastMediaTimeMs): consume-vs-render
                // timing can make the subtraction dip briefly; never go back.
                long last = Interlocked.Read(ref _lastMediaTimeMs);
                if (ms < last) return last;
                Interlocked.Exchange(ref _lastMediaTimeMs, ms);
                return ms;
            } catch {
                return 0;
            }
        }

        /// <summary>Wire-side PTS of the first sample written, in ms.
        /// Diagnostic only — not added to <see cref="GetMediaTimeMs"/>.
        /// Useful for log correlation with RTP-Info / RTCP SR
        /// timestamps from the server side.</summary>
        public long FirstSampleWirePtsMs => _baseSet ? _basePtsMs : 0;

        // ----- av-timing diagnostics (see ExternalSyncMediaController's
        // [av-timing] snapshot). All 0-relative, same basis as GetMediaTimeMs. -----

        /// <summary>Uncapped device playback position in ms: bytes the device
        /// reports it has PLAYED (GetPosition), including any silence padded on
        /// underrun. Compare against <see cref="AudioWrittenMs"/>: if this
        /// exceeds written, the device is playing silence past real content
        /// (delivery underrun); if written exceeds this, audio is buffered
        /// ahead of the playhead.</summary>
        public long AudioPlayedMs {
            get {
                if (_disposed || !_baseSet) return 0;
                try {
                    long abps = _format.AverageBytesPerSecond;
                    if (abps <= 0) return 0;
                    long p = _device.GetPosition();
                    if (p < 0) p = 0;
                    return p * 1000L / abps;
                } catch { return 0; }
            }
        }

        /// <summary>Cumulative real audio content written to the device, in ms,
        /// net of bytes ClearBuffer discarded unplayed (same basis as
        /// <see cref="GetMediaTimeMs"/>'s content cap, so the [av-timing]
        /// devSilence diagnostic stays meaningful across seeks).</summary>
        public long AudioWrittenMs {
            get {
                if (_disposed || !_baseSet) return 0;
                long abps = _format.AverageBytesPerSecond;
                return abps > 0
                    ? (Interlocked.Read(ref _bytesWritten) - Interlocked.Read(ref _bytesDiscarded)) * 1000L / abps
                    : 0;
            }
        }

        /// <summary>Wall-clock ms since the device first started playing
        /// (0 until the first <see cref="Play"/>). Real-time reference for the
        /// master clock.</summary>
        public long WallSincePlayMs => _playWall.ElapsedMilliseconds;

        /// <summary>Cumulative underrun silence the device has rendered, in ms
        /// (counted exactly at the consume point by SilenceCountingProvider).
        /// Diagnostic: this much of <see cref="AudioPlayedMs"/> is padding, not
        /// content — the master clock subtracts it. Grows only during delivery
        /// gaps; a steadily growing value mid-play means the wire is stalling.</summary>
        public long SilencePaddedMs {
            get {
                long abps = _format.AverageBytesPerSecond;
                return abps > 0 ? _silenceProvider.SilenceBytes * 1000L / abps : 0;
            }
        }

        /// <summary>Cumulative content-gap silence inserted by WritePcm's
        /// timestamp-gap fill, in ms. Diagnostic: missing CONTENT (recording
        /// defect / packet loss) the clock deliberately plays through as dead
        /// air to hold A/V alignment — unlike <see cref="SilencePaddedMs"/>,
        /// which is late-delivery padding the clock subtracts.</summary>
        public long GapFilledMs {
            get {
                long abps = _format.AverageBytesPerSecond;
                return abps > 0 ? Interlocked.Read(ref _gapFilledBytes) * 1000L / abps : 0;
            }
        }

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

        /// <summary>Real current audio buffer occupancy in milliseconds —
        /// the data queued in the BufferedWaveProvider that the device has
        /// not yet played. This is the honest telemetry the RTCP BFR W3 field
        /// should carry (the Xbox reports its true buffer level here, which
        /// dips when the server under-delivers and so closes the server's
        /// refill loop; a constant value pegged at TD does not).</summary>
        public int BufferedMs {
            get {
                if (_disposed) return 0;
                long abps = _format.AverageBytesPerSecond;
                if (abps <= 0) return 0;
                try { return (int)(_provider.BufferedBytes * 1000L / abps); }
                catch { return 0; }
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
