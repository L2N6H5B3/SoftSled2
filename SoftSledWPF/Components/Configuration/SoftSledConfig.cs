using System.Collections.Generic;

namespace SoftSled.Components.Configuration {

    /// <summary>
    /// Which playback engine to use for AV pipeline. Three options:
    /// <list type="bullet">
    ///   <item><c>Ffme</c> — Unosquare.FFME-based MediaElement +
    ///   MPEG-PS/TS muxer + AsfFfmeInputStream + AudioSilenceInjector.
    ///   The proven legacy path.</item>
    ///   <item><c>DirectLibAv</c> — SoftSledPlaybackEngine: per-stream
    ///   libav decoders + WPF or D3DImage renderer + NAudio output.
    ///   No muxer roundtrip; finer trick-play control.</item>
    ///   <item><c>MediaFoundation</c> — SharpDX.MediaFoundation
    ///   MediaEngine consuming a ByteStream from the existing AsfStreamProducer.
    ///   Uses Windows' built-in hardware-accelerated decoders +
    ///   MediaEngine's audio renderer; frames rendered via
    ///   TransferVideoFrame into a D3DImage. Experimental.</item>
    /// </list>
    /// </summary>
    public enum PlaybackEngineKind {
        Ffme = 0,
        DirectLibAv = 1,
        MediaFoundation = 2,
    }

    public class SoftSledConfig {
        public bool IsPaired = false;
        public string DeviceUDN = "";
        public string RdpLoginHost = "";
        public string RdpLoginUserName = "";
        public string RdpLoginPassword = "";
        public bool EnableRemoteRendering = true;
        public bool EnableOverscanMargin = false;
        public bool Enable2DAnimations = true;
        // Default OFF: intense animations are only used when Remote Rendering
        // is off (GDI), where every animation frame streams over RDP and can
        // overwhelm the link. "Minimal" (2D on, Intense off) is the safer
        // default and what the setup wizard recommends.
        public bool EnableIntenseAnimations = false;

        // True once the first-run setup wizard (FirstRunSetupPage) has been
        // completed or skipped. The shell shows the wizard on launch while
        // this is false; the wizard sets it true on finish. Re-runnable any
        // time from the first item in Settings.
        public bool InitialSetupComplete = false;

        // Drives the MS-MCCAP "TVS" capability ("Is a TV skin used?") in
        // ExtenderCapabilities — tells WMC to present its 10-foot TV skin.
        // Set by the setup wizard when the user says they're on a TV.
        public bool UseTvSkin = false;

        // Shell-level UI preferences (read by ShellWindow at startup).
        // RunFullScreen: launch the shell maximized with no window
        // decorations (WindowStyle=None). F11 toggles this at runtime
        // without rewriting the persisted value.
        // AutoStartWmcOnOpen: when paired, immediately jump from the
        // landing page into the Extender session on launch (skips the
        // 'Start Extender' menu click). Has no effect when unpaired —
        // the user always has to confirm the pairing flow.
        public bool RunFullScreen = true;
        public bool AutoStartWmcOnOpen = false;

        // When true, shutting down the WMC session (host-initiated
        // disconnect or session-failed) closes the SoftSled application
        // instead of returning to the landing page. ESC always returns
        // to landing regardless — this only governs the host-driven
        // disconnect path so a user navigating back doesn't accidentally
        // quit.
        public bool CloseOnWmcClose = false;

        public bool EnableHdContent = true;

        // UI experience toggles. Surfaced in the new 'UI' sub-page in
        // settings and read by the WMC devcaps handshake / future
        // SoftSled-side UI behaviour.
        public bool EnableUiSounds = true;
        public bool EnablePopups   = true;
        public bool EnableToolbar  = true;

        // When false, WPF mouse events on the RDP display surface are not
        // forwarded into the session. Useful when running on a laptop whose
        // touchpad fires near the screen edge, or for couch-only use where
        // an accidental click would interrupt navigation. Keyboard is
        // unaffected. Default ON.
        public bool EnableMouseInput = true;

        // When true, the on-screen log overlay (loggerTextBox in
        // ExtenderSessionControl) is visible at session start. Independently
        // toggleable at runtime via Ctrl+L. Default OFF — the overlay is
        // a developer diagnostic, not something the average couch user
        // wants on top of the WMC UI.
        public bool EnableLogger = false;

        // Play splash-channel UI sound effects (Sound_Play / SoundBuffer_LoadSoundData
        // from MS-RRSP2 §2.2.4.19/§2.2.4.20). WMC's MCE shell ships
        // navigation clicks / focus chimes / error sounds through the splash
        // channel as well as through the RDP fast-path 0x0D audio updates
        // that WmcFastpathAudioPlayer already plays. Both channels may carry
        // the same sounds — enabling this can produce audible doubling.
        // Default ON so newly-implemented UI feedback is heard; flip OFF if
        // every navigation click sounds twice.
        public bool EnableSplashAudio = true;

        // Per-virtual-channel + fastpath log toggles. Each gates whether
        // the corresponding handler's diagnostic output reaches the
        // logger (and the on-screen overlay). Persisted only — no
        // call sites consume these yet; the handlers still log
        // unconditionally. Wire-up is intentionally deferred until the
        // per-channel volume becomes a problem worth filtering. Default
        // OFF so a future enable doesn't surprise users with a wall of
        // text.
        public bool LogDevCapsChannel = false;
        public bool LogMcxSessChannel = false;
        public bool LogAvCtrlChannel  = false;
        public bool LogRdpFastpath    = false;

