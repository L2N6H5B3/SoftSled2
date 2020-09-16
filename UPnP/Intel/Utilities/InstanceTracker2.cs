namespace Intel.Utilities
{
    using System;
    using System.Collections;
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    internal class InstanceTracker2 : Form
    {
        private Container components = null;
        private int Current = 0;
        private Button NextButton;
        private Button PreviousButton;
        private Label Status;
        private System.Windows.Forms.TextBox TextBox;
        private ArrayList TheData;

        public InstanceTracker2(ArrayList DataList)
        {
            this.InitializeComponent();
            this.TheData = DataList;
            this.ShowStatus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (this.components != null))
            {
                this.components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.TextBox = new System.Windows.Forms.TextBox();
            this.PreviousButton = new Button();
            this.NextButton = new Button();
            this.Status = new Label();
            base.SuspendLayout();
            this.TextBox.Anchor = AnchorStyles.Right | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Top;
            this.TextBox.Location = new Point(8, 8);
            this.TextBox.Multiline = true;
            this.TextBox.Name = "TextBox";
            this.TextBox.ReadOnly = true;
            this.TextBox.Size = new Size(0x158, 0xe8);
            this.TextBox.TabIndex = 0;
            this.TextBox.Text = "";
            this.PreviousButton.Anchor = AnchorStyles.Bottom;
            this.PreviousButton.Location = new Point(8, 0xf8);
            this.PreviousButton.Name = "PreviousButton";
            this.PreviousButton.TabIndex = 1;
            this.PreviousButton.Text = "<<";
            this.PreviousButton.Click += new System.EventHandler(this.PreviousButton_Click);
            this.NextButton.Anchor = AnchorStyles.Bottom;
            this.NextButton.Location = new Point(280, 0xf8);
            this.NextButton.Name = "NextButton";
            this.NextButton.TabIndex = 2;
            this.NextButton.Text = ">>";
            this.NextButton.Click += new System.EventHandler(this.NextButton_Click);
            this.Status.Anchor = AnchorStyles.Bottom;
            this.Status.Location = new Point(0x80, 0xf8);
            this.Status.Name = "Status";
            this.Status.TabIndex = 3;
            this.Status.Text = "1 of 1";
            this.Status.TextAlign = ContentAlignment.MiddleCenter;
            this.AutoScaleBaseSize = new Size(5, 13);
            base.ClientSize = new Size(360, 0x116);
            base.Controls.AddRange(new Control[] { this.Status, this.NextButton, this.PreviousButton, this.TextBox });
            base.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            base.Name = "InstanceTracker2";
            this.Text = "InstanceTracker2";
            base.ResumeLayout(false);
        }

        private void NextButton_Click(object sender, EventArgs e)
        {
            this.Current++;
            if (this.Current > (this.TheData.Count - 1))
            {
                this.Current = this.TheData.Count - 1;
            }
            this.ShowStatus();
        }

        private void PreviousButton_Click(object sender, EventArgs e)
        {
            this.Current--;
            if (this.Current < 0)
            {
                this.Current = 0;
            }
            this.ShowStatus();
        }

        private void ShowStatus()
        {
            this.Status.Text = ((this.Current + 1)).ToString() + " of " + this.TheData.Count.ToString();
            this.TextBox.Text = (string) this.TheData[this.Current];
        }
    }
}

