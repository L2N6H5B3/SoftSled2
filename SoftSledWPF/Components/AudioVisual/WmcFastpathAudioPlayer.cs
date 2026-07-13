using NAudio.Wave;
using SoftSled.Components.Communication;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Live playback of WMC's MCX-specific fast-path 0x0D audio stream.
    /// Subscribes to the patched libfreerdp's fast-path-unknown hook,
    /// classifies each message (28-byte handshake / 44-byte trailer or
    /// heartbeat / payload), strips
    /// the 44-byte format header, byte-swaps the BE 16-bit samples to LE,
    /// and pushes the resulting PCM into a NAudio buffered output device.
    ///
    /// Single-stream playback: WMC serialises sounds in practice (idle
    /// heartbeats fill the gaps), so a plain <see cref="BufferedWaveProvider"/>
    /// covers all observed cases. If overlapping sounds turn up later we'd
    /// swap to a <c>MixingSampleProvider</c>, but no need yet.
    /// </summary>
    internal sealed class WmcFastpathAudioPlayer : IDisposable {

        // Source format is 16-bit signed BE PCM, 44.1 kHz, stereo.
        // Confirmed by ear from the WAV-dumper round-trip.
        private const int    SampleRate       = 44100;
        private const int    Channels         = 2;
        private const int    BitsPerSample    = 16;
        private const int    PayloadHeaderBytes = 44;
        private const byte   McxAudioUpdateType = 0x0D;
        private const bool   SourceIsBigEndian  = true;

        private readonly Logger _log;
        private readonly WaveFormat _format;
        private readonly BufferedWaveProvider _buffer;
        private readonly IWavePlayer _output;
        // Set SOFTSLED_AUDIO_TRACE=1 to dump every 0x0D message classification.
        // Useful when a sound that "should" play seems to go silent — we want
        // to know if (a) bytes arrived but the player didn't play them, or
        // (b) bytes never arrived (server using a cache-replay command we
        // don't yet recognise).
        private readonly bool _trace;
        private long _payloadCount, _trailer00Count, _trailer01Count, _trailer02Count, _otherCount;

        // De-bounce window for same-slot replays. Some legitimate trailers
        // arrive in close succession (e.g. STORE+PLAY immediately followed
        // by a same-slot REPLAY 10–30 ms later), producing audible double-
        // hits. We suppress any same-slot play that arrives within this
        // window of the previous one. Tuneable via SOFTSLED_AUDIO_DEBOUNCE_MS;
        // 0 disables. Default of 50 ms is below human reaction time (~250 ms)
        // so it won't impede rapid keypresses, but covers protocol echoes.
        private static readonly int DebounceMs =
            int.TryParse(Environment.GetEnvironmentVariable("SOFTSLED_AUDIO_DEBOUNCE_MS"),
                         out int v) ? v : 50;

        private byte _lastPlayedSlot;
        private long _lastPlayedAtTicks; // 0 = never

        // Multi-slot cache of decoded PCM, keyed by the trailer's last byte.
        // WMC's MCX audio protocol uses slot-indexed caching:
        //   * opcode-06 (audio payload) carries the PCM but doesn't say
        //     where to store it — the next opcode-05 trailer's last byte
        //     supplies the slot ID.
        //   * opcode-05 trailers serve dual purpose: STORE (when there's
        //     pending audio) and REPLAY (otherwise). Same wire format
        //     either way — the difference is whether _pending is non-null.
        // All accesses are from a single FreeRDP worker thread so no lock
        // is needed; AddSamples is what marshals across to NAudio.
        private readonly Dictionary<byte, byte[]> _slots = new Dictionary<byte, byte[]>();
        private byte[] _pending;

        public WmcFastpathAudioPlayer(Logger log) {
            _log = log;
            _trace = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOFTSLED_AUDIO_TRACE"));
            _format = new WaveFormat(SampleRate, BitsPerSample, Channels);

            // Buffer sized for the longest expected payload (intro chime
            // is 6 s; allow some overhead). DiscardOnBufferOverflow keeps
            // the player alive if the server unexpectedly floods us.
            _buffer = new BufferedWaveProvider(_format) {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
            };

            // WaveOutEvent is the lowest-friction NAudio backend on .NET
            // Framework — works without WASAPI permissions, plays from any
            // thread, and matches WPF Dispatcher's threading model.
            // Latency at default settings is ~150 ms; tighten via
            // DesiredLatency if click feedback feels sluggish later.
            _output = new WaveOutEvent {
                DesiredLatency = 100, // ms
            };
            _output.Init(_buffer);
            _output.Play();

            _log?.LogInfo($"WMC fastpath audio player armed: {SampleRate} Hz, {BitsPerSample}-bit, {Channels}ch");
        }

        // Native callback. Fires on the FreeRDP worker thread. Keep this
        // exception-safe — anything that escapes back into native code is a
        // crash. NAudio's BufferedWaveProvider.AddSamples is thread-safe.
        public void OnFastpath(IntPtr user, byte updateCode, IntPtr data, UIntPtr length) {
            if (updateCode != McxAudioUpdateType) return;

            int len = checked((int)(uint)length);
            if (len <= 0 || data == IntPtr.Zero) return;

            try {
                // 28-byte stream-open handshake. Ignored.
                if (len == 28) {
                    Trace($"setup len=28");
                    return;
                }

                // 44-byte trailers (opcode 05) — slot-indexed cache control.
                //   - If a fresh audio payload is pending, this trailer's
                //     last byte tells us which slot to STORE it in.
                //   - If nothing is pending, this trailer means REPLAY the
                //     PCM cached at that slot.
                // Either way, after the slot is set/found, we feed it to
                // NAudio for playback. WMC's UI fires more sounds than
                // the user directly triggers (focus shifts, transitions,
                // animations) so it's normal to hear extras beyond the
                // ones tied to keypresses.
                if (len == PayloadHeaderBytes) {
                    byte slot = Marshal.ReadByte(data, PayloadHeaderBytes - 1);
                    switch (slot) {
                        case 0x00: Interlocked.Increment(ref _trailer00Count); break;
                        case 0x01: Interlocked.Increment(ref _trailer01Count); break;
                        case 0x02: Interlocked.Increment(ref _trailer02Count); break;
                        default:
                            Interlocked.Increment(ref _otherCount);
                            // Higher slot IDs mean WMC has more cached
                            // sounds than we've seen so far; that's fine,
                            // they go into the dictionary just like 0/1/2.
                            Trace($"new slot 0x{slot:X2} — adding to cache");
                            break;
                    }

                    byte[] toPlay;
                    string action;
                    if (_pending != null) {
                        // STORE: deposit the just-decoded payload into the
                        // slot the server identified, overwriting whatever
                        // was there. Then play it.
                        _slots[slot] = _pending;
                        toPlay = _pending;
                        _pending = null;
                        action = "store+play";
                    } else if (_slots.TryGetValue(slot, out toPlay)) {
                        // REPLAY: server is asking us to play back the
                        // sound previously stored in this slot.
                        action = "replay";
                    } else {
                        // Empty slot — server's first ever reference to
                        // this slot didn't carry payload? Possible during
                        // startup; just log and skip.
                        Trace($"trailer slot=0x{slot:X2} but no cached PCM — skipped");
                        return;
                    }

                    // Same-slot debounce — drop a play that lands on the
                    // same slot as the previous one within DebounceMs.
                    // Always allow the very first play (_lastPlayedAtTicks
                    // == 0) and any switch to a different slot.
                    if (DebounceMs > 0 && _lastPlayedAtTicks != 0 && slot == _lastPlayedSlot) {
                        long elapsedMs =
                            (DateTime.UtcNow.Ticks - _lastPlayedAtTicks) / TimeSpan.TicksPerMillisecond;
                        if (elapsedMs < DebounceMs) {
                            Trace($"trailer slot=0x{slot:X2} {action} — DEBOUNCED "
                                  + $"(last same-slot play {elapsedMs}ms ago, threshold {DebounceMs}ms)");
                            // We still consumed _pending into the slot above
                            // even though we're not playing — that's correct,
                            // a future REPLAY of this slot should hear the
                            // freshly-stored audio.
                            return;
                        }
                    }
                    _lastPlayedSlot = slot;
                    _lastPlayedAtTicks = DateTime.UtcNow.Ticks;

                    _buffer.AddSamples(toPlay, 0, toPlay.Length);
                    Trace($"trailer slot=0x{slot:X2} {action}: {toPlay.Length}B "
                          + $"(queued {_buffer.BufferedBytes}B)");
                    return;
                }

                if (len < PayloadHeaderBytes) {
                    // Short control message with no audio payload — happens
                    // constantly and carries nothing actionable. Count it for
                    // the periodic stats line; no per-message logging.
                    Interlocked.Increment(ref _otherCount);
                    return;
                }

                // Audio payload (opcode 06): decode PCM into _pending. The
                // server doesn't tell us the slot ID here — that's carried
                // by the next opcode-05 trailer. Don't play yet.
                int pcmLen = len - PayloadHeaderBytes;

                byte[] pcm = new byte[pcmLen];
                Marshal.Copy(data + PayloadHeaderBytes, pcm, 0, pcmLen);

                // BE → LE byte-swap of every 16-bit sample. Drop a trailing
                // odd byte if the payload doesn't end on a sample boundary.
                if (SourceIsBigEndian) {
                    int even = pcmLen & ~1;
                    for (int i = 0; i < even; i += 2) {
                        byte b = pcm[i];
                        pcm[i] = pcm[i + 1];
                        pcm[i + 1] = b;
                    }
                }

                // If a previous _pending never received a trailer, drop it.
                // Shouldn't happen in practice (audio + trailer come within
                // a few ms of each other) but defends against stalled state.
                if (_pending != null)
                    _log?.LogDebug($"[fp0d] previous pending payload abandoned ({_pending.Length}B)");
                _pending = pcm;

                long n = Interlocked.Increment(ref _payloadCount);
                double durationMs = (pcmLen * 1000.0) / (SampleRate * Channels * (BitsPerSample / 8));
                Trace($"decoded #{n}: {len}B ({durationMs:F0} ms) — awaiting slot trailer");
            } catch (Exception ex) {
                _log?.LogDebug($"[fp0d-player] {ex.Message}");
            }
        }

        private void Trace(string msg) {
            if (_trace) _log?.LogDebug($"[fp0d] {msg}");
        }

        public void Dispose() {
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
        }
    }
}