        // Gates the A/V playback log group: FFME (Media.Open/Close, MediaOpened/Failed),
        // the FFME and external-sync controllers, the libav decoder throughput,
        // the [zoom] mode log, and the [surface-router] info lines. Defaulted ON
        // so existing behaviour is preserved when users upgrade; flip OFF when
        // isolating splash-channel diagnostics from playback noise. Unlike the
        // per-VC channel toggles above, this one defaults true because A/V
        // events were always emitted before this toggle existed.
        public bool LogAvPlayback     = true;

        // Mirror every textbox-logger line to a timestamped file under
        // LogFileDirectory. Critical for diagnosing release-build crashes
        // on remote machines where the on-screen overlay is unreachable
        // (the app may close before any session UI is up). Defaulted ON
        // because the cost is negligible (a few KB/s, auto-flushed) and
        // a captured log is the difference between "we know what crashed"
        // and "please reproduce while I shoulder-surf".
        public bool   LogToFile        = true;
        // Directory the per-session log file is created in. Empty string
        // means "use the platform default" — %LocalAppData%/SoftSled/Logs
        // on Windows. Resolved at session start; changes take effect on
        // the NEXT session, not mid-flight.
        public string LogFileDirectory = "";
        // Directory the advanced diagnostic dumps (splash raw bytes,
        // fastpath payloads, audio PCM) are written under. Empty string
        // means "use the platform default" — %LocalAppData%/SoftSled/Dumps.
        // Changes take effect on the NEXT session start (the dump dirs
        // are pushed into env vars at session-start time).
        public string DumpsDirectory   = "";

        // ---- Advanced / env-var-driven diagnostics --------------------
        //
        // Each of these mirrors a SOFTSLED_* environment variable that
        // call sites already check at session-start time. At session
        // start, ExtenderSessionControl applies these into the process-
        // scope env vars so existing consumers (RtspWireDumper.cs,
        // SplashRawDumper.cs, WmcFastpathAudioPlayer.cs, etc.) don't
        // need to change. Path-style dumps auto-route to
        // %LocalAppData%/SoftSled/Dumps/<x>; the Debugging page surfaces
        // an "Open" button so you can find them without typing the
        // path.
        //
        // Mutual relationship with the env vars themselves: the config
        // values OVERRIDE any pre-existing env var when the session
        // starts. So a checkbox click in the GUI always wins over a
        // `set SOFTSLED_X=1` in the shell. If you need shell-set vars
        // to win, leave the GUI checkbox UNticked — we only WRITE the
        // env var when the box is ticked.

        // Dump the splash MS-RRSP2 wire bytes and the decoded event
        // log to disk (mirrors SOFTSLED_SPLASH_RAW_DUMP). Default OFF
        // because the wire dump is big (~60 MB for a 30-minute session).
        public bool EnableSplashRawDump   = false;
        // Dump the RDP fastpath raw payloads (mirrors
        // SOFTSLED_FASTPATH_RAW_DUMP). Useful for the GDI-mode overlay
        // diagnostics. Off by default.
        public bool EnableFastpathRawDump = false;
        // Dump fastpath audio PCM payloads as per-slot WAV files
        // (mirrors SOFTSLED_AUDIO_DUMP). Off by default.
        public bool EnableAudioDump       = false;
        // Wire-level dump of every RTSP request and response on the
        // control channel (mirrors SOFTSLED_RTSP_WIRE_DUMP). Off by
        // default — the dump grows with session activity but is
        // small per request.
        public bool EnableRtspWireDump    = false;
        // Verbose tracing of the fastpath audio decoder state machine
        // (mirrors SOFTSLED_AUDIO_TRACE). Off by default — gets
        // noisy when audio is actively playing.
        public bool EnableAudioTrace      = false;
        // Route RTSP audio through NAudio instead of the FFME path
        // (mirrors SOFTSLED_AUDIO_VIA_NAUDIO). Diagnostic / fallback
        // for audio-stack troubleshooting. Off by default.
        public bool EnableAudioViaNAudio  = false;

        // Initial desktop / session resolution requested from the RDP
        // server. Picked from the resolution overlay in the Video sub-
        // page.
        public int SessionWidth  = 1280;
        public int SessionHeight = 720;

        // Manual A/V sync offset, in milliseconds. Applied by the
        // (Phase 2) sync controller to compensate for downstream
        // audio/video latency that's outside our pipeline:
        //   * HDMI displays often add 30-100 ms of video processing
        //     lag (TV picture processing, motion smoothing, scaler)
        //     that audio doesn't see.
        //   * AVRs (A/V receivers) sometimes add their own audio
        //     delay for DSP processing.
        //
        // Positive value = "audio is later than video" → controller
        // delays audio output by this many ms to match.
        // Negative value = "audio is earlier than video" → controller
        // delays video instead (via FFME SpeedRatio nudges) to wait
        // for audio.
        //
        // Range clamped at ±250 ms — beyond that any pipeline
        // problem is structural rather than offsettable.
        // Phase 1: stored only, not yet applied. The Phase 2 sync
        // controller will consume this in its drift-correction loop.
        public int AudioSyncOffsetMs = 0;

        // Video jitter-buffer depth (ms) for the libav + D3DImage player.
        // Larger values buffer more decoded video to absorb bursty RTSP
        // delivery (the wire delivers in ~160 ms bursts around I-frames),
        // smoothing judder at the cost of more start-up latency. Feeds
        // PtsFramePacer's pre-roll / max-buffer. Default 250 ms; clamped
        // 0–4000 ms in the UI. Takes effect on the next playback.
        public int VideoJitterBufferMs = 250;

    }
}
