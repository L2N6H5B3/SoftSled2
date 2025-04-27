using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using FFmpeg.AutoGen; // Requires FFmpeg.AutoGen NuGet package
using SoftSled.Components.NativeDecoding;
// using WmrptHandling; // Namespace for your WmrptVideoDepacketizer if used here

namespace WinFormsVideoPlayer {
    public partial class VideoForm : Form {
        private PictureBox pictureBoxDisplay;
        public H264Decoder videoDecoder;
        private FrameConverter frameConverter;
        // private WmrptVideoDepacketizer videoDepacketizer; // If used here

        private Bitmap currentBitmap = null; // To hold the bitmap for display
        private object bitmapLock = new object(); // For thread safety

        public VideoForm() {
            InitializeComponent(); // Assumes PictureBox named "pictureBoxDisplay" exists
            InitializeDecoder();

            // Example: Start receiving RTP packets and feeding the depacketizer/decoder
            // StartRtpProcessing();
        }

        private void InitializeComponent() {
            this.pictureBoxDisplay = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxDisplay)).BeginInit();
            this.SuspendLayout();
            //
            // pictureBoxDisplay
            //
            this.pictureBoxDisplay.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pictureBoxDisplay.Location = new System.Drawing.Point(0, 0);
            this.pictureBoxDisplay.Name = "pictureBoxDisplay";
            this.pictureBoxDisplay.Size = new System.Drawing.Size(800, 450);
            this.pictureBoxDisplay.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom; // Or StretchImage
            this.pictureBoxDisplay.TabIndex = 0;
            this.pictureBoxDisplay.TabStop = false;
            //
            // VideoForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.pictureBoxDisplay);
            this.Name = "VideoForm";
            this.Text = "Video Playback";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.VideoForm_FormClosing);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxDisplay)).EndInit();
            this.ResumeLayout(false);
        }


        private void InitializeDecoder() {
            videoDecoder = new H264Decoder();
            frameConverter = new FrameConverter();

            if (!videoDecoder.Initialize()) {
                MessageBox.Show("Failed to initialize video decoder.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
                return;
            }

            videoDecoder.FrameDecoded += Decoder_FrameDecoded;
        }

        // This is where you would integrate your RTP receiver and WmrptVideoDepacketizer
        // For demonstration, we'll simulate feeding NAL units
        /*
        private void StartRtpProcessing()
        {
            // 1. Setup your RTP receiver and WmrptVideoDepacketizer instance
            // videoDepacketizer = new WmrptVideoDepacketizer();
            // videoDepacketizer.NalUnitReady += VideoDepacketizer_NalUnitReady;
            // Start receiving RTP packets...
        }

        private void VideoDepacketizer_NalUnitReady(object sender, byte[] nalUnit)
        {
            // Important: Prepend Annex B start code if your depacketizer doesn't
            byte[] annexBStartCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };
            byte[] nalUnitWithStartCode = new byte[annexBStartCode.Length + nalUnit.Length];
            Buffer.BlockCopy(annexBStartCode, 0, nalUnitWithStartCode, 0, annexBStartCode.Length);
            Buffer.BlockCopy(nalUnit, 0, nalUnitWithStartCode, annexBStartCode.Length, nalUnit.Length);

            // Feed the decoder (pass appropriate PTS if available)
            videoDecoder?.DecodeNalUnits(nalUnitWithStartCode, nalUnitWithStartCode.Length);
        }
        */


        private unsafe void Decoder_FrameDecoded(object sender, DecodedFrameEventArgs e) {
            try {
                // Convert the frame to BGRA format for display
                AVFrame* bgraFrame = frameConverter.ConvertFrame(e.Frame);
                if (bgraFrame == null) {
                    Trace.WriteLine("Frame conversion failed.");
                    return;
                }

                Bitmap newBitmap = null;
                lock (bitmapLock) // Protect bitmap access
                {
                    // Create a new Bitmap from the converted BGRA frame data
                    newBitmap = new Bitmap(
                        bgraFrame->width,
                        bgraFrame->height,
                        bgraFrame->linesize[0], // Stride
                        PixelFormat.Format32bppArgb, // BGRA corresponds to Argb32 in GDI+
                        (IntPtr)bgraFrame->data[0]
                    );

                    // We need to clone the bitmap because the underlying buffer
                    // (_destBuffer in FrameConverter) will be reused or freed.
                    // Cloning copies the pixel data into managed memory.
                    Bitmap clonedBitmap = newBitmap.Clone(
                        new Rectangle(0, 0, newBitmap.Width, newBitmap.Height),
                        newBitmap.PixelFormat);

                    // Dispose the intermediate bitmap created directly from the pointer
                    newBitmap.Dispose();
                    newBitmap = clonedBitmap; // Use the cloned bitmap
                }


                // Update the PictureBox on the UI thread
                // Use BeginInvoke for better responsiveness, Invoke waits for completion
                this.BeginInvoke((MethodInvoker)delegate {
                    if (pictureBoxDisplay.IsDisposed) return;

                    lock (bitmapLock) {
                        // Swap bitmaps and dispose the old one
                        Bitmap oldBitmap = (Bitmap)pictureBoxDisplay.Image;
                        pictureBoxDisplay.Image = newBitmap; // Assign the cloned bitmap
                        oldBitmap?.Dispose(); // Dispose the previous bitmap
                    }
                });
            } catch (Exception ex) {
                Trace.WriteLine($"Error processing/displaying decoded frame: {ex.Message}");
                // Consider stopping playback or logging more details
            }
        }

        private void VideoForm_FormClosing(object sender, FormClosingEventArgs e) {
            Trace.WriteLine("VideoForm closing...");
            // Clean up resources
            // Stop your RTP processing loop here
            videoDecoder?.Dispose();
            frameConverter?.Dispose();
            // Dispose the last bitmap shown
            lock (bitmapLock) {
                ((Bitmap)pictureBoxDisplay.Image)?.Dispose();
                pictureBoxDisplay.Image = null;
            }
            Trace.WriteLine("VideoForm closed.");
        }
    }
}
