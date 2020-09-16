namespace SoftSled
{
    partial class frmMain
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(frmMain));
            this.rdpClient = new AxMSTSCLib.AxMsRdpClient7();
            this.txtLog = new System.Windows.Forms.TextBox();
            this.button2 = new System.Windows.Forms.Button();
            this.lnkGiveFocus = new System.Windows.Forms.LinkLabel();
            this.linkLabel2 = new System.Windows.Forms.LinkLabel();
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
            this.label2 = new System.Windows.Forms.Label();
            this.button1 = new System.Windows.Forms.Button();
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).BeginInit();
            this.panOverlay.SuspendLayout();
            this.SuspendLayout();
            // 
            // rdpClient
            // 
            this.rdpClient.Enabled = true;
            this.rdpClient.Location = new System.Drawing.Point(12, 12);
            this.rdpClient.Name = "rdpClient";
            this.rdpClient.OcxState = ((System.Windows.Forms.AxHost.State)(resources.GetObject("rdpClient.OcxState")));
            this.rdpClient.Size = new System.Drawing.Size(899, 555);
            this.rdpClient.TabIndex = 2;
            this.rdpClient.Visible = false;
            // 
            // txtLog
            // 
            this.txtLog.Location = new System.Drawing.Point(927, 166);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(333, 401);
            this.txtLog.TabIndex = 3;
            // 
            // button2
            // 
            this.button2.Location = new System.Drawing.Point(927, 135);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(134, 22);
            this.button2.TabIndex = 6;
            this.button2.Text = "Reset devcaps Iteration";
            this.button2.UseVisualStyleBackColor = true;
            // 
            // lnkGiveFocus
            // 
            this.lnkGiveFocus.AutoSize = true;
            this.lnkGiveFocus.Location = new System.Drawing.Point(12, 570);
            this.lnkGiveFocus.Name = "lnkGiveFocus";
            this.lnkGiveFocus.Size = new System.Drawing.Size(116, 13);
            this.lnkGiveFocus.TabIndex = 7;
            this.lnkGiveFocus.TabStop = true;
            this.lnkGiveFocus.Text = "Give RDP Client Focus";
            this.lnkGiveFocus.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkGiveFocus_LinkClicked);
            // 
            // linkLabel2
            // 
            this.linkLabel2.AutoSize = true;
            this.linkLabel2.Location = new System.Drawing.Point(924, 570);
            this.linkLabel2.Name = "linkLabel2";
            this.linkLabel2.Size = new System.Drawing.Size(52, 13);
            this.linkLabel2.TabIndex = 9;
            this.linkLabel2.TabStop = true;
            this.linkLabel2.Text = "Clear Log";
            this.linkLabel2.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.linkLabel2_LinkClicked);
            // 
            // btnDoExtenderConnect
            // 
            this.btnDoExtenderConnect.Location = new System.Drawing.Point(927, 12);
            this.btnDoExtenderConnect.Name = "btnDoExtenderConnect";
            this.btnDoExtenderConnect.Size = new System.Drawing.Size(277, 35);
            this.btnDoExtenderConnect.TabIndex = 10;
            this.btnDoExtenderConnect.Text = "Extender Connect";
            this.btnDoExtenderConnect.UseVisualStyleBackColor = true;
            this.btnDoExtenderConnect.Click += new System.EventHandler(this.btnExtenderConnect_Click);
            // 
            // btnExtenderDisconnect
            // 
            this.btnExtenderDisconnect.Location = new System.Drawing.Point(927, 53);
            this.btnExtenderDisconnect.Name = "btnExtenderDisconnect";
            this.btnExtenderDisconnect.Size = new System.Drawing.Size(277, 35);
            this.btnExtenderDisconnect.TabIndex = 11;
            this.btnExtenderDisconnect.Text = "Extender Disconnect";
            this.btnExtenderDisconnect.UseVisualStyleBackColor = true;
            this.btnExtenderDisconnect.Click += new System.EventHandler(this.btnExtenderDisconnect_Click);
            // 
            // btnExtenderSetup
            // 
            this.btnExtenderSetup.Location = new System.Drawing.Point(927, 94);
            this.btnExtenderSetup.Name = "btnExtenderSetup";
            this.btnExtenderSetup.Size = new System.Drawing.Size(277, 35);
            this.btnExtenderSetup.TabIndex = 12;
            this.btnExtenderSetup.Text = "Extender Setup";
            this.btnExtenderSetup.UseVisualStyleBackColor = true;
            this.btnExtenderSetup.Click += new System.EventHandler(this.btnExtenderSetup_Click);
            // 
            // lnkSendCtrlAltDelete
            // 
            this.lnkSendCtrlAltDelete.AutoSize = true;
            this.lnkSendCtrlAltDelete.Location = new System.Drawing.Point(134, 570);
            this.lnkSendCtrlAltDelete.Name = "lnkSendCtrlAltDelete";
            this.lnkSendCtrlAltDelete.Size = new System.Drawing.Size(117, 13);
            this.lnkSendCtrlAltDelete.TabIndex = 13;
            this.lnkSendCtrlAltDelete.TabStop = true;
            this.lnkSendCtrlAltDelete.Text = "Send Ctrl + Alt + Delete";
            this.lnkSendCtrlAltDelete.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkSendCtrlAltDelete_LinkClicked);
            // 
            // panOverlay
            // 
            this.panOverlay.Controls.Add(this.label1);
            this.panOverlay.Controls.Add(this.lbGenStatus);
            this.panOverlay.Location = new System.Drawing.Point(126, 73);
            this.panOverlay.Name = "panOverlay";
            this.panOverlay.Size = new System.Drawing.Size(643, 299);
            this.panOverlay.TabIndex = 14;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(143, 62);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(350, 25);
            this.label1.TabIndex = 1;
            this.label1.Text = "SoftSled Media Center Extender";
            // 
            // lbGenStatus
            // 
            this.lbGenStatus.Font = new System.Drawing.Font("Microsoft Sans Serif", 11.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lbGenStatus.Location = new System.Drawing.Point(0, 232);
            this.lbGenStatus.Name = "lbGenStatus";
            this.lbGenStatus.Size = new System.Drawing.Size(636, 67);
            this.lbGenStatus.TabIndex = 0;
            this.lbGenStatus.Text = "NA";
            this.lbGenStatus.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // chkLogDebug
            // 
            this.chkLogDebug.AutoSize = true;
            this.chkLogDebug.Checked = true;
            this.chkLogDebug.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkLogDebug.Location = new System.Drawing.Point(982, 570);
            this.chkLogDebug.Name = "chkLogDebug";
            this.chkLogDebug.Size = new System.Drawing.Size(134, 17);
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
            this.chkInVchanDebug.Location = new System.Drawing.Point(1120, 570);
            this.chkInVchanDebug.Name = "chkInVchanDebug";
            this.chkInVchanDebug.Size = new System.Drawing.Size(143, 17);
            this.chkInVchanDebug.TabIndex = 16;
            this.chkInVchanDebug.Text = "Include VChan In Debug";
            this.chkInVchanDebug.UseVisualStyleBackColor = true;
            // 
            // lnkShowCtrlHideInfo
            // 
            this.lnkShowCtrlHideInfo.AutoSize = true;
            this.lnkShowCtrlHideInfo.Location = new System.Drawing.Point(733, 570);
            this.lnkShowCtrlHideInfo.Name = "lnkShowCtrlHideInfo";
            this.lnkShowCtrlHideInfo.Size = new System.Drawing.Size(184, 13);
            this.lnkShowCtrlHideInfo.TabIndex = 17;
            this.lnkShowCtrlHideInfo.TabStop = true;
            this.lnkShowCtrlHideInfo.Text = "Show RDP Control + Hide Status Info";
            this.lnkShowCtrlHideInfo.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.lnkShowCtrlHideInfo_LinkClicked);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(371, 575);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(35, 13);
            this.label2.TabIndex = 19;
            this.label2.Text = "label2";
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(1067, 135);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(137, 22);
            this.button1.TabIndex = 20;
            this.button1.Text = "Reset avctrl Iteration";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click_1);
            // 
            // frmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1266, 587);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.lnkShowCtrlHideInfo);
            this.Controls.Add(this.chkInVchanDebug);
            this.Controls.Add(this.chkLogDebug);
            this.Controls.Add(this.panOverlay);
            this.Controls.Add(this.lnkSendCtrlAltDelete);
            this.Controls.Add(this.btnExtenderSetup);
            this.Controls.Add(this.btnExtenderDisconnect);
            this.Controls.Add(this.btnDoExtenderConnect);
            this.Controls.Add(this.linkLabel2);
            this.Controls.Add(this.lnkGiveFocus);
            this.Controls.Add(this.button2);
            this.Controls.Add(this.txtLog);
            this.Controls.Add(this.rdpClient);
            this.Name = "frmMain";
            this.Text = "SoftSled Dev Shell";
            this.Load += new System.EventHandler(this.frmMain_Load);
            ((System.ComponentModel.ISupportInitialize)(this.rdpClient)).EndInit();
            this.panOverlay.ResumeLayout(false);
            this.panOverlay.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private AxMSTSCLib.AxMsRdpClient7 rdpClient;
        private System.Windows.Forms.TextBox txtLog;
        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.LinkLabel lnkGiveFocus;
        private System.Windows.Forms.LinkLabel linkLabel2;
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
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Button button1;
    }
}