namespace SoftSled.Components.AudioVisual.ExternalSync {

    /// <summary>
    /// Audio format details passed from <c>RTSPClient</c> to
    /// <see cref="ExternalSyncMediaController"/> when the wire codec
    /// first commits. Lets the controller pick the right libav
    /// decoder ID (e.g. MP2 vs MP3 for the MPA codec family) and
    /// pre-configure renderer state without having to wait for the
    /// first decoded frame.
    ///
    /// <para>All "Hint" fields are zero / null when not known from
    /// SDP; the decoder will fill them in from the first decoded
    /// frame and the controller resolves them lazily.</para>
    /// </summary>
    internal sealed class ExternalAudioFormat {
        /// <summary>Wire-codec string from
        /// <c>WMFPayloadData.Codec</c> — e.g. <c>"MPA"</c>,
        /// <c>"VND.MS.WM-MPA"</c>, <c>"X-WMF-PF"</c>.</summary>
        public string WireCodec { get; set; }

        /// <summary>MPEG audio layer from the SDP fmtp
        /// <c>layer=N</c> attribute (1, 2, or 3). 0 if not
        /// present. Used to pick between <c>AV_CODEC_ID_MP1/2/3</c>
        /// for the MPA codec family — MP3 (layer 3) is by far the
        /// common case for plain .mp3 source files, but recorded
        /// TV from WMC often carries MP2 (layer 2) audio.</summary>
        public int MpegLayer { get; set; }

        /// <summary>Sample rate hint from SDP fmtp <c>samplerate=N</c>
        /// (Hz). 0 if not specified — libav reads it from the
        /// first frame header anyway.</summary>
        public int SampleRateHint { get; set; }

        /// <summary>Channel count hint from SDP <c>rtpmap</c>
        /// or fmtp <c>mode=stereo</c>. 0 if not specified.</summary>
        public int ChannelsHint { get; set; }

        /// <summary>Bits per sample for raw PCM payloads. Parsed
        /// from the X-WMF-PF audio fmtp's <c>bitspersample=</c>
        /// token (8, 16, 24, 32 are all valid). 0 for non-PCM.</summary>
        public int BitsPerSampleHint { get; set; }

        /// <summary>True if the PCM payload is big-endian. Most WMC
        /// servers ship little-endian (Intel byte order), so the
        /// default false is right almost always — but the fmtp may
        /// say <c>codec=pcm_s16be</c> for the rare big-endian case.
        /// Ignored for non-PCM codecs.</summary>
        public bool PcmBigEndian { get; set; }

        /// <summary>WMA codec-private config (the SDP fmtp <c>config=</c> hex
        /// blob decoded to bytes) — required as libav extradata to open a WMA
        /// decoder. Null for non-WMA codecs.</summary>
        public byte[] ExtraData { get; set; }

        /// <summary>WMA compressed block size (SDP fmtp <c>blocksize=</c>) →
        /// decoder block_align. 0 if not WMA.</summary>
        public int BlockAlign { get; set; }

        /// <summary>Nominal bitrate (SDP fmtp <c>bitrate=</c>, bits/s) →
        /// decoder bit_rate. 0 if not specified.</summary>
        public int BitRate { get; set; }

        public override string ToString() =>
            $"{WireCodec}, layer={MpegLayer}, rate={SampleRateHint}Hz, ch={ChannelsHint}" +
            (BitsPerSampleHint > 0 ? $", bps={BitsPerSampleHint}{(PcmBigEndian ? "BE" : "LE")}" : "") +
            (BlockAlign > 0 ? $", blockAlign={BlockAlign}, bitRate={BitRate}, extra={(ExtraData?.Length ?? 0)}B" : "");
    }
}
