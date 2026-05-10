using SoftSled.Components.Communication;
using SoftSled.Components.Diagnostics;
using System;
using System.IO;
using System.Threading;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Phase-B reverse-engineering aid for WMC's MCX-specific fast-path
    /// update type 0x0D, which carries 44.1 kHz / 16-bit / stereo PCM
    /// (intro chime + UI sounds). Not in MS-RDPBCGR.
    ///
    /// Subscribes to the patched libfreerdp's fast-path-unknown hook and
    /// classifies each incoming 0x0D message:
    /// <list type="bullet">
    ///   <item><description>28-byte one-time stream-open handshake → ignored</description></item>
    ///   <item><description>44-byte trailer (last byte 0x01) → marks end of preceding payload</description></item>
    ///   <item><description>44-byte heartbeat (last byte 0x00) → ignored, server sends every ~1.5 s during silence</description></item>
    ///   <item><description>Larger message → audio payload: 8-byte preamble + ~40-byte format header at offset 0–47, then raw 16-bit LE stereo PCM</description></item>
    /// </list>
    /// Every audio payload is serialised to disk as a standard PCM WAV file
    /// (44-byte RIFF/fmt/data header + raw PCM tail), one file per sound,
    /// so the format can be confirmed by ear in any media player before we
    /// wire up live playback in a later iteration.
    /// </summary>
    internal sealed class WmcFastpathAudioDumper {

        // Format constants — spec'd by WMC (verified via capture & math:
        // payload_bytes_minus_48 / (44100 * 2ch * 2byte) ≈ duration).
        private const int SampleRate    = 44100;
        private const int Channels      = 2;
        private const int BitsPerSample = 16;

        // The first 44 bytes of every payload are an 8-byte preamble plus a
        // 36-byte format header (mirrors the size of the standalone trailer).
        // PCM begins at offset 44 — verified by hex inspection of captured
        // payloads (offsets 0x2C+ start the silent zero-frame leadin).
        private const int PayloadHeaderBytes = 44;

        // Source samples are 16-bit signed BIG-endian, but WAV requires
        // little-endian. We byte-swap each 16-bit sample before writing.
        // Confirmed empirically: writing samples verbatim produced pure
        // static (any non-zero high byte gets interpreted as a huge value
        // with often-flipped polarity), while byte-swapping produces a
        // smooth waveform consistent with the captured fade-ins. Set to
        // false here if a future sound proves otherwise.
        private const bool SourceIsBigEndian = true;

        // Type-0x0D updates we should consume. Filtering by updateCode means
        // any *other* future MCX fast-path extension type wouldn't get
        // misinterpreted as audio.
        private const byte McxAudioUpdateType = 0x0D;

        private readonly Logger _log;
        private readonly string _dumpDir;
        private int _seq;          // monotonic sequence per session
        private int _audioSeq;     // counts only audio payloads written

        public WmcFastpathAudioDumper(Logger log, string dumpDir) {
            _log = log;
            _dumpDir = dumpDir;
            Directory.CreateDirectory(_dumpDir);
            _log?.LogInfo($"WMC fastpath audio dumper armed → {_dumpDir}");
        }

        // Native callback — fires on the FreeRDP worker thread. Keep it
        // short and exception-free; copy bytes locally before doing IO so
        // we don't hold up the network read longer than necessary.
        public void OnFastpath(IntPtr user, byte updateCode, IntPtr data, UIntPtr length) {
            if (updateCode != McxAudioUpdateType) return;

            int len = checked((int)(uint)length);
            if (len <= 0 || data == IntPtr.Zero) return;

            int seq = Interlocked.Increment(ref _seq);

            try {
                // 28-byte one-time setup at session start. Ignore.
                if (len == 28) {
                    _log?.LogDebug($"[fp0d] #{seq}: 28-byte stream-open handshake (ignored)");
                    return;
                }

                // 44-byte trailer/heartbeat. Last byte distinguishes:
                // 0x01 = end-of-payload, 0x00 = idle heartbeat. Both are
                // metadata only; nothing to dump.
                if (len == 44) {
                    byte lastByte = System.Runtime.InteropServices.Marshal.ReadByte(data, 43);
                    string kind = lastByte == 0x01 ? "end-of-payload" :
                                  lastByte == 0x00 ? "heartbeat" :
                                  $"unknown(0x{lastByte:X2})";
                    _log?.LogDebug($"[fp0d] #{seq}: 44-byte {kind}");
                    return;
                }

                // Anything bigger than the header itself counts as audio.
                if (len <= PayloadHeaderBytes) {
                    _log?.LogDebug($"[fp0d] #{seq}: {len}-byte short message (ignored)");
                    return;
                }

                // Copy the PCM tail out of native memory.
                int pcmLen = len - PayloadHeaderBytes;
                byte[] pcm = new byte[pcmLen];
                System.Runtime.InteropServices.Marshal.Copy(
                    data + PayloadHeaderBytes, pcm, 0, pcmLen);

                // Convert BE → LE 16-bit samples in place. Drop the trailing
                // odd byte if the payload doesn't end on a sample boundary
                // (defensive — should never happen with valid 16-bit data).
                if (SourceIsBigEndian) {
                    int even = pcmLen & ~1;
                    for (int i = 0; i < even; i += 2) {
                        byte b = pcm[i];
                        pcm[i] = pcm[i + 1];
                        pcm[i + 1] = b;
                    }
                }

                int audioSeq = Interlocked.Increment(ref _audioSeq);
                double durationMs = (pcmLen * 1000.0) / (SampleRate * Channels * (BitsPerSample / 8));
                string path = Path.Combine(_dumpDir,
                    $"wmc-audio-{audioSeq:D3}-{len}bytes-{durationMs:F0}ms.wav");

                WriteWav(path, pcm);
                _log?.LogInfo($"[fp0d] #{seq}: audio payload {len}B ({durationMs:F0} ms) → {Path.GetFileName(path)}");
            } catch (Exception ex) {
                _log?.LogDebug($"[fp0d] #{seq}: dumper exception: {ex.Message}");
            }
        }

        // Standard 44-byte canonical PCM WAV header + data.
        // Endianness assumption: source PCM is little-endian 16-bit signed.
        // WAV is also LE so we copy bytes verbatim. If the dumped files
        // sound like static or are reversed, flip endianness here (swap
        // every pair of bytes) and confirm.
        private static void WriteWav(string path, byte[] pcm) {
            const int byteRate    = SampleRate * Channels * (BitsPerSample / 8);
            const int blockAlign  = Channels * (BitsPerSample / 8);
            const int fmtChunkSz  = 16;
            int dataChunkSz = pcm.Length;
            int riffChunkSz = 4 + (8 + fmtChunkSz) + (8 + dataChunkSz);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var w  = new BinaryWriter(fs)) {
                // RIFF header
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(riffChunkSz);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                // fmt chunk
                w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                w.Write(fmtChunkSz);
                w.Write((short)1);                  // PCM
                w.Write((short)Channels);
                w.Write(SampleRate);
                w.Write(byteRate);
                w.Write((short)blockAlign);
                w.Write((short)BitsPerSample);
                // data chunk
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(dataChunkSz);
                w.Write(pcm);
            }
        }
    }
}
