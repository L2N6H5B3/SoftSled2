using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SoftSled.Components.Diagnostics {
    /// <summary>
    /// Logger that writes messages to a WPF <see cref="TextBox"/>.
    ///
    /// Performance notes (the previous revision was a textbook hot-path
    /// pessimisation):
    /// <list type="bullet">
    ///   <item><description>The dispatcher hop now uses <c>BeginInvoke</c>
    ///     instead of <c>Invoke</c>. Worker threads that log no longer
    ///     block waiting for the UI thread to be free. The RTP receive
    ///     loop, RTCP timer, fast-path callback, and FFME workers each
    ///     used to stall every time they logged.</description></item>
    ///   <item><description>The TextBox content is no longer rebuilt by
    ///     prepending an O(N) string concat (<c>Text = msg + NewLine +
    ///     Text</c>). Lines are appended to a bounded ring of recent
    ///     entries and the TextBox is re-rendered from that ring. Memory
    ///     and per-log-line cost are both O(<c>MaxLines</c>) instead of
    ///     O(history-length).</description></item>
    ///   <item><description>Multiple log calls within the same dispatcher
    ///     frame coalesce into one TextBox update so we don't trigger N
    ///     layout invalidations per N log lines.</description></item>
    /// </list>
    /// </summary>
    class TextBoxLogger : Logger {

        // Bounded ring of recent lines. Keep ~enough to spot startup
        // issues + the last few seconds of activity. Each line is
        // typically &lt;200 chars so the steady-state cost is well under
        // 1 MB even for a long session.
        private const int MaxLines = 500;

        private readonly TextBox m_textBox;
        private readonly Window  m_ownerWindow;
        private readonly LinkedList<string> m_lines = new LinkedList<string>();
        private readonly object m_linesLock = new object();
        private bool m_renderPending;

        public TextBoxLogger(TextBox textBox, Window ownerWindow) {
            m_textBox     = textBox     ?? throw new ArgumentNullException(nameof(textBox));
            m_ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
        }

        // ---- Message enqueue ------------------------------------------

        private void WriteMessage(string message) {
            if (m_ownerWindow == null) return;

            bool needSchedule;
            lock (m_linesLock) {
                m_lines.AddFirst(message);
                while (m_lines.Count > MaxLines) m_lines.RemoveLast();
                // Only schedule one dispatcher hop per pending batch —
                // multiple logs in the same frame coalesce.
                needSchedule = !m_renderPending;
                if (needSchedule) m_renderPending = true;
            }

            if (needSchedule) {
                // BeginInvoke = fire and forget. The worker thread that
                // called Log... is back to its real job immediately.
                // Background priority so log rendering doesn't beat real
                // input or rendering work.
                m_ownerWindow.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(RenderOnUiThread));
            }
        }

        // ---- UI-thread render -----------------------------------------

        private void RenderOnUiThread() {
            string snapshot;
            lock (m_linesLock) {
                m_renderPending = false;
                // Snapshot the ring contents under lock, then build the
                // text outside the lock to keep critical section short.
                snapshot = string.Join(Environment.NewLine, m_lines);
            }
            try {
                m_textBox.Text = snapshot;
            } catch { /* if the textbox went away, drop quietly */ }
        }

        // ---- Logger contract ------------------------------------------

        protected override void OnLogDebug(string message) {
            if (!IsLoggingDebug) return;
            WriteMessage(GetPrefix() + "Debug: " + message);
        }

        protected override void OnLogInfo(string message) {
            WriteMessage(GetPrefix() + "Info: " + message);
        }

        protected override void OnLogError(string message) {
            // Note: the previous implementation built the prefix AFTER
            // calling WriteMessage, so error lines went to the textbox
            // WITHOUT the timestamp+level prefix. Fix that while we're
            // here — errors deserve at least as much context as Info.
            WriteMessage(GetPrefix() + "Error: " + message);
        }
    }
}
