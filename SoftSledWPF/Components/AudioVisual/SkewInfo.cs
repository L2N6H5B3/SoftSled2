using System.Diagnostics;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// Payload for <see cref="IMediaController.UnrecoverableSkew"/>.
    ///
    /// MS-DMCT §2.2.2.1.2.5 UNRECOVERABLE_SKEW is raised in two
    /// distinct situations:
    /// <list type="bullet">
    ///   <item><c>DecoderOpenTooSlow</c> — the decoder took longer than
    ///   500ms to open after the first sample was handed in. WMC reads
    ///   this as "this extender is too slow to keep up with the live
    ///   stream timeline".</item>
    ///   <item><c>FirstSampleAvSkew</c> — on the first frame after a
    ///   fresh PLAY or a seek, the audio and video presentation
    ///   timestamps are more than 3500ms apart. WMC reads this as
    ///   "A/V cannot be brought into sync".</item>
    /// </list>
    /// </summary>
    [DebuggerDisplay("{TheCause} value={ValueMs}ms")]
    public readonly struct SkewInfo {

        public enum Cause {
            /// <summary>Time between first SubmitChunk and MediaOpened
            /// exceeded 500ms.</summary>
            DecoderOpenTooSlow,

            /// <summary>|video first-sample PTS − audio first-sample PTS|
            /// exceeded 3500ms after a fresh start / seek.</summary>
            FirstSampleAvSkew,
        }

        public readonly Cause TheCause;

        /// <summary>For <see cref="Cause.DecoderOpenTooSlow"/>: the open
        /// elapsed time. For <see cref="Cause.FirstSampleAvSkew"/>: the
        /// absolute A/V delta in ms.</summary>
        public readonly long ValueMs;

        public SkewInfo(Cause cause, long valueMs) {
            TheCause = cause;
            ValueMs = valueMs;
        }
    }
}
