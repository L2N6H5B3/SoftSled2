using System;
using System.Threading.Tasks;

namespace SoftSled.Components.AudioVisual {

    /// <summary>
    /// UI-agnostic playback surface used by <c>VirtualChannelAvCtrlHandler</c>
    /// to query position/duration and drive Play/Pause for the current media
    /// session. Lets the AvCtrl handler stay independent of FFME/WPF.
    ///
    /// Backed by <see cref="FfmeMediaController"/> when the recording opens
    /// a video pipeline through FFME. The audio-only RFC 2250 MP3 path
    /// (music files via NAudio) deliberately leaves this null, in which
    /// case AvCtrlHandler falls back to its stopwatch-based position
    /// estimate as before.
    ///
    /// Position semantics: <see cref="Position"/> is the elapsed media
    /// time since FFME opened the stream — for an RTSP-sourced recording
    /// that's the player-local clock, NOT the absolute file timeline. The
    /// AvCtrl handler combines this with the WMC-supplied
    /// <c>StartPayloadStartTime</c> to derive the absolute position WMC
    /// expects to see.
    /// </summary>
    internal interface IMediaController {

        /// <summary>Current media time. <see cref="TimeSpan.Zero"/> before
        /// the first frame is decoded.</summary>
        TimeSpan Position { get; }

        /// <summary>Total duration if known, <c>null</c> for live / unbounded
        /// streams. WMC's recorded-TV path advertises <c>a=range:npt=0-</c>
        /// (unbounded) so this is typically null there.</summary>
        TimeSpan? Duration { get; }

        /// <summary>True if FFME currently has a media session open. False
        /// before <c>Media.Open</c> succeeds or after <c>Media.Close</c>.</summary>
        bool IsOpen { get; }

        /// <summary>Resume playback. Must be safe to call from any thread —
        /// implementations marshal to their dispatcher as needed.</summary>
        Task PlayAsync();

        /// <summary>Pause playback. Same thread-safety contract as
        /// <see cref="PlayAsync"/>.</summary>
        Task PauseAsync();

        /// <summary>Fires when the player transitions from "buffering" to
        /// "ready to present". WMC expects this as <c>BUFFERING_STOP</c>
        /// (DMCT MediaEvent code 1) — signals the UI to dismiss its
        /// "loading" indicator and start polling GetPosition.</summary>
        event Action BufferingEnded;

        /// <summary>Fires when the underlying stream reaches its natural
        /// end. Triggers WMC's <c>END_OF_MEDIA</c> event (code 2).</summary>
        event Action MediaEnded;

        /// <summary>Fires when the player encounters an unrecoverable
        /// error. AvCtrlHandler maps this to <c>PTS_ERROR</c> (code 5)
        /// or <c>RTSP_DISCONNECT</c> (code 3) depending on the exception
        /// — both surface to WMC as a playback failure.</summary>
        event Action<Exception> MediaFailed;
    }
}
