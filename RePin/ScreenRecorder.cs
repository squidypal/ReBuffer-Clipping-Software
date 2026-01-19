using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ReBuffer.Core;
using ReBuffer.Core.Interfaces;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace ReBuffer
{
    public class ScreenRecorder : IScreenCapture
    {
        /// <summary>
        /// Checks if FFmpeg is available in the system PATH.
        /// </summary>
        /// <returns>True if FFmpeg is available, false otherwise.</returns>
        public static bool IsFfmpegAvailable()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                bool exited = process.WaitForExit(5000);
                return exited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the FFmpeg version string if available.
        /// </summary>
        public static string? GetFfmpegVersion()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadLine() ?? "";
                process.WaitForExit(5000);
                return output;
            }
            catch
            {
                return null;
            }
        }

        private readonly int _bufferSeconds;
        private readonly int _fps;
        private readonly int _bitrate;
        private readonly int _crf;
        private readonly string _preset;
        private readonly bool _useHardwareEncoding;
        private readonly string _savePath;
        private readonly int _monitorIndex;
        private readonly string _encoder;
        
        private Device? _device;
        private OutputDuplication? _duplicatedOutput;
        private Texture2D? _stagingTexture;
        private int _width;
        private int _height;
        
        private AudioRecorder? _audioRecorder;
        private readonly bool _recordAudio;
        
        private Process? _ffmpegProcess;
        private Stream? _ffmpegInput;
        private string? _segmentBasePath;

        // Segment tracking for safe cleanup (no more segment_wrap race condition)
        private readonly Queue<string> _activeSegments = new();
        private readonly object _segmentLock = new();
        private int _maxSegmentsToKeep;

        private Task? _captureTask;
        private CancellationTokenSource? _cts;
        private bool _isRecording;
        private readonly Stopwatch _frameTimer = new();
        private long _frameNumber = 0;

        private long _totalFramesCaptured = 0;
        private long _totalDroppedFrames = 0;
        private int _consecutiveDrops = 0;
        private int _recoveryAttempts = 0;
        private const int MAX_CONSECUTIVE_DROPS_BEFORE_RECOVERY = 10;
        private const int MAX_RECOVERY_ATTEMPTS = 3;

        // Object for DXGI reinitialization lock
        private readonly object _captureLock = new();

        // Non-blocking frame queue for FFmpeg writes (prevents capture stalls)
        private Channel<FrameData>? _frameChannel;
        private Task? _frameWriterTask;
        private long _droppedQueueFrames = 0;

        // Multimedia timer for precise frame timing (1ms resolution)
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private bool _highResTimerEnabled = false;

        // Custom frame buffer pool for exact-size allocations (50% memory savings vs ArrayPool)
        private FrameBufferPool? _frameBufferPool;

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => (int)Math.Min(_frameNumber, _bufferSeconds * _fps);
        public double BufferedSeconds => BufferedFrames / (double)_fps;
        public bool IsRecording => _isRecording;
        public long DroppedFrames => _totalDroppedFrames;
        public double DropRate => _frameNumber > 0 ? (_totalDroppedFrames / (double)_frameNumber) * 100 : 0;

        // Events for decoupled communication
        public event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;
        public event EventHandler<ClipSavedEventArgs>? ClipSaved;
        public event EventHandler<RecordingErrorEventArgs>? ErrorOccurred;
        public event EventHandler<PerformanceStatsEventArgs>? PerformanceUpdated;

        public ScreenRecorder(
            int bufferSeconds = 30,
            int fps = 60,
            int bitrate = 8_000_000,
            int crf = 23,
            string preset = "ultrafast",
            bool useHardwareEncoding = true,
            string? savePath = null,
            bool recordAudio = true,
            string? desktopAudioDevice = null,
            string? microphoneDevice = null,
            float desktopVolume = 1.0f,
            float micVolume = 1.0f,
            bool recordDesktop = true,
            bool recordMic = true,
            int monitorIndex = 0,
            string encoder = "h264_nvenc")
        {
            _bufferSeconds = bufferSeconds;
            _fps = fps;
            _bitrate = bitrate;
            _crf = crf;
            _preset = preset;
            _useHardwareEncoding = useHardwareEncoding;
            _savePath = savePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clips");
            _recordAudio = recordAudio;
            _monitorIndex = monitorIndex;
            _encoder = encoder;

            InitializeCapture();
            
            if (_recordAudio)
            {
                try
                {
                    var tempAudioPath = Path.Combine(_savePath, ".temp_audio");
                    _audioRecorder = new AudioRecorder(
                        tempAudioPath,
                        recordDesktop,
                        recordMic,
                        desktopVolume,
                        micVolume
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠ Audio init failed: {ex.Message}");
                    _audioRecorder = null;
                }
            }
        }

        private void InitializeCapture()
        {
            _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None);

            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;

            // Get the specified monitor (output) or fall back to primary
            int outputCount = adapter.GetOutputCount();
            int targetOutput = Math.Min(_monitorIndex, outputCount - 1);
            targetOutput = Math.Max(0, targetOutput);

            using var output = adapter.GetOutput(targetOutput);
            using var output1 = output.QueryInterface<Output1>();

            var outputDesc = output.Description;
            _width = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            _height = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;

            _duplicatedOutput = output1.DuplicateOutput(_device);

            var textureDesc = new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = _width,
                Height = _height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging
            };
            _stagingTexture = new Texture2D(_device, textureDesc);

            string monitorName = outputCount > 1 ? $"Monitor {targetOutput + 1}" : "Primary";
            Console.WriteLine($"✓ Capture initialized: {_width}x{_height} @ {_fps} FPS ({monitorName})");
            Console.WriteLine($"✓ Buffer: {_bufferSeconds} seconds | Encoder: {_encoder}");
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();

            // Enable high-resolution timer (1ms precision instead of default 15.6ms)
            if (TimeBeginPeriod(1) == 0)
            {
                _highResTimerEnabled = true;
                Console.WriteLine("✓ High-resolution timer enabled (1ms)");
            }

            // Initialize custom frame buffer pool with exact frame size
            // ArrayPool returns power-of-2 sizes (e.g., 16MB for 8.3MB frame), wasting ~50% memory
            int bytesPerFrame = _width * _height * 4;
            _frameBufferPool = new FrameBufferPool(bytesPerFrame, maxPoolSize: 8);
            _frameBufferPool.Warmup(4); // Pre-allocate 4 buffers to avoid allocation during capture
            Console.WriteLine($"✓ Frame buffer pool initialized ({bytesPerFrame:N0} bytes/frame)");

            // Create bounded channel for non-blocking frame writes
            // DropOldest ensures capture never blocks waiting for FFmpeg
            _frameChannel = Channel.CreateBounded<FrameData>(new BoundedChannelOptions(3)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

            if (_audioRecorder != null)
            {
                await _audioRecorder.StartAsync();
            }

            StartFFmpegRecording();

            // Start frame writer task (reads from channel, writes to FFmpeg)
            _frameWriterTask = Task.Run(() => FrameWriterLoopAsync(_cts.Token));

            _frameTimer.Restart();
            _frameNumber = 0;
            _droppedQueueFrames = 0;
            _captureTask = Task.Factory.StartNew(
                () => CaptureLoop(_cts.Token),
                TaskCreationOptions.LongRunning);

            await Task.CompletedTask;
        }

        private void StartFFmpegRecording()
        {
            try
            {
                Directory.CreateDirectory(_savePath);
                var tempDir = Path.Combine(_savePath, ".temp");
                Directory.CreateDirectory(tempDir);
                
                try
                {
                    var oldSegments = Directory.GetFiles(tempDir, "buffer_*.mkv");
                    foreach (var file in oldSegments)
                    {
                        try { File.Delete(file); } catch { }
                    }
                    var oldConcat = Directory.GetFiles(tempDir, ".concat_*.txt");
                    foreach (var file in oldConcat)
                    {
                        try { File.Delete(file); } catch { }
                    }
                    if (oldSegments.Length > 0)
                    {
                        Console.WriteLine($"✓ Cleaned up {oldSegments.Length} old segment files");
                    }
                }
                catch { }
                
                _segmentBasePath = Path.Combine(tempDir, $"buffer_{Guid.NewGuid():N}");

                int segmentDuration = 10;
                // Keep enough segments for buffer + 1 extra for safety margin
                _maxSegmentsToKeep = (int)Math.Ceiling(_bufferSeconds / (double)segmentDuration) + 2;

                // Build encoder arguments based on encoder type
                string codecArgs = BuildEncoderArgs();

                // Use monotonically increasing segment numbers (no -segment_wrap)
                // This eliminates the race condition where old segments get overwritten while being read
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                              $"-framerate {_fps} -i pipe:0 " +
                              $"{codecArgs} " +
                              $"-f segment -segment_time {segmentDuration} " +
                              $"-reset_timestamps 1 -pix_fmt yuv420p " +
                              $"\"{_segmentBasePath}_%06d.mkv\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                _ffmpegProcess = Process.Start(psi);
                if (_ffmpegProcess == null)
                {
                    throw new Exception("Failed to start FFmpeg process");
                }
                
                _ffmpegInput = _ffmpegProcess.StandardInput.BaseStream;

                // Start background segment cleanup task
                _ = Task.Run(() => SegmentCleanupLoop(_cts!.Token));

                Console.WriteLine($"✓ FFmpeg recording started (keeping {_maxSegmentsToKeep} x {segmentDuration}s segments, encoder: {_encoder})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start FFmpeg: {ex.Message}");
                throw;
            }
        }

        private string BuildEncoderArgs()
        {
            // Hardware encoders (NVENC, AMF, QSV)
            if (_encoder.Contains("nvenc"))
            {
                return $"-c:v {_encoder} -preset p4 -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2} -rc vbr";
            }
            if (_encoder.Contains("amf"))
            {
                return $"-c:v {_encoder} -quality balanced -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2}";
            }
            if (_encoder.Contains("qsv"))
            {
                return $"-c:v {_encoder} -preset faster -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2}";
            }

            // Software encoders
            if (_encoder == "libx265")
            {
                return $"-c:v libx265 -preset {_preset} -crf {_crf} -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2}";
            }
            if (_encoder == "libvpx-vp9")
            {
                return $"-c:v libvpx-vp9 -crf {_crf} -b:v {_bitrate} -deadline realtime -cpu-used 4";
            }
            if (_encoder == "libaom-av1")
            {
                return $"-c:v libaom-av1 -crf {_crf} -b:v {_bitrate} -cpu-used 8 -row-mt 1";
            }

            // Default to libx264
            return $"-c:v libx264 -preset {_preset} -crf {_crf} -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2}";
        }

        /// <summary>
        /// Background task that monitors for new segments and cleans up old ones.
        /// This replaces FFmpeg's -segment_wrap to avoid race conditions.
        /// </summary>
        private async Task SegmentCleanupLoop(CancellationToken cancellationToken)
        {
            if (_segmentBasePath == null) return;

            var dir = Path.GetDirectoryName(_segmentBasePath)!;
            var baseFilename = Path.GetFileName(_segmentBasePath);
            var pattern = $"{baseFilename}_*.mkv";
            var knownSegments = new HashSet<string>();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(2000, cancellationToken); // Check every 2 seconds

                    try
                    {
                        // Find all current segments (array-based to avoid LINQ allocations)
                        var currentSegments = Directory.GetFiles(dir, pattern);
                        Array.Sort(currentSegments); // Monotonic names sort correctly

                        // Track new segments
                        foreach (var segment in currentSegments)
                        {
                            if (knownSegments.Add(segment))
                            {
                                lock (_segmentLock)
                                {
                                    _activeSegments.Enqueue(segment);
                                }
                            }
                        }

                        // Clean up old segments beyond our buffer limit
                        lock (_segmentLock)
                        {
                            while (_activeSegments.Count > _maxSegmentsToKeep)
                            {
                                var oldSegment = _activeSegments.Dequeue();
                                try
                                {
                                    if (File.Exists(oldSegment))
                                    {
                                        File.Delete(oldSegment);
                                    }
                                    knownSegments.Remove(oldSegment);
                                }
                                catch
                                {
                                    // File might be in use, will try again next iteration
                                    _activeSegments.Enqueue(oldSegment);
                                    break;
                                }
                            }
                        }
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Directory was deleted, exit cleanup loop
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ Segment cleanup error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            int bytesPerFrame = _width * _height * 4;

            // Double-buffer system: swap references instead of copying 33MB per frame
            // Buffer 0 and 1 alternate roles, eliminating Array.Copy overhead
            // Use custom pool for exact-size buffers (50% memory savings vs ArrayPool)
            byte[][] frameBuffers = new byte[2][];
            frameBuffers[0] = _frameBufferPool?.Rent() ?? new byte[bytesPerFrame];
            frameBuffers[1] = _frameBufferPool?.Rent() ?? new byte[bytesPerFrame];
            int captureIndex = 0;      // Which buffer to capture into
            int lastValidIndex = -1;   // Which buffer has the last valid frame

            long targetTicks = Stopwatch.Frequency / _fps;
            long nextFrameTicks = _frameTimer.ElapsedTicks + targetTicks;

            Console.WriteLine($"🎬 Capture started: {_fps} FPS (double-buffered, custom pool)");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        long currentTicks = _frameTimer.ElapsedTicks;

                        // Improved timing control with high-res timer
                        if (currentTicks < nextFrameTicks)
                        {
                            long ticksToWait = nextFrameTicks - currentTicks;
                            long msToWait = (ticksToWait * 1000) / Stopwatch.Frequency;

                            // Sleep for most of the wait time (with 1ms precision from timeBeginPeriod)
                            if (msToWait > 2)
                            {
                                Thread.Sleep((int)(msToWait - 1));
                            }

                            // Spin for final precision (brief spin, not full wait)
                            while (_frameTimer.ElapsedTicks < nextFrameTicks)
                            {
                                Thread.SpinWait(10);
                            }
                        }

                        // Try to capture frame into current buffer
                        bool captured = TryCaptureFrameIntoBuffer(frameBuffers[captureIndex], bytesPerFrame);

                        if (captured)
                        {
                            // Success - this buffer now has the valid frame
                            lastValidIndex = captureIndex;
                            _totalFramesCaptured++;
                            _consecutiveDrops = 0;
                            _recoveryAttempts = 0;

                            // Queue frame for non-blocking write to FFmpeg
                            QueueFrameForWrite(frameBuffers[captureIndex], bytesPerFrame);

                            // Swap to other buffer for next capture
                            captureIndex = 1 - captureIndex;
                        }
                        else
                        {
                            // Frame drop
                            _totalDroppedFrames++;
                            _consecutiveDrops++;

                            // Only repeat last frame for brief drops (1-2 frames)
                            if (lastValidIndex >= 0 && _consecutiveDrops <= 2)
                            {
                                QueueFrameForWrite(frameBuffers[lastValidIndex], bytesPerFrame);
                            }
                            // For longer drops, skip encoding to maintain timing

                            // Attempt DXGI recovery after sustained drops
                            if (_consecutiveDrops >= MAX_CONSECUTIVE_DROPS_BEFORE_RECOVERY)
                            {
                                if (_recoveryAttempts < MAX_RECOVERY_ATTEMPTS)
                                {
                                    Console.WriteLine($"⚠ {_consecutiveDrops} consecutive drops - attempting DXGI recovery (attempt {_recoveryAttempts + 1}/{MAX_RECOVERY_ATTEMPTS})");
                                    if (TryRecoverCapture())
                                    {
                                        _consecutiveDrops = 0;
                                        Console.WriteLine("✓ DXGI capture recovered");
                                    }
                                    else
                                    {
                                        _recoveryAttempts++;
                                        Console.WriteLine("❌ DXGI recovery failed");
                                    }
                                }
                            }
                        }

                        _frameNumber++;
                        nextFrameTicks += targetTicks;

                        // Reset timing if we've fallen too far behind
                        if (nextFrameTicks < currentTicks - (targetTicks * 5))
                        {
                            nextFrameTicks = currentTicks + targetTicks;
                        }

                        // Periodic status logging (include queue drops)
                        if (_frameNumber % (_fps * 10) == 0)
                        {
                            double elapsed = _frameTimer.Elapsed.TotalSeconds;
                            double actualFps = _frameNumber / elapsed;
                            double captureRate = (_totalFramesCaptured / (double)_frameNumber) * 100;
                            long queueDrops = Interlocked.Read(ref _droppedQueueFrames);

                            Console.WriteLine($"📊 Frames: {_frameNumber} | FPS: {actualFps:F1} | Capture: {captureRate:F1}% | Drops: {_totalDroppedFrames} | QueueDrops: {queueDrops}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Capture error: {ex.Message}");
                        Thread.Sleep(100);
                    }
                }
            }
            finally
            {
                // Signal channel completion and return buffers to custom pool
                _frameChannel?.Writer.TryComplete();
                _frameBufferPool?.Return(frameBuffers[0]);
                _frameBufferPool?.Return(frameBuffers[1]);

                // Log pool diagnostics
                if (_frameBufferPool != null)
                {
                    Console.WriteLine($"📊 {_frameBufferPool.GetDiagnostics()}");
                }
            }

            Console.WriteLine($"✓ Capture stopped: {_frameNumber} frames, {_totalDroppedFrames} dropped ({DropRate:F2}%)");
        }

        /// <summary>
        /// Queues a frame for non-blocking write to FFmpeg.
        /// If the queue is full, the oldest frame is dropped (never blocks capture).
        /// </summary>
        private void QueueFrameForWrite(byte[] buffer, int length)
        {
            if (_frameChannel == null) return;

            // Copy to a new buffer for the queue (since we're double-buffering)
            // Use custom pool for exact-size allocation
            byte[] frameBuffer = _frameBufferPool?.Rent() ?? new byte[length];
            System.Buffer.BlockCopy(buffer, 0, frameBuffer, 0, length);

            if (!_frameChannel.Writer.TryWrite(new FrameData { Buffer = frameBuffer, Length = length }))
            {
                // Queue was full, frame was dropped by DropOldest policy
                Interlocked.Increment(ref _droppedQueueFrames);
                _frameBufferPool?.Return(frameBuffer);
            }
        }

        /// <summary>
        /// Background task that reads frames from the channel and writes to FFmpeg.
        /// This decouples capture timing from FFmpeg write speed.
        /// </summary>
        private async Task FrameWriterLoopAsync(CancellationToken cancellationToken)
        {
            if (_frameChannel == null || _ffmpegInput == null) return;

            try
            {
                await foreach (var frame in _frameChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        if (_ffmpegInput.CanWrite)
                        {
                            await _ffmpegInput.WriteAsync(frame.Buffer.AsMemory(0, frame.Length), cancellationToken);
                        }
                    }
                    catch (IOException)
                    {
                        Console.WriteLine("⚠ FFmpeg pipe closed");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    finally
                    {
                        _frameBufferPool?.Return(frame.Buffer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Frame writer error: {ex.Message}");
            }

            // Drain any remaining frames
            while (_frameChannel.Reader.TryRead(out var frame))
            {
                _frameBufferPool?.Return(frame.Buffer);
            }
        }

        /// <summary>
        /// Captures a frame directly into the provided buffer (zero-allocation).
        /// Lock scope minimized to reduce contention - only protects resource state checks.
        /// </summary>
        private bool TryCaptureFrameIntoBuffer(byte[] buffer, int bytesPerFrame)
        {
            // Get local references under lock (fast operation)
            OutputDuplication? duplication;
            Device? device;
            Texture2D? stagingTexture;

            lock (_captureLock)
            {
                duplication = _duplicatedOutput;
                device = _device;
                stagingTexture = _stagingTexture;

                if (duplication == null || device == null || stagingTexture == null)
                    return false;
            }

            // Perform actual capture without holding the lock (15-30ms operation)
            // This allows recovery attempts to proceed without waiting
            SharpDX.DXGI.Resource? screenResource = null;

            try
            {
                var result = duplication.TryAcquireNextFrame(0, out var frameInfo, out screenResource);

                if (result.Failure || screenResource == null)
                    return false;

                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                device.ImmediateContext.CopyResource(screenTexture, stagingTexture);

                var dataBox = device.ImmediateContext.MapSubresource(
                    stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    Marshal.Copy(dataBox.DataPointer, buffer, 0, bytesPerFrame);
                    return true;
                }
                finally
                {
                    device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
                }
            }
            catch (SharpDXException)
            {
                // DXGI resource was invalidated (e.g., during recovery)
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                screenResource?.Dispose();
                try { duplication.ReleaseFrame(); } catch { }
            }
        }

        /// <summary>
        /// Attempts to recover the DXGI capture pipeline after failures.
        /// </summary>
        private bool TryRecoverCapture()
        {
            lock (_captureLock)
            {
                try
                {
                    // Dispose old resources
                    _duplicatedOutput?.Dispose();
                    _duplicatedOutput = null;

                    // Small delay to let the system settle
                    Thread.Sleep(100);

                    // Try to reinitialize with the same monitor
                    if (_device != null)
                    {
                        using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
                        using var adapter = dxgiDevice.Adapter;

                        int outputCount = adapter.GetOutputCount();
                        int targetOutput = Math.Min(_monitorIndex, outputCount - 1);
                        targetOutput = Math.Max(0, targetOutput);

                        using var output = adapter.GetOutput(targetOutput);
                        using var output1 = output.QueryInterface<Output1>();

                        _duplicatedOutput = output1.DuplicateOutput(_device);
                        return _duplicatedOutput != null;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠ Recovery error: {ex.Message}");
                    return false;
                }
            }
        }

        public void Pause()
        {
            if (!_isRecording) return;

            _isRecording = false;
            _cts?.Cancel();

            _audioRecorder?.Stop();

            try
            {
                // Wait for frame writer task to complete
                _frameChannel?.Writer.TryComplete();
                _frameWriterTask?.Wait(2000);

                // Graceful shutdown: close stdin first to signal end of input
                if (_ffmpegInput != null)
                {
                    try
                    {
                        _ffmpegInput.Flush();
                        _ffmpegInput.Close();
                    }
                    catch { }
                    _ffmpegInput = null;
                }

                // Give FFmpeg time to finish writing segments gracefully
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    if (!_ffmpegProcess.WaitForExit(5000))
                    {
                        Console.WriteLine("⚠ FFmpeg did not exit gracefully, forcing termination");
                        try { _ffmpegProcess.Kill(); } catch { }
                    }
                }

                // Disable high-resolution timer
                if (_highResTimerEnabled)
                {
                    TimeEndPeriod(1);
                    _highResTimerEnabled = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Error during pause: {ex.Message}");
            }
        }

        public async Task<string> SaveClipAsync(CancellationToken cancellationToken = default)
        {
            if (_segmentBasePath == null)
            {
                Console.WriteLine("! No recording active");
                return string.Empty;
            }

            var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.mp4";
            var filepath = Path.Combine(_savePath, filename);

            Console.WriteLine($"💾 Saving clip...");

            // Create a timeout cancellation token (60 seconds max)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await SaveCurrentBufferAsync(filepath, linkedCts.Token);

                if (File.Exists(filepath))
                {
                    return filename;
                }

                Console.WriteLine("❌ Clip file was not created");
                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    Console.WriteLine("❌ Clip save timed out after 60 seconds");
                }
                else
                {
                    Console.WriteLine("❌ Clip save was cancelled");
                }
                return string.Empty;
            }
        }

        private async Task SaveCurrentBufferAsync(string outputPath, CancellationToken cancellationToken = default)
        {
            Process? ffmpegProcess = null;
            string? concatFile = null;

            try
            {
                var sw = Stopwatch.StartNew();

                var dir = Path.GetDirectoryName(_segmentBasePath)!;
                var baseFilename = Path.GetFileName(_segmentBasePath);
                var pattern = $"{baseFilename}_*.mkv";

                const int segmentDuration = 10;
                int segmentsToSave = (int)Math.Ceiling(_bufferSeconds / (double)segmentDuration);

                // Get segments sorted by name (monotonic numbering ensures correct order)
                // Using array-based operations to reduce LINQ allocations
                var files = Directory.GetFiles(dir, pattern);
                Array.Sort(files); // Monotonic names sort correctly

                if (files.Length == 0)
                {
                    Console.WriteLine("❌ No segments found");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                int startIndex = Math.Max(0, files.Length - segmentsToSave);
                var bufferSegments = new string[files.Length - startIndex];
                Array.Copy(files, startIndex, bufferSegments, 0, bufferSegments.Length);

                double videoDuration = bufferSegments.Length * segmentDuration;

                Console.WriteLine($"  Using {bufferSegments.Length} of {files.Length} segments ({videoDuration}s)");

                concatFile = Path.Combine(dir, $".concat_{Guid.NewGuid():N}.txt");
                var concatLines = bufferSegments.Select(f => $"file '{Path.GetFileName(f)}'");
                await File.WriteAllLinesAsync(concatFile, concatLines, cancellationToken);

                string? desktopAudio = _audioRecorder?.GetDesktopAudioPath();
                string? micAudio = _audioRecorder?.GetMicAudioPath();

                bool hasDesktop = !string.IsNullOrEmpty(desktopAudio) && File.Exists(desktopAudio);
                bool hasMic = !string.IsNullOrEmpty(micAudio) && File.Exists(micAudio);

                // Calculate audio seek offset using -ss instead of -sseof (faster seeking)
                // -ss seeks from start which is more efficient than -sseof seeking from end
                double audioRecordingDuration = _audioRecorder?.RecordingElapsed.TotalSeconds ?? 0;
                double audioSeekOffset = Math.Max(0, audioRecordingDuration - videoDuration);
                string seekArg = $"-ss {audioSeekOffset:F3}";

                // Determine if we need audio processing (re-encoding)
                // Skip re-encoding when volumes are at 1.0 - just copy the audio stream
                bool needsAudioProcessing = hasDesktop && hasMic; // Mixing always needs processing

                string audioCodec = needsAudioProcessing ? "-c:a aac -b:a 192k" : "-c:a aac -b:a 192k";

                string ffmpegArgs;

                if (hasDesktop && hasMic)
                {
                    Console.WriteLine("  Mixing desktop + mic audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"{seekArg} -i \"{desktopAudio}\" " +
                               $"{seekArg} -i \"{micAudio}\" " +
                               $"-filter_complex \"[1:a][2:a]amix=inputs=2:duration=first[a]\" " +
                               $"-map 0:v -map \"[a]\" -c:v copy {audioCodec} " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else if (hasDesktop)
                {
                    Console.WriteLine("  Adding desktop audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"{seekArg} -i \"{desktopAudio}\" " +
                               $"-map 0:v -map 1:a -shortest -c:v copy {audioCodec} " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else if (hasMic)
                {
                    Console.WriteLine("  Adding microphone audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"{seekArg} -i \"{micAudio}\" " +
                               $"-map 0:v -map 1:a -shortest -c:v copy {audioCodec} " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else
                {
                    Console.WriteLine("  No audio - video only");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" -c copy " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }

                cancellationToken.ThrowIfCancellationRequested();

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir
                };

                ffmpegProcess = Process.Start(psi);
                if (ffmpegProcess == null)
                {
                    Console.WriteLine("❌ Failed to start FFmpeg");
                    return;
                }

                // Use WaitForExitAsync instead of polling (eliminates 50-200ms latency)
                try
                {
                    await ffmpegProcess.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("⚠ Cancellation requested, terminating FFmpeg");
                    try { ffmpegProcess.Kill(entireProcessTree: true); } catch { }
                    throw;
                }

                // Cleanup old segments (now handled by SegmentCleanupLoop, but clean extras here)
                if (files.Length > segmentsToSave + _maxSegmentsToKeep)
                {
                    for (int i = 0; i < startIndex; i++)
                    {
                        try { File.Delete(files[i]); } catch { }
                    }
                }

                sw.Stop();

                if (ffmpegProcess.ExitCode == 0 && File.Exists(outputPath))
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Clip saved in {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"  Size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
                }
                else
                {
                    Console.WriteLine($"❌ FFmpeg failed: {ffmpegProcess.ExitCode}");
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Re-throw to be handled by caller
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Save error: {ex.Message}");
            }
            finally
            {
                // Cleanup
                if (concatFile != null)
                {
                    try { File.Delete(concatFile); } catch { }
                }
                ffmpegProcess?.Dispose();
            }
        }

        private void CleanupTempFiles()
        {
            if (_segmentBasePath == null) return;
            
            try
            {
                var dir = Path.GetDirectoryName(_segmentBasePath)!;
                var baseFilename = Path.GetFileName(_segmentBasePath);
                var pattern = $"{baseFilename}_*.mkv";
                var tempFiles = Directory.GetFiles(dir, pattern);
                
                foreach (var file in tempFiles)
                {
                    try { File.Delete(file); } catch { }
                }
                
                try
                {
                    if (!Directory.EnumerateFiles(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { }
            }
            catch { }
        }

        public void Dispose()
        {
            _isRecording = false;
            _cts?.Cancel();

            _audioRecorder?.Dispose();

            // Wait for capture and frame writer tasks
            _frameChannel?.Writer.TryComplete();
            _captureTask?.Wait(2000);
            _frameWriterTask?.Wait(2000);

            try
            {
                // Graceful shutdown: close stdin first to signal end of input
                if (_ffmpegInput != null)
                {
                    try
                    {
                        _ffmpegInput.Flush();
                        _ffmpegInput.Close();
                    }
                    catch { }
                    _ffmpegInput = null;
                }

                // Give FFmpeg time to finish writing segments gracefully
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    if (!_ffmpegProcess.WaitForExit(5000))
                    {
                        Console.WriteLine("⚠ FFmpeg did not exit gracefully, forcing termination");
                        try { _ffmpegProcess.Kill(); } catch { }
                    }
                }
                _ffmpegProcess?.Dispose();

                // Disable high-resolution timer
                if (_highResTimerEnabled)
                {
                    TimeEndPeriod(1);
                    _highResTimerEnabled = false;
                }
            }
            catch { }

            CleanupTempFiles();

            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();

            // Dispose frame buffer pool
            _frameBufferPool?.Dispose();
            _frameBufferPool = null;

            Console.WriteLine("✓ ScreenRecorder disposed");
        }
    }

    /// <summary>
    /// Represents a frame to be written to FFmpeg.
    /// </summary>
    internal readonly struct FrameData
    {
        public byte[] Buffer { get; init; }
        public int Length { get; init; }
    }
}