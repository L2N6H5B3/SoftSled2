using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using SoftSled.Components.Diagnostics;

namespace SoftSled.Components.Splash {

    /// <summary>
    /// One-shot playback of splash-channel UI sound effects (MS-RRSP2
    /// §2.2.4.19 SoundBuffer + §2.2.4.20 Sound). Called by
    /// <see cref="SplashController"/> when a <c>Sound_Play</c> message
    /// arrives. Each call creates a fresh <see cref="WaveOutEvent"/>
    /// + reader pair, plays the buffer, and disposes on completion —
    /// concurrent plays are supported by the OS audio mixer rather than
    /// by us.
    ///
    /// <para>Sound format: WMC ships these payloads in the same format as
    /// the RDP fast-path 0x0D audio stream — **16-bit signed BIG-ENDIAN
    /// PCM @ 44.1 kHz stereo**, NO container. We byte-swap to little-
    /// endian (NAudio's native order) and feed via
    /// <see cref="RawSourceWaveStream"/>. A RIFF/WAVE header sniff is
    /// kept as a defensive fast-path for any future asset that does ship
    /// with WAV wrapping — that's handled by <see cref="WaveFileReader"/>
    /// without byte-swap.</para>
    ///
    /// <para>Overlap relationship with <c>WmcFastpathAudioPlayer</c>:
    /// WMC sends some UI sounds through both channels. Enable splash
    /// audio (config flag) ON to add this channel; if you hear every
    /// click twice, flip it OFF — the fastpath player will keep covering
    /// the legacy path.</para>
    /// </summary>
    internal sealed class SplashSoundPlayer : IDisposable {

        private readonly Logger _log;
        private readonly object _lock = new object();
        private readonly List<ActivePlay> _active = new List<ActivePlay>();
        private bool _disposed;

        // Default fallback format — matches WmcFastpathAudioPlayer so any
        // raw-PCM payload that the WAV parser doesn't accept still has a
        // reasonable chance of sounding right.
        private static readonly WaveFormat FallbackPcmFormat = new WaveFormat(44100, 16, 2);

        public SplashSoundPlayer(Logger log) {
            _log = log;
        }

        /// <summary>
        /// Play the given sound bytes once. Returns immediately; the
        /// audio plays asynchronously. Concurrent calls are supported —
        /// each gets its own NAudio output device. Errors are logged
        /// and swallowed (never thrown back into the dispatcher).
        ///
        /// <para>The optional format triple comes from the wire
        /// SoundHeader (spec §2.2.6.11) via XAudSoundDevice_-
        /// CreateSoundBuffer. Plausible values override the historic
        /// 44.1 kHz/16-bit/stereo assumption; zeros / implausible values
        /// fall back to it. Raw PCM is still byte-swapped BE→LE for
        /// 16-bit formats (the WMC wire ships BE samples); 8-bit data
        /// has no byte order and is passed through.</para>
        /// </summary>
        public void Play(uint soundHandle, byte[] bytes,
                         int sampleRate = 0, int bitsPerSample = 0, int channels = 0) {
            if (_disposed || bytes == null || bytes.Length == 0) return;

            bool plausible = sampleRate >= 8000 && sampleRate <= 192000
                             && (bitsPerSample == 8 || bitsPerSample == 16)
                             && channels >= 1 && channels <= 8;
            WaveFormat rawFormat = plausible
                ? new WaveFormat(sampleRate, bitsPerSample, channels)
                : FallbackPcmFormat;

            IWavePlayer output = null;
            IWaveProvider provider = null;
            IDisposable readerDisposable = null;

            try {
                if (LooksLikeWav(bytes)) {
                    // WAV container: PCM payload inside is already LE.
                    // Use the bytes directly via WaveFileReader.
                    var wavStream = new MemoryStream(bytes, writable: false);
                    var wav = new WaveFileReader(wavStream);
                    provider = wav;
                    readerDisposable = wav;
                } else if (rawFormat.BitsPerSample == 16) {
                    // Raw BE PCM (fastpath format). Byte-swap each 16-bit
                    // sample so NAudio (LE-native) plays it correctly.
                    // Copy first — bytes might be a shared buffer owned by
                    // the SoundBuffer, and an in-place swap would corrupt
                    // it for the next play.
                    int evenLen = bytes.Length & ~1;
                    byte[] le = new byte[evenLen];
                    for (int i = 0; i < evenLen; i += 2) {
                        le[i]     = bytes[i + 1];
                        le[i + 1] = bytes[i];
                    }
                    var ms = new MemoryStream(le, writable: false);
                    provider = new RawSourceWaveStream(ms, rawFormat);
                    readerDisposable = ms;
                } else {
                    // 8-bit PCM — no byte order to swap.
                    var ms = new MemoryStream(bytes, writable: false);
                    provider = new RawSourceWaveStream(ms, rawFormat);
                    readerDisposable = ms;
                }

                output = new WaveOutEvent { DesiredLatency = 50 };
                output.Init(provider);

                var entry = new ActivePlay(soundHandle, output, readerDisposable);
                output.PlaybackStopped += (s, e) => OnPlaybackStopped(entry, e);

                lock (_lock) {
                    if (_disposed) {
                        // Race: Dispose() fired between the disposed
                        // check and now. Tear down immediately.
                        SafeDispose(output);
                        SafeDispose(readerDisposable);
                        return;
                    }
                    _active.Add(entry);
                }
                output.Play();
            } catch (Exception ex) {
                _log?.LogError($"[splash-audio] Play sound=0x{soundHandle:X8} failed: {ex.Message}");
                SafeDispose(output);
                SafeDispose(readerDisposable);
            }
        }

