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

        // Playback engine selection. See PlaybackEngineKind above for
        // a description of each option. Default is FFME — proven and
        // gives smooth playback with AudioSilenceInjector handling
        // trick play.
        public PlaybackEngineKind PlaybackEngine = PlaybackEngineKind.Ffme;

        // Legacy bool — preserved so old config files keep their
        // FFME-vs-libav choice through the upgrade. On read, if this
        // field is present, it overrides PlaybackEngine (Ffme if true,
        // DirectLibAv if false). New code should read PlaybackEngine.
        // Removed from the ConfigPage UI.
        public bool UseFfmeEngine = true;

        // Video renderer selection. When false (default) the playback
        // engine uses the software WriteableBitmap path
        // (WpfVideoRenderer) — proven, no GPU dependency. When true it
        // uses the D3DImage path (D3DImageVideoRenderer) — Phase-1
        // GPU-backed surface, intended to free the WPF compositor from
        // per-frame upload work. Defaults to OFF because both currently
        // measure the same steady-state fps on the test hardware; the
        // toggle lets future testing flip without a rebuild and gives
        // a safe fallback if the D3DImage path misbehaves on a given
        // GPU / driver combo.
        public bool EnableD3DImage = false;

    }
}
