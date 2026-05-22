using NAudio.Wave;
using SoftSled.Components.Diagnostics;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// NAudio playback sink for raw PCM MAUs emitted by
    /// <see cref="WmrptAudioDepacketizer"/> when the negotiated payload type
    /// is <c>x-wmf-pf</c> with an <c>audio/vnd.wave</c> fmtp config (i.e. the
    /// WMRTP wrapper carries little-endian PCM samples).
    ///
    /// The depacketizer has already stripped the WMRTP bit-fields and
    /// reassembled multi-fragment MAUs by the time we see the bytes here,
    /// so each MAU is a contiguous run of PCM samples in the format
    /// described by the WAVEFORMATEX in the SDP fmtp config (parsed via
    /// <see cref="WaveFormatExFromConfig"/>).
    ///
    /// Lifecycle: constructed once per session when SDP processing identifies
    /// a usable PT; <see cref="Submit"/> is called from the depacketizer's
    /// AudioDataReady event handler. Disposing tears down NAudio and is
    /// idempotent.
    /// </summary>
    internal sealed class WmrptPcmNAudioSink : IDisposable {

        private readonly Logger _log;
        private readonly WaveFormat _format;
        private readonly BufferedWaveProvider _buffer;
        private readonly IWavePlayer _output;

        private long _bytesSubmitted;
        private long _maus;
        private bool _disposed;
        // Wall-clock timer so we can compute actual data rate vs the
        // expected AverageBytesPerSecond from WaveFormat. If actual rate
        // is significantly below expected, NAudio underruns and we hear
        // stutter — the diagnostic log records this so we can verify
        // RTCP/Buffer-Info experiments.
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        // Diagnostic capture (Phase-0c follow-up): dump the first ~64 KB
        // of submitted MAU bytes verbatim to a file so we can listen to
        // them in Audacity as raw PCM and tell whether the depacketizer
        // is producing clean samples or has a hidden per-MAU header
        // leaking through. Always-on; cheap (one-shot, capped). The text
        // companion log records per-MAU lengths + first 32 bytes of each
        // of the first 10 MAUs so we can eyeball the byte pattern.
        // Cap raised after Phase-0c follow-up: with RTCP RR feedback
        // working, audio should arrive at full ~192 KB/s, so 1 MB ≈ 5 s of
        // audio — long enough to verify steady-state pacing without
        // unbounded disk usage. Stats log still caps at first 10 MAUs.
        private const long DiagDumpMaxBytes = 1024 * 1024;  // ~5.4 s at 48k/16/2
        private const int  DiagLogMauCount  = 10;
        private FileStream _diagDumpFile;
        private StreamWriter _diagLogFile;
        private long _diagBytesWritten;

        public WmrptPcmNAudioSink(WaveFormat format, Logger log) {
            _format = format ?? throw new ArgumentNullException(nameof(format));
            _log = log;

            // Buffer sized for ~2 s of audio at the negotiated rate. The
            // server-side dejitter buffer (per Buffer-Info.dlna.org in our
            // SETUP request: 6624000 µs ≈ 6.6 s) is what really controls
            // smoothness; this is just a local overflow margin.
            _buffer = new BufferedWaveProvider(_format) {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
            };

            // WaveOutEvent: same backend WmcFastpathAudioPlayer uses — works
            // on any thread, no WASAPI permissions required, ~100 ms latency
            // is fine for AV-sync purposes since video isn't synced yet.
            _output = new WaveOutEvent {
                DesiredLatency = 200,  // ms; bigger than fastpath player
                                       // because RTP jitter is less forgiving
            };
            _output.Init(_buffer);
            _output.Play();

            // Diagnostic capture files. Open both; failures are logged but
            // don't bring down the sink.
            try {
                string dumpPath = Path.Combine(Path.GetTempPath(), "softsled-first-audio-mau.bin");
                string logPath  = Path.Combine(Path.GetTempPath(), "softsled-audio-mau-stats.log");
                _diagDumpFile = new FileStream(dumpPath, FileMode.Create,
                                               FileAccess.Write, FileShare.Read);
                _diagLogFile = new StreamWriter(
                    new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                    Encoding.ASCII);
                _diagLogFile.AutoFlush = true;
                _diagLogFile.WriteLine($"# WMRPT PCM NAudio sink diagnostic log");
                _diagLogFile.WriteLine($"# Sink format: {_format.SampleRate} Hz, " +
                                       $"{_format.BitsPerSample}-bit, {_format.Channels}ch, " +
                                       $"blockAlign={_format.BlockAlign}, " +
                                       $"avgBytesPerSec={_format.AverageBytesPerSecond}");
                _diagLogFile.WriteLine($"# Raw MAU dump → {dumpPath} (first {DiagDumpMaxBytes}B)");
                _diagLogFile.WriteLine($"# Raw is interpretable by Audacity as: signed 16-bit PCM, " +
                                       $"little-endian, {_format.SampleRate} Hz, {_format.Channels} ch");
                _diagLogFile.WriteLine();
            } catch (Exception ex) {
                _log?.LogError($"[wmrpt-pcm-naudio] diagnostic file open failed: {ex.Message}");
            }

            _log?.LogInfo($"[wmrpt-pcm-naudio] sink armed: " +
                          $"{_format.SampleRate} Hz, {_format.BitsPerSample}-bit, " +
                          $"{_format.Channels}ch, encoding={_format.Encoding}");
        }

        /// <summary>
        /// Submit one reassembled MAU (raw PCM samples). Safe to call from
        /// the WMRTP depacketizer's event-handler thread.
        /// </summary>
        public void Submit(byte[] mau) {
            if (mau == null || mau.Length == 0 || _disposed) return;
            try {
                long n = Interlocked.Increment(ref _maus);
                long total = Interlocked.Add(ref _bytesSubmitted, mau.Length);

                // Diagnostic per-MAU log entry (first N MAUs only) — captures
                // length and first 32 bytes hex so we can spot a per-MAU
                // header pattern by eye. If MAUs all start with the same
                // bytes for ~12 bytes then diverge, that's a header.
                if (n <= DiagLogMauCount && _diagLogFile != null) {
                    int hexLen = mau.Length < 32 ? mau.Length : 32;
                    var sb = new StringBuilder();
                    for (int i = 0; i < hexLen; i++) {
                        if (i > 0) sb.Append(' ');
                        sb.Append(mau[i].ToString("X2"));
                    }
                    bool blockAligned = (mau.Length % _format.BlockAlign) == 0;
                    try {
                        _diagLogFile.WriteLine($"MAU#{n}: len={mau.Length}B " +
                                               $"alignedBy{_format.BlockAlign}={blockAligned} " +
                                               $"first{hexLen}B={sb}");
                    } catch { }
                }

                // Diagnostic raw dump (first ~64 KB only). Listening to this
                // file as raw PCM in Audacity tells us whether the
                // depacketizer is producing clean samples (sounds like
                // music) or garbage (sounds like static). If clean here,
                // sink is fine and a different layer is broken; if static,
                // depacketizer or per-MAU header is the issue.
                if (_diagDumpFile != null) {
                    long alreadyWritten = Interlocked.Read(ref _diagBytesWritten);
                    if (alreadyWritten < DiagDumpMaxBytes) {
                        long remaining = DiagDumpMaxBytes - alreadyWritten;
                        int writeLen = mau.Length < remaining ? mau.Length : (int)remaining;
                        try {
                            _diagDumpFile.Write(mau, 0, writeLen);
                            _diagDumpFile.Flush();
                            Interlocked.Add(ref _diagBytesWritten, writeLen);
                            if (alreadyWritten + writeLen >= DiagDumpMaxBytes) {
                                _diagLogFile?.WriteLine($"# Diag dump complete at MAU#{n} " +
                                                        $"({alreadyWritten + writeLen}B captured)");
                            }
                        } catch { }
                    }
                }

                _buffer.AddSamples(mau, 0, mau.Length);

                // First MAU is interesting (confirms format / speed); after
                // that, log every 100 to keep the trace readable.
                if (n == 1 || (n % 100) == 0) {
                    double seconds = total / (double)_format.AverageBytesPerSecond;
                    double wall = _sw.Elapsed.TotalSeconds;
                    double pct = wall > 0 ? seconds / wall * 100.0 : 0;
                    _log?.LogInfo($"[wmrpt-pcm-naudio] MAU #{n}: +{mau.Length}B " +
                                  $"(total {total}B / {seconds:F2}s of audio in {wall:F2}s wall = {pct:F0}% rt) " +
                                  $"buffer={_buffer.BufferedDuration.TotalMilliseconds:F0}ms");
                }
                // Periodic stats line to the diag log so user can confirm
                // actual data-rate vs the WAVEFORMATEX-claimed rate.
                if (_diagLogFile != null && (n % 50 == 0)) {
                    double wall = _sw.Elapsed.TotalSeconds;
                    double seconds = total / (double)_format.AverageBytesPerSecond;
                    double pct = wall > 0 ? seconds / wall * 100.0 : 0;
                    try {
                        _diagLogFile.WriteLine($"# rate@MAU#{n}: {total}B / {wall:F2}s wall = " +
                                               $"{seconds:F2}s of audio = {pct:F1}% of real-time");
                    } catch { }
                }
            } catch (Exception ex) {
                _log?.LogError($"[wmrpt-pcm-naudio] Submit failed: {ex.Message}");
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            try {
                double wall = _sw.Elapsed.TotalSeconds;
                double seconds = _bytesSubmitted / (double)_format.AverageBytesPerSecond;
                double pct = wall > 0 ? seconds / wall * 100.0 : 0;
                _diagLogFile?.WriteLine($"# Sink disposed: {_maus} MAUs, {_bytesSubmitted}B " +
                                        $"({seconds:F2}s of audio in {wall:F2}s wall = {pct:F1}% real-time)");
                _diagLogFile?.Dispose();
            } catch { }
            try { _diagDumpFile?.Dispose(); } catch { }
            _log?.LogInfo($"[wmrpt-pcm-naudio] disposed; lifetime: {_maus} MAUs, " +
                          $"{_bytesSubmitted}B");
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Decode a WAVEFORMATEX struct from the SDP fmtp <c>config=</c> value
        /// of an x-wmf-pf audio payload type. Format of the config string:
        /// <code>
        /// {GUID}/N/N/N/{GUID}/{HEX_WAVEFORMATEX}
        /// </code>
        /// The first GUID is the major type (<c>WMMEDIATYPE_Audio</c>), the
        /// trailing GUID is the format type GUID (<c>WMFORMAT_WaveFormatEx</c>
        /// = 05589F81-C356-11CE-BF01-00AA0055595A), and the final segment
        /// is the WAVEFORMATEX struct serialised as little-endian hex.
        ///
        /// Returns null if the input doesn't conform.
        /// </summary>
        public static WaveFormat WaveFormatExFromConfig(string fmtpConfigValue) {
            if (string.IsNullOrEmpty(fmtpConfigValue)) return null;

            string[] parts = fmtpConfigValue.Split('/');
            if (parts.Length < 6) return null;
            string hex = parts[parts.Length - 1];
            byte[] bytes = HexStringToBytes(hex);
            if (bytes == null || bytes.Length < 18) return null;

            ushort wFormatTag      = ReadU16Le(bytes, 0);
            ushort nChannels       = ReadU16Le(bytes, 2);
            uint   nSamplesPerSec  = ReadU32Le(bytes, 4);
            uint   nAvgBytesPerSec = ReadU32Le(bytes, 8);
            ushort nBlockAlign     = ReadU16Le(bytes, 12);
            ushort wBitsPerSample  = ReadU16Le(bytes, 14);
            // ushort cbSize       = ReadU16Le(bytes, 16);  // ignored for PCM

            // Sanity guards — server should never send these but a corrupt
            // SDP would make us hand garbage to NAudio.
            if (nChannels == 0 || nChannels > 8) return null;
            if (nSamplesPerSec < 8000 || nSamplesPerSec > 192000) return null;
            if (wBitsPerSample != 8 && wBitsPerSample != 16 &&
                wBitsPerSample != 24 && wBitsPerSample != 32) return null;

            // wFormatTag 0x0001 = WAVE_FORMAT_PCM (linear). 0x0003 = IEEE
            // float; we don't expect float here but handle defensively.
            // Other tags (e.g. 0x0055 = MPEG layer 3) would mean the MAU
            // isn't raw PCM and this sink is the wrong target — caller
            // should branch on wFormatTag before constructing the sink.
            if (wFormatTag == 0x0001) {
                return new WaveFormat((int)nSamplesPerSec, wBitsPerSample, nChannels);
            }
            if (wFormatTag == 0x0003) {
                return WaveFormat.CreateIeeeFloatWaveFormat((int)nSamplesPerSec, nChannels);
            }
            return null;  // unsupported — caller picks a different sink
        }

        private static byte[] HexStringToBytes(string hex) {
            if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return null;
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++) {
                if (!byte.TryParse(hex.Substring(i * 2, 2),
                                   System.Globalization.NumberStyles.HexNumber,
                                   System.Globalization.CultureInfo.InvariantCulture,
                                   out result[i])) return null;
            }
            return result;
        }

        private static ushort ReadU16Le(byte[] b, int off)
            => (ushort)(b[off] | (b[off + 1] << 8));

        private static uint ReadU32Le(byte[] b, int off)
            => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | ((uint)b[off + 3] << 24));
    }
}
