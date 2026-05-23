using FFmpeg.AutoGen;
using SoftSled.Components.AudioVisual.FormatStructures;
using System;

namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// Resolves a wire-codec descriptor (the codec string parsed from SDP
    /// <c>rtpmap</c>, plus the <c>AM_Media_Format</c> from the matching
    /// <c>fmtp</c> when present) into a <see cref="CodecPlan"/> the
    /// engine can hand to a libav decoder.
    ///
    /// Per the captured RTSP corpus, the codecs the WMC stack actually
    /// negotiates are:
    ///
    /// | Wire codec string | libav id | extradata | notes |
    /// |---|---|---|---|
    /// | VND.MS.WM-MPV     | MPEG2VIDEO | none | MPEG-1/2 video, in-band |
    /// | VND.MS.WM-MPA     | MP2 / MP3 | none | layer 1/2 from fmtp; in-band |
    /// | MPA (RFC 2250)    | MP3 | none | mp3 file source |
    /// | VND.MS.WM-AC3     | AC3 | none | in-band |
    /// | X-WMF-PF (video)  | H264 | from VIDEOINFOHEADER | bmiHeader FOURCC="H264" |
    /// | X-WMF-PF (audio)  | PCM_S16LE / MP3 / WMA | from WAVEFORMATEX | wFormatTag dispatch |
    ///
    /// Other variants (WMV1/2/3, AAC, WMV-DRM) are stubbed as
    /// "unsupported" — they'd be added when a real WMC session exercises
    /// them.
    /// </summary>
    internal static class CodecRegistry {

        /// <summary>What the engine needs to spin up a decoder for one
        /// committed wire codec.</summary>
        internal sealed class CodecPlan {
            public AVCodecID CodecId;
            public byte[] Extradata;            // may be null

            // Audio-only: when the wire-codec is a raw-PCM variant the
            // codec context needs sample_rate / channels / sample_fmt set
            // BEFORE avcodec_open2 (raw PCM has no in-band header). Zero
            // / NONE for container-described codecs.
            public int SrcSampleRate;
            public int SrcChannels;
            public AVSampleFormat SrcSampleFmt;

            public override string ToString() =>
                $"CodecPlan({CodecId}, extradata={(Extradata?.Length ?? 0)}B, " +
                $"src={SrcSampleRate}Hz/{SrcChannels}ch/{SrcSampleFmt})";
        }

        /// <summary>Resolve a video codec. Returns null if the wire codec
        /// isn't supported.</summary>
        public static CodecPlan ForVideo(string wireCodec, AM_Media_Format fmt,
                                         string fmtpFormatParameter) {
            if (string.IsNullOrEmpty(wireCodec)) return null;
            string c = wireCodec.ToUpperInvariant();

            if (c == "VND.MS.WM-MPV") {
                // MPEG-1/2 video. In-band sequence headers; no extradata.
                return new CodecPlan { CodecId = AVCodecID.AV_CODEC_ID_MPEG2VIDEO };
            }

            if (c == "X-WMF-PF") {
                // Inspect the format description. For video, the FOURCC
                // sits inside the VIDEOINFOHEADER's bmiHeader at byte
                // offset 16 (BITMAPINFOHEADER.biCompression).
                if (fmt != null && fmt.FormatData is VIDEOINFOHEADER vih
                    && vih.bmiHeader != null && vih.bmiHeader.Length >= 20) {
                    string fourcc = System.Text.Encoding.ASCII.GetString(vih.bmiHeader, 16, 4);
                    if (fourcc == "H264") {
                        return new CodecPlan { CodecId = AVCodecID.AV_CODEC_ID_H264 };
                    }
                }
                // Fallback: H264 is by far the most common x-wmf-pf video
                // in the corpus; assume it. If we're wrong libav will
                // fail to decode and we'll see a clear error in the log.
                return new CodecPlan { CodecId = AVCodecID.AV_CODEC_ID_H264 };
            }

            return null;
        }

        /// <summary>Resolve an audio codec. Returns null if unsupported.</summary>
        public static CodecPlan ForAudio(string wireCodec, AM_Media_Format fmt,
                                         string fmtpFormatParameter) {
            if (string.IsNullOrEmpty(wireCodec)) return null;
            string c = wireCodec.ToUpperInvariant();

            if (c == "MPA") {
                // RFC 2250 MP3.
                return new CodecPlan { CodecId = AVCodecID.AV_CODEC_ID_MP3 };
            }

            if (c == "VND.MS.WM-MPA") {
                // Layer from fmtp ("layer=1" / "layer=2" / "layer=3" =>
                // MP1 / MP2 / MP3). The captured corpus shows layer=1 and
                // layer=2; default to MP3 (covers the music-file MPA-MP3
                // case in the corpus too).
                int layer = ParseFmtpInt(fmtpFormatParameter, "layer", defaultValue: 0);
                AVCodecID id;
                switch (layer) {
                    case 1: id = AVCodecID.AV_CODEC_ID_MP1; break;
                    case 2: id = AVCodecID.AV_CODEC_ID_MP2; break;
                    case 3: id = AVCodecID.AV_CODEC_ID_MP3; break;
                    default: id = AVCodecID.AV_CODEC_ID_MP2; break;
                }
                return new CodecPlan { CodecId = id };
            }

            if (c == "VND.MS.WM-AC3") {
                return new CodecPlan { CodecId = AVCodecID.AV_CODEC_ID_AC3 };
            }

            if (c == "X-WMF-PF") {
                if (fmt != null && fmt.FormatData is WAVEFORMATEX wfx) {
                    int tag = wfx.wFormatTag;
                    AVCodecID id;
                    AVSampleFormat fmtId = AVSampleFormat.AV_SAMPLE_FMT_NONE;
                    byte[] extradata = null;
                    switch (tag) {
                        case 0x0001: // WAVE_FORMAT_PCM (linear)
                            id = wfx.wBitsPerSample == 16 ? AVCodecID.AV_CODEC_ID_PCM_S16LE
                               : wfx.wBitsPerSample == 24 ? AVCodecID.AV_CODEC_ID_PCM_S24LE
                               : wfx.wBitsPerSample == 32 ? AVCodecID.AV_CODEC_ID_PCM_S32LE
                               : AVCodecID.AV_CODEC_ID_PCM_S16LE;  // fallback
                            fmtId = AVSampleFormat.AV_SAMPLE_FMT_S16;
                            break;
                        case 0x0003: // WAVE_FORMAT_IEEE_FLOAT
                            id = AVCodecID.AV_CODEC_ID_PCM_F32LE;
                            fmtId = AVSampleFormat.AV_SAMPLE_FMT_FLT;
                            break;
                        case 0x0050: id = AVCodecID.AV_CODEC_ID_MP2; break;
                        case 0x0055: id = AVCodecID.AV_CODEC_ID_MP3; break;
                        case 0x2000: id = AVCodecID.AV_CODEC_ID_AC3; break;
                        case 0x0161: id = AVCodecID.AV_CODEC_ID_WMAV2;        extradata = wfx.extraFormatInfo; break;
                        case 0x0162: id = AVCodecID.AV_CODEC_ID_WMAPRO;       extradata = wfx.extraFormatInfo; break;
                        case 0x0163: id = AVCodecID.AV_CODEC_ID_WMALOSSLESS;  extradata = wfx.extraFormatInfo; break;
                        case 0x000A: id = AVCodecID.AV_CODEC_ID_WMAVOICE;     extradata = wfx.extraFormatInfo; break;
                        default:
                            return null;     // unsupported tag
                    }
                    return new CodecPlan {
                        CodecId = id,
                        Extradata = extradata,
                        SrcSampleRate = wfx.nSamplesPerSec,
                        SrcChannels = wfx.nChannels,
                        SrcSampleFmt = fmtId,
                    };
                }
                return null;
            }

            return null;
        }

        // Tiny fmtp helper. fmtp is "key=val;key=val;..." style.
        private static int ParseFmtpInt(string fmtp, string key, int defaultValue) {
            if (string.IsNullOrEmpty(fmtp)) return defaultValue;
            foreach (string token in fmtp.Split(';')) {
                string t = token.Trim();
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(t.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(t.Substring(eq + 1).Trim(), out int v)) return v;
                return defaultValue;
            }
            return defaultValue;
        }
    }
}
