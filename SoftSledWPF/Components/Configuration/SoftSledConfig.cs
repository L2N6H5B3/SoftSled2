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
        public string DeviceUDN = "";
        public string RdpLoginHost = "";
        public string RdpLoginUserName = "";
        public string RdpLoginPassword = "";
        public bool EnableRemoteRendering = true;
        public bool EnableOverscanMargin = false;
        public bool Enable2DAnimations = true;
        public bool EnableIntenseAnimations = false;
        public bool InitialSetupComplete = false;
        public bool UseTvSkin = false;
        public bool RunFullScreen = true;
        public bool AutoStartWmcOnOpen = false;
        public bool LockWindowAspectRatio = true;
        public bool MediaPlaybackModeEnabled = false;
        public bool KeepScreenAwake = true;
        public bool CloseOnWmcClose = false;
        public bool MinimizeToTray = false;
        public BootStartMode BootStartMode = BootStartMode.Off;
        public bool EnableHdContent = true;
        public bool EnableUiSounds = true;
        public bool EnablePopups   = true;
        public bool EnableToolbar  = true;
        public bool EnableMouseInput = true;
        public bool EnableSplashAudio = true;
        public bool LogDevCapsChannel = false;
        public bool LogMcxSessChannel = false;
        public bool LogAvCtrlChannel  = false;
        public bool LogRdpFastpath    = false;
        public bool LogRdpFps         = false;
        public bool LogAvPlayback     = true;
        public bool   LogToFile        = true;
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
