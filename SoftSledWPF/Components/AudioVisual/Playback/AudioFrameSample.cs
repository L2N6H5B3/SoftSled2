namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// One decoded chunk of audio in S16 interleaved format ready for
    /// NAudio. Produced by <see cref="AudioDecoder"/>, consumed by
    /// <see cref="NAudioRenderer"/>.
    ///
    /// S16 interleaved chosen because NAudio's <c>BufferedWaveProvider</c>
    /// with a <c>WaveFormat(int sampleRate, int bits, int channels)</c>
    /// constructor produces an LPCM 16-bit interleaved sink — the exact
    /// format <c>WmrptPcmNAudioSink</c> already uses successfully.
    /// </summary>
    internal sealed class AudioFrameSample {
        public long PresentationMs;
        public int SampleRate;       // Hz — matches the renderer's WaveFormat
        public int Channels;         // 1 or 2 (we downmix > stereo at decode time)
        public byte[] PcmS16Le;      // little-endian signed 16-bit, interleaved
    }
}
