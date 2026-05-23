using SoftSled.Components.Utility;

namespace SoftSled.Components.AudioVisual.FormatStructures {
    public class VIDEOINFOHEADER2 {
        public RECT rcSource { get; set; }
        public RECT rcTarget { get; set; }
        public int dwBitRate { get; set; }
        public int dwBitErrorRate { get; set; }
        public long AvgTimePerFrame { get; set; }
        public int dwInterlaceFlags { get; set; }
        public int dwCopyProtectFlags { get; set; }
        public int dwPictAspectRatioX { get; set; }
        public int dwPictAspectRatioY { get; set; }
        public int dwReserved1 { get; set; } // Also known as dwControlFlags
        public int dwReserved2 { get; set; }
        public byte[] bmiHeader { get; set; }

        public VIDEOINFOHEADER2(string formatData) {
            byte[] bytes = DataUtilities.HexStringToByteArray(formatData);
            rcSource = new RECT {
                left = DataUtilities.Get4ByteInt(bytes, 0, false),
                top = DataUtilities.Get4ByteInt(bytes, 4, false),
                right = DataUtilities.Get4ByteInt(bytes, 8, false),
                bottom = DataUtilities.Get4ByteInt(bytes, 12, false)
            };
            rcTarget = new RECT {
                left = DataUtilities.Get4ByteInt(bytes, 16, false),
                top = DataUtilities.Get4ByteInt(bytes, 20, false),
                right = DataUtilities.Get4ByteInt(bytes, 24, false),
                bottom = DataUtilities.Get4ByteInt(bytes, 28, false)
            };
            dwBitRate = DataUtilities.Get4ByteInt(bytes, 32, false);
            dwBitErrorRate = DataUtilities.Get4ByteInt(bytes, 36, false);
            AvgTimePerFrame = DataUtilities.Get8ByteInt(bytes, 42, false);
            dwInterlaceFlags = DataUtilities.Get4ByteInt(bytes, 46, false);
            dwCopyProtectFlags = DataUtilities.Get4ByteInt(bytes, 50, false);
            dwPictAspectRatioX = DataUtilities.Get4ByteInt(bytes, 54, false);
            dwPictAspectRatioY = DataUtilities.Get4ByteInt(bytes, 58, false);
            dwReserved1 = DataUtilities.Get4ByteInt(bytes, 62, false);
            dwReserved2 = DataUtilities.Get4ByteInt(bytes, 66, false);
            bmiHeader = DataUtilities.GetByteSubArray(bytes, 70, bytes.Length-70);
        }

        public class RECT {
            public long left;
            public long top;
            public long right;
            public long bottom;
        }
    }
}
