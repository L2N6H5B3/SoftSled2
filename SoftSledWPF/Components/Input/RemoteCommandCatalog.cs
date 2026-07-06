using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using SoftSled.Components.Configuration;

namespace SoftSled.Components.Input {

    /// <summary>
    /// Client-side actions a remote button can trigger that are NOT forwarded to
    /// WMC — handled locally by the shell instead. Lets buttons/keys be bound to
    /// things like leaving the session, so e.g. ESC (which used to hard-quit) is
    /// itself just a rebindable command.
    /// </summary>
    public enum RemoteLocalAction {
        None = 0,
        /// <summary>Disconnect the running session and return to the landing menu.</summary>
        ExitSession,
    }

    /// <summary>
    /// The catalogue of remappable remote-control commands, and the helpers
    /// that resolve/mutate the user's learned bindings against a
    /// <see cref="SoftSledConfig"/>.
    ///
    /// <para>The <see cref="Defs"/> list IS the shipped DEFAULT mapping — the
    /// button→command table that used to live hard-coded in
    /// <see cref="McxRemoteInput"/>. Each entry pins a friendly command
    /// (Play, Guide, Zoom, …) to a default HID usage. The Remote settings page
    /// lets the user "learn" a different button for any command; those
    /// overrides live in <see cref="SoftSledConfig.RemoteButtonBindings"/>.
    /// An empty override list means "everything is at its default", and
    /// <see cref="ResetToDefaults"/> restores that by clearing the list.</para>
    ///
    /// <para>A command's target is EITHER a WMC RemoteCommand id (resolved to
    /// keystrokes via the McxSess RegisterRemoteCommandBindings table) OR a
    /// direct PS/2 Set-1 scan-code chord (for buttons WMC doesn't bind, e.g.
    /// Skip Forward/Back → Ctrl+F / Ctrl+B).</para>
    /// </summary>
    public static class RemoteCommandCatalog {

        // Report ids the eHome remote uses (first byte of each HID report).
        public const int REPORT_CONSUMER = 0x02; // Consumer page (0x0C), 16-bit usage
        public const int REPORT_MCE      = 0x03; // MCE vendor page (0xFFBC), 8-bit usage
        // Synthetic "report id" for a plain keyboard key (e.g. ESC, F12) so it
        // can live in the same usage-key space as the HID buttons without
        // colliding. The low 16 bits carry the WPF Key value.
        public const int REPORT_KEYBOARD = 0x7F;

        /// <summary>Compose the map key the way McxRemoteInput does.</summary>
        public static int UsageKey(int reportId, int usage) => (reportId << 16) | usage;

        /// <summary>Usage key for a keyboard key (WPF Key value).</summary>
        public static int KeyboardUsageKey(int wpfKey) => (REPORT_KEYBOARD << 16) | (wpfKey & 0xFFFF);

        /// <summary>True if the usage key represents a keyboard key rather than an HID button.</summary>
        public static bool IsKeyboardUsage(int usageKey) => ((usageKey >> 16) & 0xFF) == REPORT_KEYBOARD;

        public sealed class RemoteCommandDef {
            /// <summary>Stable identity, persisted in config. Never localise/rename.</summary>
            public string Key;
            /// <summary>Human label shown on the Remote settings page.</summary>
            public string Label;
            /// <summary>WMC RemoteCommand id, or -1 when this is a direct chord.</summary>
            public int CmdId;
            /// <summary>Direct PS/2 Set-1 scan-code chord, or null when CmdId is used.</summary>
            public int[] Chord;
            /// <summary>Default HID usage bound to this command (reportId&lt;&lt;16 | usage).</summary>
            public int DefaultUsageKey;
            /// <summary>Local (client-side) action, when this isn't a WMC command/chord.</summary>
            public RemoteLocalAction Local = RemoteLocalAction.None;

            public bool IsChord => Chord != null;
            public bool IsLocal => Local != RemoteLocalAction.None;
        }

