using System;
using System.Runtime.InteropServices;

namespace SoftSled.Components.Utility {

    /// <summary>
    /// Keeps the display (and system) awake while SoftSled is running.
    ///
    /// <para>A WMC extender spends long stretches showing video or sitting on
    /// a menu with no keyboard / mouse input — exactly the idle pattern that
    /// trips the Windows screen-saver and display/sleep idle timers, blanking
    /// the screen mid-playback. We suppress that the same way media players do:
    /// <c>SetThreadExecutionState</c> with <c>ES_DISPLAY_REQUIRED |
    /// ES_SYSTEM_REQUIRED</c>.</para>
    ///
    /// <para>With <c>ES_CONTINUOUS</c> the request stays in effect until the
    /// next call on the same thread clears it — so a single <see cref="Acquire"/>
    /// at startup holds the display on, and <see cref="Release"/> at exit lets
    /// the normal idle timers resume. No periodic refresh timer is needed. Both
    /// calls MUST happen on a long-lived thread (the WPF UI thread): the request
    /// is scoped to the calling thread and is dropped if that thread exits.</para>
    /// </summary>
    public static class DisplayKeepAwake {

        [Flags]
        private enum ExecutionState : uint {
            ES_CONTINUOUS        = 0x80000000,
            ES_SYSTEM_REQUIRED   = 0x00000001,
            ES_DISPLAY_REQUIRED  = 0x00000002,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

        private static bool _held;

        /// <summary>
        /// Tell Windows to keep the display and system awake until
        /// <see cref="Release"/> is called. Idempotent — calling twice is
        /// harmless. Must be invoked on the UI thread.
        /// </summary>
        public static void Acquire() {
            try {
                var prev = SetThreadExecutionState(
                    ExecutionState.ES_CONTINUOUS |
                    ExecutionState.ES_DISPLAY_REQUIRED |
                    ExecutionState.ES_SYSTEM_REQUIRED);
                _held = prev != 0;
            } catch {
                // Keeping the screen awake is best-effort — never let a
                // failure here disturb startup.
                _held = false;
            }
        }

        /// <summary>
        /// Drop the keep-awake request so the normal screen-saver / sleep
        /// idle timers resume. Safe to call even if <see cref="Acquire"/>
        /// was never called.
        /// </summary>
        public static void Release() {
            try {
                // Clearing the DISPLAY/SYSTEM flags while keeping ES_CONTINUOUS
                // resets us to "no requirement", restoring default idle timeouts.
                SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
            } catch {
                // best-effort
            } finally {
                _held = false;
            }
        }

        /// <summary>True while a keep-awake request is currently held.</summary>
        public static bool IsHeld => _held;
    }
}
