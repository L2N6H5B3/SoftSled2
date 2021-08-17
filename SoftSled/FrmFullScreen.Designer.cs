namespace SoftSled
{
    partial class FrmFullScreen {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FrmFullScreen));
            this.rdpClient = new AxMSTSCLib.AxMsRdpClient7();
            this.videoView1 = new LibVLCSharp.WinForms.VideoView();
            this.txtLog = new System.Windows.Forms.TextBox();
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.videoView1)).BeginInit();
            this.SuspendLayout();
            // 
            // rdpClient
            // 
            this.rdpClient.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rdpClient.Enabled = true;
            this.rdpClient.Location = new System.Drawing.Point(0, 0);
            this.rdpClient.Name = "rdpClient";
            this.rdpClient.OcxState = ((System.Windows.Forms.AxHost.State)(resources.GetObject("rdpClient.OcxState")));
            this.rdpClient.Size = new System.Drawing.Size(1280, 720);
            this.rdpClient.TabIndex = 2;
            this.rdpClient.Visible = false;
            // 
            // videoView1
            // 
            this.videoView1.BackColor = System.Drawing.Color.Black;
            this.videoView1.Location = new System.Drawing.Point(964, 171);
            this.videoView1.MediaPlayer = null;
            this.videoView1.Name = "videoView1";
            this.videoView1.Size = new System.Drawing.Size(259, 145);
            this.videoView1.TabIndex = 21;
            this.videoView1.Text = "videoView1";
            this.videoView1.Visible = false;
            // 
            // txtLog
            // 
            this.txtLog.Location = new System.Drawing.Point(1294, 12);
            this.txtLog.Name = "txtLog";
            this.txtLog.Size = new System.Drawing.Size(171, 20);
            this.txtLog.TabIndex = 22;
            this.txtLog.Visible = false;
            // 
            // FrmFullScreen
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoSize = true;
            this.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.BackColor = System.Drawing.Color.Black;
            this.ClientSize = new System.Drawing.Size(1280, 720);
            this.Controls.Add(this.rdpClient);
            this.Controls.Add(this.videoView1);
            this.Controls.Add(this.txtLog);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "FrmFullScreen";
            this.Text = "SoftSled Dev Shell";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.Load += new System.EventHandler(this.FrmFullScreen_Load);
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.videoView1)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private AxMSTSCLib.AxMsRdpClient7 rdpClient;
        private LibVLCSharp.WinForms.VideoView videoView1;
        private System.Windows.Forms.TextBox txtLog;
    }
}