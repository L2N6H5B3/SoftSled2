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

        // -------------------------------------------------------------
        //  Extensions for the Start-payload + spec-event work (Phase 2)
        // -------------------------------------------------------------
        //
        //  MS-DMCT's Start request carries four parameters we previously
        //  parsed-and-dropped: StartTime, RequestedPlayRate, AvailableBandwidth,
        //  UseOptimisedPreroll. The handler now pushes each into the
        //  controller via these methods so the playback engine can apply them
        //  to FFME and/or the underlying RTSP session.
        //
        //  The three additional events (RtspDisconnected/PtsError/
        //  UnrecoverableSkew) cover the MediaEvent codes the original
        //  MediaFailed event couldn't distinguish — see [MS-DMCT]
        //  2.2.2.1.2.3 / .4 / .5.

        /// <summary>Seek to the given absolute media position. Best-effort:
        /// for live RTSP streams FFME's internal Seek is a no-op and the
        /// real reposition happens via <c>RTSPClient.Play(startMs)</c>
        /// (Range: npt=...-). Safe to call before the pipeline opens —
        /// implementations stash the request and apply it at MediaOpened.</summary>
        Task SeekAsync(TimeSpan position);

        /// <summary>Change playback rate. 1.0 = normal. Values in
        /// [0.5, 2.0] are applied to FFME's <c>SpeedRatio</c>; values
        /// outside that range are deferred to the RTSP server (PLAY with
        /// Scale: / Speed: headers). Negative values are reverse —
        /// not supported by FFME, RTSP-only.</summary>
        Task SetRateAsync(double rate);

        /// <summary>Hint from WMC about the available downstream
        /// bandwidth. Influences <c>Buffer-Info.dlna.org</c> header in
        /// the next RTSP SETUP/SET_PARAMETER. Fire-and-forget.</summary>
        void SetAvailableBandwidth(long bitsPerSecond);

        /// <summary>Hint from WMC asking us to skip the buffer-fullness
        /// ramp for a faster first frame at the cost of robustness.
        /// Sets BFR=0;BTM=0 in the next <c>Buffer-Info.dlna.org</c>
        /// header. Fire-and-forget.</summary>
        void SetOptimisedPreroll(bool optimised);

        /// <summary>Fires when the RTSP socket disconnects unexpectedly
        /// (TCP exception, UDP receive failure, missed keepalives, BYE
        /// from the server on the data stream). Maps to MS-DMCT
        /// <c>RTSP_DISCONNECT</c> (code 3).</summary>
        event Action<Exception> RtspDisconnected;

        /// <summary>Fires when a PTS jump on the wire exceeds the spec
        /// thresholds (&lt; -200ms or &gt; 2000ms vs. previous PTS on
        /// the same stream). Maps to MS-DMCT <c>PTS_ERROR</c> (code 5).</summary>
        event Action<PtsErrorInfo> PtsError;

        /// <summary>Fires when the decoder takes more than 500ms to
        /// open, OR when the audio↔video first-sample skew exceeds
        /// 3500ms after start/seek. Maps to MS-DMCT
        /// <c>UNRECOVERABLE_SKEW</c> (code 6).</summary>
        event Action<SkewInfo> UnrecoverableSkew;

        /// <summary>Bind (or unbind, with null) the live RTSP session.
        /// The controller forwards Seek/SetRate/SetBufferInfo to it and
        /// subscribes to its Disconnected/PtsError/UnrecoverableSkew
        /// events so they propagate as <see cref="RtspDisconnected"/>
        /// / <see cref="PtsError"/> / <see cref="UnrecoverableSkew"/>.
        ///
        /// Called from AvCtrlHandler immediately after constructing the
        /// RTSPClient in OpenMedia, and again with null in CloseMedia.
        /// </summary>
        void AttachRtspClient(SoftSled.Components.RTSP.RTSPClient client);
    }
}
