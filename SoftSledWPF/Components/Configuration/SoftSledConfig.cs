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

        // Initial desktop / session resolution requested from the RDP
        // server. Picked from the resolution overlay in the Video sub-
        // page.
        public int SessionWidth  = 1280;
        public int SessionHeight = 720;

    }
}
