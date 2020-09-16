namespace Intel.Utilities
{
    using System;
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    internal class ExceptionForm : Form
    {
        private Button breakButton;
        private Container components = null;
        private TextBox ErrorBox;
        private Button ignoreButton;

        public ExceptionForm(Exception e)
        {
            this.InitializeComponent();
            this.Text = e.Source;
            this.ErrorBox.Text = e.ToString();
            this.ErrorBox.SelectionLength = 0;
        }

        private void breakButton_Click(object sender, EventArgs e)
        {
            base.DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (this.components != null))
            {
                this.components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ignoreButton_Click(object sender, EventArgs e)
        {
            base.DialogResult = DialogResult.Cancel;
        }

        private void InitializeComponent()
        {
            this.ErrorBox = new TextBox();
            this.breakButton = new Button();
            this.ignoreButton = new Button();
            base.SuspendLayout();
            this.ErrorBox.Anchor = AnchorStyles.Right | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Top;
            this.ErrorBox.Location = new Point(8, 8);
            this.ErrorBox.Multiline = true;
            this.ErrorBox.Name = "ErrorBox";
            this.ErrorBox.ReadOnly = true;
            this.ErrorBox.Size = new Size(0x1a0, 0xa8);
            this.ErrorBox.TabIndex = 0;
            this.ErrorBox.Text = "";
            this.breakButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            this.breakButton.DialogResult = DialogResult.Cancel;
            this.breakButton.Location = new Point(0x110, 0xb8);
            this.breakButton.Name = "breakButton";
            this.breakButton.TabIndex = 1;
            this.breakButton.Text = "Break";
            this.breakButton.Click += new System.EventHandler(this.breakButton_Click);
            this.ignoreButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            this.ignoreButton.Location = new Point(0x160, 0xb8);
            this.ignoreButton.Name = "ignoreButton";
            this.ignoreButton.TabIndex = 2;
            this.ignoreButton.Text = "Ignore";
            this.ignoreButton.Click += new System.EventHandler(this.ignoreButton_Click);
            base.AcceptButton = this.ignoreButton;
            this.AutoScaleBaseSize = new Size(5, 13);
            base.ClientSize = new Size(0x1b0, 0xd6);
            base.Controls.AddRange(new Control[] { this.ignoreButton, this.breakButton, this.ErrorBox });
            base.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            base.Name = "ExceptionForm";
            base.StartPosition = FormStartPosition.CenterParent;
            this.Text = "ExceptionForm";
            base.ResumeLayout(false);
        }
    }
}

