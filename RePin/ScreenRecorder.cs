using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace RePin
{
    public class ScreenRecorder : IDisposable
    {
        private readonly int _bufferSeconds;
        private readonly int _fps;
        private readonly int _bitrate;
        private readonly int _crf;
        private readonly string _preset;
        private readonly bool _useHardwareEncoding;
        private readonly string _savePath;
        private readonly ConcurrentQueue<FrameData> _frameBuffer;
        private readonly int _maxFrames;
        
        private Device? _device;
        private OutputDuplication? _duplicatedOutput;
        private Texture2D? _stagingTexture;
        
        private Task? _captureTask;
        private CancellationTokenSource? _cts;
        
        private int _width;
        private int _height;
        private bool _isRecording;
        private readonly object _saveLock = new();

        // Diagnostics
        private long _totalFramesCaptured = 0;
        private long _totalFramesAttempted = 0;
        private Stopwatch _captureStopwatch = new Stopwatch();

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => _frameBuffer.Count;
        public double BufferedSeconds => (double)_frameBuffer.Count / _fps;

        public ScreenRecorder(
            int bufferSeconds = 30, 
            int fps = 60, 
            int bitrate = 8_000_000,
            int crf = 23,
            string preset = "ultrafast",
            bool useHardwareEncoding = true,
            string? savePath = null)
        {
            _bufferSeconds = bufferSeconds;
            _fps = fps;
            _bitrate = bitrate;
            _crf = crf;
            _preset = preset;
            _useHardwareEncoding = useHardwareEncoding;
            _savePath = savePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clips");
            _maxFrames = bufferSeconds * fps;
            _frameBuffer = new ConcurrentQueue<FrameData>();

            InitializeCapture();
        }

        private void InitializeCapture()
        {
            // Create D3D11 Device
            _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None);

            // Get DXGI Device
            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;
            using var output = adapter.GetOutput(0);
            using var output1 = output.QueryInterface<Output1>();

            // Get output description
            var outputDesc = output.Description;
            _width = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            _height = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;

            // Duplicate output
            _duplicatedOutput = output1.DuplicateOutput(_device);

            // Create staging texture for CPU readback
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

            Console.WriteLine($"✓ Hardware acceleration initialized: {_width}x{_height}");
            Console.WriteLine($"✓ Memory buffer: ~{EstimateMemoryMB():F1} MB");
        }

        public double EstimateMemoryMB()
        {
            long bytesPerFrame = _width * _height * 4; // BGRA
            return (bytesPerFrame * _maxFrames) / (1024.0 * 1024.0);
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            _captureStopwatch.Restart();
            _totalFramesCaptured = 0;
            _totalFramesAttempted = 0;
            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));

            await Task.CompletedTask;
        }

        public void Pause()
        {
            _isRecording = false;
            _cts?.Cancel();
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            // Use high-resolution timing
            var frameIntervalMs = 1000.0 / _fps;
            var sw = Stopwatch.StartNew();
            long frameNumber = 0;
            long missedFrames = 0;
            long duplicatedFrames = 0;
            
            FrameData? lastFrame = null;

            Console.WriteLine($"Starting capture loop: {_fps} FPS (frame interval: {frameIntervalMs:F2}ms)");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _totalFramesAttempted++;
                    
                    // Calculate when this frame should be captured
                    var targetTimeMs = frameNumber * frameIntervalMs;
                    var currentTimeMs = sw.Elapsed.TotalMilliseconds;
                    var sleepTimeMs = targetTimeMs - currentTimeMs;

                    // If we're ahead of schedule, sleep
                    if (sleepTimeMs > 1)
                    {
                        Thread.Sleep((int)sleepTimeMs);
                    }
                    else if (sleepTimeMs > 0)
                    {
                        // Spin-wait for sub-millisecond precision
                        SpinWait.SpinUntil(() => sw.Elapsed.TotalMilliseconds >= targetTimeMs);
                    }

                    // Try to capture frame
                    var capturedFrame = CaptureFrame();
                    
                    if (capturedFrame != null)
                    {
                        // Successfully captured a new frame
                        _frameBuffer.Enqueue(capturedFrame);
                        _totalFramesCaptured++;
                        lastFrame = capturedFrame;
                    }
                    else if (lastFrame != null)
                    {
                        // No new frame available - duplicate the last frame to maintain timing
                        var duplicatedFrame = new FrameData
                        {
                            Timestamp = DateTime.UtcNow,
                            Width = lastFrame.Width,
                            Height = lastFrame.Height,
                            Stride = lastFrame.Stride,
                            Data = new byte[lastFrame.Data.Length]
                        };
                        Array.Copy(lastFrame.Data, duplicatedFrame.Data, lastFrame.Data.Length);
                        
                        _frameBuffer.Enqueue(duplicatedFrame);
                        duplicatedFrames++;
                        
                        if (duplicatedFrames % 60 == 0)
                        {
                            Console.WriteLine($"⚠ Duplicated {duplicatedFrames} frames (no new screen content)");
                        }
                    }
                    else
                    {
                        // No frame available and no previous frame to duplicate
                        missedFrames++;
                    }

                    frameNumber++;

                    // Maintain buffer size
                    while (_frameBuffer.Count > _maxFrames)
                    {
                        if (_frameBuffer.TryDequeue(out var oldFrame))
                        {
                            oldFrame.Dispose();
                        }
                    }

                    // Log timing stats every 5 seconds
                    if (frameNumber % (_fps * 5) == 0)
                    {
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var expectedFrames = elapsed * _fps;
                        var actualFps = frameNumber / elapsed;
                        var captureRate = (_totalFramesCaptured / (double)_totalFramesAttempted) * 100;
                        
                        Console.WriteLine($"📊 Stats: {frameNumber} frames in {elapsed:F1}s | " +
                                        $"Actual FPS: {actualFps:F1} | " +
                                        $"Captured: {_totalFramesCaptured} ({captureRate:F1}%) | " +
                                        $"Duplicated: {duplicatedFrames} | " +
                                        $"Buffer: {_frameBuffer.Count}");
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    // Timeout - continue
                    frameNumber++;
                    continue;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code)
                {
                    Console.WriteLine("Display mode changed, reinitializing...");
                    ReinitializeCapture();
                    sw.Restart();
                    frameNumber = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Capture error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }

            var finalElapsed = sw.Elapsed.TotalSeconds;
            var finalFps = frameNumber / finalElapsed;
            Console.WriteLine($"Capture stopped: {frameNumber} frames in {finalElapsed:F1}s (avg {finalFps:F1} FPS)");
            Console.WriteLine($"Total captured: {_totalFramesCaptured}, Duplicated: {duplicatedFrames}, Missed: {missedFrames}");
        }

        private FrameData? CaptureFrame()
        {
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return null;

            SharpDX.DXGI.Resource? screenResource = null;
            OutputDuplicateFrameInformation frameInfo;
            
            try
            {
                // Try to get duplicated frame with 0ms timeout (non-blocking)
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out frameInfo, out screenResource);
                
                // Check if there's actually a new frame
                if (result.Failure || screenResource == null || frameInfo.LastPresentTime == 0)
                {
                    return null;
                }

                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                
                // Copy to staging texture
                _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                // Map the staging texture to read pixels
                var dataBox = _device.ImmediateContext.MapSubresource(
                    _stagingTexture, 
                    0, 
                    MapMode.Read, 
                    MapFlags.None);

                try
                {
                    // Copy frame data
                    var frameData = new FrameData
                    {
                        Timestamp = DateTime.UtcNow,
                        Width = _width,
                        Height = _height,
                        Data = new byte[_height * dataBox.RowPitch],
                        Stride = dataBox.RowPitch
                    };

                    Marshal.Copy(dataBox.DataPointer, frameData.Data, 0, frameData.Data.Length);

                    return frameData;
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                }
            }
            finally
            {
                screenResource?.Dispose();
                try
                {
                    _duplicatedOutput?.ReleaseFrame();
                }
                catch { }
            }
        }

        private void ReinitializeCapture()
        {
            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();

            Thread.Sleep(500);
            InitializeCapture();
        }

        public async Task<string> SaveClipAsync()
        {
            lock (_saveLock)
            {
                var frames = _frameBuffer.ToArray();
                
                if (frames.Length == 0)
                {
                    Console.WriteLine("! No frames in buffer");
                    return string.Empty;
                }

                var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                var filepath = Path.Combine(_savePath, filename);
                Directory.CreateDirectory(_savePath);

                Console.WriteLine($"📹 Encoding {frames.Length} frames at {_fps} FPS...");
                Console.WriteLine($"   Expected duration: {frames.Length / (double)_fps:F2} seconds");

                // Use FFmpeg for encoding
                Task.Run(() => EncodeToMp4(frames, filepath));

                return filename;
            }
        }

        private void EncodeToMp4(FrameData[] frames, string outputPath)
        {
            try
            {
                string codecArgs;
                
                if (_useHardwareEncoding)
                {
                    // NVENC with strict constant frame rate
                    codecArgs = $"-c:v h264_nvenc -preset {_preset} -cq {_crf} -r {_fps}";
                }
                else
                {
                    // Software encoding with strict constant frame rate
                    codecArgs = $"-c:v libx264 -preset {_preset} -crf {_crf} -r {_fps}";
                }

                // CRITICAL: Multiple framerate settings to ensure proper timing
                var ffmpegArgs = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                                $"-framerate {_fps} " +  // Input framerate
                                $"-i - " +
                                $"{codecArgs} " +
                                $"-vsync 1 " +  // Use 1 (auto) instead of cfr for better compatibility
                                $"-fps_mode cfr " +  // Modern FFmpeg: force constant frame rate
                                $"-pix_fmt yuv420p " +
                                $"-movflags +faststart " +
                                $"-loglevel warning " +  // Show warnings to debug
                                $"-y \"{outputPath}\"";

                Console.WriteLine($"FFmpeg command: ffmpeg {ffmpegArgs}");

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("Failed to start FFmpeg process");
                    return;
                }

                var sw = Stopwatch.StartNew();

                // Start error reader on separate thread
                var errorOutput = string.Empty;
                var errorThread = new Thread(() =>
                {
                    errorOutput = process.StandardError.ReadToEnd();
                });
                errorThread.Start();

                using var stdin = process.StandardInput.BaseStream;

                // Write frames to FFmpeg
                int frameCount = 0;
                foreach (var frame in frames)
                {
                    stdin.Write(frame.Data, 0, frame.Data.Length);
                    frameCount++;
                    
                    if (frameCount % 300 == 0)
                    {
                        Console.WriteLine($"   Writing frame {frameCount}/{frames.Length}...");
                    }
                }

                stdin.Close();
                
                errorThread.Join();
                process.WaitForExit();

                sw.Stop();

                if (process.ExitCode == 0)
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Encoding completed in {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"   File size: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
                    Console.WriteLine($"   Frames written: {frameCount}");
                }
                else
                {
                    Console.WriteLine($"✗ FFmpeg failed with exit code {process.ExitCode}");
                    Console.WriteLine($"Error output: {errorOutput}");

                    // If hardware encoding failed, try software encoding as fallback
                    if (_useHardwareEncoding && errorOutput.Contains("h264_nvenc"))
                    {
                        Console.WriteLine("Retrying with software encoding...");
                        
                        var softwareArgs = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                                          $"-framerate {_fps} -i - " +
                                          $"-c:v libx264 -preset {_preset} -crf {_crf} -r {_fps} " +
                                          $"-vsync 1 -fps_mode cfr " +
                                          $"-pix_fmt yuv420p -movflags +faststart " +
                                          $"-y \"{outputPath}\"";

                        psi.Arguments = softwareArgs;
                        using var retryProcess = Process.Start(psi);
                        if (retryProcess != null)
                        {
                            using var retryStdin = retryProcess.StandardInput.BaseStream;
                            foreach (var frame in frames)
                            {
                                retryStdin.Write(frame.Data, 0, frame.Data.Length);
                            }
                            retryStdin.Close();
                            retryProcess.WaitForExit();
                            
                            if (retryProcess.ExitCode == 0)
                            {
                                Console.WriteLine("✓ Software encoding successful");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Encoding error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            _isRecording = false;
            _cts?.Cancel();
            _captureTask?.Wait(1000);

            // Clear buffer
            while (_frameBuffer.TryDequeue(out var frame))
            {
                frame.Dispose();
            }

            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
        }
    }

    public class FrameData : IDisposable
    {
        public DateTime Timestamp { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }

        public void Dispose()
        {
            Data = Array.Empty<byte>();
        }
    }
}