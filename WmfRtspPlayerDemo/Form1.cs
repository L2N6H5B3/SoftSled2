using System;
using System.Windows.Forms;
using System.Runtime.InteropServices; // For COMException, Marshal
using MediaFoundation;
using MediaFoundation.EVR; // For Enhanced Video Renderer
using MediaFoundation.Misc; // For MFError, MFResolution, MFRect etc. // <-- Changed RECT to MFRect based on user feedback
using System.Diagnostics; // For Debug.WriteLine

namespace WmfRtspPlayerDemo {
    public partial class Form1 : Form {
        // ... (Keep variables as before) ...
        private IMFMediaSession m_mediaSession = null;
        private IMFMediaSource m_mediaSource = null;
        private IMFVideoDisplayControl m_videoDisplayControl = null;
        private IMFTopology m_topology = null;
        private int m_hr = 0; // S_OK

        // ... (Keep Form1_Load, Form1_FormClosing, btnPlay_Click, btnStop_Click, ShutdownSession) ...
        public Form1() {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e) {
            try {
                // Initialize Media Foundation
                // Use explicit value 0x70
                MFExtern.MFStartup(0x70, MFStartup.Full);
                statusLabel.Text = "Media Foundation Initialized.";
            } catch (Exception ex) {
                MessageBox.Show($"Failed to initialize Media Foundation: {ex.Message}", "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // Optionally close the form or disable functionality
                this.Close();
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) {
            // Clean up resources
            ShutdownSession();

            // Shutdown Media Foundation
            try {
                MFExtern.MFShutdown();
                Debug.WriteLine("Media Foundation Shutdown."); // Use Debug for background info
            } catch (Exception ex) {
                Debug.WriteLine($"MFShutdown Error: {ex.Message}");
            }
        }

        private void btnPlay_Click(object sender, EventArgs e) {
            string rtspUrl = txtRtspUrl.Text;
            if (string.IsNullOrWhiteSpace(rtspUrl)) {
                MessageBox.Show("Please enter an RTSP URL.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Ensure previous session is stopped and resources released
            ShutdownSession();

            try {
                statusLabel.Text = $"Attempting to load: {rtspUrl}";
                Application.DoEvents(); // Allow UI update

                // 1. Create the Media Session
                MFExtern.MFCreateMediaSession(null, out m_mediaSession);

                // 2. Create the Media Source from URL
                IMFSourceResolver sourceResolver = null;
                MFExtern.MFCreateSourceResolver(out sourceResolver);

                MFObjectType objectType = MFObjectType.Invalid;
                object sourceObj = null; // Initialize to null explicitly

                Debug.WriteLine($"Attempting CreateObjectFromURL for: {rtspUrl}"); // Debug URL

                // Let CreateObjectFromURL throw COMException on failure
                sourceResolver.CreateObjectFromURL(
                    rtspUrl,
                    MFResolution.MediaSource, // We want a media source
                    null, // No specific properties
                    out objectType,
                    out sourceObj); // Output is object

                // --- !! ADD CHECKS HERE !! ---
                if (sourceObj == null) {
                    // If CreateObjectFromURL succeeded (didn't throw) but sourceObj is null,
                    // something is fundamentally wrong with the source resolution process.
                    // This might indicate the URL is syntactically valid but points to nothing
                    // usable, or the resolver couldn't handle it.
                    statusLabel.Text = "Error: Failed to create source object (null returned).";
                    Debug.WriteLine("Error: CreateObjectFromURL returned successfully but sourceObj is null.");
                    // Clean up resolver before throwing/returning
                    if (sourceResolver != null) Marshal.ReleaseComObject(sourceResolver);
                    // You might want to throw an exception here or show a specific message box
                    throw new InvalidOperationException("Media source object creation returned null.");
                    // return; // Or just return after showing a message
                }

                Debug.WriteLine($"CreateObjectFromURL returned object of type: {sourceObj.GetType().FullName}");

                // Attempt the cast
                m_mediaSource = (IMFMediaSource)sourceObj;

                if (m_mediaSource == null) {
                    // This would be very unusual - indicates the cast failed silently.
                    statusLabel.Text = "Error: Failed to cast source object to IMFMediaSource.";
                    Debug.WriteLine("Error: Casting sourceObj to IMFMediaSource resulted in null.");
                    // Clean up resolver before throwing/returning
                    if (sourceResolver != null) Marshal.ReleaseComObject(sourceResolver);
                    throw new InvalidOperationException("Failed to cast source object to IMFMediaSource.");
                    // return; // Or just return
                }
                // --- End Checks ---


                // Release the resolver COM object - moved this after checks and assignment
                if (sourceResolver != null) Marshal.ReleaseComObject(sourceResolver);
                sourceResolver = null; // Good practice

                statusLabel.Text = "Media source created. Building topology...";
                Application.DoEvents();

                // 3. Create the Playback Topology (MANUALLY)
                Debug.WriteLine($"Calling CreateTopologyFromSource with m_mediaSource ({(m_mediaSource == null ? "NULL" : "Not NULL")})...");
                CreateTopologyFromSource(m_mediaSource, out m_topology); // Let this throw on error
                Debug.WriteLine("CreateTopologyFromSource completed.");
                statusLabel.Text = "Topology created.";
                Application.DoEvents();

                // 4. Get the Video Display Control (for the EVR)
                object serviceObj = null; // Use object for the output parameter
                try {
                    // Use standard HRESULT return check convention for GetService if needed
                    m_hr = MFExtern.MFGetService(
                        m_mediaSession, // Can get service from Session
                        MFServices.MR_VIDEO_RENDER_SERVICE,
                        typeof(IMFVideoDisplayControl).GUID,
                        out serviceObj); // Use 'out object'

                    if (m_hr >= 0 && serviceObj != null) // Check HRESULT >= S_OK (0)
                    {
                        m_videoDisplayControl = (IMFVideoDisplayControl)serviceObj;
                        m_videoDisplayControl.SetAspectRatioMode(MFVideoAspectRatioMode.PreservePicture);
                    } else {
                        Debug.WriteLine($"Warning: Could not get IMFVideoDisplayControl service (HR=0x{m_hr:X}). Video might not display correctly.");
                    }
                } catch (COMException getServiceEx) {
                    Debug.WriteLine($"Warning: Could not get IMFVideoDisplayControl service (HR=0x{getServiceEx.HResult:X}). Video might not display correctly.");
                    // Continue even if we can't get the control explicitly
                }


                // 5. Set the Topology on the Media Session
                // Use integer 0 for no flags (instead of MFSetTopologyFlags.None)
                m_mediaSession.SetTopology(0, m_topology); // Use 0 for flags

                // 6. Start Playback
                PropVariant varStart = new PropVariant();
                m_mediaSession.Start(Guid.Empty, varStart); // Use Guid.Empty

                statusLabel.Text = "Playback session started.";

            } catch (COMException comEx) {
                // HResult property should exist on COMException
                string errorDetails = MFError.GetErrorText(comEx.HResult);
                statusLabel.Text = $"Error: {errorDetails ?? comEx.Message}";
                MessageBox.Show($"Failed to play RTSP stream.\nError code: 0x{comEx.ErrorCode:X} (HR: 0x{comEx.HResult:X})\nDetails: {errorDetails ?? comEx.Message}",
                                "Playback Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShutdownSession(); // Clean up on failure
            } catch (Exception ex) {
                statusLabel.Text = $"Error: {ex.Message}";
                MessageBox.Show($"An unexpected error occurred: {ex.Message}",
                                "Playback Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShutdownSession(); // Clean up on failure
            }
        }


        // --- Updated CreateTopologyFromSource ---
        private void CreateTopologyFromSource(IMFMediaSource mediaSource, out IMFTopology topology) {
            topology = null;
            IMFPresentationDescriptor presDesc = null;

            try {
                // Create an empty topology object.
                MFExtern.MFCreateTopology(out topology);

                // Get the presentation descriptor for the media source.
                mediaSource.CreatePresentationDescriptor(out presDesc);

                // Get the number of streams in the presentation.
                int sourceStreams;
                presDesc.GetStreamDescriptorCount(out sourceStreams);

                // For each stream, create a source and output node and add it to the topology.
                for (int i = 0; i < sourceStreams; i++) {
                    bool selected;
                    IMFStreamDescriptor streamDesc = null; // Initialize to null
                    IMFMediaTypeHandler handler = null;    // Initialize handler to null

                    try // Add try block for releasing streamDesc and handler
                    {
                        // Get the stream descriptor for this stream index.
                        presDesc.GetStreamDescriptorByIndex(i, out selected, out streamDesc);

                        // If the stream is selected by default, add it to the topology.
                        if (selected) {
                            IMFActivate rendererActivate = null;
                            IMFTopologyNode sourceNode = null;
                            IMFTopologyNode outputNode = null;

                            try // Add try block for releasing nodes/activator
                            {
                                // Create the topology node for the source stream.
                                // Try SourceStreamNode again, ensure casing is exact.
                                // If this fails, inspect MFTopologyType enum in your library.
                                MFExtern.MFCreateTopologyNode(MFTopologyType.SourcestreamNode, out sourceNode);

                                // Associate the source node with the media source and stream.
                                sourceNode.SetUnknown(MFAttributesClsid.MF_TOPONODE_SOURCE, mediaSource);
                                sourceNode.SetUnknown(MFAttributesClsid.MF_TOPONODE_PRESENTATION_DESCRIPTOR, presDesc);
                                sourceNode.SetUnknown(MFAttributesClsid.MF_TOPONODE_STREAM_DESCRIPTOR, streamDesc);

                                // Add the source node to the topology.
                                topology.AddNode(sourceNode);

                                // --- Corrected Media Type Handling ---
                                // Get the media type handler using the out parameter
                                streamDesc.GetMediaTypeHandler(out handler); // Assuming this throws on error

                                // Get the major type from the handler
                                Guid majorType;
                                handler.GetMajorType(out majorType);
                                // --- End Correction ---

                                if (majorType == MFMediaType.Audio) {
                                    MFExtern.MFCreateAudioRendererActivate(out rendererActivate);
                                    Debug.WriteLine("Created Audio Renderer Activate");
                                } else if (majorType == MFMediaType.Video) {
                                    MFExtern.MFCreateVideoRendererActivate(panelVideo.Handle, out rendererActivate);
                                    Debug.WriteLine("Created Video Renderer Activate");
                                } else {
                                    Debug.WriteLine($"Skipping unsupported stream type: {majorType}");
                                    continue; // Skip to next stream (handler/streamDesc released in finally)
                                }

                                // Create the topology node for the renderer. Use OutputNode.
                                MFExtern.MFCreateTopologyNode(MFTopologyType.OutputNode, out outputNode);

                                // Associate the output node with the renderer activation object.
                                outputNode.SetObject(rendererActivate);

                                // Add the output node to the topology.
                                topology.AddNode(outputNode);

                                // Connect the source node to the output node.
                                sourceNode.ConnectOutput(0, outputNode, 0);
                                Debug.WriteLine($"Connected stream index {i}");
                            } finally {
                                // Release temporary node and activation objects for this stream
                                if (sourceNode != null) Marshal.ReleaseComObject(sourceNode);
                                if (outputNode != null) Marshal.ReleaseComObject(outputNode);
                                if (rendererActivate != null) Marshal.ReleaseComObject(rendererActivate);
                            }
                        }
                    } // End if(selected)
                    finally {
                        // Release handler and stream descriptor for this iteration
                        if (handler != null) Marshal.ReleaseComObject(handler);
                        if (streamDesc != null) Marshal.ReleaseComObject(streamDesc);
                    }
                } // End for loop
            } catch // Catch any exception during topology build
              {
                // Clean up topology if created partially
                if (topology != null) {
                    Marshal.ReleaseComObject(topology);
                    topology = null;
                }
                throw; // Re-throw the exception to be caught by btnPlay_Click
            } finally {
                // Release presentation descriptor
                if (presDesc != null) Marshal.ReleaseComObject(presDesc);
            }
        }

        private void btnStop_Click(object sender, EventArgs e) {
            ShutdownSession();
            statusLabel.Text = "Playback stopped.";
        }

        // ShutdownSession (Keep the version from the previous correction)
        private void ShutdownSession() {
            Debug.WriteLine("ShutdownSession called.");
            try {
                // Stop the session - Remove state check
                if (m_mediaSession != null) {
                    Debug.WriteLine("Attempting to stop session...");
                    try {
                        m_mediaSession.Stop(); // Best effort stop
                    } catch (COMException ex) {
                        // Ignore errors like "invalid state" if already stopped/closed
                        Debug.WriteLine($"Ignoring error during session Stop: 0x{ex.HResult:X}");
                    } catch (InvalidComObjectException) {
                        Debug.WriteLine("Ignoring error during session Stop: Session already released.");
                    }
                }

                // Shutdown the session object (signals pipeline to release resources)
                if (m_mediaSession != null) {
                    Debug.WriteLine("Attempting to shutdown session object...");
                    try {
                        m_mediaSession.Shutdown();
                    } catch (COMException ex) {
                        Debug.WriteLine($"Ignoring error during session Shutdown: 0x{ex.HResult:X}");
                    } catch (InvalidComObjectException) {
                        Debug.WriteLine("Ignoring error during session Shutdown: Session already released.");
                    }
                }

                // Release the topology
                if (m_topology != null) {
                    Debug.WriteLine("Releasing topology...");
                    Marshal.ReleaseComObject(m_topology);
                    m_topology = null;
                }

                // Release the video display control
                if (m_videoDisplayControl != null) {
                    Debug.WriteLine("Releasing video display control...");
                    Marshal.ReleaseComObject(m_videoDisplayControl);
                    m_videoDisplayControl = null;
                }

                // Shutdown and release the media source
                if (m_mediaSource != null) {
                    Debug.WriteLine("Attempting to shutdown source...");
                    try { m_mediaSource.Shutdown(); } catch { /* Ignore */ }
                    Debug.WriteLine("Releasing source...");
                    Marshal.ReleaseComObject(m_mediaSource);
                    m_mediaSource = null;
                }

                // Close and release the session itself last
                if (m_mediaSession != null) {
                    Debug.WriteLine("Attempting to close session object...");
                    try { m_mediaSession.Close(); } catch { /* Ignore */ }
                    Debug.WriteLine("Releasing session object...");
                    Marshal.ReleaseComObject(m_mediaSession);
                    m_mediaSession = null;
                }
                Debug.WriteLine("ShutdownSession finished.");
            } catch (Exception ex) // Catch any unexpected errors during cleanup
              {
                Debug.WriteLine($"Unexpected error during session shutdown: {ex.Message}");
            } finally {
                // Ensure references are cleared
                m_mediaSession = null;
                m_videoDisplayControl = null;
                m_mediaSource = null;
                m_topology = null;
                // Invalidate panel only if handle is created to prevent errors during form closing
                if (panelVideo != null && panelVideo.IsHandleCreated) {
                    panelVideo.Invalidate();
                }
            }
        }


        // --- Updated panelVideo_Resize --- Use MFRect
        private void panelVideo_Resize(object sender, EventArgs e) {
            if (m_videoDisplayControl != null && m_mediaSession != null) // Check if control and session exist
            {
                try {
                    // Use MediaFoundation.Misc.MFRect explicitly (based on user feedback)
                    MediaFoundation.Misc.MFRect panelRect = new MediaFoundation.Misc.MFRect(
                        panelVideo.ClientRectangle.Left,
                        panelVideo.ClientRectangle.Top,
                        panelVideo.ClientRectangle.Right,
                        panelVideo.ClientRectangle.Bottom);

                    MFVideoNormalizedRect normRect = new MFVideoNormalizedRect(0, 0, 1, 1);

                    // SetVideoPosition might throw if session is stopped/closed
                    m_videoDisplayControl.SetVideoPosition(normRect, panelRect);
                } catch (COMException ex) {
                    // Ignore errors if session is not in a valid state for this call
                    Debug.WriteLine($"Resize Error (SetVideoPosition): 0x{ex.HResult:X}");
                } catch (InvalidComObjectException) {
                    // Ignore if session/control has been released during shutdown
                    Debug.WriteLine("Resize Error: COM object released.");
                } catch (Exception ex) // Catch any other unexpected errors
                  {
                    Debug.WriteLine($"Unexpected resize Error: {ex.Message}");
                }
            }
        }

    } // End Class Form1
} // End Namespace