namespace SoftSled.Components.Configuration {

    /// <summary>One pickable session resolution. Shared by the settings page
    /// resolution picker and the first-run setup wizard.</summary>
    public struct ScreenResolution {
        public int Width, Height;
        public string Label;   // Short marketing name ("HD", "WUXGA"), may be empty
        public string Aspect;  // "4:3", "16:9", "16:10"

        public ScreenResolution(int w, int h, string label, string aspect) {
            Width = w; Height = h; Label = label; Aspect = aspect;
        }

        /// <summary>List-row text, e.g. "1920 × 1080    16:9   Full HD".</summary>
        public string Display =>
            $"{Width} × {Height}    {Aspect}" +
            (string.IsNullOrEmpty(Label) ? "" : "   " + Label);
    }

    /// <summary>
    /// Catalogue of session resolutions SoftSled will request from the RDP
    /// host, plus helpers to detect the current screen and map an arbitrary
    /// size onto the nearest supported entry.
    /// </summary>
    public static class ScreenResolutions {

        // Grouped by aspect, smallest 4:3 → largest 16:10. Kept to sizes the
        // average extender host will realistically negotiate.
        public static readonly ScreenResolution[] All = {
            // 4:3
            new ScreenResolution(640,  480,  "VGA",     "4:3"),
            new ScreenResolution(800,  600,  "SVGA",    "4:3"),
            new ScreenResolution(1024, 768,  "XGA",     "4:3"),
            new ScreenResolution(1280, 960,  "",        "4:3"),
            new ScreenResolution(1400, 1050, "SXGA+",   "4:3"),
            new ScreenResolution(1600, 1200, "UXGA",    "4:3"),
            // 16:9
            new ScreenResolution(1280, 720,  "HD",      "16:9"),
            new ScreenResolution(1366, 768,  "",        "16:9"),
            new ScreenResolution(1600, 900,  "HD+",     "16:9"),
            new ScreenResolution(1920, 1080, "Full HD", "16:9"),
            new ScreenResolution(2560, 1440, "QHD",     "16:9"),
            new ScreenResolution(3840, 2160, "4K UHD",  "16:9"),
            // 16:10
            new ScreenResolution(1280, 800,  "",        "16:10"),
            new ScreenResolution(1440, 900,  "",        "16:10"),
            new ScreenResolution(1680, 1050, "WSXGA+",  "16:10"),
            new ScreenResolution(1920, 1200, "WUXGA",   "16:10"),
            new ScreenResolution(2560, 1600, "WQXGA",   "16:10"),
        };

        /// <summary>Primary-screen pixel size. Uses WinForms (actual device
        /// pixels for a non-per-monitor-DPI-aware app) and falls back to a
        /// safe 1920×1080 if detection throws.</summary>
        public static void DetectPrimaryScreen(out int width, out int height) {
            try {
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                width = b.Width;
                height = b.Height;
                if (width > 0 && height > 0) return;
            } catch { /* fall through to default */ }
            width = 1920; height = 1080;
        }

        /// <summary>Nearest supported resolution to an arbitrary size. Prefers
        /// an exact match, otherwise the smallest Euclidean distance in
        /// (width, height) space.</summary>
        public static ScreenResolution MapToSupported(int width, int height) {
            ScreenResolution best = All[0];
            long bestDist = long.MaxValue;
            foreach (var r in All) {
                if (r.Width == width && r.Height == height) return r;
                long dw = r.Width - width, dh = r.Height - height;
                long dist = dw * dw + dh * dh;
                if (dist < bestDist) { bestDist = dist; best = r; }
            }
            return best;
        }

        /// <summary>Index of an (w,h) pair in <see cref="All"/>, or -1.</summary>
        public static int IndexOf(int width, int height) {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Width == width && All[i].Height == height) return i;
            return -1;
        }
    }
}
