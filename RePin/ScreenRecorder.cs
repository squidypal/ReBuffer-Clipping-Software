using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace ReBuffer
{
    public class ScreenRecorder : IDisposable
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

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => (int)Math.Min(_frameNumber, _bufferSeconds * _fps);
        public double BufferedSeconds => BufferedFrames / (double)_fps;
        public bool IsRecording => _isRecording;
        public long DroppedFrames => _totalDroppedFrames;
        public double DropRate => _frameNumber > 0 ? (_totalDroppedFrames / (double)_frameNumber) * 100 : 0;

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
            bool recordMic = true)
        {
            _bufferSeconds = bufferSeconds;
            _fps = fps;
            _bitrate = bitrate;
            _crf = crf;
            _preset = preset;
            _useHardwareEncoding = useHardwareEncoding;
            _savePath = savePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clips");
            _recordAudio = recordAudio;
            
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
            using var output = adapter.GetOutput(0);
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

            Console.WriteLine($"✓ Capture initialized: {_width}x{_height} @ {_fps} FPS");
            Console.WriteLine($"✓ Buffer: {_bufferSeconds} seconds");
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            
            if (_audioRecorder != null)
            {
                await _audioRecorder.StartAsync();
            }
            
            StartFFmpegRecording();
            
            _frameTimer.Restart();
            _frameNumber = 0;
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
                int maxSegments = (int)Math.Ceiling(_bufferSeconds / (double)segmentDuration) + 1;
                
                string codecArgs = _useHardwareEncoding
                    ? $"-c:v h264_nvenc -preset p4 -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2} -rc vbr"
                    : $"-c:v libx264 -preset {_preset} -crf {_crf} -b:v {_bitrate} -maxrate {_bitrate * 2} -bufsize {_bitrate * 2}";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                              $"-framerate {_fps} -i pipe:0 " +
                              $"{codecArgs} " +
                              $"-f segment -segment_time {segmentDuration} -segment_wrap {maxSegments} " +
                              $"-reset_timestamps 1 -pix_fmt yuv420p " +
                              $"\"{_segmentBasePath}_%03d.mkv\"",
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
                Console.WriteLine($"✓ FFmpeg recording started (segments: {maxSegments} x {segmentDuration}s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start FFmpeg: {ex.Message}");
                throw;
            }
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            int bytesPerFrame = _width * _height * 4;

            // Use ArrayPool for frame buffers to reduce GC pressure
            byte[] currentFrameBuffer = ArrayPool<byte>.Shared.Rent(bytesPerFrame);
            byte[] lastValidFrameBuffer = ArrayPool<byte>.Shared.Rent(bytesPerFrame);
            bool hasLastValidFrame = false;

            long targetTicks = Stopwatch.Frequency / _fps;
            long nextFrameTicks = _frameTimer.ElapsedTicks + targetTicks;

            Console.WriteLine($"🎬 Capture started: {_fps} FPS (using pooled buffers)");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        long currentTicks = _frameTimer.ElapsedTicks;

                        // Timing control
                        if (currentTicks < nextFrameTicks)
                        {
                            long ticksToWait = nextFrameTicks - currentTicks;
                            if (ticksToWait > targetTicks / 10)
                            {
                                Thread.Sleep((int)((ticksToWait * 1000) / Stopwatch.Frequency));
                            }
                            SpinWait.SpinUntil(() => _frameTimer.ElapsedTicks >= nextFrameTicks);
                        }

                        // Try to capture frame into pooled buffer
                        bool captured = TryCaptureFrameIntoBuffer(currentFrameBuffer, bytesPerFrame);

                        if (captured)
                        {
                            // Success - copy to last valid and write to FFmpeg
                            Array.Copy(currentFrameBuffer, lastValidFrameBuffer, bytesPerFrame);
                            hasLastValidFrame = true;
                            _totalFramesCaptured++;
                            _consecutiveDrops = 0;
                            _recoveryAttempts = 0;

                            WriteFrameToFFmpeg(currentFrameBuffer, bytesPerFrame);
                        }
                        else
                        {
                            // Frame drop
                            _totalDroppedFrames++;
                            _consecutiveDrops++;

                            // Only repeat last frame for brief drops (1-2 frames)
                            if (hasLastValidFrame && _consecutiveDrops <= 2)
                            {
                                WriteFrameToFFmpeg(lastValidFrameBuffer, bytesPerFrame);
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

                        // Periodic status logging
                        if (_frameNumber % (_fps * 10) == 0)
                        {
                            double elapsed = _frameTimer.Elapsed.TotalSeconds;
                            double actualFps = _frameNumber / elapsed;
                            double captureRate = (_totalFramesCaptured / (double)_frameNumber) * 100;

                            Console.WriteLine($"📊 Frames: {_frameNumber} | FPS: {actualFps:F1} | Capture: {captureRate:F1}% | Drops: {_totalDroppedFrames}");
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
                // Return buffers to pool
                ArrayPool<byte>.Shared.Return(currentFrameBuffer);
                ArrayPool<byte>.Shared.Return(lastValidFrameBuffer);
            }

            Console.WriteLine($"✓ Capture stopped: {_frameNumber} frames, {_totalDroppedFrames} dropped ({DropRate:F2}%)");
        }

        private void WriteFrameToFFmpeg(byte[] buffer, int length)
        {
            if (_ffmpegInput == null || !_ffmpegInput.CanWrite) return;

            try
            {
                _ffmpegInput.Write(buffer, 0, length);
            }
            catch (IOException)
            {
                Console.WriteLine("⚠ FFmpeg pipe closed");
            }
        }

        /// <summary>
        /// Captures a frame directly into the provided buffer (zero-allocation).
        /// </summary>
        private bool TryCaptureFrameIntoBuffer(byte[] buffer, int bytesPerFrame)
        {
            lock (_captureLock)
            {
                if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                    return false;

                SharpDX.DXGI.Resource? screenResource = null;

                try
                {
                    var result = _duplicatedOutput.TryAcquireNextFrame(0, out var frameInfo, out screenResource);

                    if (result.Failure || screenResource == null)
                        return false;

                    using var screenTexture = screenResource.QueryInterface<Texture2D>();
                    _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                    var dataBox = _device.ImmediateContext.MapSubresource(
                        _stagingTexture, 0, MapMode.Read, MapFlags.None);

                    try
                    {
                        Marshal.Copy(dataBox.DataPointer, buffer, 0, bytesPerFrame);
                        return true;
                    }
                    finally
                    {
                        _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                    }
                }
                catch
                {
                    return false;
                }
                finally
                {
                    screenResource?.Dispose();
                    try { _duplicatedOutput?.ReleaseFrame(); } catch { }
                }
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

                    // Try to reinitialize
                    if (_device != null)
                    {
                        using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
                        using var adapter = dxgiDevice.Adapter;
                        using var output = adapter.GetOutput(0);
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
                await Task.Run(() => SaveCurrentBuffer(filepath, linkedCts.Token), linkedCts.Token);

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

        private void SaveCurrentBuffer(string outputPath, CancellationToken cancellationToken = default)
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

                var allSegments = Directory.GetFiles(dir, pattern)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastWriteTime)
                    .ToList();

                if (allSegments.Count == 0)
                {
                    Console.WriteLine("❌ No segments found");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var bufferSegments = allSegments
                    .Skip(Math.Max(0, allSegments.Count - segmentsToSave))
                    .Select(f => f.FullName)
                    .ToArray();

                double videoDuration = bufferSegments.Length * segmentDuration;

                Console.WriteLine($"  Using {bufferSegments.Length} of {allSegments.Count} segments ({videoDuration}s)");

                concatFile = Path.Combine(dir, $".concat_{Guid.NewGuid():N}.txt");
                var concatLines = bufferSegments.Select(f => $"file '{Path.GetFileName(f)}'");
                File.WriteAllLines(concatFile, concatLines);

                string? desktopAudio = _audioRecorder?.GetDesktopAudioPath();
                string? micAudio = _audioRecorder?.GetMicAudioPath();

                bool hasDesktop = !string.IsNullOrEmpty(desktopAudio) && File.Exists(desktopAudio);
                bool hasMic = !string.IsNullOrEmpty(micAudio) && File.Exists(micAudio);

                string ffmpegArgs;

                if (hasDesktop && hasMic)
                {
                    Console.WriteLine("  Mixing desktop + mic audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"-sseof -{videoDuration} -i \"{desktopAudio}\" " +
                               $"-sseof -{videoDuration} -i \"{micAudio}\" " +
                               $"-filter_complex \"[1:a][2:a]amix=inputs=2:duration=first[a]\" " +
                               $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else if (hasDesktop)
                {
                    Console.WriteLine("  Adding desktop audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"-sseof -{videoDuration} -i \"{desktopAudio}\" " +
                               $"-map 0:v -map 1:a -shortest -c:v copy -c:a aac -b:a 192k " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else if (hasMic)
                {
                    Console.WriteLine("  Adding microphone audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"-sseof -{videoDuration} -i \"{micAudio}\" " +
                               $"-map 0:v -map 1:a -shortest -c:v copy -c:a aac -b:a 192k " +
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

                // Wait for FFmpeg with cancellation support
                while (!ffmpegProcess.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine("⚠ Cancellation requested, terminating FFmpeg");
                        try { ffmpegProcess.Kill(); } catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    Thread.Sleep(100);
                }

                // Cleanup old segments
                if (allSegments.Count > segmentsToSave)
                {
                    var oldSegments = allSegments.Take(allSegments.Count - segmentsToSave);
                    foreach (var oldFile in oldSegments)
                    {
                        try { File.Delete(oldFile.FullName); } catch { }
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

            _captureTask?.Wait(2000);

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
            }
            catch { }

            CleanupTempFiles();

            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();

            Console.WriteLine("✓ ScreenRecorder disposed");
        }
    }
}