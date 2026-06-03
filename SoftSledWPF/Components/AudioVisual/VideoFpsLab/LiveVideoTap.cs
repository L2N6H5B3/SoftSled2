using System;

namespace SoftSled.Components.AudioVisual.VideoFpsLab {

    /// <summary>
    /// Process-wide tap that lets the Video FPS Lab observe the live RTSP
    /// video elementary stream without holding a reference to the per-session
    /// <c>RTSPClient</c> (which is created on demand inside the AvCtrl handler).
    ///
    /// <para><c>RTSPClient</c> calls <see cref="Publish"/> for each video MAU;
    /// it is a cheap no-op (one null check) when the lab isn't listening, so
    /// leaving the call in the hot path costs nothing in normal sessions.</para>
    ///
    /// <para>This is a diagnostic hook for the lab only. The eventual Stage-1
    /// integration will route video through a push decoder directly rather
    /// than via this tap.</para>
    /// </summary>
    public static class LiveVideoTap {

        /// <summary>Fires per live video MAU: (elementaryStreamBytes, rtpTimestamp,
        /// wireCodec). The codec string is the committed wire codec
        /// (e.g. "X-WMF-PF" = H.264, "VND.MS.WM-MPV" = MPEG-1/2 video).
        /// Handlers run on the RTSP depacketizer thread and must not block.</summary>
        public static event Action<byte[], uint, string> Frame;

        /// <summary>True when at least one consumer is attached.</summary>
        public static bool HasSubscribers => Frame != null;

        public static void Publish(byte[] data, uint rtpTs, string wireCodec) {
            var handler = Frame;
            if (handler == null) return;
            try { handler(data, rtpTs, wireCodec); }
            catch { /* a lab consumer must never break the RTSP receive loop */ }
        }
    }
}
