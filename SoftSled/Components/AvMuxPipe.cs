using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes; // Required for Named Pipes
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SoftSled.Components.AvMuxPipe // New namespace
{
    /// <summary>
    /// Manages piping separate audio and video data streams via named pipes
    /// into an ffmpeg process for muxing, and then pipes the muxed output
    /// into an ffplay process for playback.
    /// Assumes video input is H.264 NAL units and audio input is raw frames/packets.
    /// </summary>
    public class AvPipeMuxer : IDisposable {
        // Configuration
        private readonly string _ffmpegPath;
        private readonly string _ffplayPath;
        private readonly string _videoInputFormat; // e.g., "h264"
        private readonly string _audioInputFormat; // e.g., "ac3", "mp3", "pcm_s16le" etc.
        private readonly int _audioSampleRate;    // Sample rate for raw audio input
        private readonly int _audioChannels;      // Channel count for raw audio input
        private readonly string _videoPipeName;
        private readonly string _audioPipeName;
        private readonly string _ffmpegArgs;
        private readonly string _ffplayArgs;

        // State
        private Process _ffmpegProcess;
        private Process _ffplayProcess;
        private NamedPipeServerStream _videoPipeServer;
        private NamedPipeServerStream _audioPipeServer;
        private Stream _videoPipeStream; // Client stream after connection
        private Stream _audioPipeStream; // Client stream after connection
        private bool _disposed = false;
        private CancellationTokenSource _processCts; // To signal process stopping
        private Task _videoPipeConnectTask; // Keep track for potential errors
        private Task _audioPipeConnectTask; // Keep track for potential errors
        private Task _ffmpegErrorReadTask;
        private Task _ffplayErrorReadTask;
        private Task _pipeBridgeTask; // Task for piping ffmpeg stdout -> ffplay stdin


        // Annex B start code for H.264
        private static readonly byte[] AnnexBStartCode = { 0x00, 0x00, 0x00, 0x01 };

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
        /// <param name="videoInputFormat">Input format for video pipe (e.g., "h264").</param>
        /// <param name="audioInputFormat">Input format for audio pipe (e.g., "s16le", "ac3").</param>
        /// <param name="audioSampleRate">The audio sample rate (needed if audioInputFormat is raw PCM like s16le).</param>
        /// <param name="audioChannels">The number of audio channels (needed if audioInputFormat is raw PCM like s16le).</param>
        /// <param name="videoPipeName">Unique name for the video pipe (e.g., "myvidpipe").</param>
        /// <param name="audioPipeName">Unique name for the audio pipe (e.g., "myaudpipe").</param>
        /// <param name="additionalFfmpegInputArgs">Optional additional args applied BEFORE inputs (e.g., "-re").</param>
        /// <param name="additionalFfmpegVideoInputArgs">Optional additional args applied BEFORE video input (e.g., "-video_size 1280x720").</param>
        /// <param name="additionalFfmpegOutputArgs">Optional additional args applied BEFORE output (e.g., "-b:v").</param>
        /// <param name="additionalFfplayArgs">Optional additional args for ffplay.</param>
        public AvPipeMuxer(
            string ffmpegPath,
            string ffplayPath,
            string videoInputFormat,
            string audioInputFormat,
            int audioSampleRate, // Added required param
            int audioChannels,   // Added required param
            string videoPipeName = "ffmpeg_video_pipe",
            string audioPipeName = "ffmpeg_audio_pipe",
            string additionalFfmpegInputArgs = "", // e.g. -re
            string additionalFfmpegVideoInputArgs = "", // e.g. -video_size 1280x720
            string additionalFfmpegOutputArgs = "", // e.g. -b:v
            string additionalFfplayArgs = "") {
            // Basic validation (same as before)
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath)) throw new FileNotFoundException("ffmpeg.exe not found.", ffmpegPath);
            if (string.IsNullOrEmpty(ffplayPath) || !File.Exists(ffplayPath)) throw new FileNotFoundException("ffplay.exe not found.", ffplayPath);
            if (string.IsNullOrEmpty(videoInputFormat)) throw new ArgumentNullException(nameof(videoInputFormat));
            if (string.IsNullOrEmpty(audioInputFormat)) throw new ArgumentNullException(nameof(audioInputFormat));
            if (audioInputFormat.ToLowerInvariant().Contains("pcm") || audioInputFormat.ToLowerInvariant().StartsWith("s") || audioInputFormat.ToLowerInvariant().StartsWith("u") || audioInputFormat.ToLowerInvariant().StartsWith("f")) {
                if (audioSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(audioSampleRate), "Sample rate must be positive for raw audio formats.");
                if (audioChannels <= 0) throw new ArgumentOutOfRangeException(nameof(audioChannels), "Channel count must be positive for raw audio formats.");
            }
            if (string.IsNullOrEmpty(videoPipeName)) throw new ArgumentNullException(nameof(videoPipeName));
            if (string.IsNullOrEmpty(audioPipeName)) throw new ArgumentNullException(nameof(audioPipeName));
            if (videoPipeName == audioPipeName) throw new ArgumentException("Video and audio pipe names must be different.");

            _ffmpegPath = ffmpegPath;
            _ffplayPath = ffplayPath;
            _videoInputFormat = videoInputFormat;
            _audioInputFormat = audioInputFormat;
            _audioSampleRate = audioSampleRate;
            _audioChannels = audioChannels;
            _videoPipeName = videoPipeName;
            _audioPipeName = audioPipeName;

            // Construct ffmpeg arguments with targeted probing
            string videoPipePath = $@"\\.\pipe\{_videoPipeName}";
            string audioPipePath = $@"\\.\pipe\{_audioPipeName}";

            // *** Force re-encode for video (-c:v libx264), keep audio encode (-c:a aac) ***
            // *** Removed -copyts ***
            _ffmpegArgs = $"-loglevel debug " +
                          $"{additionalFfmpegInputArgs} " +
                          // Input 0: Audio
                          $"-probesize 32 -analyzeduration 0 -avioflags direct -f {_audioInputFormat} -ar {_audioSampleRate} -ac {_audioChannels} -i \"{audioPipePath}\" " +
                          // Input 1: Video
                          $"-probesize 4k -analyzeduration 500k -fpsprobesize 0 -avioflags direct -f {_videoInputFormat} {additionalFfmpegVideoInputArgs} -i \"{videoPipePath}\" " +
                          // Mapping (Audio=0, Video=1)
                          $"-map 1:v? -map 0:a? " +
                          $"-c:v libx264 -preset ultrafast -crf 23 " + // *** Re-encode video ***
                          $"-c:a aac -b:a 128k " + // Encode audio (AAC)
                          $"{additionalFfmpegOutputArgs} " +
                          $"-f mpegts pipe:1";

            // Construct ffplay arguments
            _ffplayArgs = $"-loglevel debug -i pipe:0 {additionalFfplayArgs}";

            Trace.WriteLine($"AvPipeMuxer configured.");
            Trace.WriteLine($"  Video Pipe: {videoPipePath}");
            Trace.WriteLine($"  Audio Pipe: {audioPipePath}");
            Trace.WriteLine($"  FFmpeg Args: {_ffmpegArgs}");
            Trace.WriteLine($"  FFplay Args: {_ffplayArgs}");
        }

        /// <summary>
        /// Starts the named pipes and the ffmpeg/ffplay processes.
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
            bool ffmpegStarted = false;
            bool ffplayStarted = false;
            bool videoPipeCreated = false;
            bool audioPipeCreated = false;


            try {
                Trace.WriteLine("Creating named pipes...");
                _audioPipeServer = new NamedPipeServerStream(_audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                audioPipeCreated = true;
                _videoPipeServer = new NamedPipeServerStream(_videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                videoPipeCreated = true;

                // Assign streams immediately
                _audioPipeStream = _audioPipeServer;
                _videoPipeStream = _videoPipeServer;
                Trace.WriteLine("Pipe streams assigned (but not connected yet).");


                // Start ffmpeg BEFORE waiting for connection
                Trace.WriteLine($"Starting ffmpeg: {_ffmpegPath} {_ffmpegArgs}");
                var ffmpegStartInfo = new ProcessStartInfo {
                    FileName = _ffmpegPath,
                    Arguments = _ffmpegArgs, // Use the combined args
                    UseShellExecute = false,
                    RedirectStandardInput = false, // Reads from named pipes
                    RedirectStandardOutput = true, // Output is piped to ffplay
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffmpegProcess = Process.Start(ffmpegStartInfo);
                if (_ffmpegProcess == null) throw new Exception("Failed to start ffmpeg process.");
                ffmpegStarted = true;
                _ffmpegErrorReadTask = Task.Run(() => ReadStdErrorAsync(_ffmpegProcess, "FFMPEG", FfmpegErrorDataReceived, _processCts.Token));
                Trace.WriteLine($"ffmpeg started (PID: {_ffmpegProcess.Id}).");


                // Now start waiting for connections asynchronously
                Trace.WriteLine("Initiating pipe connection waits asynchronously...");
                _audioPipeConnectTask = _audioPipeServer.WaitForConnectionAsync(_processCts.Token);
                _videoPipeConnectTask = _videoPipeServer.WaitForConnectionAsync(_processCts.Token);
                // Log potential connection errors in the background
                _audioPipeConnectTask.ContinueWith(t => { if (t.IsFaulted) Trace.WriteLine($"Audio Pipe Connection Task Failed: {t.Exception.InnerException?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);
                _videoPipeConnectTask.ContinueWith(t => { if (t.IsFaulted) Trace.WriteLine($"Video Pipe Connection Task Failed: {t.Exception.InnerException?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);


                // Start ffplay
                Trace.WriteLine($"Starting ffplay: {_ffplayPath} {_ffplayArgs}");
                var ffplayStartInfo = new ProcessStartInfo {
                    FileName = _ffplayPath,
                    Arguments = _ffplayArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true, // Reads from ffmpeg's output
                    RedirectStandardOutput = true, // Prevent blocking
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffplayProcess = Process.Start(ffplayStartInfo);
                if (_ffplayProcess == null) throw new Exception("Failed to start ffplay process.");
                ffplayStarted = true;
                _ffplayErrorReadTask = Task.Run(() => ReadStdErrorAsync(_ffplayProcess, "FFPLAY", FfplayErrorDataReceived, _processCts.Token));
                Trace.WriteLine($"ffplay started (PID: {_ffplayProcess.Id}).");

                // Asynchronously pipe ffmpeg's stdout to ffplay's stdin
                _pipeBridgeTask = Task.Run(() => PipeStreamAsync(_ffmpegProcess.StandardOutput.BaseStream, _ffplayProcess.StandardInput.BaseStream, "FFMPEG->FFPLAY", _processCts.Token));

                // *** REMOVED Task.WaitAll for pipe connections ***
                Trace.WriteLine("Muxer start sequence initiated successfully (not waiting for pipe connections).");

                return true;
            }
            // Removed TimeoutException catch as we are no longer waiting synchronously
            catch (OperationCanceledException) {
                Trace.WriteLine("Start operation cancelled.");
                Stop(); // Clean up
                return false;
            } catch (Exception ex) {
                Trace.WriteLine($"Error starting AvPipeMuxer: {ex.Message}");
                // Clean up only resources that were successfully created/started
                if (ffplayStarted) StopProcess(_ffplayProcess, "ffplay");
                if (ffmpegStarted) StopProcess(_ffmpegProcess, "ffmpeg");
                _audioPipeStream = null; // Ensure streams are null on failure
                _videoPipeStream = null;
                if (audioPipeCreated) _audioPipeServer?.Dispose();
                if (videoPipeCreated) _videoPipeServer?.Dispose();
                _processCts?.Cancel();
                _processCts?.Dispose();
                _ffmpegProcess = null; _ffplayProcess = null; _audioPipeServer = null; _videoPipeServer = null; // Reset state
                return false;
            }
        }

        /// <summary>
        /// Stops the ffmpeg/ffplay processes and closes pipes.
        /// </summary>
        public void Stop() {
            if (_disposed) return;
            Trace.WriteLine("Stopping AvPipeMuxer...");

            _processCts?.Cancel(); // Signal tasks to stop

            // Close pipe streams first to signal EOF to ffmpeg readers
            try { _videoPipeServer?.Close(); } catch { /* Ignore */ } finally { _videoPipeStream = null; }
            try { _audioPipeServer?.Close(); } catch { /* Ignore */ } finally { _audioPipeStream = null; }


            // Close pipe servers (Dispose also closes if not already closed)
            try { _videoPipeServer?.Dispose(); } catch { /* Ignore */ }
            try { _audioPipeServer?.Dispose(); } catch { /* Ignore */ }
            _videoPipeServer = null;
            _audioPipeServer = null;

            // Stop ffplay first
            StopProcess(_ffplayProcess, "ffplay");
            _ffplayProcess = null;

            // Stop ffmpeg
            StopProcess(_ffmpegProcess, "ffmpeg");
            _ffmpegProcess = null;

            // Wait briefly for async tasks
            Task.WhenAll(_ffmpegErrorReadTask ?? Task.CompletedTask,
                         _ffplayErrorReadTask ?? Task.CompletedTask,
                         _pipeBridgeTask ?? Task.CompletedTask)
                .Wait(TimeSpan.FromMilliseconds(500));

            _processCts?.Dispose();
            _processCts = null;
            Trace.WriteLine("AvPipeMuxer stopped.");
        }

        /// <summary>
        /// Processes a collection of complete H.264 NAL units for one video frame.
        /// Prepends Annex B start codes and writes to the video pipe.
        /// Waits for pipe connection if necessary.
        /// </summary>
        public async Task ProcessVideoFrameNalUnitsAsync(IEnumerable<byte[]> nalUnits) {
            if (nalUnits == null) return;
            Stream pipe = _videoPipeStream;
            NamedPipeServerStream serverPipe = _videoPipeServer;
            Task connectTask = _videoPipeConnectTask; // Capture task locally

            // Check if disposed or pipe unavailable
            if (_disposed || pipe == null || serverPipe == null) return;

            // Wait for connection if not yet established before writing
            if (!serverPipe.IsConnected) {
                if (connectTask == null || connectTask.IsCompleted) // Check if task is already done or wasn't started properly
                {
                    // Don't log error here, just return as data might arrive before connection
                    // Trace.WriteLine("Video pipe connection task not running or already completed, cannot write.");
                    return;
                }
                try {
                    // Trace.WriteLine("Video pipe waiting for connection before write...");
                    await connectTask; // Wait for the connection task initiated in Start()
                    // Trace.WriteLine("Video pipe connected.");
                } catch (Exception ex) {
                    Trace.WriteLine($"Error waiting for video pipe connection: {ex.Message}");
                    Stop(); // Stop if connection fails
                    return;
                }
            }

            // Double check pipe writability after potentially waiting
            if (!pipe.CanWrite) return;

            try {
                foreach (byte[] nalUnit in nalUnits) {
                    if (nalUnit == null || nalUnit.Length == 0) continue;
                    await pipe.WriteAsync(AnnexBStartCode, 0, AnnexBStartCode.Length);
                    await pipe.WriteAsync(nalUnit, 0, nalUnit.Length);
                }
                // await pipe.FlushAsync(); // Avoid flushing unless needed
            } catch (IOException ex) { Trace.WriteLine($"Video Pipe IO Error: {ex.Message}"); Stop(); } catch (ObjectDisposedException) { /* Trace.WriteLine("Video Pipe Disposed."); */ } catch (InvalidOperationException ex) { Trace.WriteLine($"Video Pipe Invalid Op: {ex.Message}"); Stop(); } catch (Exception ex) { Trace.WriteLine($"Video Pipe Write Error: {ex.Message}"); }
        }

        /// <summary>
        /// Processes a complete raw audio frame/packet.
        /// Writes directly to the audio pipe.
        /// Waits for pipe connection if necessary.
        /// </summary>
        public async Task ProcessAudioDataAsync(byte[] audioData) {
            if (audioData == null || audioData.Length == 0) return;
            Stream pipe = _audioPipeStream;
            NamedPipeServerStream serverPipe = _audioPipeServer;
            Task connectTask = _audioPipeConnectTask; // Capture task locally

            // Check if disposed or pipe unavailable
            if (_disposed || pipe == null || serverPipe == null) return;

            // Wait for connection if not yet established before writing
            if (!serverPipe.IsConnected) {
                if (connectTask == null || connectTask.IsCompleted) {
                    // Trace.WriteLine("Audio pipe connection task not running or already completed, cannot write.");
                    return;
                }
                try {
                    // Trace.WriteLine("Audio pipe waiting for connection before write...");
                    await connectTask; // Wait for the connection task initiated in Start()
                    // Trace.WriteLine("Audio pipe connected.");
                } catch (Exception ex) {
                    Trace.WriteLine($"Error waiting for audio pipe connection: {ex.Message}");
                    Stop(); // Stop if connection fails
                    return;
                }
            }

            // Double check pipe writability after potentially waiting
            if (!pipe.CanWrite) return;

            try {
                await pipe.WriteAsync(audioData, 0, audioData.Length);
                // await pipe.FlushAsync();
            } catch (IOException ex) { Trace.WriteLine($"Audio Pipe IO Error: {ex.Message}"); Stop(); } catch (ObjectDisposedException) { /* Trace.WriteLine("Audio Pipe Disposed."); */ } catch (InvalidOperationException ex) { Trace.WriteLine($"Audio Pipe Invalid Op: {ex.Message}"); Stop(); } catch (Exception ex) { Trace.WriteLine($"Audio Pipe Write Error: {ex.Message}"); }
        }


        // --- Helper Methods --- (Identical to previous version)

        private void StopProcess(Process process, string processName) {
            if (process == null) return;
            // Trace.WriteLine($"Stopping {processName} (PID: {process.Id})...");
            try {
                if (!process.HasExited) {
                    if (process.WaitForExit(2000)) // Shorter wait
                    {
                        // Trace.WriteLine($"{processName} exited (Code: {process.ExitCode}).");
                    } else {
                        Trace.WriteLine($"{processName} (PID: {process.Id}) did not exit gracefully. Killing.");
                        process.Kill();
                    }
                }
            } catch { /* Ignore errors stopping process */ } finally {
                process.Dispose();
            }
        }

        private async Task ReadStdErrorAsync(Process process, string prefix, EventHandler<string> eventHandler, CancellationToken cancellationToken) {
            if (process == null) return;
            // Trace.WriteLine($"{prefix} stderr reader task started.");
            try {
                if (!process.StartInfo.RedirectStandardError) return;

                using (var reader = process.StandardError) {
                    await Task.Delay(50, cancellationToken);
                    if (process.HasExited) return;

                    while (!process.HasExited && !cancellationToken.IsCancellationRequested) {
                        string line = null;
                        try {
                            var readTask = reader.ReadLineAsync();
                            var completedTask = await Task.WhenAny(readTask, Task.Delay(500, cancellationToken));
                            if (completedTask == readTask) {
                                line = await readTask;
                                if (line == null) break;
                            } else { if (process.HasExited) break; continue; }
                        } catch (OperationCanceledException) { break; } catch { break; }

                        if (line != null) {
                            eventHandler?.Invoke(this, line);
                        }
                    }
                }
            } catch { /* Ignore errors */ }
            // Trace.WriteLine($"{prefix} stderr reader task finished.");
        }

        private async Task PipeStreamAsync(Stream input, Stream output, string pipeName, CancellationToken cancellationToken) {
            Trace.WriteLine($"Starting {pipeName} pipe task...");
            byte[] buffer = new byte[81920]; // 80KB buffer
            int bytesRead;
            try {
                while (!cancellationToken.IsCancellationRequested) {
                    bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead <= 0) { Trace.WriteLine($"{pipeName} input stream ended."); break; }

                    if (output.CanWrite) {
                        await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        // await output.FlushAsync(cancellationToken); // Maybe needed?
                    } else { Trace.WriteLine($"{pipeName} output stream cannot write, stopping pipe task."); break; }
                }
            } catch (OperationCanceledException) { Trace.WriteLine($"{pipeName} pipe task cancelled."); } catch (ObjectDisposedException) { Trace.WriteLine($"{pipeName} stream disposed during copy."); } catch (IOException ex) { Trace.WriteLine($"{pipeName} stream IO error: {ex.Message}"); } catch (Exception ex) { Trace.WriteLine($"Unexpected error piping {pipeName} streams: {ex.Message}"); } finally {
                Trace.WriteLine($"{pipeName} pipe task finished.");
                try { output?.Close(); } catch { /* Ignored */ } // Close downstream pipe
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

        ~AvPipeMuxer() { Dispose(false); }
    }
}
