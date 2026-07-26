using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SoftSled.Components.AudioVisual.ExternalSync {

    /// <summary>
    /// Thin libav audio decoder. Takes encoded audio MAUs (e.g. raw MP3
    /// frames stripped of their RFC 2250 sub-header), decodes them via
    /// <c>avcodec_send_packet</c> / <c>avcodec_receive_frame</c>, and
    /// emits int16 PCM at a fixed output rate via <c>swr_convert</c>
    /// for stable downstream NAudio consumption.
    ///
    /// <para>Phase 1 is MP3-only (RFC 2250 MPA). The class is shaped
    /// to extend to MP2 / AC3 / others later by parameterising
    /// <see cref="AVCodecID"/>.</para>
    ///
    /// <para>Threading: decode happens on a dedicated worker thread.
    /// <see cref="SubmitPacket"/> is safe from any thread; the
    /// <see cref="OnPcm"/> callback fires on the decoder thread.</para>
    /// </summary>
    internal sealed unsafe class LibAvAudioDecoder : IDisposable {

        // Fixed output format. NAudio's BufferedWaveProvider expects
        // a stable WaveFormat at construction, so we resample
        // whatever the decoder emits to one canonical shape. 48 kHz
        // int16 stereo is the lowest-common-denominator format every
        // Windows audio device supports natively (no driver upmix
        // / downmix).
        public const int OutSampleRate = 48000;
        public const int OutChannels   = 2;
        public const int OutBitsPerSample = 16;

        private readonly AVCodecID _codecId;
        private readonly Logger _log;
        // Pre-open hints — only used for codecs that don't carry
        // sample-rate / channel info in the bitstream (raw PCM). For
        // MP3 / MP2 / AC3 these can be left at 0; libav will read
        // the values from the first frame's header.
        private readonly int _hintSampleRate;
        private readonly int _hintChannels;
        // WMA (and other codecs that carry no in-band config) need these set
        // BEFORE avcodec_open2: extradata is the codec-private config (from the
        // SDP fmtp config= blob), block_align the compressed frame size, and
        // bit_rate the nominal rate. 0/null = not a WMA-style codec.
        private readonly int _blockAlign;
        private readonly int _bitRate;
        private readonly byte[] _extradata;
        // Deep input queue: with decode backpressure (see WorkerLoop) coded MAUs
        // accumulate here while we decode at the device's real drain rate. WMPNss
        // front-loads audio far ahead (CDB≈200s of coded audio) and IGNORES our
        // BFR buffer report, so the reservoir must be able to hold that burst —
        // otherwise drop-oldest would discard imminent audio (a glitch). 16384
        // MP2 frames ≈ 390s; each entry is a small compressed MAU (~1KB), so the
        // worst-case memory is a few MB. Flush() empties it on seek/trick-play.
        private readonly BlockingCollection<QueuedPacket> _queue
            = new BlockingCollection<QueuedPacket>(boundedCapacity: 16384);

        private AVCodecContext* _ctx;
        private SwrContext* _swr;
        private Thread _worker;
        private bool _disposed;

        // Pause gate. Set = running; reset = paused. The worker blocks on this
        // before consuming each packet, so a Pause() holds decoding (and keeps
        // the input queue + already-decoded buffers intact) without tearing the
        // decoder down. Starts set (running). Dispose sets it so a paused worker
        // can unblock and exit cleanly.
        private readonly ManualResetEventSlim _runGate = new ManualResetEventSlim(true);

        // Decode backpressure. Set by the controller to () => renderer.BufferedMs.
        // The worker holds coded MAUs in the (deep) input queue instead of
        // decoding them while the renderer already has >= PcmHighWaterMs of PCM
        // buffered — so we consume at the device's drain rate, not the server's
        // (over-)delivery rate. Without this, WMPNss's ~121% audio flood overran
        // the 5s PCM buffer, dropping ~19% of frames and skewing audio ahead of
        // video (the sync wobble ~25-30s in). Null → no backpressure (decode as
        // fast as MAUs arrive, the pre-fix behaviour).
        public Func<int> PcmBufferedMsProvider { get; set; }
        private const int PcmHighWaterMs = 2000; // ~TD; Xbox holds its audio buffer near here

        // Set by Flush() (any thread); honoured on the worker thread before the
        // next decode (avcodec_flush_buffers is not thread-safe).
        private int _flushRequested;

        // Output-format readiness. The first decoded frame tells us
        // the decoder's native sample_rate / channels; we configure
        // swr from that. Subscribers register via OnFormatReady to
        // know when it's safe to construct the NAudio renderer with
        // the right WaveFormat (always 48/16/2 by construction
        // here, but the event also gates "decoder is alive" for
        // downstream).
        public event Action OnFormatReady;

        /// <summary>Fires for every decoded PCM frame. The byte[] is
        /// rented from a per-decoder reusable buffer; subscribers
        /// MUST copy it before returning if they need to retain it
        /// beyond the call. Length is bytes (NOT samples). PtsMs is
        /// the engine-time PTS of the source packet (we don't
        /// re-derive it from frame->pts because for MP3 over RFC 2250
        /// the wire-side RTP timestamp IS the authoritative PTS).</summary>
        /// <para>The 4th arg is the sync EPOCH the source packet was submitted
        /// under. Stamped at submit time, not emit time, so a packet queued before
        /// a seek stays stale even if it decodes after the flush — that in-flight
        /// PCM is exactly what used to re-anchor the renderer's gap tracker on the
        /// OLD timeline after a seek (spurious multi-second silence fill).</para>
        public event Action<byte[], int, long, long> OnPcm;

        public LibAvAudioDecoder(AVCodecID codecId, Logger log)
            : this(codecId, log, hintSampleRate: 0, hintChannels: 0) { }

        public LibAvAudioDecoder(AVCodecID codecId, Logger log,
                                 int hintSampleRate, int hintChannels)
            : this(codecId, log, hintSampleRate, hintChannels,
                   blockAlign: 0, bitRate: 0, extradata: null) { }

        /// <summary>Full ctor for codecs that need pre-open config — notably
        /// WMA, which requires extradata (the SDP fmtp <c>config=</c> blob),
        /// block_align (compressed frame size) and sample_rate/channels set
        /// before <c>avcodec_open2</c>.</summary>
        public LibAvAudioDecoder(AVCodecID codecId, Logger log,
                                 int hintSampleRate, int hintChannels,
                                 int blockAlign, int bitRate, byte[] extradata) {
            _codecId = codecId;
            _log = log;
            _hintSampleRate = hintSampleRate;
            _hintChannels = hintChannels;
            _blockAlign = blockAlign;
            _bitRate = bitRate;
            _extradata = extradata;
        }

        public void Start() {
            if (_worker != null) return;

            AVCodec* codec = ffmpeg.avcodec_find_decoder(_codecId);
            if (codec == null) {
                _log?.LogError($"[libav-audio] avcodec_find_decoder({_codecId}) → null");
                throw new InvalidOperationException($"libav: no decoder for {_codecId}");
            }
            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx == null) {
                throw new InvalidOperationException("libav: avcodec_alloc_context3 failed");
            }

            // Raw PCM decoders (pcm_s16le etc.) carry no header in
            // the bitstream — they need sample_rate / channels /
            // channel_layout set BEFORE avcodec_open2 or the decoder
            // will reject the first packet with EINVAL. For framed
            // codecs (MP3 / MP2 / AC3) leaving these zero is fine;
            // libav reads them from the frame header on first
            // packet.
            if (_hintSampleRate > 0) _ctx->sample_rate = _hintSampleRate;
            if (_hintChannels > 0) {
                _ctx->channels = _hintChannels;
                _ctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(_hintChannels);
            }

            // WMA: block_align (compressed frame size), bit_rate and the
            // codec-private extradata MUST be set before open or the decoder
            // can't initialise its frame layout. extradata must live in
            // av_malloc'd memory (libav takes ownership / frees it) with
            // AV_INPUT_BUFFER_PADDING_SIZE trailing zero bytes.
            if (_blockAlign > 0) _ctx->block_align = _blockAlign;
            if (_bitRate > 0) _ctx->bit_rate = _bitRate;
            if (_extradata != null && _extradata.Length > 0) {
                int extLen = _extradata.Length;
                byte* buf = (byte*)ffmpeg.av_mallocz(
                    (ulong)(extLen + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
                if (buf != null) {
                    System.Runtime.InteropServices.Marshal.Copy(_extradata, 0, (IntPtr)buf, extLen);
                    _ctx->extradata = buf;
                    _ctx->extradata_size = extLen;
                    _log?.LogInfo($"[libav-audio] {_codecId}: extradata={extLen}B " +
                                  $"blockAlign={_blockAlign} bitRate={_bitRate} " +
                                  $"rate={_hintSampleRate} ch={_hintChannels}");
                }
            }

            int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
            if (ret < 0) {
                _log?.LogError($"[libav-audio] avcodec_open2({_codecId}) failed: {AvStrError(ret)}");
                AVCodecContext* tmp = _ctx;
                ffmpeg.avcodec_free_context(&tmp);
                _ctx = null;
                throw new InvalidOperationException(
                    $"libav: avcodec_open2 failed for {_codecId}: {AvStrError(ret)}");
            }

            _log?.LogInfo($"[libav-audio] codec opened: {_codecId}");

            _worker = new Thread(WorkerLoop) {
                IsBackground = true,
                Name = $"LibAvAudioDecoder-{_codecId}",
            };
            _worker.Start();
        }

        public void SubmitPacket(byte[] data, long ptsMs, long epoch) {
            if (_disposed || data == null || data.Length == 0) return;

            // Diagnostics: log first MAU's first 64 bytes so we can
            // compare with the wire log and verify what the
            // depacketizer is emitting (wrapper stripped or not,
            // byte-order, etc.). One-shot.
            if (Interlocked.CompareExchange(ref _loggedFirstMau, 1, 0) == 0) {
                int n = System.Math.Min(64, data.Length);
                var sb = new System.Text.StringBuilder(n * 3 + 32);
                sb.Append($"[libav-audio] FIRST MAU len={data.Length} bytes[0..{n}]: ");
                for (int i = 0; i < n; i++) {
                    if (i > 0 && (i & 0xF) == 0) sb.Append(' ');
                    sb.Append(data[i].ToString("X2"));
                    if (i < n - 1) sb.Append('-');
                }
                _log?.LogInfo(sb.ToString());
            }
            Interlocked.Add(ref _statPacketsIn, 1);
            Interlocked.Add(ref _statBytesIn, data.Length);

            if (!_queue.TryAdd(new QueuedPacket { Data = data, PtsMs = ptsMs, Epoch = epoch })) {
                // Queue full — drop oldest, retry. Bounded backpressure
                // matches the LibAvDecoder pattern: better to lose old
                // audio than to block the depacketizer.
                if (_queue.TryTake(out _)) _queue.TryAdd(new QueuedPacket { Data = data, PtsMs = ptsMs, Epoch = epoch });
            }
        }

        // Per-second throughput stats. Helps distinguish wire-level
        // underfeeding from decoder-level under-production: if
        // packetsIn/bytesIn is healthy but pcmOut is low, the decoder
        // is producing too little (wrong codec / wrong format). If
        // packetsIn/bytesIn is itself low, the wire / depacketizer
        // is the bottleneck.
        private int _loggedFirstMau;
        private long _statPacketsIn;
        private long _statBytesIn;
        private long _statFramesOut;
        private long _statPcmBytesOut;
        private long _statWindowStartMs;
        private readonly System.Diagnostics.Stopwatch _statsClock = System.Diagnostics.Stopwatch.StartNew();

        private void MaybeEmitStats() {
            long nowMs = _statsClock.ElapsedMilliseconds;
            long start = Interlocked.Read(ref _statWindowStartMs);
            long elapsed = nowMs - start;
            if (elapsed < 1000) return;
            if (Interlocked.CompareExchange(ref _statWindowStartMs, nowMs, start) != start) return;
            long pktsIn = Interlocked.Exchange(ref _statPacketsIn, 0);
            long bytesIn = Interlocked.Exchange(ref _statBytesIn, 0);
            long framesOut = Interlocked.Exchange(ref _statFramesOut, 0);
            long pcmBytesOut = Interlocked.Exchange(ref _statPcmBytesOut, 0);
            // Expected PCM rate at the canonical output format.
            const long expectedBytesPerSec =
                (long)OutSampleRate * OutChannels * (OutBitsPerSample / 8);
            double pctOfRealTime = pcmBytesOut * 100.0 / expectedBytesPerSec;
            _log?.LogInfo($"[libav-audio] stats: pktsIn={pktsIn} bytesIn={bytesIn} " +
                          $"framesOut={framesOut} pcmBytesOut={pcmBytesOut} " +
                          $"({pctOfRealTime:F1}% of {expectedBytesPerSec}B/s expected) " +
                          $"queueDepth={_queue.Count} window={elapsed}ms");
        }

        public void Complete() {
            try { _queue.CompleteAdding(); } catch { }
        }

        /// <summary>Hold decoding (worker blocks before consuming the next
        /// packet). The input queue and already-decoded output are retained;
        /// no CPU is spent while paused. Idempotent.</summary>
        public void Pause() { if (!_disposed) _runGate.Reset(); }

        /// <summary>Resume decoding after <see cref="Pause"/>. Idempotent.</summary>
        public void Resume() { if (!_disposed) _runGate.Set(); }

        /// <summary>Discard all queued (undecoded) coded MAUs and reset the
        /// codec's internal buffers. Called on seek / trick-play so the deep
        /// input-queue reservoir doesn't replay stale pre-seek audio at the new
        /// position. Safe from any thread: the queue drain is lock-free; the
        /// codec flush is deferred to the worker thread via <c>_flushRequested</c>
        /// (avcodec_flush_buffers is not thread-safe).</summary>
        public void Flush() {
            if (_disposed) return;
            while (_queue.TryTake(out _)) { }
            Interlocked.Exchange(ref _flushRequested, 1);
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            // Release the pause gate FIRST so a paused worker unblocks and can
            // observe CompleteAdding / _disposed and exit (else Join deadlocks).
            try { _runGate.Set(); } catch { }
            try { _queue.CompleteAdding(); } catch { }
            try { _worker?.Join(2000); } catch { }
            try { _runGate.Dispose(); } catch { }
            try { _queue.Dispose(); } catch { }
            if (_swr != null) {
                SwrContext* tmp = _swr;
                ffmpeg.swr_free(&tmp);
                _swr = null;
            }
            if (_ctx != null) {
                AVCodecContext* tmp = _ctx;
                ffmpeg.avcodec_free_context(&tmp);
                _ctx = null;
            }
            _log?.LogInfo("[libav-audio] disposed");
        }

        // ---------- Worker ----------

        private void WorkerLoop() {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* frame = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null) {
                _log?.LogError("[libav-audio] av_packet_alloc / av_frame_alloc failed");
                if (pkt != null)   { AVPacket* t = pkt;   ffmpeg.av_packet_free(&t); }
                if (frame != null) { AVFrame*  t = frame; ffmpeg.av_frame_free(&t);  }
                return;
            }
            try {
                foreach (var qp in _queue.GetConsumingEnumerable()) {
                    // Block here while paused — the packet just dequeued is held
                    // in `qp` (not lost) and processed once resumed. During a
                    // pause RTSP is also stopped, so no further packets arrive
                    // and the queue simply holds.
                    _runGate.Wait();
                    if (_disposed) break;

                    // Backpressure: don't decode ahead of real time. While the
                    // renderer already holds >= PcmHighWaterMs of PCM, wait — the
                    // server's front-load stays in the (deep) coded queue rather
                    // than overflowing the PCM buffer. The held `qp` decodes as
                    // soon as the device drains below the mark.
                    var probe = PcmBufferedMsProvider;
                    if (probe != null) {
                        while (!_disposed && probe() >= PcmHighWaterMs) {
                            _runGate.Wait();               // honour pause during the wait
                            if (_disposed) break;
                            Thread.Sleep(8);
                        }
                        if (_disposed) break;
                    }

                    // Seek/trick-play flushed us: reset codec state (worker-thread
                    // only — avcodec_flush_buffers is not thread-safe).
                    if (Interlocked.Exchange(ref _flushRequested, 0) == 1) {
                        ffmpeg.avcodec_flush_buffers(_ctx);
                    }

                    SendPacket(pkt, qp);
                    DrainFrames(frame, qp.PtsMs, qp.Epoch);
                    MaybeEmitStats();
                }
                // Flush
                ffmpeg.avcodec_send_packet(_ctx, null);
                DrainFrames(frame, 0, long.MaxValue);   // EOF drain: never epoch-gated
            } catch (Exception ex) {
                _log?.LogError($"[libav-audio] worker exception: {ex.Message}");
            } finally {
                AVPacket* pTmp = pkt;
                AVFrame* fTmp = frame;
                ffmpeg.av_packet_free(&pTmp);
                ffmpeg.av_frame_free(&fTmp);
            }
        }

        private void SendPacket(AVPacket* pkt, QueuedPacket qp) {
            fixed (byte* src = qp.Data) {
                pkt->data = src;
                pkt->size = qp.Data.Length;
                pkt->pts = qp.PtsMs;
                pkt->dts = qp.PtsMs;
                pkt->flags = 0;
                int ret = ffmpeg.avcodec_send_packet(_ctx, pkt);
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
                    _log?.LogDebug($"[libav-audio] send_packet ret={AvStrError(ret)}");
                }
                pkt->data = null;
                pkt->size = 0;
            }
        }

        private bool _formatReadyFired;
        private byte[] _outBuf;

        private void DrainFrames(AVFrame* frame, long ptsMs, long epoch) {
            while (true) {
                int ret = ffmpeg.avcodec_receive_frame(_ctx, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) return;
                if (ret < 0) {
                    _log?.LogDebug($"[libav-audio] receive_frame ret={AvStrError(ret)}");
                    return;
                }

                // First decoded frame: configure swr to convert
                // decoder-native -> 48kHz int16 stereo.
                if (_swr == null) {
                    if (!ConfigureSwrFromFrame(frame)) {
                        ffmpeg.av_frame_unref(frame);
                        continue;
                    }
                    if (!_formatReadyFired) {
                        _formatReadyFired = true;
                        try { OnFormatReady?.Invoke(); }
                        catch (Exception ex) {
                            _log?.LogError($"[libav-audio] OnFormatReady threw: {ex.Message}");
                        }
                    }
                }

                // Resample → int16 stereo @ 48 kHz. swr_convert wants
                // an output buffer sized in OUTPUT samples per channel.
                // For 48k stereo, every input frame (typically 1152
                // samples @ 48k for MP3 → ~1152 output samples) is
                // at most ~4608 bytes of int16 stereo. Cap rented
                // buffer at 8192 bytes to cover ms-frame outliers.
                int outSamplesPerCh = (int)ffmpeg.av_rescale_rnd(
                    ffmpeg.swr_get_delay(_swr, frame->sample_rate) + frame->nb_samples,
                    OutSampleRate, frame->sample_rate, AVRounding.AV_ROUND_UP);
                int outBytes = outSamplesPerCh * OutChannels * (OutBitsPerSample / 8);
                if (_outBuf == null || _outBuf.Length < outBytes) {
                    _outBuf = new byte[Math.Max(outBytes, 8192)];
                }

                int produced;
                fixed (byte* outPtr = _outBuf) {
                    byte** outArr = stackalloc byte*[1];
                    outArr[0] = outPtr;
                    produced = ffmpeg.swr_convert(_swr, outArr, outSamplesPerCh,
                        frame->extended_data, frame->nb_samples);
                }
                if (produced < 0) {
                    _log?.LogDebug($"[libav-audio] swr_convert ret={produced}");
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }
                int producedBytes = produced * OutChannels * (OutBitsPerSample / 8);
                Interlocked.Add(ref _statFramesOut, 1);
                Interlocked.Add(ref _statPcmBytesOut, producedBytes);
                try {
                    OnPcm?.Invoke(_outBuf, producedBytes, ptsMs, epoch);
                } catch (Exception ex) {
                    _log?.LogError($"[libav-audio] OnPcm threw: {ex.Message}");
                }
                ffmpeg.av_frame_unref(frame);
            }
        }

        private bool ConfigureSwrFromFrame(AVFrame* frame) {
            int srcRate = frame->sample_rate;
            long srcLayout = (long)frame->channel_layout;
            int srcChannels = frame->channels;
            var srcFmt = (AVSampleFormat)frame->format;
            if (srcLayout == 0) {
                // Some decoders leave layout = 0 but set channels.
                // Synthesize a default layout (mono / stereo / 5.1).
                srcLayout = (long)ffmpeg.av_get_default_channel_layout(srcChannels);
            }
            long dstLayout = (long)ffmpeg.AV_CH_LAYOUT_STEREO;

            _swr = ffmpeg.swr_alloc();
            if (_swr == null) {
                _log?.LogError("[libav-audio] swr_alloc failed");
                return false;
            }
            ffmpeg.av_opt_set_channel_layout(_swr, "in_channel_layout",  srcLayout, 0);
            ffmpeg.av_opt_set_channel_layout(_swr, "out_channel_layout", dstLayout, 0);
            ffmpeg.av_opt_set_int(_swr, "in_sample_rate",  srcRate, 0);
            ffmpeg.av_opt_set_int(_swr, "out_sample_rate", OutSampleRate, 0);
            ffmpeg.av_opt_set_sample_fmt(_swr, "in_sample_fmt",  srcFmt, 0);
            ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt", AVSampleFormat.AV_SAMPLE_FMT_S16, 0);

            int ret = ffmpeg.swr_init(_swr);
            if (ret < 0) {
                _log?.LogError($"[libav-audio] swr_init failed: {AvStrError(ret)}");
                SwrContext* tmp = _swr;
                ffmpeg.swr_free(&tmp);
                _swr = null;
                return false;
            }
            _log?.LogInfo($"[libav-audio] swr ready: in={srcRate}Hz/{srcChannels}ch/{srcFmt} " +
                          $"→ out={OutSampleRate}Hz/{OutChannels}ch/s16");
            return true;
        }

        // ---------- Helpers ----------

        private static string AvStrError(int ret) {
            const int BufSize = 256;
            byte* buf = stackalloc byte[BufSize];
            ffmpeg.av_strerror(ret, buf, (ulong)BufSize);
            return new string((sbyte*)buf);
        }

        private struct QueuedPacket {
            public byte[] Data;
            public long PtsMs;
            public long Epoch;   // sync epoch at submit time (see OnPcm)
        }
    }
}
