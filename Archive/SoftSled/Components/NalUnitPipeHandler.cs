using System;
using System.Collections.Generic; // Added for IEnumerable
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NalUnitHandling.Pipe {
    /// <summary>
    /// Handles piping complete H.264 NAL units (Network Abstraction Layer units)
    /// to an external ffplay process after prepending Annex B start codes.
    /// Assumes the input byte arrays are complete NAL units (e.g., from an RTP depacketizer).
    /// </summary>
    public class NalUnitPipeHandler : IDisposable {
        private readonly string _ffplayPath;
        private readonly string _ffplayArguments;
        private Process _ffplayProcess;
        private Stream _ffplayInputStream;
        private bool _disposed = false;
        private CancellationTokenSource _errorReadCts;

        // Annex B start code (0x00 0x00 0x00 0x01)
        private static readonly byte[] AnnexBStartCode = { 0x00, 0x00, 0x00, 0x01 };

        /// <summary>
        /// Event raised when ffplay writes to its standard error stream.
        /// </summary>
        public event EventHandler<string> FfplayErrorDataReceived;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ffplayPath">Full path to ffplay.exe.</param>
        /// <param name="ffplayArguments">
        /// Command line arguments for ffplay. Must include "-f h264 -i pipe:0".
        /// Example: "-f h264 -i pipe:0"
        /// Example with size hint: "-f h264 -video_size 1280x720 -i pipe:0"
        /// </param>
        public NalUnitPipeHandler(string ffplayPath, string ffplayArguments) {
            if (string.IsNullOrEmpty(ffplayPath)) throw new ArgumentNullException(nameof(ffplayPath));
            if (!File.Exists(ffplayPath)) throw new FileNotFoundException("ffplay.exe not found at specified path.", ffplayPath);
            // Ensure required arguments are present
            //if (string.IsNullOrEmpty(ffplayArguments) || !ffplayArguments.Contains("-i pipe:0") || !ffplayArguments.Contains("-f h264")) {
            if (string.IsNullOrEmpty(ffplayArguments) || !ffplayArguments.Contains("-i pipe:0")) {
                    throw new ArgumentException("ffplayArguments must include '-f h264 -i pipe:0' to read H.264 from standard input.", nameof(ffplayArguments));
            }

            _ffplayPath = ffplayPath;
            _ffplayArguments = ffplayArguments;
            Trace.WriteLine($"NalUnitPipeHandler configured for Path: {_ffplayPath}");
        }

        /// <summary>
        /// Starts the ffplay process.
        /// </summary>
        /// <returns>True if the process started successfully, false otherwise.</returns>
        public bool Start() {
            if (_ffplayProcess != null && !_ffplayProcess.HasExited) {
                Trace.WriteLine("ffplay process already running.");
                return true;
            }
            if (_disposed) {
                Trace.WriteLine("Error: Cannot start, handler has been disposed.");
                return false;
            }

            Trace.WriteLine($"Starting ffplay with args: {_ffplayArguments}");
            var startInfo = new ProcessStartInfo {
                FileName = _ffplayPath,
                Arguments = _ffplayArguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true, // Prevent blocking
                RedirectStandardError = true,
                CreateNoWindow = true, // Usually preferred
            };

            try {
                _ffplayProcess = Process.Start(startInfo);
                if (_ffplayProcess == null) {
                    Trace.WriteLine("Error: Failed to start ffplay process (Process.Start returned null).");
                    return false;
                }

                // Use a buffered stream for potentially better performance with many small writes
                _ffplayInputStream = new BufferedStream(_ffplayProcess.StandardInput.BaseStream);

                _errorReadCts = new CancellationTokenSource();
                Task.Run(() => ReadStdErrorAsync(_ffplayProcess, _errorReadCts.Token));

                Trace.WriteLine($"ffplay process started (PID: {_ffplayProcess.Id}).");
                return true;
            } catch (Exception ex) {
                Trace.WriteLine($"Error starting ffplay process: {ex.Message}");
                _ffplayProcess?.Dispose();
                _ffplayProcess = null;
                _ffplayInputStream = null;
                return false;
            }
        }

        /// <summary>
        /// Stops the ffplay process gracefully by closing its input stream.
        /// </summary>
        public void Stop() {
            if (_disposed) return;
            // Check if process exists before trying to access HasExited
            Process processToCheck = _ffplayProcess;
            if (processToCheck == null) return;

            bool hasExited = false;
            try {
                hasExited = processToCheck.HasExited; // Can throw if access denied after exit
            } catch {
                hasExited = true; // Assume exited if we can't check
            }
            // Inside the Stop method, within the try block, after canceling CTS:
            if (_ffplayProcess != null) // Check if process object exists
            {
                bool alreadyExited = false;
                int exitCode = -999; // Default value
                try {
                    alreadyExited = _ffplayProcess.HasExited; // Check if already exited
                    if (alreadyExited) exitCode = _ffplayProcess.ExitCode;
                } catch { /* Ignore errors checking state */ }

                if (!alreadyExited && _ffplayProcess.WaitForExit(5000)) // Wait only if not already exited
                {
                    try { exitCode = _ffplayProcess.ExitCode; } catch { /* Ignore */ }
                    Trace.WriteLine($"ffplay process exited gracefully (Exit Code: {exitCode}).");
                } else if (!alreadyExited) // Didn't exit gracefully within timeout
                  {
                    Trace.WriteLine($"ffplay process did not exit gracefully. Killing. (Exit code if available before kill: {exitCode})");
                    try { if (!_ffplayProcess.HasExited) _ffplayProcess.Kill(); } catch (Exception killEx) { Trace.WriteLine($"Kill exception: {killEx.Message}"); }
                } else // Already exited before WaitForExit
                  {
                    Trace.WriteLine($"ffplay process had already exited (Exit Code: {exitCode}).");
                }
            }
            if (hasExited) return;


            Trace.WriteLine($"Stopping ffplay process (PID: {processToCheck?.Id})...");
            try {
                // Close the buffered stream, which closes the underlying stream
                _ffplayInputStream?.Close();
                _ffplayInputStream = null;
                _errorReadCts?.Cancel();

                // Re-check process before waiting
                processToCheck = _ffplayProcess;
                if (processToCheck != null && !processToCheck.HasExited) {
                    if (processToCheck.WaitForExit(5000)) {
                        Trace.WriteLine($"ffplay process exited gracefully (Exit Code: {processToCheck.ExitCode}).");
                    } else {
                        int? exitCodeBeforeKill = null;
                        bool alreadyExited = false;
                        try { alreadyExited = processToCheck.HasExited; if (alreadyExited) exitCodeBeforeKill = processToCheck.ExitCode; } catch { /* Ignore */ }
                        Trace.WriteLine($"ffplay process did not exit gracefully (Already Exited: {alreadyExited}, Code: {exitCodeBeforeKill?.ToString() ?? "N/A"}). Killing.");
                        try { if (!processToCheck.HasExited) processToCheck.Kill(); } catch (Exception killEx) { Trace.WriteLine($"Kill exception: {killEx.Message}"); }
                    }
                }
            } catch (InvalidOperationException ex) { Trace.WriteLine($"Error stopping ffplay (likely already exited): {ex.Message}"); } catch (IOException ex) { Trace.WriteLine($"IO Error stopping ffplay: {ex.Message}"); } catch (Exception ex) { Trace.WriteLine($"Error stopping ffplay process: {ex.Message}"); } finally {
                _ffplayProcess?.Dispose();
                _ffplayProcess = null;
                _errorReadCts?.Dispose();
                _errorReadCts = null;
                Trace.WriteLine("ffplay process stop routine finished.");
            }
        }

        /// <summary>
        /// Processes a collection of complete H.264 NAL units representing one video frame.
        /// Prepends the Annex B start code to each NAL unit and writes the result
        /// sequentially to the ffplay process's standard input.
        /// </summary>
        /// <param name="nalUnits">A collection (e.g., List<byte[]>) of byte arrays, each containing one complete NAL unit for the frame.</param>
        public async Task ProcessFrameNalUnitsAsync(IEnumerable<byte[]> nalUnits) {
            if (nalUnits == null) {
                Trace.WriteLine("Warning: Received null NAL unit collection. Skipping.");
                return;
            }

            if (_disposed || _ffplayProcess == null || _ffplayProcess.HasExited || _ffplayInputStream == null) {
                // Don't process if stopped, disposed, or ffplay isn't running
                return;
            }

            try {
                foreach (byte[] nalUnit in nalUnits) {
                    if (nalUnit == null || nalUnit.Length == 0) {
                        // Trace.WriteLine("Warning: Skipping null or empty NAL unit within frame.");
                        continue; // Skip empty NAL units in the list
                    }

                    //// 1. Write the Annex B start code
                    //await _ffplayInputStream.WriteAsync(AnnexBStartCode, 0, AnnexBStartCode.Length);

                    // 2. Write the NAL unit data itself
                    await _ffplayInputStream.WriteAsync(nalUnit, 0, nalUnit.Length);
                }
                // Optionally flush after writing all NAL units for a frame
                await _ffplayInputStream.FlushAsync();
            } catch (IOException ex) // Catches pipe closed errors
              {
                Trace.WriteLine($"IO Error writing frame NAL units to ffplay stdin (pipe likely closed): {ex.Message}");
                Stop(); // Stop if we can no longer write
            } catch (ObjectDisposedException) {
                Trace.WriteLine("Error writing frame NAL units to ffplay stdin: Input stream disposed.");
            } catch (Exception ex) {
                Trace.WriteLine($"Error writing frame NAL units to ffplay stdin: {ex.Message}");
            }
        }


        /// <summary>
        /// Processes a single complete H.264 NAL unit. Prepends the Annex B start code
        /// and writes the result to the ffplay process's standard input.
        /// (Kept for cases where individual NAL units are handled, e.g., SPS/PPS separately)
        /// </summary>
        /// <param name="nalUnit">A byte array containing one complete NAL unit (e.g., SPS, PPS, IDR, P-frame).</param>
        public async Task ProcessNalUnitAsync(byte[] nalUnit) {
            if (nalUnit == null || nalUnit.Length == 0) {
                Trace.WriteLine("Warning: Received null or empty NAL unit. Skipping.");
                return;
            }

            if (_disposed || _ffplayProcess == null || _ffplayProcess.HasExited || _ffplayInputStream == null) {
                // Don't process if stopped, disposed, or ffplay isn't running
                return;
            }

            try {
                if (_ffplayInputStream != null) {

                    //// 1. Write the Annex B start code
                    //await _ffplayInputStream.WriteAsync(AnnexBStartCode, 0, AnnexBStartCode.Length);

                    // 2. Write the NAL unit data itself
                    await _ffplayInputStream.WriteAsync(nalUnit, 0, nalUnit.Length);

                }
                // Optional: Flush if needed
                // await _ffplayInputStream.FlushAsync();
            } catch (IOException ex) // Catches pipe closed errors
              {
                Trace.WriteLine($"IO Error writing NAL unit to ffplay stdin (pipe likely closed): {ex.Message}");
                Stop(); // Stop if we can no longer write
            } catch (ObjectDisposedException) {
                Trace.WriteLine("Error writing NAL unit to ffplay stdin: Input stream disposed.");
            } catch (Exception ex) {
                Trace.WriteLine($"Error writing NAL unit to ffplay stdin: {ex.Message}");
            }
        }

        /// <summary>
        /// Asynchronously reads the standard error stream of the ffplay process.
        /// </summary>
        private async Task ReadStdErrorAsync(Process process, CancellationToken cancellationToken) {
            // Identical implementation to the previous handler...
            try {
                if (process == null || process.HasExited || !process.StartInfo.RedirectStandardError) return;

                using (var reader = process.StandardError) {
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested) {
                        string line = null;
                        try {
                            var readTask = reader.ReadLineAsync();
                            var completedTask = await Task.WhenAny(readTask, Task.Delay(200, cancellationToken));
                            if (completedTask == readTask) {
                                line = await readTask;
                                if (line == null) break; // End of stream
                            } else { if (process.HasExited) break; continue; } // Timeout
                        } catch (OperationCanceledException) { break; } catch { break; } // Catch other exceptions like InvalidOp, IO, ObjectDisposed

                        if (line != null) {
                            FfplayErrorDataReceived?.Invoke(this, line);
                        }
                    }
                }
            } catch { /* Ignore errors during cleanup or if process exited unexpectedly */ }
            // Trace.WriteLine("ffplay stderr reading task finished.");
        }

        /// <summary>
        /// Dispose pattern implementation.
        /// </summary>
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose pattern implementation.
        /// </summary>
        protected virtual void Dispose(bool disposing) {
            if (_disposed) return;

            if (disposing) {
                Stop();
                _errorReadCts?.Dispose();
            }
            _disposed = true;
        }

        ~NalUnitPipeHandler() { Dispose(false); }
    }
}
