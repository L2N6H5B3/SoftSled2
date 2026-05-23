using SoftSled.Components.Utility;

namespace SoftSled.Components.AudioVisual.FormatStructures {
    public class WAVEFORMATEX {
        public int wFormatTag { get; set; }
        public int nChannels { get; set; }
        public int nSamplesPerSec { get; set; }
        public int nAvgBytesPerSec { get; set; }
        public int nBlockAlign { get; set; }
        public int wBitsPerSample { get; set; }
        public int cbSize { get; set; }
        public byte[] extraFormatInfo { get; set; }

        public WAVEFORMATEX(string formatData) {
            byte[] bytes = DataUtilities.HexStringToByteArray(formatData);
            wFormatTag = DataUtilities.Get2ByteInt(bytes, 0, false);
            nChannels = DataUtilities.Get2ByteInt(bytes, 2, false);
            nSamplesPerSec = DataUtilities.Get4ByteInt(bytes, 4, false);
            nAvgBytesPerSec = DataUtilities.Get4ByteInt(bytes, 8, false);
            nBlockAlign = DataUtilities.Get2ByteInt(bytes, 12, false);
            wBitsPerSample = DataUtilities.Get2ByteInt(bytes, 14, false);
            cbSize = DataUtilities.Get2ByteInt(bytes, 16, false);
            extraFormatInfo = DataUtilities.GetByteSubArray(bytes, 18, cbSize);
        }
    }
}
