using System;
using System.Collections.Concurrent; // For BlockingCollection
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes; // Required for Named Pipes
using System.Linq; // For FirstOrDefault on SortedList
using System.Runtime.InteropServices; // For checking OS
using System.Threading;
using System.Threading.Tasks;

namespace SoftSled.Components.AudioVisual {
    // Replace record with class, implement IComparable for sorting/priority logic
    public class MediaData : IComparable<MediaData> {
        public byte[] Data { get; private set; }
        public uint RtpTimestamp { get; private set; }

        public MediaData(byte[] data, uint rtpTimestamp) {
            Data = data;
            RtpTimestamp = rtpTimestamp;
        }

        // Compare based on RTP Timestamp for PriorityQueue ordering logic
        public int CompareTo(MediaData other) {
            if (other == null) return 1;

            // Handle RTP timestamp wrap-around using unsigned comparison logic
            if (this.RtpTimestamp == other.RtpTimestamp) return 0;
            uint diff = this.RtpTimestamp - other.RtpTimestamp;
            const uint halfWay = uint.MaxValue / 2;

            // Simplified logic: Assume smaller timestamp is earlier unless difference is huge (wrap-around)
            if (diff == 0) return 0;
            if (diff < halfWay) return -1; // this is earlier or other wrapped around
            return 1; // other is earlier or this wrapped around
        }

        // Override Equals and GetHashCode if necessary, though not strictly needed for SortedList key comparison here
        public override bool Equals(object obj) {
            return obj is MediaData other && RtpTimestamp == other.RtpTimestamp && Data == other.Data; // Basic equality
        }

        public override int GetHashCode() {
            // Simple hash code, adjust if more complex equality is needed
            return RtpTimestamp.GetHashCode() ^ (Data?.Length ?? 0);
        }
    }


    public class AvPipeMuxer : IDisposable // Implement IDisposable, not IAsyncDisposable
    {
        // --- Buffering Configuration ---
        private readonly TimeSpan _bufferingDelay;

        // --- Replace Channels with BlockingCollection ---
        private BlockingCollection<MediaData> _videoInputQueue;
        private BlockingCollection<MediaData> _audioInputQueue;

        // --- Replace PriorityQueue with SortedList + Queue ---
        private SortedList<uint, Queue<MediaData>> _videoJitterBuffer;
        private SortedList<uint, Queue<MediaData>> _audioJitterBuffer;
        private readonly object _videoBufferLock = new object();
        private readonly object _audioBufferLock = new object();


        // --- State ---
        private Task _videoWriterTask;
        private Task _audioWriterTask;

        // Pacing state (remains similar)
        private readonly object _pacingInitLock = new object();
        private Stopwatch _stopwatch = new Stopwatch();
        private bool _stopwatchInitialized = false;
        private TimeSpan _baseTimeOffset = TimeSpan.Zero;
        private bool _videoFirstTimestampSeen = false;
        private uint _videoFirstRtpTimestamp;
        private bool _audioFirstTimestampSeen = false;
        private uint _audioFirstRtpTimestamp;

        // Other existing fields
        private readonly string _ffmpegPath;
        private readonly string _ffplayPath;
        private readonly string _videoInputFormat;
        private readonly string _audioInputFormat;
        private readonly int _audioSampleRate;
        private readonly int _audioChannels;
        private readonly string _videoPipeName;
        private readonly string _audioPipeName;
        private readonly string _ffmpegArgs;
        private readonly string _ffplayArgs;
        private const int VideoClockRate = 90000;
        private Process _ffmpegProcess;
        private Process _ffplayProcess;
        private NamedPipeServerStream _videoPipeServer;
        private NamedPipeServerStream _audioPipeServer;
        private bool _disposed = false;
        private CancellationTokenSource _processCts;
        private Task _videoPipeConnectTask;
        private Task _audioPipeConnectTask;
        private Task _ffmpegErrorReadTask;
        private Task _ffplayErrorReadTask;
        private Task _pipeBridgeTask;
        private static readonly byte[] AnnexBStartCode = { 0x00, 0x00, 0x00, 0x01 };
        public event EventHandler<string> FfmpegErrorDataReceived;
        public event EventHandler<string> FfplayErrorDataReceived;


