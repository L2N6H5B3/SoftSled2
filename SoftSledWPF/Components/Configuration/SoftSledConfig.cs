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
