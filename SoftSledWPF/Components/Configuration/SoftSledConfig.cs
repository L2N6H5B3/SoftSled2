using System.Collections.Generic;

namespace SoftSled.Components.Configuration {

    /// <summary>How SoftSled launches when Windows starts. Persisted in config
    /// and applied to the HKCU Run key by StartupRegistration.</summary>
    public enum BootStartMode {
        Off = 0,
        Tray = 1,
        Ui = 2,
    }

    public class SoftSledConfig {
        public bool IsPaired = false;
        // Write-only BY DESIGN — nothing reads this back. ExtenderDevice
        // derives the live UDN from the UPnP device each launch and records
        // it here purely as a human-readable breadcrumb: the same uuid shows
        // up on the Windows host attached to the MCX account, so having it in
        // the config file is what lets you tell which account is ours.
        // Cleared on unpair. Don't "clean up" as dead config.
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

        // Constrain interactive window resizing to the aspect ratio of the
        // selected session resolution (SessionWidth : SessionHeight). The
        // shell content and the live RDP framebuffer are stretched Uniform,
        // so a window whose client area doesn't match that ratio shows black
        // bars on the top/bottom or sides. Locking the window's resize to the
        // ratio keeps the client area matched and removes the bars. Only
        // affects windowed mode — full-screen fills the monitor and isn't
        // resizable. Default ON.
        //
        // Deliberately NOT surfaced in the settings UI (removed by request) —
        // edit Config.xml to change it. Still read and honoured by ShellWindow
        // every time settings change, and it round-trips through the settings
        // page untouched because ConfigPage saves the config object it loaded.
        // Don't "clean up" as dead config.
        public bool LockWindowAspectRatio = true;

        // Media Playback Mode remote mappings. When ON, and the extender is
        // showing full-screen video with NO WMC UI over the centre of the screen
        // (i.e. actually watching, not in a menu/OSD), a few nav buttons are
        // remapped to transport controls: Left/Right = Skip Back/Forward,
        // OK/Enter = Play-Pause. The moment any UI covers the centre (seek bar,
        // menu, OSD) the mappings revert to normal so you can navigate. Default
        // ON.
        public bool MediaPlaybackModeEnabled = true;

        // Keep the display (and system) awake while SoftSled is running, so
        // the Windows screen-saver / display-off / sleep idle timers don't
        // blank the screen during long playback or while idling on a menu
        // with no input. Implemented via SetThreadExecutionState
        // (DisplayKeepAwake). Applied at app launch and toggled live from
        // the General settings page. Default ON — that's the behaviour a
        // 10-foot media-center client wants.
        public bool KeepScreenAwake = true;

        // When true, shutting down the WMC session (host-initiated
        // disconnect or session-failed) closes the SoftSled application
        // instead of returning to the landing page. ESC always returns
        // to landing regardless — this only governs the host-driven
        // disconnect path so a user navigating back doesn't accidentally
        // quit.
        public bool CloseOnWmcClose = false;

        // System-tray behaviour. When true, closing or minimizing the shell
        // window hides it to a tray icon instead of exiting, keeping the
        // process (and its Raw Input HWND) resident so the MCE remote's Green
        // Start button can wake it straight into a session while idle. Default
        // OFF — a plain launch behaves exactly as before. See BootStartMode
        // (a "boot to tray" launch implies this behaviour).
        public bool MinimizeToTray = false;

