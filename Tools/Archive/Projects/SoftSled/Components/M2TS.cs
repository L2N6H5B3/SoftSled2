using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices; // Only needed if RtpPacket parser uses it
using System.Threading;
using System.Threading.Tasks; // For async writing

namespace M2TsHandling.Pipe // Renamed namespace slightly for clarity
{
    /// <summary>
    /// Helper class to parse basic RTP packet headers.
    /// </summary>
    public class RtpPacket {
        public int Version { get; private set; }
        public bool Padding { get; private set; }
        public bool Extension { get; private set; }
        public int CSRCCount { get; private set; }
        public bool Marker { get; private set; }
        public int PayloadType { get; private set; }
        public ushort SequenceNumber { get; private set; }
        public uint Timestamp { get; private set; }
        public uint SSRC { get; private set; }
        public int HeaderLength { get; private set; }
        public int PayloadOffset { get; private set; }
        public int PayloadLength { get; private set; } // This is the length AFTER removing padding

        private RtpPacket() { }

        public static RtpPacket Parse(byte[] buffer, int length) {
            if (buffer == null || length < 12) return null; // Minimum RTP header size
            var packet = new RtpPacket();
            try {
                packet.Version = (buffer[0] >> 6) & 0x03;
                if (packet.Version != 2) { Trace.WriteLine("Error: Invalid RTP version."); return null; }

                packet.Padding = ((buffer[0] >> 5) & 0x01) != 0;
                packet.Extension = ((buffer[0] >> 4) & 0x01) != 0;
                packet.CSRCCount = buffer[0] & 0x0F;
                packet.Marker = ((buffer[1] >> 7) & 0x01) != 0;
                packet.PayloadType = buffer[1] & 0x7F;
                packet.SequenceNumber = (ushort)((buffer[2] << 8) | buffer[3]);
                packet.Timestamp = (uint)((buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7]);
                packet.SSRC = (uint)((buffer[8] << 24) | (buffer[9] << 16) | (buffer[10] << 8) | buffer[11]);

                packet.HeaderLength = 12 + packet.CSRCCount * 4;
                if (length < packet.HeaderLength) { Trace.WriteLine($"Error: Packet SN {packet.SequenceNumber} too short for header ({length} < {packet.HeaderLength})."); return null; }

                packet.PayloadOffset = packet.HeaderLength;
                int payloadLengthWithPadding = length - packet.PayloadOffset; // Initial length including potential padding

                // --- Correctly handle Extension Header ---
                if (packet.Extension) {
                    if (payloadLengthWithPadding < 4) { Trace.WriteLine($"Error: Packet SN {packet.SequenceNumber} too short for extension header length field."); return null; }
                    // Defined By Profile field (2 bytes) + Length field (2 bytes)
                    // Length field indicates the length of the header extension in 32-bit words (excluding the first 4 bytes)
                    int extensionHeaderLengthInWords = (buffer[packet.PayloadOffset + 2] << 8) | buffer[packet.PayloadOffset + 3];
                    int extensionHeaderLengthInBytes = (extensionHeaderLengthInWords * 4) + 4; // Total length including profile + length fields
                    if (payloadLengthWithPadding < extensionHeaderLengthInBytes) { Trace.WriteLine($"Error: Packet SN {packet.SequenceNumber} too short for calculated extension header data."); return null; }

                    // Advance payload offset and adjust remaining length
                    packet.PayloadOffset += extensionHeaderLengthInBytes;
                    payloadLengthWithPadding -= extensionHeaderLengthInBytes; // Update length
                }

                // --- Correctly handle Padding ---
                packet.PayloadLength = payloadLengthWithPadding; // Assume no padding initially
                if (packet.Padding) {
                    if (payloadLengthWithPadding <= 0) { Trace.WriteLine($"Error: Invalid padding on SN {packet.SequenceNumber} - no payload or only padding byte."); return null; }
                    // The last byte of the payload indicates the number of padding bytes (including itself)
                    int paddingLength = buffer[length - 1];
                    if (paddingLength == 0 || paddingLength > payloadLengthWithPadding) {
                        // Invalid padding length value
                        Trace.WriteLine($"Error: Invalid RTP padding length {paddingLength} for payload size {payloadLengthWithPadding}. SN: {packet.SequenceNumber}");
                        return null; // Discard packet with invalid padding
                    }
                    packet.PayloadLength = payloadLengthWithPadding - paddingLength; // Actual payload length
                }

                if (packet.PayloadLength < 0) { Trace.WriteLine($"Error: Negative payload length after parsing SN {packet.SequenceNumber}."); return null; }

                return packet;
            } catch (IndexOutOfRangeException ioorex) {
                Trace.WriteLine($"RTP Parse Exception (Index): {ioorex.Message}. Buffer Length: {length}, SN: {packet?.SequenceNumber.ToString() ?? "N/A"}");
                return null;
            } catch (Exception ex) {
                Trace.WriteLine($"RTP Parse Exception: {ex.Message}, SN: {packet?.SequenceNumber.ToString() ?? "N/A"}");
                return null;
            }
        }
    }