        // The shipped defaults. Order = display order on the settings page.
        // These values are exactly the mappings that were previously hard-coded
        // as McxRemoteInput.UsageToCmd / UsageToChord.
        public static readonly IReadOnlyList<RemoteCommandDef> Defs = new[] {
            // --- Transport (Consumer page, report 0x02) ---
            Cmd("Play",       "Play",            9,  REPORT_CONSUMER, 0x00B0),
            Cmd("Pause",      "Pause",           8,  REPORT_CONSUMER, 0x00B1),
            Cmd("Stop",       "Stop",            6,  REPORT_CONSUMER, 0x00B7),
            Cmd("FastFwd",    "Fast Forward",   11,  REPORT_CONSUMER, 0x00B3),
            Cmd("Rewind",     "Rewind",         10,  REPORT_CONSUMER, 0x00B4),
            Cmd("Record",     "Record",          7,  REPORT_CONSUMER, 0x00B2),
            Chord("SkipFwd",  "Skip Forward",   new[] { 0x1D, 0x21 }, REPORT_CONSUMER, 0x00B5), // Ctrl+F
            Chord("SkipBack", "Skip Back",      new[] { 0x1D, 0x30 }, REPORT_CONSUMER, 0x00B6), // Ctrl+B
            // --- Channel / volume (Consumer page) ---
            Cmd("ChannelUp",  "Channel Up",     24,  REPORT_CONSUMER, 0x009C),
            Cmd("ChannelDn",  "Channel Down",   25,  REPORT_CONSUMER, 0x009D),
            Cmd("VolumeUp",   "Volume Up",      21,  REPORT_CONSUMER, 0x00E9),
            Cmd("VolumeDn",   "Volume Down",    22,  REPORT_CONSUMER, 0x00EA),
            Cmd("Mute",       "Mute",           26,  REPORT_CONSUMER, 0x00E2),
            // --- Navigation / info (Consumer page) ---
            Cmd("Guide",      "Guide",          28,  REPORT_CONSUMER, 0x008D),
            Cmd("Info",       "Info / More",    15,  REPORT_CONSUMER, 0x0209),
            // Back is sent as the Backspace keystroke, NOT via a WMC command id:
            // WMC's RegisterRemoteCommandBindings table leaves command 45 (Back)
            // UNBOUND (confirmed on the wire: "RemoteCmd[45] → (no binding)"), so
            // forwarding it as a command silently did nothing. WMC navigates back
            // on Backspace (Set-1 scancode 0x0E), so send that directly.
            Chord("Back",     "Back",           new[] { 0x0E }, REPORT_CONSUMER, 0x0224),
            // Client-side session control. Default = the ESC key, which is what
            // used to hard-quit the session — now it's just this rebindable
            // command, so ESC can be freed up for anything else.
            Local("ExitSession", "Exit Session (to menu)", RemoteLocalAction.ExitSession, Key.Escape),
            // --- Media Center menu buttons (MCE vendor page, report 0x03) ---
            Cmd("GreenStart", "Green (Start)",  23,  REPORT_MCE, 0x000D),
            Cmd("LiveTv",     "Live TV",        29,  REPORT_MCE, 0x0025),
            Cmd("RecordedTv", "Recorded TV",    27,  REPORT_MCE, 0x0048),
            Cmd("DvdMenu",    "DVD Menu",       30,  REPORT_MCE, 0x0024),
            Cmd("Zoom",       "Zoom / Aspect",  48,  REPORT_MCE, 0x0027),
            Cmd("Music",      "Music",           3,  REPORT_MCE, 0x0047),
            Cmd("Radio",      "Radio",          46,  REPORT_MCE, 0x0050),
            Cmd("Pictures",   "Pictures",        4,  REPORT_MCE, 0x0049),
            Cmd("Videos",     "Videos",          5,  REPORT_MCE, 0x004A),
            // --- Teletext / coloured buttons (MCE vendor page) ---
            Cmd("Teletext",   "Teletext",       54,  REPORT_MCE, 0x005A),
            Cmd("Red",        "Red",            55,  REPORT_MCE, 0x005B),
            Cmd("GreenTtx",   "Green (Teletext)", 56, REPORT_MCE, 0x005C),
            Cmd("Yellow",     "Yellow",         57,  REPORT_MCE, 0x005D),
            Cmd("Blue",       "Blue",           58,  REPORT_MCE, 0x005E),
        };

        private static RemoteCommandDef Cmd(string key, string label, int cmdId, int report, int usage)
            => new RemoteCommandDef { Key = key, Label = label, CmdId = cmdId, Chord = null,
                                      DefaultUsageKey = UsageKey(report, usage) };

        private static RemoteCommandDef Chord(string key, string label, int[] chord, int report, int usage)
            => new RemoteCommandDef { Key = key, Label = label, CmdId = -1, Chord = chord,
                                      DefaultUsageKey = UsageKey(report, usage) };

        private static RemoteCommandDef Local(string key, string label, RemoteLocalAction action, Key defaultKey)
            => new RemoteCommandDef { Key = key, Label = label, CmdId = -1, Chord = null,
                                      Local = action, DefaultUsageKey = KeyboardUsageKey((int)defaultKey) };

        public static RemoteCommandDef FindByKey(string key)
            => Defs.FirstOrDefault(d => d.Key == key);

        // ---- Binding resolution against a config -----------------------

        /// <summary>
        /// The HID usage currently bound to a command: the user's override if
        /// present, otherwise the shipped default. Returns -1 when the user has
        /// explicitly unbound it.
        /// </summary>
        public static int GetEffectiveUsage(SoftSledConfig cfg, string commandKey) {
            var ov = cfg?.RemoteButtonBindings?.FirstOrDefault(b => b.CommandKey == commandKey);
            if (ov != null) return ov.UsageKey;
            var def = FindByKey(commandKey);
            return def?.DefaultUsageKey ?? -1;
        }