        public AvPipeMuxer(
            string ffmpegPath,
            string ffplayPath,
            string videoInputFormat,
            string audioInputFormat,
            int audioSampleRate,
            int audioChannels,
            int bufferingDelayMs = 1150,
            string videoPipeName = "ffmpeg_video_pipe",
            string audioPipeName = "ffmpeg_audio_pipe",
            string additionalFfmpegInputArgs = "",
            string additionalFfmpegVideoInputArgs = "",
            string additionalFfmpegOutputArgs = "",
            string additionalFfplayArgs = "") {
            // Validation remains the same
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
            if (bufferingDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(bufferingDelayMs), "Buffering delay cannot be negative.");

            _bufferingDelay = TimeSpan.FromMilliseconds(bufferingDelayMs);

            // Initialize other fields
            _ffmpegPath = ffmpegPath;
            _ffplayPath = ffplayPath;
            _videoInputFormat = videoInputFormat;
            _audioInputFormat = audioInputFormat;
            _audioSampleRate = audioSampleRate;
            _audioChannels = audioChannels;
            _videoPipeName = videoPipeName;
            _audioPipeName = audioPipeName;

            // --- Initialize BlockingCollections and SortedLists ---
            _videoInputQueue = new BlockingCollection<MediaData>(new ConcurrentQueue<MediaData>());
            _audioInputQueue = new BlockingCollection<MediaData>(new ConcurrentQueue<MediaData>());
            _videoJitterBuffer = new SortedList<uint, Queue<MediaData>>(new RtpTimestampComparer()); // Use custom comparer for SortedList
            _audioJitterBuffer = new SortedList<uint, Queue<MediaData>>(new RtpTimestampComparer()); // Use custom comparer for SortedList

            // FFmpeg/FFplay args remain the same as the previous version
            string videoPipePath = GetPlatformPipePath(_videoPipeName);
            string audioPipePath = GetPlatformPipePath(_audioPipeName);
            _ffmpegArgs = $"-loglevel level+info " +
                          $"-fflags +genpts " +
                          $"-use_wallclock_as_timestamps 1 -f {_videoInputFormat} {additionalFfmpegVideoInputArgs} -i \"{videoPipePath}\" " +
                          $"-use_wallclock_as_timestamps 1 -f {_audioInputFormat} -ar {_audioSampleRate} -ac {_audioChannels} -i \"{audioPipePath}\" " +
                          $"-map 0:v? -map 1:a? " +
                          $"-c:v copy " +
                          $"-c:a copy " +
                          $"{additionalFfmpegOutputArgs} " +
                          $"-f mpegts -mpegts_flags +initial_discontinuity pipe:1";
            _ffplayArgs = $"-loglevel debug -i pipe:0 {additionalFfplayArgs}";

            Trace.WriteLine($"AvPipeMuxer configured with {_bufferingDelay.TotalMilliseconds}ms buffering delay.");
            Trace.WriteLine($"  Video Pipe: {videoPipePath}");
            Trace.WriteLine($"  Audio Pipe: {audioPipePath}");
            Trace.WriteLine($"  FFmpeg Args: {_ffmpegArgs}");
            Trace.WriteLine($"  FFplay Args: {_ffplayArgs}");
        }

        // Custom Comparer for SortedList keys to handle RTP timestamp wrap-around
        private class RtpTimestampComparer : IComparer<uint> {
            public int Compare(uint x, uint y) {
                if (x == y) return 0;
                uint diff = x - y;
                const uint halfWay = uint.MaxValue / 2;
                if (diff < halfWay) return -1; // x is earlier or y wrapped around
                return 1; // y is earlier or x wrapped around
            }
        }


