using NAudio.Wave;
using SoftSled.Components.Diagnostics;
using System;
using System.Diagnostics;
using System.IO;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// NAudio playback sink for RFC 2250 MPA (MPEG Audio Layer I/II/III over
    /// RTP) packets. WMPNss negotiates plain MP3-over-RTP when the source
    /// media is an MP3 file — payload type 96 with <c>rtpmap MPA/90000/N</c>.
    ///
    /// Wire format (per RFC 2250 §3.5):
    ///   <code>
    ///   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///   |             MBZ               |          Frag_offset          |
    ///   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///   </code>
    /// followed by the raw MPEG audio frame bytes. We strip the 4-byte header
    /// and feed the remainder to a rolling buffer; complete MP3 frames are
    /// peeled off via <see cref="Mp3Frame.LoadFromStream"/>, decoded by
    /// <see cref="AcmMp3FrameDecompressor"/>, and pushed into a
    /// <see cref="BufferedWaveProvider"/> driving a <see cref="WaveOutEvent"/>.
    ///
    /// Format discovery is lazy: we don't know the sample rate/channels until
    /// the first parsed MP3 frame, so NAudio output is initialized on first
    /// decode. Subsequent frames are pushed into the same pipeline.
    ///
    /// Lifecycle: constructed once per session when SDP processing identifies
    /// an MPA audio PT; <see cref="SubmitRtpPayload"/> is called from the
    /// audio RTP dispatch path. Disposing tears down NAudio and is idempotent.
    /// </summary>
    internal sealed class Mp3OverRtpAudioSink : IDisposable {

        private readonly Logger _log;
        private readonly object _gate = new object();

        // Rolling buffer of post-MPA-header bytes. Mp3Frame.LoadFromStream
        // scans for the next sync word, so a small amount of leading garbage
        // (e.g. partial first packet) self-corrects after one frame's worth
        // of bytes. We compact this on each pass to avoid unbounded growth.
        private readonly MemoryStream _accum = new MemoryStream();

        private IMp3FrameDecompressor _decompressor;
        private BufferedWaveProvider _buffer;
        private IWavePlayer _output;

        private long _packetsSubmitted;
        private long _framesDecoded;
        private long _pcmBytesPushed;
        private bool _initialized;
        private bool _disposed;

        // Diagnostic dump for "did we recover sync?" inspection — first
        // ~64 KB of post-MPA-header bytes. Open as raw .mp3 to verify.
        private const long DiagDumpMaxBytes = 64 * 1024;
        private FileStream _diagDumpFile;
        private long _diagBytesWritten;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public Mp3OverRtpAudioSink(Logger log) {
            _log = log;
            try {
                string dumpPath = Path.Combine(Path.GetTempPath(),
                                               "softsled-first-audio-mpa.bin");
                _diagDumpFile = new FileStream(dumpPath, FileMode.Create,
                                               FileAccess.Write, FileShare.Read);
                _log?.LogInfo($"[mpa-rtp-naudio] sink armed; first {DiagDumpMaxBytes}B " +
                              $"of post-MPA-header bytes dumped to {dumpPath}");
            } catch (Exception ex) {
                _log?.LogError($"[mpa-rtp-naudio] diag dump open failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Submit one RTP payload (after the 12-byte RTP header has been
        /// stripped by the caller, but INCLUDING the 4-byte RFC 2250 MPA
        /// sub-header). Length is in bytes.
        /// </summary>
        public void SubmitRtpPayload(byte[] payload, int offset, int length) {
            if (payload == null || length <= 4 || _disposed) return;

            // RFC 2250 §3.5: 16 bits MBZ + 16 bits Frag_offset, big-endian.
            // We don't strictly need Frag_offset for layer III @ 192 kbps
            // (frame is ~626 B, fits in one packet — Frag_offset always 0
            // on our test stream) but we read it for diagnostics.
            // ushort fragOffset = (ushort)((payload[offset + 2] << 8) | payload[offset + 3]);

            int dataOffset = offset + 4;
            int dataLength = length - 4;

            SubmitFrameBytes(payload, dataOffset, dataLength);
        }

        /// <summary>
        /// Submit raw MPEG audio frames (Layer I / II / III) with no sub-header.
        /// Used by the WMRTP-wrapped <c>vnd.ms.wm-MPA</c> path, where the WMRTP
        /// depacketizer has already stripped its own wrapper and the resulting
        /// MAU is a clean run of MPEG audio frame bytes (no RFC 2250 MBZ/Frag
        /// prefix). The remainder of the decode pipeline is identical.
        /// </summary>
        public void SubmitRawMpegAudioFrames(byte[] data, int offset, int length) {
            if (data == null || length <= 0 || _disposed) return;
            SubmitFrameBytes(data, offset, length);
        }

        /// <summary>
        /// Common tail: append <paramref name="length"/> bytes from
        /// <paramref name="src"/> at <paramref name="srcOffset"/> into the
        /// rolling MPEG-frame buffer, update the diagnostic dump, and run the
        /// frame parser/decoder.
        /// </summary>
        private void SubmitFrameBytes(byte[] src, int srcOffset, int length) {
            lock (_gate) {
                if (_disposed) return;
                _accum.Position = _accum.Length;
                _accum.Write(src, srcOffset, length);

                // Diagnostic dump
                if (_diagDumpFile != null && _diagBytesWritten < DiagDumpMaxBytes) {
                    int writeBytes = (int)Math.Min(length,
                                                   DiagDumpMaxBytes - _diagBytesWritten);
                    try {
                        _diagDumpFile.Write(src, srcOffset, writeBytes);
                        _diagBytesWritten += writeBytes;
                        if (_diagBytesWritten >= DiagDumpMaxBytes) {
                            _diagDumpFile.Flush();
                            _diagDumpFile.Close();
                            _diagDumpFile = null;
                        }
                    } catch { /* ignore */ }
                }

                _packetsSubmitted++;
                ParseAndDecodeFrames();
            }
        }

        /// <summary>
        /// Peel complete MP3 frames out of <see cref="_accum"/> and feed each
        /// to the decompressor. Called under <see cref="_gate"/>.
        /// </summary>
        private void ParseAndDecodeFrames() {
            _accum.Position = 0;
            long lastConsumed = 0;

            while (_accum.Position < _accum.Length) {
                long frameStart = _accum.Position;
                Mp3Frame frame;
                try {
                    frame = Mp3Frame.LoadFromStream(_accum);
                } catch (EndOfStreamException) {
                    // Need more bytes — rewind to where this attempt started
                    // so they'll be re-read once the next packet arrives.
                    _accum.Position = frameStart;
                    break;
                } catch (Exception ex) {
                    _log?.LogDebug($"[mpa-rtp-naudio] Mp3Frame parse error: {ex.Message}");
                    // Skip one byte and retry; corrupt sync usually self-recovers.
                    _accum.Position = frameStart + 1;
                    continue;
                }

                if (frame == null) {
                    // No sync found at all — bail; the head of the buffer is
                    // unusable. Rewind so we keep the bytes for next attempt
                    // (in case a sync straddles packets).
                    _accum.Position = frameStart;
                    break;
                }

                // Frame parsed successfully. Initialise NAudio on first frame.
                if (!_initialized) {
                    try {
                        InitializeFromFirstFrame(frame);
                    } catch (Exception ex) {
                        _log?.LogError($"[mpa-rtp-naudio] NAudio init failed: " +
                                       $"{ex.Message}");
                        _disposed = true;
                        return;
                    }
                }

                // Decode → PCM → buffered wave provider.
                byte[] pcmScratch = new byte[16384];
                int decoded;
                try {
                    decoded = _decompressor.DecompressFrame(frame, pcmScratch, 0);
                } catch (Exception ex) {
                    _log?.LogDebug($"[mpa-rtp-naudio] decode error: {ex.Message}");
                    decoded = 0;
                }

                if (decoded > 0) {
                    try { _buffer.AddSamples(pcmScratch, 0, decoded); }
                    catch (Exception ex) {
                        _log?.LogDebug($"[mpa-rtp-naudio] buffer add error: {ex.Message}");
                    }
                    _pcmBytesPushed += decoded;
                }
                _framesDecoded++;
                lastConsumed = _accum.Position;
            }

            // Compact: drop everything before lastConsumed; keep tail
            // (potentially a partial frame waiting for more bytes).
            CompactAccumulator(lastConsumed);
        }

        private void CompactAccumulator(long consumed) {
            if (consumed <= 0) return;
            long remaining = _accum.Length - consumed;
            if (remaining < 0) remaining = 0;

            if (remaining == 0) {
                _accum.SetLength(0);
                _accum.Position = 0;
                return;
            }

            // Copy tail to front
            byte[] tail = new byte[remaining];
            _accum.Position = consumed;
            _accum.Read(tail, 0, (int)remaining);
            _accum.SetLength(0);
            _accum.Write(tail, 0, tail.Length);
            // Position now at end (ready for append)
        }

        private void InitializeFromFirstFrame(Mp3Frame frame) {
            // Mp3WaveFormat captures Layer/SampleRate/Channels/BlockAlign etc.
            // FrameLength on the first frame is the canonical size for the
            // ACM decoder's block alignment expectation.
            var fmt = new Mp3WaveFormat(
                frame.SampleRate,
                frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                frame.FrameLength,
                frame.BitRate);

            _decompressor = new AcmMp3FrameDecompressor(fmt);

            _buffer = new BufferedWaveProvider(_decompressor.OutputFormat) {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true,
            };

            _output = new WaveOutEvent {
                DesiredLatency = 200,  // ms; RTP jitter forgiveness
            };
            _output.Init(_buffer);
            _output.Play();

            _initialized = true;
            _log?.LogInfo($"[mpa-rtp-naudio] initialised: " +
                          $"{frame.SampleRate} Hz, " +
                          $"{(frame.ChannelMode == ChannelMode.Mono ? 1 : 2)}ch, " +
                          $"{frame.BitRate / 1000} kbps, " +
                          $"frame={frame.FrameLength}B " +
                          $"→ PCM {_decompressor.OutputFormat.SampleRate}/" +
                          $"{_decompressor.OutputFormat.BitsPerSample}/" +
                          $"{_decompressor.OutputFormat.Channels}");
        }

        public void Dispose() {
            lock (_gate) {
                if (_disposed) return;
                _disposed = true;
            }

            _log?.LogInfo($"[mpa-rtp-naudio] sink disposing: " +
                          $"packets={_packetsSubmitted}, frames={_framesDecoded}, " +
                          $"pcmBytes={_pcmBytesPushed}, wall={_sw.Elapsed.TotalSeconds:F2}s");

            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _decompressor?.Dispose(); } catch { }
            try { _diagDumpFile?.Dispose(); } catch { }
            _output = null;
            _decompressor = null;
            _buffer = null;
            _diagDumpFile = null;
        }
    }
}
