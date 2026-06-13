using System;

namespace SoftSled.Components.Diagnostics {
    /// <summary>
    /// Fan-out logger: broadcasts every Log call to a set of child
    /// loggers. Used to mirror the on-screen <see cref="TextBoxLogger"/>
    /// to a <see cref="FileLogger"/> so a crash that closes the app
    /// before the user can read the textbox still leaves a captured
    /// log on disk for post-mortem.
    ///
    /// <para>Child failures don't propagate — if one logger throws, the
    /// others still receive the message. This matches the philosophy
    /// that logging should never be the reason a session dies.</para>
    /// </summary>
    public sealed class CompositeLogger : Logger {

        private readonly Logger[] _children;

        public CompositeLogger(params Logger[] children) {
            _children = children ?? new Logger[0];
        }

        protected override void OnLogDebug(string message) {
            for (int i = 0; i < _children.Length; i++) {
                if (_children[i] == null) continue;
                try { _children[i].LogDebug(message); } catch { /* keep broadcasting */ }
            }
        }

        protected override void OnLogInfo(string message) {
            for (int i = 0; i < _children.Length; i++) {
                if (_children[i] == null) continue;
                try { _children[i].LogInfo(message); } catch { /* keep broadcasting */ }
            }
        }

        protected override void OnLogError(string message) {
            for (int i = 0; i < _children.Length; i++) {
                if (_children[i] == null) continue;
                try { _children[i].LogError(message); } catch { /* keep broadcasting */ }
            }
        }

        /// <summary>
        /// Fan IsLoggingDebug out to children too so a <c>true</c> on the
        /// composite enables Debug for every child. Useful when the
        /// composite is the one the rest of the app holds a reference
        /// to — flipping it propagates.
        /// </summary>
        public new bool IsLoggingDebug {
            get => base.IsLoggingDebug;
            set {
                base.IsLoggingDebug = value;
                for (int i = 0; i < _children.Length; i++) {
                    if (_children[i] == null) continue;
                    try { _children[i].IsLoggingDebug = value; } catch { }
                }
            }
        }
    }
}
