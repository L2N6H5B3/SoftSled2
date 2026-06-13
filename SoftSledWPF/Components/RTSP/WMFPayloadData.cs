using SoftSled.Components.AudioVisual.FormatStructures;
using SoftSledWPF.Components.Utility;

namespace SoftSled.Components.RTSP {
    public class WMFPayloadData {
        public MediaType Type { get; set; }
        public string Codec { get; set; }
        public int PayloadNumber { get; set; }
        public string FormatParameter { get; set; }
        /// <summary>
        /// RTP timestamp clock in Hz, parsed from the rtpmap entry
        /// (e.g. <c>vnd.ms.wm-MPA/90000</c> → 90000, <c>x-wmf-pf/1000</c> → 1000).
        /// Used by container muxers to convert per-stream RTP timestamps
        /// to a canonical PTS clock. Defaults to 90000 if unknown.
        /// </summary>
        public int ClockHz { get; set; } = 90000;
        public string EncodingParameters { get; set; }
        public AM_Media_Format AM_Media_Format { get; set; }
    }
}
