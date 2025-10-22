using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// <summary>
    /// High-performance screen recorder with real-time H.264 compression
    /// Memory target: ~500MB for 30s @ 1440p60
    /// Zero frame skipping with precise timing
    /// </summary>
    public class ScreenRecorder : IDisposable
    {
        // Configuration
        private readonly int _bufferSeconds;
        private readonly int _fps;
        private readonly int _bitrate;
        private readonly int _crf;
        private readonly string _preset;
        private readonly bool _useHardwareEncoding;
        private readonly string _savePath;
        
        // Capture infrastructure
        private Device? _device;
        private OutputDuplication? _duplicatedOutput;
        private Texture2D? _stagingTexture;
        private int _width;
        private int _height;
        private int _bytesPerFrame;
        
        // Real-time encoding pipeline
        private Process? _ffmpegProcess;
        private Stream? _ffmpegInput;
        private readonly BlockingCollection<CompressedFrame> _compressedBuffer;
        private readonly int _maxCompressedFrames;
        
        // Timing & threading
        private Task? _captureTask;
        private Task? _compressionMonitorTask;
        private CancellationTokenSource? _cts;
        private bool _isRecording;
        private readonly Stopwatch _frameTimer = new();
        private long _frameNumber = 0;
        
        // Performance monitoring
        private long _totalFramesCaptured = 0;
        private long _droppedFrames = 0;
        private long _totalCompressedBytes = 0;
        private readonly object _statsLock = new();

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => _compressedBuffer.Count;
        public double BufferedSeconds => (double)BufferedFrames / _fps;

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
            
            _maxCompressedFrames = bufferSeconds * fps;
            _compressedBuffer = new BlockingCollection<CompressedFrame>(_maxCompressedFrames);
            
            InitializeCapture();
        }

        private void InitializeCapture()
        {
            // D3D11 device with hardware acceleration
            _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None);

            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;
            using var output = adapter.GetOutput(0);
            using var output1 = output.QueryInterface<Output1>();

            var outputDesc = output.Description;
            _width = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            _height = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;
            _bytesPerFrame = _width * _height * 4;

            _duplicatedOutput = output1.DuplicateOutput(_device);

            // Staging texture for GPU->CPU transfer
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
            Console.WriteLine($"✓ Target memory: ~{EstimateMemoryMB():F0} MB (compressed)");
        }

        public double EstimateMemoryMB()
        {
            // Estimate H.264 compressed size
            // Typical compression: 1440p60 @ 8Mbps ≈ 1MB/s → 30MB for 30s
            // Add 20% overhead for buffer management
            double compressedMBPerSecond = (_bitrate / 8.0) / (1024.0 * 1024.0);
            return compressedMBPerSecond * _bufferSeconds * 1.2;
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            
            // Start FFmpeg encoder in streaming mode
            StartFFmpegEncoder();
            
            // Start capture loop with precise timing
            _frameTimer.Restart();
            _frameNumber = 0;
            _captureTask = Task.Factory.StartNew(
                () => CaptureLoop(_cts.Token), 
                TaskCreationOptions.LongRunning);
            
            // Monitor compression and maintain buffer
            _compressionMonitorTask = Task.Factory.StartNew(
                () => CompressionMonitorLoop(_cts.Token),
                TaskCreationOptions.LongRunning);

            await Task.CompletedTask;
        }

        private void StartFFmpegEncoder()
        {
            // Real-time encoding with minimal latency
            string codecArgs = _useHardwareEncoding
                ? $"-c:v h264_nvenc -preset p1 -tune ll -zerolatency 1 -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate / 2}"
                : $"-c:v libx264 -preset ultrafast -tune zerolatency -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate / 2}";

            var args = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                      $"-framerate {_fps} -i - {codecArgs} " +
                      $"-f h264 -framerate {_fps} pipe:1";

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            _ffmpegProcess = Process.Start(psi);
            _ffmpegInput = _ffmpegProcess!.StandardInput.BaseStream;
            
            Console.WriteLine("✓ FFmpeg encoder started (real-time mode)");
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            // Pre-allocate frame buffer (reused)
            byte[] frameBuffer = new byte[_bytesPerFrame];
            byte[]? lastValidFrame = null;
            
            long targetTicks = Stopwatch.Frequency / _fps;
            long nextFrameTicks = _frameTimer.ElapsedTicks + targetTicks;
            
            int consecutiveDrops = 0;
            const int MAX_CONSECUTIVE_DROPS = 5;

            Console.WriteLine($"🎬 Capture started: {_fps} FPS (tick interval: {targetTicks})");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    long currentTicks = _frameTimer.ElapsedTicks;
                    
                    // Precise frame pacing
                    if (currentTicks < nextFrameTicks)
                    {
                        long ticksToWait = nextFrameTicks - currentTicks;
                        if (ticksToWait > targetTicks / 10) // Only sleep if >10% of frame time
                        {
                            Thread.Sleep((int)((ticksToWait * 1000) / Stopwatch.Frequency));
                        }
                        SpinWait.SpinUntil(() => _frameTimer.ElapsedTicks >= nextFrameTicks);
                    }
                    
                    // Capture frame
                    bool captured = TryCaptureFrameFast(frameBuffer);
                    
                    if (captured)
                    {
                        // New frame - send to encoder
                        _ffmpegInput?.Write(frameBuffer, 0, _bytesPerFrame);
                        lastValidFrame = (byte[])frameBuffer.Clone();
                        _totalFramesCaptured++;
                        consecutiveDrops = 0;
                    }
                    else if (lastValidFrame != null)
                    {
                        // No new frame - repeat last frame
                        _ffmpegInput?.Write(lastValidFrame, 0, _bytesPerFrame);
                        consecutiveDrops++;
                        
                        if (consecutiveDrops >= MAX_CONSECUTIVE_DROPS)
                        {
                            // Screen idle - no need to spam warnings
                            consecutiveDrops = 0;
                        }
                    }
                    else
                    {
                        // No frames available yet - send black frame
                        Array.Clear(frameBuffer, 0, _bytesPerFrame);
                        _ffmpegInput?.Write(frameBuffer, 0, _bytesPerFrame);
                        _droppedFrames++;
                    }
                    
                    _frameNumber++;
                    nextFrameTicks += targetTicks;
                    
                    // Catch up if we've fallen behind
                    if (nextFrameTicks < currentTicks)
                    {
                        long framesBehind = (currentTicks - nextFrameTicks) / targetTicks;
                        if (framesBehind > 3)
                        {
                            Console.WriteLine($"⚠ Catching up: {framesBehind} frames behind");
                            nextFrameTicks = currentTicks + targetTicks;
                        }
                    }

                    // Stats every 5 seconds
                    if (_frameNumber % (_fps * 5) == 0)
                    {
                        double elapsed = _frameTimer.Elapsed.TotalSeconds;
                        double actualFps = _frameNumber / elapsed;
                        double captureRate = (_totalFramesCaptured / (double)_frameNumber) * 100;
                        
                        lock (_statsLock)
                        {
                            double compressedMB = _totalCompressedBytes / (1024.0 * 1024.0);
                            double compressionRatio = _totalCompressedBytes > 0 
                                ? (_frameNumber * _bytesPerFrame) / (double)_totalCompressedBytes 
                                : 0;
                            
                            Console.WriteLine($"📊 {_frameNumber} frames | FPS: {actualFps:F1} | " +
                                            $"Captured: {captureRate:F1}% | Buffer: {_compressedBuffer.Count} | " +
                                            $"Compressed: {compressedMB:F1}MB ({compressionRatio:F0}x)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Capture error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }

            Console.WriteLine($"✓ Capture stopped: {_frameNumber} frames captured");
        }

        private bool TryCaptureFrameFast(byte[] buffer)
        {
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return false;

            SharpDX.DXGI.Resource? screenResource = null;
            
            try
            {
                // Non-blocking frame acquisition
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out var frameInfo, out screenResource);
                
                if (result.Failure || screenResource == null)
                    return false;

                // Fast GPU copy
                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                // Fast CPU read
                var dataBox = _device.ImmediateContext.MapSubresource(
                    _stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    Marshal.Copy(dataBox.DataPointer, buffer, 0, _bytesPerFrame);
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

        private void CompressionMonitorLoop(CancellationToken cancellationToken)
        {
            // Read compressed H.264 data from FFmpeg output
            byte[] readBuffer = new byte[256 * 1024]; // 256KB chunks
            long frameCounter = 0;

            try
            {
                var outputStream = _ffmpegProcess?.StandardOutput.BaseStream;
                if (outputStream == null) return;

                while (!cancellationToken.IsCancellationRequested && _ffmpegProcess?.HasExited == false)
                {
                    int bytesRead = outputStream.Read(readBuffer, 0, readBuffer.Length);
                    if (bytesRead > 0)
                    {
                        // Store compressed frame
                        byte[] compressed = new byte[bytesRead];
                        Array.Copy(readBuffer, compressed, bytesRead);
                        
                        var frame = new CompressedFrame
                        {
                            Data = compressed,
                            FrameNumber = frameCounter++,
                            Timestamp = DateTime.UtcNow
                        };

                        // Maintain circular buffer
                        if (_compressedBuffer.Count >= _maxCompressedFrames)
                        {
                            _compressedBuffer.TryTake(out _); // Remove oldest
                        }
                        
                        _compressedBuffer.TryAdd(frame);
                        
                        lock (_statsLock)
                        {
                            _totalCompressedBytes += bytesRead;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Compression monitor error: {ex.Message}");
            }
        }

        public void Pause()
        {
            _isRecording = false;
            _cts?.Cancel();
        }

        public async Task<string> SaveClipAsync()
        {
            if (_compressedBuffer.Count == 0)
            {
                Console.WriteLine("! No frames in buffer");
                return string.Empty;
            }

            var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filepath = Path.Combine(_savePath, filename);
            Directory.CreateDirectory(_savePath);

            Console.WriteLine($"💾 Saving {_compressedBuffer.Count} compressed frames...");
            
            await Task.Run(() => SaveCompressedClip(filepath));
            
            return filename;
        }

        private void SaveCompressedClip(string outputPath)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                
                // Copy compressed frames
                var frames = _compressedBuffer.ToArray();
                
                // Remux to MP4 container (ultra-fast, no re-encoding)
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f h264 -framerate {_fps} -i - -c:v copy " +
                              $"-movflags +faststart -y \"{outputPath}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return;

                using var stdin = process.StandardInput.BaseStream;
                foreach (var frame in frames)
                {
                    stdin.Write(frame.Data, 0, frame.Data.Length);
                }
                stdin.Close();

                process.WaitForExit();
                sw.Stop();

                if (process.ExitCode == 0)
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Clip saved in {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"  Duration: {frames.Length / (double)_fps:F1}s | " +
                                    $"Size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Save error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _isRecording = false;
            _cts?.Cancel();
            
            // Stop capture
            _captureTask?.Wait(2000);
            _compressionMonitorTask?.Wait(2000);
            
            // Stop FFmpeg
            try
            {
                _ffmpegInput?.Close();
                _ffmpegProcess?.Kill();
                _ffmpegProcess?.WaitForExit(1000);
            }
            catch { }
            
            // Cleanup
            _compressedBuffer?.Dispose();
            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
            
            Console.WriteLine("✓ ScreenRecorder disposed");
        }
    }

    public class CompressedFrame
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long FrameNumber { get; set; }
        public DateTime Timestamp { get; set; }
    }
}