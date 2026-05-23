using System.Diagnostics;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Payload for <see cref="IMediaController.PtsError"/>.
    ///
    /// MS-DMCT §2.2.2.1.2.4 PTS_ERROR is raised whenever the current
    /// presentation timestamp for a stream diverges from the previous
    /// one by more than the spec thresholds: behind by >200ms, or ahead
    /// by >2000ms. The detector lives inside the RTSP depacketizers so
    /// it fires the moment a sample is committed — independent of
    /// whether the decoder has caught up to it.
    /// </summary>
    [DebuggerDisplay("{IsAudio ? \"audio\" : \"video\"} prev={PreviousPtsMs} cur={CurrentPtsMs} delta={DeltaMs}")]
    public readonly struct PtsErrorInfo {

        /// <summary>True for the audio stream, false for video. The two
        /// streams have independent PTS timelines (separate SSRC and
        /// clock) so the previous-PTS check is per-stream.</summary>
        public readonly bool IsAudio;

        /// <summary>The PTS of the last committed sample on this stream
        /// (milliseconds, RTCP-SR-anchored absolute timeline).</summary>
        public readonly long PreviousPtsMs;

        /// <summary>The PTS of the sample that triggered the alarm.</summary>
        public readonly long CurrentPtsMs;

        /// <summary>Signed delta. Positive = sample is ahead of where we
        /// expected; negative = behind. The spec wants firing on
        /// delta &lt; -200ms (large backward jump) OR delta &gt; 2000ms
        /// (large forward jump).</summary>
        public readonly long DeltaMs;

        public PtsErrorInfo(bool isAudio, long previousPtsMs, long currentPtsMs) {
            IsAudio = isAudio;
            PreviousPtsMs = previousPtsMs;
            CurrentPtsMs = currentPtsMs;
            DeltaMs = currentPtsMs - previousPtsMs;
        }
    }
}