        public bool Start() {
            if (_ffmpegProcess != null || _ffplayProcess != null) {
                Trace.WriteLine("Processes already seem to be running or initialized.");
                return true;
            }
            if (_disposed) {
                Trace.WriteLine("Error: Cannot start, handler has been disposed.");
                return false;
            }

            // Reset state
            _stopwatchInitialized = false;
            _stopwatch.Reset();
            _videoFirstTimestampSeen = false;
            _audioFirstTimestampSeen = false;
            lock (_videoBufferLock) { _videoJitterBuffer.Clear(); }
            lock (_audioBufferLock) { _audioJitterBuffer.Clear(); }
            // Recreate BlockingCollections if previously completed
            if (_videoInputQueue.IsCompleted) _videoInputQueue = new BlockingCollection<MediaData>(new ConcurrentQueue<MediaData>());
            if (_audioInputQueue.IsCompleted) _audioInputQueue = new BlockingCollection<MediaData>(new ConcurrentQueue<MediaData>());

            _processCts = new CancellationTokenSource();
            bool ffmpegStarted = false;
            bool ffplayStarted = false;
            bool videoPipeCreated = false;
            bool audioPipeCreated = false;

            try {
                Trace.WriteLine("Creating named pipes...");
                _audioPipeServer = new NamedPipeServerStream(_audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                audioPipeCreated = true;
                _videoPipeServer = new NamedPipeServerStream(_videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                videoPipeCreated = true;

                // Start ffmpeg (same logic)
                Trace.WriteLine($"Starting ffmpeg: {_ffmpegPath} {_ffmpegArgs}");
                var ffmpegStartInfo = new ProcessStartInfo {
                    FileName = _ffmpegPath,
                    Arguments = _ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffmpegProcess = Process.Start(ffmpegStartInfo);
                if (_ffmpegProcess == null) throw new Exception("Failed to start ffmpeg process.");
                ffmpegStarted = true;
                _ffmpegErrorReadTask = Task.Run(() => ReadStdErrorAsync(_ffmpegProcess, "FFMPEG", FfmpegErrorDataReceived, _processCts.Token));
                Trace.WriteLine($"ffmpeg started (PID: {_ffmpegProcess.Id}).");

                // Start pipe connection waits (same logic)
                Trace.WriteLine("Initiating pipe connection waits asynchronously...");
                _audioPipeConnectTask = _audioPipeServer.WaitForConnectionAsync(_processCts.Token);
                _videoPipeConnectTask = _videoPipeServer.WaitForConnectionAsync(_processCts.Token);
                _audioPipeConnectTask.ContinueWith(t => { if (t.IsFaulted) Trace.WriteLine($"Audio Pipe Connection Task Failed: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);
                _videoPipeConnectTask.ContinueWith(t => { if (t.IsFaulted) Trace.WriteLine($"Video Pipe Connection Task Failed: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}"); }, TaskContinuationOptions.OnlyOnFaulted);

                // Start ffplay (same logic)
                Trace.WriteLine($"Starting ffplay: {_ffplayPath} {_ffplayArgs}");
                var ffplayStartInfo = new ProcessStartInfo {
                    FileName = _ffplayPath,
                    Arguments = _ffplayArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffplayProcess = Process.Start(ffplayStartInfo);
                if (_ffplayProcess == null) throw new Exception("Failed to start ffplay process.");
                ffplayStarted = true;
                _ffplayErrorReadTask = Task.Run(() => ReadStdErrorAsync(_ffplayProcess, "FFPLAY", FfplayErrorDataReceived, _processCts.Token));
                Trace.WriteLine($"ffplay started (PID: {_ffplayProcess.Id}).");

                // Pipe ffmpeg stdout -> ffplay stdin (same logic)
                _pipeBridgeTask = Task.Run(() => PipeStreamAsync(_ffmpegProcess.StandardOutput.BaseStream, _ffplayProcess.StandardInput.BaseStream, "FFMPEG->FFPLAY", _processCts.Token));

                // Start the Jitter Buffer Processing Tasks
                _videoWriterTask = Task.Run(() => VideoWriteLoopAsync(_processCts.Token), _processCts.Token);
                _audioWriterTask = Task.Run(() => VideoWriteLoopAsync(_processCts.Token), _processCts.Token);

                Trace.WriteLine("Muxer start sequence initiated successfully (including jitter buffer writers).");
                return true;
            } catch (OperationCanceledException) {
                Trace.WriteLine("Start operation cancelled.");
                Stop();
                return false;
            } catch (Exception ex) {
                Trace.WriteLine($"Error starting AvPipeMuxer: {ex.ToString()}");
                // Cleanup logic remains similar
                if (ffplayStarted) StopProcess(_ffplayProcess, "ffplay");
                if (ffmpegStarted) StopProcess(_ffmpegProcess, "ffmpeg");
                if (audioPipeCreated) try { _audioPipeServer?.Dispose(); } catch { }
                if (videoPipeCreated) try { _videoPipeServer?.Dispose(); } catch { }
                _processCts?.Cancel();
                try { _processCts?.Dispose(); } catch { }
                _ffmpegProcess = null; _ffplayProcess = null; _audioPipeServer = null; _videoPipeServer = null;
                _videoInputQueue?.CompleteAdding();
                _audioInputQueue?.CompleteAdding();
                return false;
            }
        }

        public void Stop() {
            if (_disposed) return;
            Trace.WriteLine("Stopping AvPipeMuxer...");

            _processCts?.Cancel(); // Signal tasks to stop FIRST

            // Signal BlockingCollections that no more data is coming
            _videoInputQueue?.CompleteAdding();
            _audioInputQueue?.CompleteAdding();

            // Dispose pipe servers (breaks connections, helps writer loops exit)
            try { _videoPipeServer?.Dispose(); } catch (Exception ex) { Trace.WriteLine($"Error disposing video pipe server: {ex.Message}"); }
            try { _audioPipeServer?.Dispose(); } catch (Exception ex) { Trace.WriteLine($"Error disposing audio pipe server: {ex.Message}"); }
            _videoPipeServer = null;
            _audioPipeServer = null;

            // Stop ffplay first
            StopProcess(_ffplayProcess, "ffplay");
            _ffplayProcess = null;

            // Stop ffmpeg
            StopProcess(_ffmpegProcess, "ffmpeg");
            _ffmpegProcess = null;

            // Wait briefly for async tasks
            try {
                var allTasks = new List<Task>();
                if (_videoWriterTask != null) allTasks.Add(_videoWriterTask);
                if (_audioWriterTask != null) allTasks.Add(_audioWriterTask);
                if (_ffmpegErrorReadTask != null) allTasks.Add(_ffmpegErrorReadTask);
                if (_ffplayErrorReadTask != null) allTasks.Add(_ffplayErrorReadTask);
                if (_pipeBridgeTask != null) allTasks.Add(_pipeBridgeTask);

                if (allTasks.Count > 0) {
                    Task.WaitAll(allTasks.ToArray(), TimeSpan.FromMilliseconds(1500)); // Slightly longer wait
                }
            } catch (AggregateException ae) {
                foreach (var ex in ae.Flatten().InnerExceptions) {
                    Trace.WriteLine($"Error waiting for background task: {ex.Message}");
                }
            } catch (Exception ex) { Trace.WriteLine($"Error waiting for background tasks: {ex.Message}"); }


            _processCts?.Dispose();
            _processCts = null;
            _stopwatch.Stop();
            Trace.WriteLine("AvPipeMuxer stopped.");
        }

        private void InitializePacingIfNeeded() {
            // Same logic as before
            if (!_stopwatchInitialized) {
                lock (_pacingInitLock) {
                    if (!_stopwatchInitialized) {
                        _stopwatch.Start();
                        _baseTimeOffset = _stopwatch.Elapsed;
                        _stopwatchInitialized = true;
                        Trace.WriteLine($"Pacing stopwatch initialized. BaseTime: {_baseTimeOffset.TotalMilliseconds:F2} ms");
                    }
                }
            }
        }

        // --- MODIFIED Process Methods: Use BlockingCollection.TryAdd ---

        public bool ProcessVideoFrameNalUnits(IEnumerable<byte[]> nalUnits, uint rtpTimestamp) {
            if (nalUnits == null || _disposed || _videoInputQueue == null || _videoInputQueue.IsAddingCompleted) return false;

            using (var ms = new MemoryStream()) {
                foreach (byte[] nalUnit in nalUnits) {
                    if (nalUnit == null || nalUnit.Length == 0) continue;
                    ms.Write(AnnexBStartCode, 0, AnnexBStartCode.Length);
                    ms.Write(nalUnit, 0, nalUnit.Length);
                }
                if (ms.Length == 0) return true;

                var data = new MediaData(ms.ToArray(), rtpTimestamp);
                // TryAdd is non-blocking, returns false if adding is completed
                if (!_videoInputQueue.TryAdd(data)) {
                    Trace.WriteLine("[Video] Input queue is completed, cannot queue data.");
                    return false;
                }
                return true;
            }
        }

        public bool ProcessAudioData(byte[] audioData, uint rtpTimestamp) {
            if (audioData == null || audioData.Length == 0 || _disposed || _audioInputQueue == null || _audioInputQueue.IsAddingCompleted) return false;

            var data = new MediaData(audioData, rtpTimestamp);
            if (!_audioInputQueue.TryAdd(data)) {
                Trace.WriteLine("[Audio] Input queue is completed, cannot queue data.");
                return false;
            }
            return true;
        }


        // --- REWRITTEN Jitter Buffer / Writer Loop Methods for C# 7.3 ---

        private async Task VideoWriteLoopAsync(CancellationToken cancellationToken) {
            Trace.WriteLine("[Video] Async Writer loop started.");
            NamedPipeServerStream pipe = _videoPipeServer;
            BlockingCollection<MediaData> inputQueue = _videoInputQueue;
            SortedList<uint, Queue<MediaData>> buffer = _videoJitterBuffer;
            object bufferLock = _videoBufferLock;
            int clockRate = VideoClockRate;
            string streamType = "Video";
            Task connectTask = _videoPipeConnectTask;

            try {
                // Wait for pipe connection asynchronously
                if (connectTask != null) {
                    Trace.WriteLine($"[{streamType}] Writer waiting for pipe connection...");
                    await connectTask.ConfigureAwait(false); // Wait async
                    Trace.WriteLine($"[{streamType}] Pipe connected.");
                    if (!pipe.IsConnected) throw new IOException("Pipe failed to connect after wait.");
                } else { throw new InvalidOperationException("Pipe connection task is null!"); }

                // Task to consume input queue and fill buffer
                Task inputProcessorTask = Task.Run(() => {
                    try {
                        // Use GetConsumingEnumerable which blocks until item available or completed
                        foreach (var newItem in inputQueue.GetConsumingEnumerable(cancellationToken)) {
                            if (cancellationToken.IsCancellationRequested) break;
                            lock (bufferLock) {
                                if (!_videoFirstTimestampSeen) {
                                    _videoFirstRtpTimestamp = newItem.RtpTimestamp;
                                    _videoFirstTimestampSeen = true;
                                    Trace.WriteLine($"[{streamType}] First RTP timestamp seen: {_videoFirstRtpTimestamp}");
                                }
                                Queue<MediaData> queueForTimestamp;
                                if (!buffer.TryGetValue(newItem.RtpTimestamp, out queueForTimestamp)) {
                                    queueForTimestamp = new Queue<MediaData>();
                                    buffer.Add(newItem.RtpTimestamp, queueForTimestamp);
                                }
                                queueForTimestamp.Enqueue(newItem);
                                // Optional: Buffer size check
                            }
                        }
                    } catch (OperationCanceledException) { Trace.WriteLine($"[{streamType}] Input queue consumption cancelled."); } catch (InvalidOperationException) { Trace.WriteLine($"[{streamType}] Input queue completed."); } // Thrown when completed
                    catch (Exception ex) { Trace.WriteLine($"[{streamType}] Error consuming input queue: {ex.Message}"); } finally { Trace.WriteLine($"[{streamType}] Input queue processing finished."); }

                }, cancellationToken);


                // Main loop to check buffer and write scheduled packets
                while (!cancellationToken.IsCancellationRequested) {
                    MediaData packetToWrite = null;
                    bool packetDequeued = false;
                    TimeSpan timeUntilScheduled = TimeSpan.MaxValue; // Default to wait indefinitely if buffer empty

                    lock (bufferLock) {
                        if (buffer.Count > 0) {
                            uint earliestTimestamp = buffer.Keys[0];
                            Queue<MediaData> earliestQueue = buffer.Values[0];
                            MediaData candidatePacket = earliestQueue.Peek();

                            InitializePacingIfNeeded();

                            if (_stopwatchInitialized && _videoFirstTimestampSeen) {
                                uint rtpDiff = candidatePacket.RtpTimestamp - _videoFirstRtpTimestamp;
                                double elapsedSeconds = (double)rtpDiff / clockRate;
                                TimeSpan targetElapsed = TimeSpan.FromSeconds(elapsedSeconds);
                                TimeSpan scheduledWallclockOffset = _baseTimeOffset + targetElapsed + _bufferingDelay;
                                TimeSpan currentWallclockOffset = _stopwatch.Elapsed;
                                timeUntilScheduled = scheduledWallclockOffset - currentWallclockOffset;

                                if (timeUntilScheduled <= TimeSpan.FromMilliseconds(1)) {
                                    packetToWrite = earliestQueue.Dequeue();
                                    packetDequeued = true;
                                    if (earliestQueue.Count == 0) { buffer.RemoveAt(0); }

                                    if (timeUntilScheduled < TimeSpan.FromMilliseconds(-10)) {
                                        Trace.WriteLine($"[{streamType}] Warning: Packet RTP {packetToWrite.RtpTimestamp} written {-timeUntilScheduled.TotalMilliseconds:F2} ms late.");
                                    }
                                }
                                // else: Packet not due yet, timeUntilScheduled holds the positive delay needed
                            }
                            // else: pacing/buffer not ready, wait default amount
                        } else if (inputQueue.IsCompleted) {
                            Trace.WriteLine($"[{streamType}] Input complete and buffer empty. Exiting writer loop.");
                            break; // Input done and buffer empty
                        }
                        // else: Buffer is empty but input not yet complete, wait default amount

                    } // End lock

                    // Perform write or delay OUTSIDE lock
                    if (packetDequeued && packetToWrite != null) {
                        if (!pipe.IsConnected) { Trace.WriteLine($"[{streamType}] Pipe disconnected before writing."); break; }
                        await pipe.WriteAsync(packetToWrite.Data, 0, packetToWrite.Data.Length, cancellationToken).ConfigureAwait(false); // Async Write
                        // await pipe.FlushAsync(cancellationToken).ConfigureAwait(false); // Optional
                    } else {
                        // No packet ready to write, calculate appropriate delay
                        TimeSpan delayDuration;
                        if (timeUntilScheduled != TimeSpan.MaxValue && timeUntilScheduled > TimeSpan.Zero) {
                            // We peeked a packet, wait until its scheduled time (or a minimum poll interval)
                            delayDuration = timeUntilScheduled < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : timeUntilScheduled;
                        } else {
                            // Buffer was empty or pacing not ready, wait a default poll interval
                            delayDuration = TimeSpan.FromMilliseconds(10);
                        }

                        // Ensure delay doesn't exceed reasonable bounds if timeUntilScheduled is huge
                        if (delayDuration > TimeSpan.FromMilliseconds(100)) delayDuration = TimeSpan.FromMilliseconds(100);

                        await Task.Delay(delayDuration, cancellationToken).ConfigureAwait(false); // Async Delay
                    }

                } // End while loop
            } catch (OperationCanceledException) { Trace.WriteLine($"[{streamType}] Async Write loop cancelled."); } catch (InvalidOperationException ex) when (ex.Message.Contains("pipe has not been connected")) { Trace.WriteLine($"[{streamType}] Async Pipe connection failed or closed prematurely: {ex.Message}"); } catch (IOException ex) when (ex.Message.Contains("Pipe is broken")) { Trace.WriteLine($"[{streamType}] Async Pipe broken (client likely disconnected)."); } catch (Exception ex) { Trace.WriteLine($"[{streamType}] Error in async write loop: {ex.ToString()}"); } finally { Trace.WriteLine($"[{streamType}] Async Writer loop exiting."); }
        }

        // Async Audio write loop - similar structure to Async VideoWriteLoop
        private async Task AudioWriteLoopAsync(CancellationToken cancellationToken) {
            Trace.WriteLine("[Audio] Async Writer loop started.");
            NamedPipeServerStream pipe = _audioPipeServer;
            BlockingCollection<MediaData> inputQueue = _audioInputQueue;
            SortedList<uint, Queue<MediaData>> buffer = _audioJitterBuffer;
            object bufferLock = _audioBufferLock;
            int clockRate = _audioSampleRate;
            string streamType = "Audio";
            Task connectTask = _audioPipeConnectTask;

            try {
                // Wait for pipe connection asynchronously
                if (connectTask != null) { /*...*/ await connectTask.ConfigureAwait(false); /*...*/ } else { throw new InvalidOperationException("Pipe connection task is null!"); }


                // Task to consume input queue and fill buffer
                Task inputProcessorTask = Task.Run(() => {
                    try {
                        foreach (var newItem in inputQueue.GetConsumingEnumerable(cancellationToken)) {
                            if (cancellationToken.IsCancellationRequested) break;
                            lock (bufferLock) {
                                if (!_audioFirstTimestampSeen) {
                                    _audioFirstRtpTimestamp = newItem.RtpTimestamp;
                                    _audioFirstTimestampSeen = true;
                                    Trace.WriteLine($"[{streamType}] First RTP timestamp seen: {_audioFirstRtpTimestamp}");
                                }
                                Queue<MediaData> queueForTimestamp;
                                if (!buffer.TryGetValue(newItem.RtpTimestamp, out queueForTimestamp)) {
                                    queueForTimestamp = new Queue<MediaData>();
                                    buffer.Add(newItem.RtpTimestamp, queueForTimestamp);
                                }
                                queueForTimestamp.Enqueue(newItem);
                            }
                        }
                    } catch (OperationCanceledException) { Trace.WriteLine($"[{streamType}] Input queue consumption cancelled."); } catch (InvalidOperationException) { Trace.WriteLine($"[{streamType}] Input queue completed."); } catch (Exception ex) { Trace.WriteLine($"[{streamType}] Error consuming input queue: {ex.Message}"); } finally { Trace.WriteLine($"[{streamType}] Input queue processing finished."); }
                }, cancellationToken);


                // Main loop to check buffer and write scheduled packets
                while (!cancellationToken.IsCancellationRequested) {
                    MediaData packetToWrite = null;
                    bool packetDequeued = false;
                    TimeSpan timeUntilScheduled = TimeSpan.MaxValue;

                    lock (bufferLock) {
                        if (buffer.Count > 0) {
                            uint earliestTimestamp = buffer.Keys[0];
                            Queue<MediaData> earliestQueue = buffer.Values[0];
                            MediaData candidatePacket = earliestQueue.Peek();
                            InitializePacingIfNeeded();

                            if (_stopwatchInitialized && _audioFirstTimestampSeen) {
                                uint rtpDiff = candidatePacket.RtpTimestamp - _audioFirstRtpTimestamp;
                                double elapsedSeconds = (double)rtpDiff / clockRate;
                                TimeSpan targetElapsed = TimeSpan.FromSeconds(elapsedSeconds);
                                TimeSpan scheduledWallclockOffset = _baseTimeOffset + targetElapsed + _bufferingDelay;
                                TimeSpan currentWallclockOffset = _stopwatch.Elapsed;
                                timeUntilScheduled = scheduledWallclockOffset - currentWallclockOffset;

                                if (timeUntilScheduled <= TimeSpan.FromMilliseconds(1)) {
                                    packetToWrite = earliestQueue.Dequeue();
                                    packetDequeued = true;
                                    if (earliestQueue.Count == 0) { buffer.RemoveAt(0); }

                                    if (timeUntilScheduled < TimeSpan.FromMilliseconds(-10)) {
                                        Trace.WriteLine($"[{streamType}] Warning: Packet RTP {packetToWrite.RtpTimestamp} written {-timeUntilScheduled.TotalMilliseconds:F2} ms late.");
                                    }
                                }
                            }
                        } else if (inputQueue.IsCompleted) {
                            Trace.WriteLine($"[{streamType}] Input complete and buffer empty. Exiting writer loop.");
                            break;
                        }
                    } // End lock

                    // Perform write or delay OUTSIDE lock
                    if (packetDequeued && packetToWrite != null) {
                        if (!pipe.IsConnected) { Trace.WriteLine($"[{streamType}] Pipe disconnected before writing."); break; }
                        await pipe.WriteAsync(packetToWrite.Data, 0, packetToWrite.Data.Length, cancellationToken).ConfigureAwait(false);
                        // await pipe.FlushAsync(cancellationToken).ConfigureAwait(false); // Optional
                    } else {
                        TimeSpan delayDuration = (timeUntilScheduled != TimeSpan.MaxValue && timeUntilScheduled > TimeSpan.Zero)
                            ? (timeUntilScheduled < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : timeUntilScheduled)
                            : TimeSpan.FromMilliseconds(10);
                        if (delayDuration > TimeSpan.FromMilliseconds(100)) delayDuration = TimeSpan.FromMilliseconds(100);
                        await Task.Delay(delayDuration, cancellationToken).ConfigureAwait(false);
                    }
                } // End while
            } catch (OperationCanceledException) { Trace.WriteLine($"[{streamType}] Async Write loop cancelled."); } catch (InvalidOperationException ex) when (ex.Message.Contains("pipe has not been connected")) { Trace.WriteLine($"[{streamType}] Async Pipe connection failed or closed prematurely: {ex.Message}"); } catch (IOException ex) when (ex.Message.Contains("Pipe is broken")) { Trace.WriteLine($"[{streamType}] Async Pipe broken (client likely disconnected)."); } catch (Exception ex) { Trace.WriteLine($"[{streamType}] Error in async write loop: {ex.ToString()}"); } finally { Trace.WriteLine($"[{streamType}] Async Writer loop exiting."); }
        }


        // --- Helper Methods --- (GetPlatformPipePath, StopProcess, ReadStdErrorAsync, PipeStreamAsync need minor async adjustments if kept async)

        // ReadStdErrorAsync and PipeStreamAsync can remain largely the same Task-based async methods
        // as they interact with Process streams which support async well.

        private string GetPlatformPipePath(string pipeName) {
            // Same as before
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                return $@"\\.\pipe\{pipeName}";
            } else {
                return Path.Combine("/tmp", pipeName);
            }
        }

        private void StopProcess(Process process, string processName) {
            // Same as before
            if (process == null) return;
            try {
                if (!process.HasExited) {
                    if (process.CloseMainWindow()) {
                        if (process.WaitForExit(1000)) { process.Dispose(); return; }
                    }
                    if (!process.HasExited) {
                        Trace.WriteLine($"{processName} did not exit gracefully. Killing.");
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                }
            } catch (Exception ex) { Trace.WriteLine($"Error stopping {processName}: {ex.Message}"); } finally {
                try { process.Dispose(); } catch { }
            }
        }

        // Keep this async as it deals with Process streams
        private async Task ReadStdErrorAsync(Process process, string prefix, EventHandler<string> eventHandler, CancellationToken cancellationToken) {
            if (process == null || process.HasExited || !process.StartInfo.RedirectStandardError) return;
            try {
                using (var reader = process.StandardError) {
                    while (!cancellationToken.IsCancellationRequested) {
                        string line = null;
                        Task<string> readLineTask = null;
                        Task delayTask = null;
                        Task completedTask = null;
                        try {
                            readLineTask = reader.ReadLineAsync();
                            // Use Task.Delay with CancellationToken for timeout/cancellation
                            delayTask = Task.Delay(500, cancellationToken);
                            completedTask = await Task.WhenAny(readLineTask, delayTask).ConfigureAwait(false); // Use ConfigureAwait(false)

                            if (completedTask == readLineTask) {
                                line = await readLineTask.ConfigureAwait(false); // Get result
                                if (line == null) break; // End of stream
                            } else {
                                // Delay finished or was cancelled
                                if (cancellationToken.IsCancellationRequested || process.HasExited) break;
                                continue; // Timeout, continue loop
                            }
                        } catch (OperationCanceledException) { break; } // Task was cancelled
                        catch (Exception ex) { Trace.WriteLine($"{prefix} stderr ReadLineAsync error: {ex.Message}"); break; }

                        if (line != null) {
                            eventHandler?.Invoke(this, $"[{prefix}] {line}");
                        }
                        if (!process.HasExited) await Task.Yield(); // Yield if still running
                    }
                }
            } catch (InvalidOperationException ex) { Trace.WriteLine($"{prefix} stderr InvalidOp (process likely exited): {ex.Message}"); } catch (Exception ex) { Trace.WriteLine($"{prefix} stderr reader unexpected error: {ex.Message}"); }
        }

        // Keep this async
        private async Task PipeStreamAsync(Stream input, Stream output, string pipeName, CancellationToken cancellationToken) {
            Trace.WriteLine($"Starting {pipeName} pipe task...");
            byte[] buffer = new byte[81920]; // 80KB buffer
            try {
                while (!cancellationToken.IsCancellationRequested) {
                    int bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (bytesRead <= 0) { Trace.WriteLine($"{pipeName} input stream ended."); break; }

                    if (output.CanWrite) {
                        try {
                            await output.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        } catch (IOException ex) { Trace.WriteLine($"{pipeName} output stream write error: {ex.Message} (Likely downstream process closed). Stopping pipe task."); break; }
                    } else { Trace.WriteLine($"{pipeName} output stream cannot write, stopping pipe task."); break; }
                }
            } catch (OperationCanceledException) { Trace.WriteLine($"{pipeName} pipe task cancelled."); } catch (ObjectDisposedException) { Trace.WriteLine($"{pipeName} stream disposed during copy."); } catch (IOException ex) { Trace.WriteLine($"{pipeName} input stream read error: {ex.Message}"); } catch (Exception ex) { Trace.WriteLine($"Unexpected error piping {pipeName} streams: {ex.Message}"); } finally {
                Trace.WriteLine($"{pipeName} pipe task finished.");
                try { output?.Close(); } catch (Exception ex) { Trace.WriteLine($"Error closing downstream pipe for {pipeName}: {ex.Message}"); }
            }
        }


        // --- Dispose Method (Standard IDisposable) ---
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (_disposed) return;

            if (disposing) {
                Stop(); // Stop should handle cleanup of processes, pipes, tasks

                // Dispose managed resources like BlockingCollection
                _videoInputQueue?.Dispose();
                _audioInputQueue?.Dispose();
                _processCts?.Dispose(); // Dispose CTS last after signalling stop
            }
            // Release unmanaged resources here if any (none in this class)
            _disposed = true;
            Trace.WriteLine("AvPipeMuxer disposed.");
        }

        ~AvPipeMuxer() { Dispose(false); }

    } // End Class
} // End Namespace