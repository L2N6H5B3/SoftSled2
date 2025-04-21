namespace SoftSled
{
    partial class FrmMain
    {
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FrmMain));
            this.rdpClient = new AxMSTSCLib.AxMsRdpClient7NotSafeForScripting();
            this.txtLog = new System.Windows.Forms.TextBox();
            this.lnkGiveFocus = new System.Windows.Forms.LinkLabel();
            this.btnDoExtenderConnect = new System.Windows.Forms.Button();
            this.btnExtenderDisconnect = new System.Windows.Forms.Button();
            this.btnExtenderSetup = new System.Windows.Forms.Button();
            this.lnkSendCtrlAltDelete = new System.Windows.Forms.LinkLabel();
            this.panOverlay = new System.Windows.Forms.Panel();
            this.label1 = new System.Windows.Forms.Label();
            this.lbGenStatus = new System.Windows.Forms.Label();
            this.chkLogDebug = new System.Windows.Forms.CheckBox();
            this.chkInVchanDebug = new System.Windows.Forms.CheckBox();
            this.lnkShowCtrlHideInfo = new System.Windows.Forms.LinkLabel();
            this.videoView1 = new LibVLCSharp.WinForms.VideoView();
            this.label3 = new System.Windows.Forms.Label();
            this.axWindowsMediaPlayer1 = new AxWMPLib.AxWindowsMediaPlayer();
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).BeginInit();
            this.panOverlay.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.videoView1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.axWindowsMediaPlayer1)).BeginInit();
            this.SuspendLayout();
            // 
            // rdpClient
            // 
            this.rdpClient.Enabled = true;
            this.rdpClient.Location = new System.Drawing.Point(12, 12);
            this.rdpClient.Margin = new System.Windows.Forms.Padding(4);
            this.rdpClient.Name = "rdpClient";
            this.rdpClient.OcxState = ((System.Windows.Forms.AxHost.State)(resources.GetObject("rdpClient.OcxState")));
            this.rdpClient.Size = new System.Drawing.Size(899, 555);
            this.rdpClient.TabIndex = 2;
            this.rdpClient.Visible = false;
            // 
            // txtLog
            // 
            this.txtLog.Location = new System.Drawing.Point(1236, 204);
            this.txtLog.Margin = new System.Windows.Forms.Padding(4);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(721, 493);
            this.txtLog.TabIndex = 3;
            // 
            // lnkGiveFocus
            // 
            this.lnkGiveFocus.AutoSize = true;
            this.lnkGiveFocus.Location = new System.Drawing.Point(16, 702);
            this.lnkGiveFocus.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lnkGiveFocus.Name = "lnkGiveFocus";
            this.lnkGiveFocus.Size = new System.Drawing.Size(151, 17);
            this.lnkGiveFocus.TabIndex = 7;
            this.lnkGiveFocus.TabStop = true;
            this.lnkGiveFocus.Text = "Give RDP Client Focus";
            this.lnkGiveFocus.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkGiveFocus_LinkClicked);
            // 
            // btnDoExtenderConnect
            // 
            this.btnDoExtenderConnect.Location = new System.Drawing.Point(1236, 15);
            this.btnDoExtenderConnect.Margin = new System.Windows.Forms.Padding(4);
            this.btnDoExtenderConnect.Name = "btnDoExtenderConnect";
            this.btnDoExtenderConnect.Size = new System.Drawing.Size(369, 43);
            this.btnDoExtenderConnect.TabIndex = 10;
            this.btnDoExtenderConnect.Text = "Extender Connect";
            this.btnDoExtenderConnect.UseVisualStyleBackColor = true;
            this.btnDoExtenderConnect.Click += new System.EventHandler(this.btnExtenderConnect_Click);
            // 
            // btnExtenderDisconnect
            // 
            this.btnExtenderDisconnect.Location = new System.Drawing.Point(1236, 65);
            this.btnExtenderDisconnect.Margin = new System.Windows.Forms.Padding(4);
            this.btnExtenderDisconnect.Name = "btnExtenderDisconnect";
            this.btnExtenderDisconnect.Size = new System.Drawing.Size(369, 43);
            this.btnExtenderDisconnect.TabIndex = 11;
            this.btnExtenderDisconnect.Text = "Extender Disconnect";
            this.btnExtenderDisconnect.UseVisualStyleBackColor = true;
            this.btnExtenderDisconnect.Click += new System.EventHandler(this.BtnExtenderDisconnect_Click);
            // 
            // btnExtenderSetup
            // 
            this.btnExtenderSetup.Location = new System.Drawing.Point(1236, 116);
            this.btnExtenderSetup.Margin = new System.Windows.Forms.Padding(4);
            this.btnExtenderSetup.Name = "btnExtenderSetup";
            this.btnExtenderSetup.Size = new System.Drawing.Size(369, 43);
            this.btnExtenderSetup.TabIndex = 12;
            this.btnExtenderSetup.Text = "Extender Setup";
            this.btnExtenderSetup.UseVisualStyleBackColor = true;
            this.btnExtenderSetup.Click += new System.EventHandler(this.BtnExtenderSetup_Click);
            // 
            // lnkSendCtrlAltDelete
            // 
            this.lnkSendCtrlAltDelete.AutoSize = true;
            this.lnkSendCtrlAltDelete.Location = new System.Drawing.Point(179, 702);
            this.lnkSendCtrlAltDelete.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lnkSendCtrlAltDelete.Name = "lnkSendCtrlAltDelete";
            this.lnkSendCtrlAltDelete.Size = new System.Drawing.Size(155, 17);
            this.lnkSendCtrlAltDelete.TabIndex = 13;
            this.lnkSendCtrlAltDelete.TabStop = true;
            this.lnkSendCtrlAltDelete.Text = "Send Ctrl + Alt + Delete";
            this.lnkSendCtrlAltDelete.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkSendCtrlAltDelete_LinkClicked);
            // 
            // panOverlay
            // 
            this.panOverlay.Controls.Add(this.label1);
            this.panOverlay.Controls.Add(this.lbGenStatus);
            this.panOverlay.Location = new System.Drawing.Point(168, 90);
            this.panOverlay.Margin = new System.Windows.Forms.Padding(4);
            this.panOverlay.Name = "panOverlay";
            this.panOverlay.Size = new System.Drawing.Size(857, 368);
            this.panOverlay.TabIndex = 14;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(191, 76);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(431, 31);
            this.label1.TabIndex = 1;
            this.label1.Text = "SoftSled Media Center Extender";
            // 
            // lbGenStatus
            // 
            this.lbGenStatus.Font = new System.Drawing.Font("Microsoft Sans Serif", 11.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lbGenStatus.Location = new System.Drawing.Point(0, 286);
            this.lbGenStatus.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lbGenStatus.Name = "lbGenStatus";
            this.lbGenStatus.Size = new System.Drawing.Size(848, 82);
            this.lbGenStatus.TabIndex = 0;
            this.lbGenStatus.Text = "NA";
            this.lbGenStatus.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // chkLogDebug
            // 
            this.chkLogDebug.AutoSize = true;
            this.chkLogDebug.Checked = true;
            this.chkLogDebug.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkLogDebug.Location = new System.Drawing.Point(1309, 702);
            this.chkLogDebug.Margin = new System.Windows.Forms.Padding(4);
            this.chkLogDebug.Name = "chkLogDebug";
            this.chkLogDebug.Size = new System.Drawing.Size(174, 21);
            this.chkLogDebug.TabIndex = 15;
            this.chkLogDebug.Text = "Log Debug Information";
            this.chkLogDebug.UseVisualStyleBackColor = true;
            this.chkLogDebug.CheckedChanged += new System.EventHandler(this.chkLogDebug_CheckedChanged);
            // 
            // chkInVchanDebug
            // 
            this.chkInVchanDebug.AutoSize = true;
            this.chkInVchanDebug.Checked = true;
            this.chkInVchanDebug.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkInVchanDebug.Location = new System.Drawing.Point(1493, 702);
            this.chkInVchanDebug.Margin = new System.Windows.Forms.Padding(4);
            this.chkInVchanDebug.Name = "chkInVchanDebug";
            this.chkInVchanDebug.Size = new System.Drawing.Size(182, 21);
            this.chkInVchanDebug.TabIndex = 16;
            this.chkInVchanDebug.Text = "Include VChan In Debug";
            this.chkInVchanDebug.UseVisualStyleBackColor = true;
            // 
            // lnkShowCtrlHideInfo
            // 
            this.lnkShowCtrlHideInfo.AutoSize = true;
            this.lnkShowCtrlHideInfo.Location = new System.Drawing.Point(977, 702);
            this.lnkShowCtrlHideInfo.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lnkShowCtrlHideInfo.Name = "lnkShowCtrlHideInfo";
            this.lnkShowCtrlHideInfo.Size = new System.Drawing.Size(240, 17);
            this.lnkShowCtrlHideInfo.TabIndex = 17;
            this.lnkShowCtrlHideInfo.TabStop = true;
            this.lnkShowCtrlHideInfo.Text = "Show RDP Control + Hide Status Info";
            this.lnkShowCtrlHideInfo.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkShowCtrlHideInfo_LinkClicked);
            // 
            // videoView1
            // 
            this.videoView1.BackColor = System.Drawing.Color.Black;
            this.videoView1.Location = new System.Drawing.Point(1613, 15);
            this.videoView1.Margin = new System.Windows.Forms.Padding(4);
            this.videoView1.MediaPlayer = null;
            this.videoView1.Name = "videoView1";
            this.videoView1.Size = new System.Drawing.Size(345, 178);
            this.videoView1.TabIndex = 21;
            this.videoView1.Text = "videoView1";
            this.videoView1.Visible = false;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(1883, 177);
            this.label3.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(75, 17);
            this.label3.TabIndex = 22;
            this.label3.Text = "libvlcsharp";
            // 
            // axWindowsMediaPlayer1
            // 
            this.axWindowsMediaPlayer1.Enabled = true;
            this.axWindowsMediaPlayer1.Location = new System.Drawing.Point(19, 519);
            this.axWindowsMediaPlayer1.Name = "axWindowsMediaPlayer1";
            this.axWindowsMediaPlayer1.OcxState = ((System.Windows.Forms.AxHost.State)(resources.GetObject("axWindowsMediaPlayer1.OcxState")));
            this.axWindowsMediaPlayer1.Size = new System.Drawing.Size(310, 178);
            this.axWindowsMediaPlayer1.TabIndex = 23;
            this.axWindowsMediaPlayer1.Enter += new System.EventHandler(this.axWindowsMediaPlayer1_Enter);
            // 
            // FrmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1924, 722);
            this.Controls.Add(this.axWindowsMediaPlayer1);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.lnkShowCtrlHideInfo);
            this.Controls.Add(this.chkInVchanDebug);
            this.Controls.Add(this.chkLogDebug);
            this.Controls.Add(this.panOverlay);
            this.Controls.Add(this.lnkSendCtrlAltDelete);
            this.Controls.Add(this.btnExtenderSetup);
            this.Controls.Add(this.btnExtenderDisconnect);
            this.Controls.Add(this.btnDoExtenderConnect);
            this.Controls.Add(this.lnkGiveFocus);
            this.Controls.Add(this.txtLog);
            this.Controls.Add(this.rdpClient);
            this.Controls.Add(this.videoView1);
            this.Margin = new System.Windows.Forms.Padding(4);
            this.Name = "FrmMain";
            this.Text = "SoftSled Dev Shell";
            this.Load += new System.EventHandler(this.FrmMain_Load);
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).EndInit();
            this.panOverlay.ResumeLayout(false);
            this.panOverlay.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.videoView1)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.axWindowsMediaPlayer1)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private AxMSTSCLib.AxMsRdpClient7NotSafeForScripting rdpClient;
        private System.Windows.Forms.TextBox txtLog;
        private System.Windows.Forms.LinkLabel lnkGiveFocus;
        private System.Windows.Forms.Button btnDoExtenderConnect;
        private System.Windows.Forms.Button btnExtenderDisconnect;
        private System.Windows.Forms.Button btnExtenderSetup;
        private System.Windows.Forms.LinkLabel lnkSendCtrlAltDelete;
        private System.Windows.Forms.Panel panOverlay;
        private System.Windows.Forms.Label lbGenStatus;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.CheckBox chkLogDebug;
        private System.Windows.Forms.CheckBox chkInVchanDebug;
        private System.Windows.Forms.LinkLabel lnkShowCtrlHideInfo;
        private LibVLCSharp.WinForms.VideoView videoView1;
        private System.Windows.Forms.Label label3;
        private AxWMPLib.AxWindowsMediaPlayer axWindowsMediaPlayer1;
    }
}