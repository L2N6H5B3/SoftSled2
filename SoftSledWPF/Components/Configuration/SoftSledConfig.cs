using System.Collections.Generic;

namespace SoftSled.Components.Configuration {
    public class SoftSledConfig {
        public bool IsPaired = false;
        public string DeviceUDN = "";
        public string RdpLoginHost = "";
        public string RdpLoginUserName = "";
        public string RdpLoginPassword = "";
        public bool EnableRemoteRendering = true;
        public bool EnableOverscanMargin = false;
        public bool Enable2DAnimations = true;
        public bool EnableIntenseAnimations = true;

        // Shell-level UI preferences (read by ShellWindow at startup).
        // RunFullScreen: launch the shell maximized with no window
        // decorations (WindowStyle=None). F11 toggles this at runtime
        // without rewriting the persisted value.
        // AutoStartWmcOnOpen: when paired, immediately jump from the
        // landing page into the Extender session on launch (skips the
        // 'Start Extender' menu click). Has no effect when unpaired —
        // the user always has to confirm the pairing flow.
        public bool RunFullScreen = false;
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

        // External-sync mode — Phase 1 of the FFME-bypass audio
        // path. When true, audio decoding + rendering goes through
        // a libav + NAudio pipeline owned by
        // ExternalSyncMediaController instead of FFME. The audio
        // device's playback position becomes the master clock,
        // which (in later phases) the video pipeline will chase
        // via SpeedRatio nudges. Phase 1 only covers MP3-only
        // audio sessions; other content still uses the FFME path
        // and this flag has no effect there. Default OFF —
        // experimental.
        public bool UseExternalSyncMode = false;

        // Heuristic PiP routing: when active video is playing, the
        // splash controller sniffs for gradient-only ~16:9 Visuals in
        // the bottom-left of the screen and routes the video element
        // onto the first match. Works because WMC's MS-RRSP2 wire does
        // NOT actually emit VideoPool_Draw (spec §2.2.4.13.1, msgid 0)
        // on this corpus — WMC relies on the Xbox hardware-overlay
        // convention which we can't replicate. The heuristic
        // approximates the result by finding the placeholder Visual the
        // overlay WOULD have landed on.
        //
        // ---- DEFAULTED OFF — known-broken in two ways ----
        //   1. The right candidate often isn't what the heuristic
        //      catches. Captured corpus has THREE PiP-shaped Visuals
        //      per chrome rebuild: a 258×145 horizontal-gradient
        //      selector ring (the actually-visible focused tile) and
        //      a pair of 256×144 / 256×148 vertical-gradient
        //      placeholders in scrolled-off carousel rows whose
        //      parent-chain accumulates to negative Y. The Vertical-
        //      gradient filter targets the wrong tree.
        //   2. Z-order: the WPF Grid in ExtenderSessionControl.xaml
        //      puts splashHost ABOVE MediaCanvas, so the splash
        //      scene-graph's placeholder gradient is drawn ON TOP
        //      of the video element. Even if (1) were fixed, the
        //      user would see the gradient, not the video. Needs
        //      either alpha=0 on the locked Visual or a ZIndex
        //      promotion of MediaCanvas while a PIP is locked.
        //
        // The diagnostic events ([PIP-CAND], [PIP-LOCK], [PIP-UNLOCK])
        // and the VideoPipCandidateChanged hook stay live so future
        // work can iterate on a better discriminator without rewiring
        // the plumbing. Flip ON via the Debugging page to test new
        // heuristics; expect false-positive shrinks until both issues
        // above are addressed.
        public bool EnableSplashPipRouting = false;

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

    }
}