    /// <summary>
    /// Handles RTP packets for a specific payload type by piping
    /// the raw payload to an external ffmpeg/ffplay process.
    /// </summary>
    public class RtpPipeHandler : IDisposable // Renamed class for generality
    {
        private readonly int _targetPayloadType; // Changed variable name
        private readonly string _processPath;     // Changed variable name
        private readonly string _processArguments;// Changed variable name
        private Process _process;                 // Changed variable name
        private Stream _processInputStream;       // Changed variable name
        private bool _disposed = false;
        private CancellationTokenSource _errorReadCts;

        /// <summary>
        /// Event raised when the process writes to its standard error stream.
        /// </summary>
        public event EventHandler<string> ProcessErrorDataReceived; // Renamed event

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="targetPayloadType">The RTP payload type number to process.</param>
        /// <param name="processPath">Full path to the executable (e.g., ffmpeg.exe or ffplay.exe).</param>
        /// <param name="processArguments">
        /// Command line arguments for the process. Must include "-i pipe:0".
        /// Example ffplay for H.264: "-f h264 -i pipe:0"
        /// Example ffplay for MPEG-TS: "-f mpegts -i pipe:0"
        /// </param>
        public RtpPipeHandler(int targetPayloadType, string processPath, string processArguments) {
            if (string.IsNullOrEmpty(processPath)) throw new ArgumentNullException(nameof(processPath));
            if (!File.Exists(processPath)) throw new FileNotFoundException($"{Path.GetFileName(processPath)} not found at specified path.", processPath);
            if (string.IsNullOrEmpty(processArguments) || !processArguments.Contains("-i pipe:0")) {
                throw new ArgumentException("processArguments must include '-i pipe:0' to read from standard input.", nameof(processArguments));
            }

            _targetPayloadType = targetPayloadType;
            _processPath = processPath;
            _processArguments = processArguments;
            Trace.WriteLine($"RtpPipeHandler configured for PT: {_targetPayloadType}, Path: {_processPath}");
        }

