namespace SoftSled.Components.AudioVisual.FormatStructures {
    public class AM_Media_Format {
        public const string GUID_WAVEFORMATEX = "05589F81-C356-11CE-BF01-00AA0055595A";
        public const string GUID_VIDEOINFOHEADER = "05589F80-C356-11CE-BF01-00AA0055595A";
        public const string GUID_VIDEOINFOHEADER2 = "F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA";

        public string MajorType { get; set; }
        public string FixedSizeSamples { get; set; }
        public string TemporalCompression { get; set; }
        public string SampleSize { get; set; }
        public string FormatType { get; set; }
        public dynamic FormatData { get; set; }

        public AM_Media_Format(string format) {
            // Split the Format String into Segments
            string[] formatSegments = format.Split('/');
            // Set Format Info
            MajorType = formatSegments[0];
            FixedSizeSamples = formatSegments[1];
            TemporalCompression = formatSegments[2];
            SampleSize = formatSegments[3];
            FormatType = formatSegments[4].ToUpper();
            // Populate FormatData
            PopulateFormatData(formatSegments[5]);
        }

        public void PopulateFormatData(string formatData) {
            switch (FormatType) {
                case GUID_WAVEFORMATEX:
                    FormatData = new WAVEFORMATEX(formatData);
                    break;
                case GUID_VIDEOINFOHEADER:
                    FormatData = new VIDEOINFOHEADER(formatData);
                    break;
                case GUID_VIDEOINFOHEADER2:
                    FormatData = new VIDEOINFOHEADER2(formatData);
                    break;
            };
        }
    }
}