        // How SoftSled should launch when Windows starts, if at all. Written to
        // the HKCU ...\Run key by StartupRegistration when this changes.
        //   Off  = no boot auto-start (Run entry removed).
        //   Tray = start hidden in the system tray, idle, waiting for Green
        //          (implies MinimizeToTray). Launched with "--tray".
        //   Ui   = open the normal shell on screen at login.
        public BootStartMode BootStartMode = BootStartMode.Off;

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
        // logger: ExtenderSessionControl passes m_logger to the handler
        // when the toggle is on and null when it's off, so an unticked
        // box silences that channel at the source. Default OFF so the
        // log isn't a wall of text.
        public bool LogDevCapsChannel = false;
        public bool LogMcxSessChannel = false;
        public bool LogAvCtrlChannel  = false;
        public bool LogRdpFastpath    = false;
        // Gates the [rdp-fps] meter: one line/second giving the incoming WMC
        // frame rate (pre-coalescing) vs the rendered rate (post-coalescing).
        // Default OFF — it's a tuning aid, not routine output.
        public bool LogRdpFps         = false;

        // Gates the A/V playback log group: FFME (Media.Open/Close, MediaOpened/Failed),
        // the FFME and external-sync controllers, the libav decoder throughput,
        // the [zoom] mode log, and the [surface-router] info lines. Defaulted ON
        // so existing behaviour is preserved when users upgrade; flip OFF when
        // isolating splash-channel diagnostics from playback noise. Unlike the
        // per-VC channel toggles above, this one defaults true because A/V
        // events were always emitted before this toggle existed.
        public bool LogAvPlayback     = true;

        // Write every log line to a timestamped file under
        // <DiagnosticsDirectory>\Logs. Critical for diagnosing release-build crashes
        // on remote machines (the app may close before any session UI is
        // up), and the only log sink there is. Defaulted ON
        // because the cost is negligible (a few KB/s, auto-flushed) and
        // a captured log is the difference between "we know what crashed"
        // and "please reproduce while I shoulder-surf".
        public bool   LogToFile        = true;

        // ONE root for every kind of diagnostic output. See DiagnosticsPaths:
        //   <root>\Logs\   — the per-session log file
        //   <root>\Dumps\  — splash / fastpath / rtsp raw dumps
        // Empty string means "use the platform default" — %LocalAppData%\SoftSled.
        // Log changes take effect on the NEXT app launch (the app log opens at
        // startup); dump changes on the NEXT session start (the dump dirs are
        // pushed into env vars then).
        public string DiagnosticsDirectory = "";

        // ---- Legacy, migration-only ----------------------------------
        // Superseded by DiagnosticsDirectory (which is a ROOT holding Logs\ and
        // Dumps\, where these two were the leaf dirs themselves). Kept public so
        // XmlSerializer still reads the elements out of an existing Config.xml —
        // MigrateLegacyPaths() folds them into DiagnosticsDirectory so an
        // upgrade doesn't silently relocate a user's chosen folder. Not
        // surfaced in the UI and not read by anything else. Safe to delete once
        // no config in the wild carries them.
        public string LogFileDirectory = "";
        public string DumpsDirectory   = "";

        /// <summary>
        /// Fold the pre-merge <see cref="LogFileDirectory"/> /
        /// <see cref="DumpsDirectory"/> settings into the single
        /// <see cref="DiagnosticsDirectory"/> root. In-memory only and
        /// idempotent — deliberately NOT written back from the read path (that
        /// would race across the many threads that call ReadConfig); the next
        /// ordinary settings write persists it. LogFileDirectory wins when the
        /// two disagree, since it's the one the app log used.
        /// </summary>
        internal void MigrateLegacyPaths() {
            if (string.IsNullOrWhiteSpace(DiagnosticsDirectory)) {
                string legacy = !string.IsNullOrWhiteSpace(LogFileDirectory)
                    ? LogFileDirectory
                    : DumpsDirectory;
                if (!string.IsNullOrWhiteSpace(legacy)) DiagnosticsDirectory = legacy;
            }
            // Folded (or nothing to fold) — clear them so the next ordinary
            // WriteConfig drops the dead elements from Config.xml.
            LogFileDirectory = "";
            DumpsDirectory   = "";
        }

