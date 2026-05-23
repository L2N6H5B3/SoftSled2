using FFmpeg.AutoGen;
using SoftSled.Components.Diagnostics;
using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Audio specialization of <see cref="LibAvDecoder"/>. Each decoded
    /// frame is converted to S16 interleaved at a target sample rate / channel
    /// count (specified at construction) via <c>swr_convert</c>, then
    /// handed to the renderer.
    ///
    /// Target format chosen up-front (rather than passed-through from the
    /// decoder's native sample_fmt) so the <see cref="NAudioRenderer"/>'s
    /// <c>WaveFormat</c> stays stable for the life of the session — NAudio
    /// can't change format on a live <c>WaveOutEvent</c> without tearing
    /// the device down and back up.
    ///
    /// For raw PCM passthrough (codec_id = PCM_S16LE with matching target),
    /// libav's PCM decoder is a near-no-op (validates + emits the sample
    /// data unchanged); the resampler then just memcpy's the planes.
    /// </summary>
    internal sealed unsafe class AudioDecoder : LibAvDecoder {

        private readonly Action<AudioFrameSample> _onFrame;
        private readonly int _targetSampleRate;
        private readonly int _targetChannels;
        // For raw-PCM codecs (PCM_S16LE etc) we need to set sample_rate /
        // channels / sample_fmt on the codec context BEFORE avcodec_open2,
        // because the codec carries no in-band header.
        private readonly int _srcSampleRate;
        private readonly int _srcChannels;
        private readonly AVSampleFormat _srcSampleFmt;

        private SwrContext* _swr;
        private int _swrSrcSampleRate, _swrSrcChannels;
        private AVSampleFormat _swrSrcFmt = AVSampleFormat.AV_SAMPLE_FMT_NONE;

        /// <param name="codecId">libav decoder identifier.</param>
        /// <param name="extradata">Codec extradata (WAVEFORMATEX cbSize-relevant bytes
        /// for some WMA variants; null for most MPEG audio).</param>
        /// <param name="srcSampleRate">For raw PCM codecs, the wire sample rate
        /// (from WAVEFORMATEX). For container-described codecs, pass 0 — the
        /// decoder discovers it from the first frame.</param>
        /// <param name="srcChannels">For raw PCM codecs, the wire channel count.
        /// 0 for container-described.</param>
        /// <param name="srcSampleFmt">For raw PCM codecs, the wire sample format.
        /// AV_SAMPLE_FMT_NONE for container-described.</param>
        /// <param name="targetSampleRate">Renderer-stable output rate (Hz).</param>
        /// <param name="targetChannels">Renderer-stable output channel count.</param>
        public AudioDecoder(AVCodecID codecId, byte[] extradata,
                            int srcSampleRate, int srcChannels, AVSampleFormat srcSampleFmt,
                            int targetSampleRate, int targetChannels,
                            Action<AudioFrameSample> onFrame, Logger log)
            : base(codecId, extradata, "audio", log) {
            _srcSampleRate = srcSampleRate;
            _srcChannels = srcChannels;
            _srcSampleFmt = srcSampleFmt;
            _targetSampleRate = targetSampleRate;
            _targetChannels = targetChannels;
            _onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
        }

        protected override void ConfigureContext(AVCodecContext* ctx) {
            if (_srcSampleRate > 0) ctx->sample_rate = _srcSampleRate;
            if (_srcChannels > 0) {
                ctx->channels = _srcChannels;
                ctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(_srcChannels);
            }
            if (_srcSampleFmt != AVSampleFormat.AV_SAMPLE_FMT_NONE) {
                ctx->sample_fmt = _srcSampleFmt;
            }
        }

        protected override void OnFrameDecoded(AVFrame* frame, long ptsMs) {
            int srcRate = frame->sample_rate;
            int srcCh = frame->channels;
            AVSampleFormat srcFmt = (AVSampleFormat)frame->format;
            int srcSamples = frame->nb_samples;
            if (srcRate <= 0 || srcCh <= 0 || srcSamples <= 0) return;

            EnsureResampler(srcRate, srcCh, srcFmt);
            if (_swr == null) return;

            // Output buffer sizing. Worst case: rate ratio < 1 (no expansion
            // beyond srcSamples). Add a margin for swr's internal delay.
            long dstSamples = ffmpeg.av_rescale_rnd(
                ffmpeg.swr_get_delay(_swr, srcRate) + srcSamples,
                _targetSampleRate, srcRate, AVRounding.AV_ROUND_UP);

            int dstBytes = (int)dstSamples * _targetChannels * 2;  // S16 = 2 bytes/sample
            byte[] outBuf = new byte[dstBytes];

            int produced;
            fixed (byte* outPtr = outBuf) {
                byte** dstPlanes = stackalloc byte*[1];
                dstPlanes[0] = outPtr;
                produced = ffmpeg.swr_convert(_swr,
                    dstPlanes, (int)dstSamples,
                    frame->extended_data, srcSamples);
            }
            if (produced <= 0) return;

            int producedBytes = produced * _targetChannels * 2;
            byte[] copy;
            if (producedBytes == dstBytes) {
                copy = outBuf;
            } else {
                copy = new byte[producedBytes];
                Buffer.BlockCopy(outBuf, 0, copy, 0, producedBytes);
            }

            _onFrame(new AudioFrameSample {
                PresentationMs = ptsMs,
                SampleRate = _targetSampleRate,
                Channels = _targetChannels,
                PcmS16Le = copy,
            });
        }

        private void EnsureResampler(int srcRate, int srcCh, AVSampleFormat srcFmt) {
            if (_swr != null && srcRate == _swrSrcSampleRate &&
                srcCh == _swrSrcChannels && srcFmt == _swrSrcFmt) {
                return;
            }
            if (_swr != null) {
                SwrContext* tmp = _swr;
                ffmpeg.swr_free(&tmp);
                _swr = null;
            }
            long dstLayout = ffmpeg.av_get_default_channel_layout(_targetChannels);
            long srcLayout = ffmpeg.av_get_default_channel_layout(srcCh);
            _swr = ffmpeg.swr_alloc_set_opts(null,
                dstLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, _targetSampleRate,
                srcLayout, srcFmt, srcRate,
                0, null);
            if (_swr == null) {
                Log?.LogError($"[libav-audio] swr_alloc_set_opts failed");
                return;
            }
            int ret = ffmpeg.swr_init(_swr);
            if (ret < 0) {
                Log?.LogError($"[libav-audio] swr_init failed: {AvStrError(ret)}");
                SwrContext* tmp = _swr;
                ffmpeg.swr_free(&tmp);
                _swr = null;
                return;
            }
            _swrSrcSampleRate = srcRate;
            _swrSrcChannels = srcCh;
            _swrSrcFmt = srcFmt;
        }

        public override void Dispose() {
            if (_swr != null) {
                SwrContext* tmp = _swr;
                ffmpeg.swr_free(&tmp);
                _swr = null;
            }
            base.Dispose();
        }
    }
}
