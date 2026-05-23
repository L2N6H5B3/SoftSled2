using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SoftSled.Components.AudioHandling.Pipe {
    /// <summary>
    /// Handles piping raw audio data (e.g., PCM) to an external ffmpeg process
    /// for parameter validation/conversion to WAV, and then pipes ffmpeg's output
    /// to an ffplay process for actual playback.
    /// </summary>
    public class AudioPipeHandler : IDisposable {
        // Configuration
        private readonly string _ffmpegPath;
        private readonly string _ffplayPath;
        private readonly string _ffmpegInputArgs; // Args for ffmpeg reading from C# pipe
        private readonly string _ffmpegOutputArgs; // Args for ffmpeg writing WAV to ffplay pipe
        private readonly string _ffplayArgs; // Args for ffplay reading WAV from ffmpeg pipe
        private readonly string _audioFormat; // e.g., pcm_s16le
        private readonly int _sampleRate;    // e.g., 48000
        private readonly int _channels;      // e.g., 2

        // State
        private Process _ffmpegProcess;
        private Process _ffplayProcess;
        private Stream _ffmpegInputStream; // Pipe from C# to ffmpeg
        private bool _disposed = false;
        private CancellationTokenSource _processCts; // To signal process stopping
        private Task _ffmpegErrorReadTask;
        private Task _ffplayErrorReadTask;
        private Task _pipeBridgeTask; // Task for piping ffmpeg stdout -> ffplay stdin

        /// <summary>
        /// Event raised when ffmpeg writes to its standard error stream.
        /// </summary>
        public event EventHandler<string> FfmpegErrorDataReceived;
        /// <summary>
        /// Event raised when ffplay writes to its standard error stream.
        /// </summary>
        public event EventHandler<string> FfplayErrorDataReceived;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ffmpegPath">Full path to ffmpeg.exe.</param>
        /// <param name="ffplayPath">Full path to ffplay.exe.</param>
        /// <param name="audioFormat">The format string for ffmpeg input (e.g., "pcm_s16le").</param>
        /// <param name="sampleRate">The audio sample rate (e.g., 48000).</param>
        /// <param name="channels">The number of audio channels (e.g., 2 for stereo).</param>
        /// <param name="additionalFfmpegArgs">Optional additional arguments for ffmpeg (applied to input).</param>
        /// <param name="additionalFfplayArgs">Optional additional arguments for ffplay.</param>
        public AudioPipeHandler(
            string ffmpegPath,
            string ffplayPath,
            string audioFormat,
            int sampleRate,
            int channels,
            string additionalFfmpegArgs = "",
            string additionalFfplayArgs = "") {
            // Basic validation
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentNullException(nameof(ffmpegPath));
            if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("ffmpeg.exe not found.", ffmpegPath);
            if (string.IsNullOrEmpty(ffplayPath)) throw new ArgumentNullException(nameof(ffplayPath));
            if (!File.Exists(ffplayPath)) throw new FileNotFoundException("ffplay.exe not found.", ffplayPath);
            if (string.IsNullOrEmpty(audioFormat)) throw new ArgumentNullException(nameof(audioFormat));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            _ffmpegPath = ffmpegPath;
            _ffplayPath = ffplayPath;
            _audioFormat = audioFormat;
            _sampleRate = sampleRate;
            _channels = channels;

            // Construct ffmpeg arguments
            // Input: Read specified raw format from stdin (pipe:0)
            // Output: Write standard WAV format to stdout (pipe:1)
            // Use -vn -sn -dn to ignore video/subtitle/data if accidentally present
            _ffmpegInputArgs = $"-loglevel debug -f {_audioFormat} -ar {_sampleRate} -ac {_channels} {additionalFfmpegArgs} -i pipe:0";
            _ffmpegOutputArgs = $"-vn -sn -dn -f wav pipe:1"; // Output WAV format
            string combinedFfmpegArgs = $"{_ffmpegInputArgs} {_ffmpegOutputArgs}";

            // Construct ffplay arguments
            // Input: Read WAV format from stdin (pipe:0). ffplay should auto-detect parameters from WAV header.
            _ffplayArgs = $"-loglevel debug -i pipe:0 {additionalFfplayArgs}"; // Simpler args for ffplay

            Trace.WriteLine($"AudioPipeHandler configured for FFmpeg->FFplay chain.");
            Trace.WriteLine($"  FFmpeg Path: {_ffmpegPath}");
            Trace.WriteLine($"  FFmpeg Args: {combinedFfmpegArgs}");
            Trace.WriteLine($"  FFplay Path: {_ffplayPath}");
            Trace.WriteLine($"  FFplay Args: {_ffplayArgs}");
        }

        /// <summary>
        /// Starts the ffmpeg and ffplay processes and connects them.
        /// </summary>
        /// <returns>True if successful, false otherwise.</returns>
        public bool Start() {
            if (_ffmpegProcess != null || _ffplayProcess != null) {
                Trace.WriteLine("Processes already seem to be running or initialized.");
                return true;
            }
            if (_disposed) {
                Trace.WriteLine("Error: Cannot start, handler has been disposed.");
                return false;
            }

            _processCts = new CancellationTokenSource();

            try {
                // Start ffmpeg
                Trace.WriteLine($"Starting ffmpeg: {_ffmpegPath} {_ffmpegInputArgs} {_ffmpegOutputArgs}");
                var ffmpegStartInfo = new ProcessStartInfo {
                    FileName = _ffmpegPath,
                    Arguments = $"{_ffmpegInputArgs} {_ffmpegOutputArgs}",
                    UseShellExecute = false,
                    RedirectStandardInput = true, // Input from C#
                    RedirectStandardOutput = true, // Output piped to ffplay
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffmpegProcess = Process.Start(ffmpegStartInfo);
                if (_ffmpegProcess == null) throw new Exception("Failed to start ffmpeg process.");
                _ffmpegInputStream = new BufferedStream(_ffmpegProcess.StandardInput.BaseStream); // Pipe C# -> ffmpeg
                _ffmpegErrorReadTask = Task.Run(() => ReadStdErrorAsync(_ffmpegProcess, "FFMPEG", FfmpegErrorDataReceived, _processCts.Token));
                Trace.WriteLine($"ffmpeg started (PID: {_ffmpegProcess.Id}).");

                // Start ffplay
                Trace.WriteLine($"Starting ffplay: {_ffplayPath} {_ffplayArgs}");
                var ffplayStartInfo = new ProcessStartInfo {
                    FileName = _ffplayPath,
                    Arguments = _ffplayArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true, // Input from ffmpeg's output
                    RedirectStandardOutput = true, // Prevent blocking
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffplayProcess = Process.Start(ffplayStartInfo);
                if (_ffplayProcess == null) throw new Exception("Failed to start ffplay process.");
                _ffplayErrorReadTask = Task.Run(() => ReadStdErrorAsync(_ffplayProcess, "FFPLAY", FfplayErrorDataReceived, _processCts.Token));
                Trace.WriteLine($"ffplay started (PID: {_ffplayProcess.Id}).");

                // Asynchronously pipe ffmpeg's stdout to ffplay's stdin
                _pipeBridgeTask = Task.Run(() => PipeStreamAsync(_ffmpegProcess.StandardOutput.BaseStream, _ffplayProcess.StandardInput.BaseStream, "FFMPEG->FFPLAY", _processCts.Token));

                Trace.WriteLine("AudioPipeHandler started successfully.");
                return true;
            } catch (Exception ex) {
                Trace.WriteLine($"Error starting AudioPipeHandler: {ex.Message}");
                Stop(); // Clean up partially started resources
                return false;
            }
        }

        /// <summary>
        /// Stops the ffmpeg/ffplay processes gracefully.
        /// </summary>
        public void Stop() {
            if (_disposed) return;
            Trace.WriteLine("Stopping AudioPipeHandler...");

            _processCts?.Cancel(); // Signal tasks to stop

            // Close pipe stream from C# to ffmpeg first
            try { _ffmpegInputStream?.Close(); } catch (Exception ex) { Trace.WriteLine($"Error closing ffmpeg input pipe: {ex.Message}"); }
            _ffmpegInputStream = null;

            // Stop ffplay first (since it depends on ffmpeg output)
            StopProcess(_ffplayProcess, "ffplay");
            _ffplayProcess = null;

            // Stop ffmpeg
            StopProcess(_ffmpegProcess, "ffmpeg");
            _ffmpegProcess = null;

            // Wait briefly for async tasks to potentially finish logging/closing
            Task.WhenAll(_ffmpegErrorReadTask ?? Task.CompletedTask,
                         _ffplayErrorReadTask ?? Task.CompletedTask,
                         _pipeBridgeTask ?? Task.CompletedTask)
                .Wait(TimeSpan.FromMilliseconds(500)); // Short wait

            _processCts?.Dispose();
            _processCts = null;
            Trace.WriteLine("AudioPipeHandler stopped.");
        }

        /// <summary>
        /// Processes a chunk of raw audio data.
        /// Writes directly to the ffmpeg process's standard input.
        /// </summary>
        /// <param name="audioData">A byte array containing raw audio samples (e.g., PCM).</param>
        public async Task ProcessAudioDataAsync(byte[] audioData) {
            if (audioData == null || audioData.Length == 0) return;

            Stream pipe = _ffmpegInputStream; // Local ref for thread safety
            Process proc = _ffmpegProcess;    // Local ref
            if (_disposed || proc == null || proc.HasExited || pipe == null || !pipe.CanWrite) {
                return;
            }

            try {
                // Write the raw audio data asynchronously to ffmpeg
                await pipe.WriteAsync(audioData, 0, audioData.Length);
                // await pipe.FlushAsync(); // Maybe needed if data chunks are small/infrequent
            } catch (IOException ex) { Trace.WriteLine($"Audio Pipe IO Error (C#->ffmpeg): {ex.Message}"); Stop(); } catch (ObjectDisposedException) { Trace.WriteLine("Audio Pipe Disposed (C#->ffmpeg)."); } catch (InvalidOperationException ex) { Trace.WriteLine($"Audio Pipe Invalid Op (C#->ffmpeg): {ex.Message}"); Stop(); } // Pipe likely broken
            catch (Exception ex) { Trace.WriteLine($"Audio Pipe Write Error (C#->ffmpeg): {ex.Message}"); }
        }


        /// <summary>
        /// Asynchronously reads the standard error stream of a process.
        /// </summary>
        private async Task ReadStdErrorAsync(Process process, string prefix, EventHandler<string> eventHandler, CancellationToken cancellationToken) {
            if (process == null) return;
            // Trace.WriteLine($"{prefix} stderr reader task started.");
            try {
                if (!process.StartInfo.RedirectStandardError) return;

                using (var reader = process.StandardError) {
                    await Task.Delay(50, cancellationToken); // Give stderr a moment
                    if (process.HasExited) return;

                    while (!process.HasExited && !cancellationToken.IsCancellationRequested) {
                        string line = null;
                        try {
                            var readTask = reader.ReadLineAsync();
                            var completedTask = await Task.WhenAny(readTask, Task.Delay(500, cancellationToken)); // Increased timeout
                            if (completedTask == readTask) {
                                line = await readTask;
                                if (line == null) break; // End of stream
                            } else { if (process.HasExited) break; continue; } // Timeout
                        } catch (OperationCanceledException) { break; } catch { break; } // Catch other exceptions

                        if (line != null) {
                            eventHandler?.Invoke(this, line);
                        }
                    }
                }
            } catch { /* Ignore errors during cleanup or if process exited unexpectedly */ }
            // Trace.WriteLine($"{prefix} stderr reader task finished.");
        }

        /// <summary>
        /// Asynchronously consumes the standard output stream of ffmpeg to prevent blocking.
        /// </summary>
        private async Task ConsumeStdOutAsync(Process process, CancellationToken cancellationToken) {
            // Trace.WriteLine("FFmpeg stdout consumer task started.");
            try {
                if (process == null || !process.StartInfo.RedirectStandardOutput) return;

                using (var reader = process.StandardOutput) {
                    char[] buffer = new char[1024];
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested) {
                        try {
                            var readTask = reader.ReadAsync(buffer, 0, buffer.Length);
                            var completedTask = await Task.WhenAny(readTask, Task.Delay(500, cancellationToken));
                            if (completedTask == readTask) {
                                int charsRead = await readTask;
                                if (charsRead <= 0) break;
                            } else { if (process.HasExited) break; continue; }
                        } catch (OperationCanceledException) { break; } catch { break; }
                    }
                }
            } catch { /* Ignore errors */ }
            // Trace.WriteLine("FFmpeg stdout consumer task finished.");
        }

        /// <summary>
        /// Asynchronously pipes data from an input stream to an output stream.
        /// </summary>
        private async Task PipeStreamAsync(Stream input, Stream output, string pipeName, CancellationToken cancellationToken) {
            Trace.WriteLine($"Starting {pipeName} pipe task...");
            byte[] buffer = new byte[81920]; // 80KB buffer
            int bytesRead;
            try {
                while (!cancellationToken.IsCancellationRequested) {
                    bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead <= 0) {
                        Trace.WriteLine($"{pipeName} input stream ended.");
                        break; // End of input stream
                    }

                    if (output.CanWrite) {
                        await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        // Flush maybe needed if ffplay buffers too much?
                        // await output.FlushAsync(cancellationToken);
                    } else {
                        Trace.WriteLine($"{pipeName} output stream cannot write, stopping pipe task.");
                        break;
                    }
                }
            } catch (OperationCanceledException) { Trace.WriteLine($"{pipeName} pipe task cancelled."); } catch (ObjectDisposedException) { Trace.WriteLine($"{pipeName} stream disposed during copy."); } catch (IOException ex) { Trace.WriteLine($"{pipeName} stream IO error: {ex.Message}"); } catch (Exception ex) { Trace.WriteLine($"Unexpected error piping {pipeName} streams: {ex.Message}"); } finally {
                Trace.WriteLine($"{pipeName} pipe task finished.");
                // Ensure the receiving process's input gets closed if the sending process finishes/errors
                try { output?.Close(); } catch { /* Ignored */ }
            }
        }

        /// <summary>
        /// Helper to stop a process gracefully, then kill if necessary.
        /// </summary>
        private void StopProcess(Process process, string processName) {
            if (process == null) return;
            // Trace.WriteLine($"Stopping {processName} (PID: {process.Id})..."); // Can be noisy
            try {
                if (!process.HasExited) {
                    // Try closing standard input first if redirected and not already closed
                    // (Not applicable here as we close the pipe stream)

                    if (process.WaitForExit(2000)) // Shorter wait for cleanup
                    {
                        // Trace.WriteLine($"{processName} exited (Code: {process.ExitCode}).");
                    } else {
                        Trace.WriteLine($"{processName} (PID: {process.Id}) did not exit gracefully. Killing.");
                        process.Kill();
                    }
                }
            } catch (Exception ex) { Trace.WriteLine($"Error stopping {processName}: {ex.Message}"); } finally {
                process.Dispose();
            }
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
                Stop(); // Stop processes and close pipes
                _processCts?.Dispose();
            }
            _disposed = true;
        }

        ~AudioPipeHandler() { Dispose(false); }
    }
}