        /// <summary>
        /// Starts the external process.
        /// </summary>
        /// <returns>True if the process started successfully, false otherwise.</returns>
        public bool Start() {
            if (_process != null && !_process.HasExited) {
                Trace.WriteLine("Process already running.");
                return true;
            }
            if (_disposed) {
                Trace.WriteLine("Error: Cannot start, handler has been disposed.");
                return false;
            }

            Trace.WriteLine($"Starting process '{_processPath}' with args: {_processArguments}");
            var startInfo = new ProcessStartInfo {
                FileName = _processPath,
                Arguments = _processArguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try {
                _process = Process.Start(startInfo);
                if (_process == null) {
                    Trace.WriteLine("Error: Failed to start process (Process.Start returned null).");
                    return false;
                }

                _processInputStream = _process.StandardInput.BaseStream;
                _errorReadCts = new CancellationTokenSource();
                Task.Run(() => ReadStdErrorAsync(_process, _errorReadCts.Token));

                Trace.WriteLine($"Process started (PID: {_process.Id}).");
                return true;
            } catch (Exception ex) {
                Trace.WriteLine($"Error starting process: {ex.Message}");
                _process?.Dispose();
                _process = null;
                _processInputStream = null;
                return false;
            }
        }

        /// <summary>
        /// Stops the external process gracefully by closing its input stream.
        /// </summary>
        public void Stop() {
            // Check if disposed first
            if (_disposed) return;
            // Check if process exists and hasn't already exited
            if (_process == null || _process.HasExited) return;


            Trace.WriteLine($"Stopping process (PID: {_process?.Id})...");
            try {
                _processInputStream?.Close();
                _processInputStream = null;
                _errorReadCts?.Cancel();

                if (_process != null && !_process.HasExited) // Check again before waiting
                {
                    if (_process.WaitForExit(5000)) {
                        Trace.WriteLine($"Process exited gracefully (Exit Code: {_process.ExitCode}).");
                    } else {
                        int? exitCodeBeforeKill = null;
                        bool alreadyExited = false;
                        try { alreadyExited = _process.HasExited; if (alreadyExited) exitCodeBeforeKill = _process.ExitCode; } catch { /* Ignore */ }
                        Trace.WriteLine($"Process did not exit gracefully (Already Exited: {alreadyExited}, Code: {exitCodeBeforeKill?.ToString() ?? "N/A"}). Killing.");
                        try { if (!_process.HasExited) _process.Kill(); } catch (Exception killEx) { Trace.WriteLine($"Kill exception: {killEx.Message}"); }
                    }
                }
            } catch (InvalidOperationException ex) { Trace.WriteLine($"Error stopping process (likely already exited): {ex.Message}"); } catch (IOException ex) { Trace.WriteLine($"IO Error stopping process: {ex.Message}"); } catch (Exception ex) { Trace.WriteLine($"Error stopping process: {ex.Message}"); } finally {
                _process?.Dispose(); // Dispose the process object
                _process = null;
                _errorReadCts?.Dispose();
                _errorReadCts = null;
                Trace.WriteLine("Process stop routine finished.");
            }
        }

        /// <summary>
        /// Processes an incoming RTP packet. If it matches the target payload type,
        /// writes the raw payload to the process's standard input.
        /// </summary>
        /// <param name="packetBuffer">Raw buffer containing the UDP datagram (RTP packet).</param>
        /// <param name="packetLength">Length of the data in the buffer.</param>
        public async Task ProcessRtpPacketAsync(byte[] packetBuffer, int packetLength) {
            if (_disposed || _process == null || _process.HasExited || _processInputStream == null) {
                return;
            }

            // 1. Parse RTP Header
            RtpPacket rtpPacket = RtpPacket.Parse(packetBuffer, packetLength);

            // 2. Basic RTP Validation
            if (rtpPacket == null) return;

            // 3. Check Payload Type
            if (rtpPacket.PayloadType != _targetPayloadType) return;

            // 4. Payload Validation (Generic - just check length)
            if (rtpPacket.PayloadLength <= 0) return; // Ignore empty packets

            // 5. Write Raw Payload to Pipe
            try {
                // Write the raw payload asynchronously to the process's stdin
                await _processInputStream.WriteAsync(packetBuffer, rtpPacket.PayloadOffset, rtpPacket.PayloadLength);
            } catch (IOException ex) { Trace.WriteLine($"IO Error writing to process stdin (pipe likely closed): {ex.Message}"); Stop(); } catch (ObjectDisposedException) { Trace.WriteLine("Error writing to process stdin: Input stream disposed."); } catch (Exception ex) { Trace.WriteLine($"Error writing to process stdin: {ex.Message}"); }
        }

        /// <summary>
        /// Asynchronously reads the standard error stream of the process.
        /// </summary>
        private async Task ReadStdErrorAsync(Process process, CancellationToken cancellationToken) {
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
                            ProcessErrorDataReceived?.Invoke(this, line);
                        }
                    }
                }
            } catch { /* Ignore errors during cleanup or if process exited unexpectedly */ }
            // Trace.WriteLine("Process stderr reading task finished.");
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

        ~RtpPipeHandler() { Dispose(false); }
    }
}
