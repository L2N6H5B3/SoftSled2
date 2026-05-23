namespace SoftSled.Components.AudioVisual.Playback {

    /// <summary>
    /// One decoded video frame, converted to BGRA32 ready for a
    /// <c>WriteableBitmap</c> blit. Produced by <see cref="VideoDecoder"/>,
    /// consumed by <see cref="WpfVideoRenderer"/>.
    ///
    /// The pixel buffer is a heap-allocated managed byte[] so it's safe to
    /// hand across threads without worrying about libav's frame pool —
    /// the libav AVFrame's lifetime ends inside <c>OnFrameDecoded</c>.
    /// BGRA32 chosen to match the <c>PixelFormats.Bgra32</c> format the
    /// existing FreeRDP rendering path uses (zero conversion at blit time).
    /// </summary>
    internal sealed class VideoFrameSample {
        public long PresentationMs;
        public int Width;
        public int Height;
        public int Stride;          // bytes per row, may be > Width*4 for padding
        public byte[] Bgra32;       // exactly Stride * Height bytes
    }
}
