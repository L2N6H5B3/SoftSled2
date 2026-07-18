using Rtsp;
using SoftSled.Components.Diagnostics;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SoftSled.Components.Diagnostics {

    /// <summary>
    /// Phase-0 reverse-engineering aid for the WMC MS-WMSP RTSP exchange.
    /// Tees every byte read from / written to the RTSP TCP socket into a
    /// timestamped <c>softsled-rtsp-wire.log</c> (and RTP packets into
    /// <c>softsled-rtp-wire.log</c>).
    ///
    /// Toggle on with <c>SOFTSLED_RTSP_WIRE_DUMP</c>. The session sets this to
    /// the configured dumps directory (<c>&lt;DiagnosticsDirectory&gt;\Dumps\rtsp</c>) so
    /// the dump lands alongside the other diagnostic dumps in the folder the
    /// Debugging page points at. A bare <c>SOFTSLED_RTSP_WIRE_DUMP=1</c> (e.g.
    /// set manually in a shell) still works and falls back to <c>%TEMP%</c>.
    /// When the env var is unset (the default)
    /// <see cref="MaybeWrap(IRtspTransport, Logger)"/> returns the original
    /// transport unchanged so the dumper has zero runtime cost in normal
    /// operation.
    ///
    /// Format: a small ASCII delimiter line precedes each chunk:
    /// <code>
    /// --- RECV @+12.345s size=4096 ---
    /// &lt;raw bytes&gt;
    /// --- SEND @+12.347s size=128 ---
    /// &lt;raw bytes&gt;
    /// </code>
    /// RTSP messages (request/response/SDP body) are pure ASCII so the file
    /// is readable in any text editor; if we later flip to TCP-interleaved
    /// RTP the binary $-frames will look like garbage between the delimiters
    /// and we'd want to revisit the dump format. UDP RTP keeps this file
    /// clean.
    /// </summary>
    internal static class RtspWireDumper {

        private const string EnvVar = "SOFTSLED_RTSP_WIRE_DUMP";

        public static bool IsEnabled =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar));

        /// <summary>
        /// Directory the dump files are written to. The session sets
        /// <c>SOFTSLED_RTSP_WIRE_DUMP</c> to the configured dumps dir
        /// (<c>&lt;DiagnosticsDirectory&gt;\Dumps\rtsp</c>); a bare <c>"1"</c>/<c>"true"</c>/
        /// <c>"yes"</c> (shell-set) falls back to <c>%TEMP%</c>. Returns null
        /// when the dumper is disabled.
        /// </summary>
        private static string ResolveDumpDir() {
            string v = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrEmpty(v)) return null;
            if (v == "1"
                || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes",  StringComparison.OrdinalIgnoreCase)) {
                return Path.GetTempPath();
            }
            return v;
        }

        /// <summary>Full path for a dump file under the resolved dump dir,
        /// creating the directory if needed. Falls back to <c>%TEMP%</c> if the
        /// directory can't be resolved or created.</summary>
        private static string DumpFilePath(string fileName) {
            string dir = ResolveDumpDir();
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            try { Directory.CreateDirectory(dir); }
            catch { dir = Path.GetTempPath(); }
            return Path.Combine(dir, fileName);
        }

        // --- UDP RTP tap ----------------------------------------------------
        // Phase-0c investigation: with com.microsoft.wm.rtp.asf advertised in
        // the SETUP request and 30+ rtpmaps offered by WMPNss for video, the
        // server is free to pick any of them at runtime — and the PT chosen
        // is only visible inside the RTP packets themselves. This tap dumps
        // the parsed RTP header + leading payload bytes for every packet,
        // capped at MaxPacketsPerLabel so a 30-minute playback doesn't fill
        // the disk. The cap is per (label, port) tuple — i.e. enough to see
        // patterns over the first few seconds of stream.
        private const int MaxPacketsPerLabel = 200;
        private const int PayloadHexBytes    = 128;

        private static StreamWriter _rtpLog;
        private static string _rtpTag;   // the play tag the current _rtpLog belongs to
        private static readonly object _rtpLogGate = new object();
        private static readonly Stopwatch _rtpLogSw = Stopwatch.StartNew();
        // Per-(label, port) packet counter for the cap.
        private static readonly System.Collections.Generic.Dictionary<string, int> _rtpCounts
            = new System.Collections.Generic.Dictionary<string, int>();

        /// <summary>Insert a per-play <paramref name="tag"/> before the extension so each
        /// play gets its own file (e.g. softsled-rtp-wire-20260715-104530-f9b58a22.log)
        /// instead of overwriting a single fixed name. Null/empty tag → base name.</summary>
        private static string TaggedName(string baseName, string tag) {
            if (string.IsNullOrEmpty(tag)) return baseName;
            int dot = baseName.LastIndexOf('.');
            return dot < 0 ? baseName + "-" + tag
                           : baseName.Substring(0, dot) + "-" + tag + baseName.Substring(dot);
        }

        /// <summary>
        /// Subscribe a packet-dumping handler to the given UDP socket pair's
        /// DataReceived event. No-op when the env-var toggle is off.
        /// </summary>
        public static void AttachUdpTap(Rtsp.UDPSocket pair, string label, Logger log, string tag = null) {
            if (!IsEnabled || pair == null) return;
            EnsureRtpLog(log, tag);
            pair.DataReceived += (sender, e) => {
                try {
                    var data = e.Message as Rtsp.Messages.RtspData;
                    if (data?.Data == null) return;
                    DumpRtpPacket(label, data.Channel, data.Data);
                } catch (Exception ex) {
                    Trace.WriteLine($"[rtsp-wire-dump] UDP tap error: {ex.Message}");
                }
            };
        }

        private static void EnsureRtpLog(Logger log, string tag) {
            lock (_rtpLogGate) {
                // New play (tag changed) → close the old capture and start a fresh
                // per-play file so earlier plays' RTP dumps are preserved (and the
                // 200-packet cap counters reset for the new play).
                if (_rtpLog != null && tag != _rtpTag) {
                    try { _rtpLog.Dispose(); } catch { }
                    _rtpLog = null;
                    _rtpCounts.Clear();
                    _rtpLogSw.Restart();   // per-play @+Xs timestamps start near 0
                }
                if (_rtpLog != null) return;
                _rtpTag = tag;
                string path = DumpFilePath(TaggedName("softsled-rtp-wire.log", tag));
                try {
                    _rtpLog = new StreamWriter(
                        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                        Encoding.ASCII);
                    _rtpLog.AutoFlush = true;
                    log?.LogInfo($"RTP packet dumper armed → {path} (cap {MaxPacketsPerLabel}/label)");
                } catch (Exception ex) {
                    log?.LogError($"RTP dumper failed to open log: {ex.Message}");
                }
            }
        }

        private static void DumpRtpPacket(string label, int port, byte[] frame) {
            string key = label + ":" + port;
            int seq;
            lock (_rtpLogGate) {
                _rtpCounts.TryGetValue(key, out seq);
                if (seq >= MaxPacketsPerLabel) return;
                _rtpCounts[key] = seq + 1;
            }

            // Parse RTP header (RFC 3550 §5.1). Even RTCP frames pass
            // through this event — annotate them differently so the
            // dump is unambiguous (V=2 PT≥72 indicates RTCP convention).
            if (frame.Length < 12) {
                WriteRtpLine($"--- {label} port={port} #{seq} len={frame.Length} TOO_SHORT ---");
                return;
            }

            int v   = (frame[0] >> 6) & 0x03;
            bool p  = (frame[0] & 0x20) != 0;
            bool x  = (frame[0] & 0x10) != 0;
            int cc  = frame[0] & 0x0F;
            bool m  = (frame[1] & 0x80) != 0;
            int pt  = frame[1] & 0x7F;
            int sn  = (frame[2] << 8) | frame[3];
            uint ts = ((uint)frame[4] << 24) | ((uint)frame[5] << 16)
                    | ((uint)frame[6] << 8)  | frame[7];
            uint ssrc = ((uint)frame[8] << 24) | ((uint)frame[9] << 16)
                      | ((uint)frame[10] << 8) | frame[11];
            int hdrLen = 12 + cc * 4;

            // Slot for RTCP heuristic: PT 72-76 are SR/RR/SDES/BYE/APP per
            // RFC 3551. Mark them so we don't conflate with media payloads.
            string rtcpHint = (pt >= 72 && pt <= 76) ? " (probably RTCP)" : "";

            string elapsed = (_rtpLogSw.Elapsed.TotalSeconds).ToString("F3");
            WriteRtpLine($"--- {label} port={port} #{seq} @+{elapsed}s len={frame.Length} ---");
            WriteRtpLine($"V={v} P={(p ? 1 : 0)} X={(x ? 1 : 0)} CC={cc} M={(m ? 1 : 0)} " +
                         $"PT={pt}{rtcpHint} SN={sn} TS={ts} SSRC=0x{ssrc:X8} hdrLen={hdrLen}");

            // Payload (everything after the optional CSRCs and extension).
            int payloadOffset = hdrLen;
            if (x && payloadOffset + 4 <= frame.Length) {
                int extWords = (frame[payloadOffset + 2] << 8) | frame[payloadOffset + 3];
                int extBytes = 4 + (extWords * 4);
                payloadOffset += extBytes;
            }
            if (payloadOffset > frame.Length) payloadOffset = frame.Length;

            int dumpLen = Math.Min(PayloadHexBytes, frame.Length - payloadOffset);
            if (dumpLen > 0) {
                WriteRtpLine($"payload[{frame.Length - payloadOffset}B] first {dumpLen}B: " +
                             HexDump(frame, payloadOffset, dumpLen));
            } else {
                WriteRtpLine("payload[0B] (empty)");
            }
            WriteRtpLine("");
        }

        private static void WriteRtpLine(string line) {
            lock (_rtpLogGate) {
                if (_rtpLog == null) return;
                try { _rtpLog.WriteLine(line); } catch { }
            }
        }

        private static string HexDump(byte[] data, int off, int len) {
            var sb = new StringBuilder(len * 3);
            for (int i = 0; i < len; i++) {
                if (i > 0 && (i & 0x0F) == 0) sb.Append(' ');
                else if (i > 0) sb.Append('-');
                sb.Append(data[off + i].ToString("X2"));
            }
            return sb.ToString();
        }

        // --------------------------------------------------------------------

        /// <summary>
        /// Wraps the given transport with a tee if the env-var toggle is set;
        /// otherwise returns the transport unchanged.
        /// </summary>
        public static IRtspTransport MaybeWrap(IRtspTransport inner, Logger log, string tag = null) {
            if (!IsEnabled) return inner;

            string path = DumpFilePath(TaggedName("softsled-rtsp-wire.log", tag));
            try {
                // Per-play file (tagged with timestamp+mediaid) so each play's wire
                // transcript is preserved instead of overwriting a single fixed name.
                FileStream fs = new FileStream(path, FileMode.Create,
                                               FileAccess.Write, FileShare.Read);
                var sink = new TimestampedTeeSink(fs);
                log?.LogInfo($"RTSP wire dumper armed → {path}");
                return new TeeingRtspTransport(inner, sink);
            } catch (Exception ex) {
                // Failing to open the dump file shouldn't break RTSP — fall
                // back to the unwrapped transport with a warning.
                log?.LogError($"RTSP wire dumper failed to open log file: {ex.Message}");
                return inner;
            }
        }

        // ----------------------------------------------------------------

        /// <summary>Shared writable sink — owns the file handle.</summary>
        private sealed class TimestampedTeeSink : IDisposable {
            private readonly FileStream _file;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            private readonly object _gate = new object();
            private bool _disposed;

            public TimestampedTeeSink(FileStream file) {
                _file = file;
            }

            public void Write(string direction, byte[] buf, int offset, int count) {
                if (count <= 0 || _disposed) return;
                lock (_gate) {
                    if (_disposed) return;
                    string header = $"\n--- {direction} @+{_sw.Elapsed.TotalSeconds:F3}s size={count} ---\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                    try {
                        _file.Write(headerBytes, 0, headerBytes.Length);
                        _file.Write(buf, offset, count);
                        _file.Flush();
                    } catch (Exception ex) {
                        // Don't let dump failures bubble out and crash the
                        // RTSP session — best-effort only.
                        Trace.WriteLine($"[rtsp-wire-dump] write failed: {ex.Message}");
                    }
                }
            }

            public void Dispose() {
                lock (_gate) {
                    if (_disposed) return;
                    _disposed = true;
                    try { _file.Dispose(); } catch { }
                }
            }
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// IRtspTransport decorator that hands out a teed stream from
        /// <see cref="GetStream"/>; everything else delegates straight
        /// through. RtspListener calls GetStream() once and caches the
        /// result, so we can return a fresh TeeStream each call without
        /// worrying about identity.
        /// </summary>
        private sealed class TeeingRtspTransport : IRtspTransport, IDisposable {
            private readonly IRtspTransport _inner;
            private readonly TimestampedTeeSink _sink;

            public TeeingRtspTransport(IRtspTransport inner, TimestampedTeeSink sink) {
                _inner = inner;
                _sink = sink;
            }

            public Stream GetStream() => new TeeStream(_inner.GetStream(), _sink);
            public string RemoteAddress => _inner.RemoteAddress;
            public bool Connected => _inner.Connected;
            public void Close() {
                try { _inner.Close(); } finally { _sink.Dispose(); }
            }
            public void Reconnect() => _inner.Reconnect();

            public void Dispose() {
                if (_inner is IDisposable d) d.Dispose();
                _sink.Dispose();
            }
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Stream decorator that tees every Read/Write to the sink. Read
        /// records the bytes that actually came back (after the underlying
        /// stream returned), so partial reads are accurately captured.
        ///
        /// RtspListener's hot path is one-byte-at-a-time line scanning via
        /// <see cref="Stream.ReadByte"/>; emitting a delimiter header per
        /// byte would inflate the dump 50x and obscure the content. We
        /// instead buffer ReadByte calls and flush on newline (RTSP line
        /// terminator) or when a block Read happens (SDP/data body). This
        /// gives the dump file naturally line-aligned chunks that read like
        /// the wire transcript.
        /// </summary>
        private sealed class TeeStream : Stream {
            private readonly Stream _inner;
            private readonly TimestampedTeeSink _sink;
            private readonly MemoryStream _readByteBuffer = new MemoryStream(256);

            public TeeStream(Stream inner, TimestampedTeeSink sink) {
                _inner = inner;
                _sink = sink;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position {
                get => _inner.Position;
                set => _inner.Position = value;
            }
            public override void Flush() => _inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            public override int Read(byte[] buffer, int offset, int count) {
                FlushReadByteBuffer();
                int n = _inner.Read(buffer, offset, count);
                if (n > 0) _sink.Write("RECV", buffer, offset, n);
                return n;
            }

            public override int ReadByte() {
                int b = _inner.ReadByte();
                if (b >= 0) {
                    _readByteBuffer.WriteByte((byte)b);
                    // Flush on '\n' (RTSP/HTTP line terminator) or after a
                    // safety cap so binary data without newlines still gets
                    // flushed in reasonable chunks.
                    if (b == '\n' || _readByteBuffer.Length >= 4096) {
                        FlushReadByteBuffer();
                    }
                }
                return b;
            }

            private void FlushReadByteBuffer() {
                if (_readByteBuffer.Length == 0) return;
                byte[] data = _readByteBuffer.ToArray();
                _readByteBuffer.SetLength(0);
                _sink.Write("RECV", data, 0, data.Length);
            }

            public override void Write(byte[] buffer, int offset, int count) {
                // Flush any pending RECV bytes first so chronology is correct
                // in the dump (interleaved sends and receives stay ordered).
                FlushReadByteBuffer();
                _sink.Write("SEND", buffer, offset, count);
                _inner.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing) {
                if (disposing) {
                    FlushReadByteBuffer();
                    try { _inner.Dispose(); } catch { }
                }
                base.Dispose(disposing);
            }
        }
    }
}