        /// <summary>
        /// Stop any currently-playing instances of the given sound handle.
        /// Each <see cref="Play"/> registers an independent active entry;
        /// Stop tears down every matching one.
        /// </summary>
        public void Stop(uint soundHandle) {
            if (_disposed) return;
            List<ActivePlay> toStop = null;
            lock (_lock) {
                for (int i = _active.Count - 1; i >= 0; i--) {
                    if (_active[i].SoundHandle == soundHandle) {
                        if (toStop == null) toStop = new List<ActivePlay>();
                        toStop.Add(_active[i]);
                        _active.RemoveAt(i);
                    }
                }
            }
            if (toStop == null) return;
            foreach (var p in toStop) {
                try { p.Output.Stop(); } catch { }
                SafeDispose(p.Output);
                SafeDispose(p.Reader);
            }
        }

        private void OnPlaybackStopped(ActivePlay entry, StoppedEventArgs e) {
            lock (_lock) { _active.Remove(entry); }
            if (e.Exception != null) {
                _log?.LogError($"[splash-audio] playback stopped with error: {e.Exception.Message}");
            }
            SafeDispose(entry.Output);
            SafeDispose(entry.Reader);
        }

        public void Dispose() {
            ActivePlay[] snapshot;
            lock (_lock) {
                if (_disposed) return;
                _disposed = true;
                snapshot = _active.ToArray();
                _active.Clear();
            }
            foreach (var p in snapshot) {
                try { p.Output.Stop(); } catch { }
                SafeDispose(p.Output);
                SafeDispose(p.Reader);
            }
        }

        private static bool LooksLikeWav(byte[] data) {
            // RIFF/WAVE: "RIFF" at byte 0, "WAVE" at byte 8.
            return data.Length >= 12
                && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45;
        }

        private static void SafeDispose(IDisposable d) {
            if (d == null) return;
            try { d.Dispose(); } catch { }
        }

        private sealed class ActivePlay {
            public uint SoundHandle { get; }
            public IWavePlayer Output { get; }
            public IDisposable Reader { get; }
            public ActivePlay(uint sndH, IWavePlayer output, IDisposable reader) {
                SoundHandle = sndH;
                Output = output;
                Reader = reader;
            }
        }
    }
}