        // ---- Advanced / env-var-driven diagnostics --------------------
        //
        // Each of these mirrors a SOFTSLED_* environment variable that
        // call sites already check at session-start time. At session
        // start, ExtenderSessionControl applies these into the process-
        // scope env vars so existing consumers (RtspWireDumper.cs,
        // SplashRawDumper.cs, WmcFastpathRawDumper.cs,
        // WmcFastpathAudioPlayer.cs) don't
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
        // Wire-level dump of every RTSP request and response on the
        // control channel (mirrors SOFTSLED_RTSP_WIRE_DUMP). Off by
        // default — the dump grows with session activity but is
        // small per request.
        public bool EnableRtspWireDump    = false;
        // Verbose tracing of the fastpath audio decoder state machine
        // (mirrors SOFTSLED_AUDIO_TRACE). Off by default — gets
        // noisy when audio is actively playing.
        public bool EnableAudioTrace      = false;
        // DEBUG: keep the raw RDP framebuffer on screen for the whole session.
        // Normally the "connecting" curtain covers everything until the WMC
        // shell reports open, and rdpDisplay is deliberately kept Hidden in RUI
        // mode (there the splash channel IS the UI, and the host's fallback
        // framebuffer would paint over the splash composition and the video
        // plane). Both of those hide exactly what you need to see when a
        // connection never completes — so this toggle suppresses the curtain
        // and forces rdpDisplay Visible from session start regardless of render
        // mode or shell state. Expect a broken-looking picture in RUI: that's
        // the point. Off by default. Takes effect on the next session.
        public bool AlwaysShowRdp         = false;

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
        // Clamped at ±500 ms in the settings page (AudioSyncOffsetClampMs)
        // — beyond that any pipeline problem is structural rather than
        // offsettable. Consumed by ExternalSyncMediaController as its
        // per-media trim baseline; the live Ctrl+[ / Ctrl+] nudge adjusts
        // the running pacer only and is deliberately NOT persisted here.
        public int AudioSyncOffsetMs = 0;

        // (H264ExtraSyncOffsetMs removed 2026-07-15: the "H.264 residual" it
        // compensated turned out to be master-clock corruption — startup/underrun
        // silence and unhandled audio content gaps — fixed in NAudioMasterRenderer.
        // All codecs now sync at the content offset alone. XmlSerializer ignores
        // the stale element in existing config files.)

        // Video jitter-buffer depth (ms) for the libav + D3DImage player.
        // Larger values buffer more decoded video to absorb bursty RTSP
        // delivery (the wire delivers in ~160 ms bursts around I-frames),
        // smoothing judder at the cost of more start-up latency. Feeds
        // PtsFramePacer's pre-roll / max-buffer. Default 250 ms; clamped
        // 0–4000 ms in the UI. Takes effect on the next playback.
        public int VideoJitterBufferMs = 250;

        // User-customised remote-control button → command mappings, set from
        // the Remote settings page's "learn" flow. Each entry pins a catalog
        // command (by its stable Key, see RemoteCommandCatalog) to a specific
        // HID usage. An EMPTY list means "use the built-in defaults" — that's
        // the shipped mapping, and "Reset to defaults" simply clears this list.
        // XmlSerializer handles a List&lt;T&gt; of a public class with public
        // fields, so this round-trips in the config file automatically.
        public List<RemoteButtonBinding> RemoteButtonBindings = new List<RemoteButtonBinding>();

    }

    /// <summary>
    /// One learned remote-control mapping: pins a catalog command (identified
    /// by its stable <see cref="CommandKey"/>) to a captured HID usage. Public
    /// with a parameterless ctor + public fields so it serialises cleanly in
    /// the XML config.
    /// </summary>
    public class RemoteButtonBinding {
        /// <summary>Stable command id — matches a RemoteCommandCatalog def Key.</summary>
        public string CommandKey;
        /// <summary>Captured HID usage as (reportId &lt;&lt; 16) | usage. -1 = unbound.</summary>
        public int UsageKey;
    }
}
