using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;
using Unosquare.FFME.Common;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// FFME custom-input-stream adapter for <see cref="AsfStreamProducer"/>.
    /// Exposes the producer's reconstructed byte stream to FFmpeg via
    /// FFME's <see cref="IMediaInputStream"/> contract.
    ///
    /// The input format the demuxer should expect is set at construction
    /// time via <paramref name="forcedInputFormat"/>: <c>"asf"</c> for the
    /// ASF mode, <c>"mpegvideo"</c> for raw MPEG-1/2 video elementary
    /// streams (the WMC recorded-TV <c>vnd.ms.wm-MPV</c> path), <c>"h264"</c>
    /// for raw Annex-B, etc.
    ///
    /// Lifecycle: created when the AvCtrl pipeline becomes ready, handed
    /// to <c>Media.Open(IMediaInputStream)</c>. FFME calls <see cref="Read"/>
    /// on its demux thread; we copy from the producer's blocking-collection-
    /// backed buffer into FFmpeg's native target. No seeks: streams are
    /// live (RTP), seek returns -1.
    /// </summary>
    internal sealed unsafe class AsfFfmeInputStream : IMediaInputStream, IDisposable {

        // Buffer size passed to libav via FFME's avio_alloc_context. This
        // controls TWO things:
        //
        //  1. How many bytes our Read callback is asked for per round trip.
        //  2. How many bytes of history libav keeps available for backward
        //     seeks WITHOUT calling our Seek callback (libav serves seeks
        //     entirely inside the AVIO buffer when possible).
        //
        // The MP3 demuxer's init path (mp3_read_header) reads through ID3
        // + a few frames + VBR-tag probing, THEN unconditionally does
        // avio_seek(off, SEEK_SET) back to the position right after ID3.
        // If that target is no longer in the AVIO buffer libav calls our
        // Seek; we return EPIPE; the demuxer aborts; open fails.
        //
        // With the old 4 KB value, libav's fill_buffer flushed on every
        // refill (per the IO_BUFFER_SIZE=32 KB check in libav 4.4) so the
        // MP3 init seek-back never had its target in-buffer. 64 KB is the
        // smallest value that lets libav accumulate ~32 KB of history
        // before forced flush, comfortably covering the MP3 init reads.
        private const int InternalReadBufferLength = 64 * 1024;

        private readonly AsfStreamProducer _producer;
        private readonly Logger _log;
        private readonly byte[] _scratch = new byte[InternalReadBufferLength];
        private readonly string _forcedInputFormat;
        private readonly System.Collections.Generic.IReadOnlyDictionary<string, string> _extraOptions;

        public AsfFfmeInputStream(AsfStreamProducer producer, Logger log)
            : this(producer, log, forcedInputFormat: "asf", extraOptions: null) { }

        public AsfFfmeInputStream(AsfStreamProducer producer, Logger log, string forcedInputFormat)
            : this(producer, log, forcedInputFormat, extraOptions: null) { }

        /// <summary>
        /// Create an FFME input stream with explicit demuxer options applied
        /// on top of the format-defaults. Used by the raw-PCM (<c>s16le</c>)
        /// path that needs <c>sample_rate</c> + <c>channels</c> overrides
        /// before libavformat can decode the headerless stream.
        /// </summary>
        public AsfFfmeInputStream(AsfStreamProducer producer, Logger log, string forcedInputFormat,
                                  System.Collections.Generic.IReadOnlyDictionary<string, string> extraOptions) {
            _producer = producer ?? throw new ArgumentNullException(nameof(producer));
            _log = log;
            _forcedInputFormat = string.IsNullOrEmpty(forcedInputFormat) ? "asf" : forcedInputFormat;
            _extraOptions = extraOptions;

            // FFME insists on a non-null URI for logging; the protocol
            // doesn't matter, FFmpeg never opens it as a real URL because
            // we provide all bytes via Read().
            StreamUri = new Uri("softsled://" + _forcedInputFormat + "/" + Guid.NewGuid().ToString("N"));

            // OnInitializing fires before FFmpeg opens the format context.
            // Force the input format to the value supplied at construction
            // time and pick probe options that suit it. Two key concerns:
            //
            //   * probesize / analyzeduration — how much data libav reads
            //     to detect codec parameters before find_stream_info
            //     completes. Larger = more accurate detection, longer
            //     startup latency.
            //
            //   * fflags=nobuffer — tells libav NOT to queue probed
            //     packets for later playback. Fine for video (saves a
            //     few hundred ms of startup latency at zero cost since
            //     video probing inspects a keyframe that gets redelivered
            //     in the stream anyway), but BAD for audio: the packets
            //     consumed during MP3's find_stream_info pass are simply
            //     dropped, and the first ~1 s of the recording never
            //     reaches the audio renderer.
            //
            // So: video paths keep aggressive low-latency settings, audio
            // paths drop nobuffer and tighten probesize so find_stream_info
            // commits after a single frame.
            bool isAudioOnly = !HasVideo;
            OnInitializing = (config, source) => {
                config.ForcedInputFormat = _forcedInputFormat;
                // NOTE: IsTimeSyncDisabled lives on MediaOptions (set via
                // MediaElement.MediaOpening), not on ContainerConfiguration
                // here. Wired in ExtenderSessionControl's MediaOpening
                // handler so per-stream clocks are disconnected and the
                // video element keeps advancing when the server drops
                // audio during trick play.
                if (isAudioOnly) {
                    // MP3/AC-3/WAV: one frame header is enough to commit
                    // codec parameters. 32 KB probesize, 500 ms analyze.
                    // No nobuffer — keep the probed packets so they play.
                    config.PrivateOptions["probesize"]       = "32768";
                    config.PrivateOptions["analyzeduration"] = "500000";
                } else {
                    // Video probe budget. Previously 2 MB / 5 s — libav's
                    // safe defaults for fully-unknown streams. We
                    // already know the codecs from the SDP fmtp lines
                    // (WMFPayloadData dict is committed in RTSPClient
                    // before this stream opens), so find_stream_info
                    // doesn't need to scan multiple GOPs to figure out
                    // what's playing. Tightening these two values is
                    // the single biggest knob for FFME open latency:
                    //
                    //   probesize: 2 MB → 512 KB. Covers the PS pack
                    //   header + system header + PSM (~few KB) + a
                    //   full IDR frame at 1080p (~100 KB) + first PES
                    //   on each stream. find_stream_info commits codec
                    //   parameters well below this ceiling for the
                    //   known-codec streams we feed it.
                    //
                    //   analyzeduration: 5 s → 1.5 s. find_stream_info
                    //   completes as soon as EITHER probesize is hit
                    //   OR analyzeduration of stream-time has been
                    //   observed. With data flowing at real-time
                    //   1.5 s wall-time is reached in ~1.5 s.
                    //
                    // Tested with VND.MS.WM-MPV (MPEG-2 video) +
                    // VND.MS.WM-MPA (MP2/MP3 audio), AC-3, and X-WMF-PF
                    // H.264 + PCM streams — all commit codec params
                    // well inside the new limits.
                    config.PrivateOptions["probesize"]       = "524288";    // 512 KB
                    config.PrivateOptions["analyzeduration"] = "1500000";   // 1.5 s
                    // Video can afford to drop probed packets — the next
                    // keyframe redelivers the necessary decoder state.
                    config.PrivateOptions["fflags"]          = "nobuffer";
                }
                // Caller-supplied overrides (e.g. sample_rate / channels for
                // raw PCM s16le). Applied last so they win over the defaults.
                if (_extraOptions != null) {
                    foreach (var kv in _extraOptions) {
                        config.PrivateOptions[kv.Key] = kv.Value;
                    }
                }
            };
        }

        // ----- IMediaInputStream contract -------------------------------

        public Uri StreamUri { get; }
        public bool CanSeek => false;
        public int ReadBufferLength => InternalReadBufferLength;

        /// <summary>
        /// True when the forced input format carries a video stream.
        /// MainWindow uses this to decide whether the FFME element should
        /// be made visible (video streams) or stay <c>Collapsed</c> (audio-
        /// only streams that just need FFME's audio renderer to run).
        ///
        /// Derived from <c>forcedInputFormat</c> rather than probed from
        /// the actual stream — saves us racing FFME's MediaOpening event,
        /// and the caller knows up front which format hint it passed.
        /// </summary>
        public bool HasVideo {
            get {
                switch (_forcedInputFormat) {
                    case "mp3":
                    case "ac3":
                    case "wav":
                    case "s16le":
                    case "s16be":
                    case "s24le":
                    case "f32le":
                        return false;
                    default:
                        // mpeg / mpegvideo / h264 / asf — assume video present.
                        return true;
                }
            }
        }

        public InputStreamInitializing OnInitializing { get; }

        public InputStreamInitialized OnInitialized { get; } = null;

        public int Read(void* opaque, byte* targetBuffer, int targetBufferLength) {
            // Cap the request at our scratch buffer — anything larger is
            // multiple FFmpeg-callback round trips, which is fine. Keeping
            // a stack-allocated path here would be faster but unsafe-pointer
            // gymnastics aren't worth the complexity for the throughput
            // we're targeting (a few MB/s).
            int toRead = targetBufferLength < _scratch.Length
                ? targetBufferLength : _scratch.Length;

            int n;
            try {
                n = _producer.Read(_scratch, 0, toRead);
            } catch (Exception ex) {
                _log?.LogError($"[asf-ffme] Read exception: {ex.Message}");
                // Any negative value flags "error" to libav; we
                // deliberately pick AVERROR(EIO) (-5 in POSIX) for the
                // sake of meaningful logs, but any non-zero negative
                // works — the demuxer's behaviour is identical.
                return -5;
            }

            if (n <= 0) {
                // Returning a literal 0 from a custom-IO Read callback
                // triggers libav's "Invalid return value 0 for stream
                // protocol" warning (libavformat/avio.c retry_transfer_wrapper)
                // — FFmpeg's API requires AVERROR_EOF for end-of-stream,
                // not 0. Returning AVERROR_EOF cleanly tells the demuxer
                // "no more data ever" so it can finalise any in-flight
                // frames and let Media.Close finish without spurious
                // protocol-level warnings.
                //
                // n < 0 (cancellation thrown during disposal) is treated
                // as EOF too — semantically there's no more data coming.
                return AVERROR_EOF;
            }

            Marshal.Copy(_scratch, 0, (IntPtr)targetBuffer, n);
            return n;
        }

        // FFERRTAG('E','O','F',' ') = -(('E') | ('O'<<8) | ('F'<<16) | (' '<<24))
        //                           = -(0x20464F45) = -541478725 (0xDFB9B0BB as int32)
        // Defined here rather than pulled from FFmpeg.AutoGen so the file
        // doesn't add a heavy dependency just for one int literal.
        private const int AVERROR_EOF = unchecked((int)0xDFB9B0BB);

        public long Seek(void* opaque, long offset, int whence) {
            // Non-seekable RTSP stream. libav's custom-IO Seek callback
            // reports errors as AVERROR(errno) = -errno. The previous
            // implementation returned a literal -1 — which equals
            // AVERROR(EPERM) ("Operation not permitted"), a code several
            // demuxers (notably mp3_read_header → avio_seek-back after
            // ID3/probe) treat as a hard failure and abort opening on.
            //
            // Return AVERROR(EPIPE) = -32 instead. "Broken pipe" is the
            // canonical "stream cannot be seeked" signal — demuxers fall
            // back to non-seekable behaviour (skip VBR-tag parsing,
            // accept unknown duration, etc.) rather than refusing to
            // initialise. Same code regardless of whence (SEEK_SET /
            // SEEK_CUR / SEEK_END / AVSEEK_SIZE) because every variant
            // means "we don't support seeking" for a live RTSP source.
            const int AVERROR_EPIPE = -32;
            return AVERROR_EPIPE;
        }

        public void Dispose() {
            // We don't own the producer (caller does); just log.
            _log?.LogInfo($"[asf-ffme] disposed; consumed " +
                          $"{_producer.BytesConsumed}B from producer");
        }
    }
}
