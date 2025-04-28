using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SoftSled.Components.Diagnostics {
    // Simple little temporary logger to write directly to the main form.
    class TextBoxLogger : Logger {
        delegate void dTextWrite(string message);

        private TextBox m_textBox;
        private Window m_ownerWindow;



        public TextBoxLogger(TextBox textBox, Window ownerWindow) {
            if (textBox == null)
                throw new ArgumentNullException("textBox");
            else if (ownerWindow == null)
                throw new ArgumentNullException("ownerWindow");

            m_textBox = textBox;
            m_ownerWindow = ownerWindow;
        }

        delegate void WriteMessageCallback(string message);

        void WriteMessage(string message) {
            // Ensure the form is open.
            if (m_ownerWindow != null) {
                if (!m_ownerWindow.Dispatcher.CheckAccess()) {
                    WriteMessageCallback d = new WriteMessageCallback(WriteMessage);
                    m_ownerWindow.Dispatcher.Invoke(d, new object[] { message });
                } else {
                    m_textBox.Text = message + Environment.NewLine + m_textBox.Text;
                }
            }

        }

        protected override void OnLogDebug(string message) {
            if (IsLoggingDebug) {
                message = GetPrefix() + "Debug: " + message;
                WriteMessage(message);
            }
        }

        protected override void OnLogInfo(string message) {
            message = GetPrefix() + "Info: " + message;
            WriteMessage(message);
        }

        protected override void OnLogError(string message) {
            WriteMessage(message);
            message = GetPrefix() + "Error: " + message;
        }


    }
}
