using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace SoftSled.Components
{
    // Simple little temporary logger to write directly to the main form.
    class TextBoxLogger : Logger
    {
        delegate void dTextWrite(string message);

        private TextBox m_textBox;
        private Form m_ownerForm;

      

        public TextBoxLogger(TextBox textBox, Form ownerForm)
        {
            if (textBox == null)
                throw new ArgumentNullException("textBox");
            else if (ownerForm == null)
                throw new ArgumentNullException("ownerForm");

            m_textBox = textBox;
            m_ownerForm = ownerForm;
        }

        void WriteMessage(string message)
        {
            // Ensure the form is open.
            if (m_ownerForm != null)
            {
                m_ownerForm.Invoke(new dTextWrite(delegate(string ex)
                {
                    m_textBox.Text += ex + Environment.NewLine;
                }), message);
            }

        }

        protected override void OnLogDebug(string message)
        {
            if (IsLoggingDebug)
            {
                message = GetPrefix() + "Debug: " + message;
                WriteMessage(message);
            }
        }

        protected override void OnLogInfo(string message)
        {
            message = GetPrefix() + "Info: " + message;
            WriteMessage(message);
        }

        protected override void OnLogError(string message)
        {
            WriteMessage(message);
            message = GetPrefix() + "Error: " + message;
        }


    }
}