        /// <summary>True when the command is at its shipped default binding.</summary>
        public static bool IsDefault(SoftSledConfig cfg, string commandKey)
            => cfg?.RemoteButtonBindings?.All(b => b.CommandKey != commandKey) ?? true;

        /// <summary>
        /// Bind <paramref name="usageKey"/> to <paramref name="commandKey"/>.
        /// Keeps the mapping 1:1 — any OTHER command currently on that usage is
        /// unbound so a single button never fires two commands. Pass -1 to
        /// unbind. Mutates <paramref name="cfg"/> in memory (caller persists).
        /// </summary>
        public static void SetBinding(SoftSledConfig cfg, string commandKey, int usageKey) {
            if (cfg == null) return;
            if (cfg.RemoteButtonBindings == null)
                cfg.RemoteButtonBindings = new List<RemoteButtonBinding>();

            if (usageKey >= 0) {
                foreach (var d in Defs) {
                    if (d.Key == commandKey) continue;
                    if (GetEffectiveUsage(cfg, d.Key) == usageKey)
                        SetOverride(cfg, d.Key, -1); // steal the button from its old owner
                }
            }
            SetOverride(cfg, commandKey, usageKey);
        }

        /// <summary>Unbind a command (its button will do nothing until re-learned).</summary>
        public static void ClearBinding(SoftSledConfig cfg, string commandKey)
            => SetOverride(cfg, commandKey, -1);

        /// <summary>Reset a SINGLE command to its shipped default binding. Also
        /// reclaims its default button from whichever command currently holds it
        /// (keeping the mapping 1:1), then drops the override.</summary>
        public static void ResetBinding(SoftSledConfig cfg, string commandKey) {
            var def = FindByKey(commandKey);
            if (def == null) return;
            SetBinding(cfg, commandKey, def.DefaultUsageKey);
        }

        /// <summary>Restore every command to its shipped default binding.</summary>
        public static void ResetToDefaults(SoftSledConfig cfg) {
            if (cfg?.RemoteButtonBindings != null) cfg.RemoteButtonBindings.Clear();
        }

        private static void SetOverride(SoftSledConfig cfg, string commandKey, int usageKey) {
            var def = FindByKey(commandKey);
            // Setting a command back to its own default: drop the override
            // entirely so the list stays minimal (and IsDefault reports true).
            if (def != null && usageKey == def.DefaultUsageKey) {
                cfg.RemoteButtonBindings.RemoveAll(b => b.CommandKey == commandKey);
                return;
            }
            var existing = cfg.RemoteButtonBindings.FirstOrDefault(b => b.CommandKey == commandKey);
            if (existing != null) existing.UsageKey = usageKey;
            else cfg.RemoteButtonBindings.Add(new RemoteButtonBinding { CommandKey = commandKey, UsageKey = usageKey });
        }

        // ---- Runtime maps for McxRemoteInput ---------------------------

        /// <summary>
        /// Build the dispatch maps McxRemoteInput uses: usage → WMC command id,
        /// usage → direct scan-code chord, and usage → client-side local action,
        /// honouring the user's overrides.
        /// </summary>
        public static void BuildRuntimeMaps(SoftSledConfig cfg,
                                            out Dictionary<int, int> usageToCmd,
                                            out Dictionary<int, int[]> usageToChord,
                                            out Dictionary<int, RemoteLocalAction> usageToLocal) {
            usageToCmd = new Dictionary<int, int>();
            usageToChord = new Dictionary<int, int[]>();
            usageToLocal = new Dictionary<int, RemoteLocalAction>();
            foreach (var d in Defs) {
                int usage = GetEffectiveUsage(cfg, d.Key);
                if (usage < 0) continue;             // unbound
                if (d.IsLocal)      usageToLocal[usage] = d.Local;
                else if (d.IsChord) usageToChord[usage] = d.Chord;
                else                usageToCmd[usage]   = d.CmdId;
            }
        }

        // ---- Display -----------------------------------------------------

        /// <summary>
        /// Friendly description of a bound usage for the settings page. When the
        /// usage matches a catalogue default we name the physical button
        /// ("Zoom button"); otherwise we fall back to the raw report/usage.
        /// </summary>
        public static string DescribeUsage(int usageKey) {
            if (usageKey < 0) return "(not assigned)";
            if (IsKeyboardUsage(usageKey)) {
                var k = (Key)(usageKey & 0xFFFF);
                return DescribeKey(k) + " key";
            }
            var owner = Defs.FirstOrDefault(d => d.DefaultUsageKey == usageKey);
            if (owner != null) return owner.Label + " button";
            int report = (usageKey >> 16) & 0xFF;
            int usage  = usageKey & 0xFFFF;
            return $"button {report:X2}:{usage:X4}";
        }

        private static string DescribeKey(Key k) {
            switch (k) {
                case Key.Escape: return "Esc";
                case Key.Return: return "Enter";
                case Key.Back:   return "Backspace";
                case Key.Space:  return "Space";
                default:         return k.ToString();
            }
        }
    }
}
