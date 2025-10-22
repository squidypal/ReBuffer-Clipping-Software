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
    /// Fixed to ensure proper MP4 file generation
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
        
        // Frame buffering - store raw frames for reliability
        private readonly ConcurrentQueue<RawFrame> _frameBuffer;
        private readonly int _maxBufferFrames;
        
        // Timing & threading
        private Task? _captureTask;
        private CancellationTokenSource? _cts;
        private bool _isRecording;
        private readonly Stopwatch _frameTimer = new();
        private long _frameNumber = 0;
        
        // Performance monitoring
        private long _totalFramesCaptured = 0;
        private readonly object _statsLock = new();

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => _frameBuffer.Count;
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
            
            _maxBufferFrames = bufferSeconds * fps;
            _frameBuffer = new ConcurrentQueue<RawFrame>();
            
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
            Console.WriteLine($"✓ Target memory: ~{EstimateMemoryMB():F0} MB");
        }

        public double EstimateMemoryMB()
        {
            // Raw frame buffer estimation
            double rawMB = (_bytesPerFrame * _maxBufferFrames) / (1024.0 * 1024.0);
            return rawMB;
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            
            // Start capture loop with precise timing
            _frameTimer.Restart();
            _frameNumber = 0;
            _captureTask = Task.Factory.StartNew(
                () => CaptureLoop(_cts.Token), 
                TaskCreationOptions.LongRunning);

            await Task.CompletedTask;
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
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
                        if (ticksToWait > targetTicks / 10)
                        {
                            Thread.Sleep((int)((ticksToWait * 1000) / Stopwatch.Frequency));
                        }
                        SpinWait.SpinUntil(() => _frameTimer.ElapsedTicks >= nextFrameTicks);
                    }
                    
                    // Capture frame
                    byte[]? frameData = TryCaptureFrame();
                    
                    if (frameData != null)
                    {
                        // New frame captured
                        lastValidFrame = frameData;
                        _totalFramesCaptured++;
                        consecutiveDrops = 0;
                        
                        AddFrameToBuffer(frameData);
                    }
                    else if (lastValidFrame != null)
                    {
                        // No new frame - repeat last frame
                        consecutiveDrops++;
                        
                        if (consecutiveDrops < MAX_CONSECUTIVE_DROPS)
                        {
                            byte[] duplicateFrame = new byte[lastValidFrame.Length];
                            Array.Copy(lastValidFrame, duplicateFrame, lastValidFrame.Length);
                            AddFrameToBuffer(duplicateFrame);
                        }
                        else
                        {
                            consecutiveDrops = 0; // Reset to avoid spam
                        }
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
                        
                        Console.WriteLine($"📊 {_frameNumber} frames | FPS: {actualFps:F1} | " +
                                        $"Captured: {captureRate:F1}% | Buffer: {_frameBuffer.Count}");
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

        private void AddFrameToBuffer(byte[] frameData)
        {
            var frame = new RawFrame
            {
                Data = frameData,
                FrameNumber = _frameNumber,
                Timestamp = DateTime.UtcNow
            };

            _frameBuffer.Enqueue(frame);

            // Maintain circular buffer
            while (_frameBuffer.Count > _maxBufferFrames)
            {
                _frameBuffer.TryDequeue(out _);
            }
        }

        private byte[]? TryCaptureFrame()
        {
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return null;

            SharpDX.DXGI.Resource? screenResource = null;
            
            try
            {
                // Non-blocking frame acquisition
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out var frameInfo, out screenResource);
                
                if (result.Failure || screenResource == null)
                    return null;

                // Fast GPU copy
                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                // Fast CPU read
                var dataBox = _device.ImmediateContext.MapSubresource(
                    _stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    byte[] buffer = new byte[_bytesPerFrame];
                    Marshal.Copy(dataBox.DataPointer, buffer, 0, _bytesPerFrame);
                    return buffer;
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                screenResource?.Dispose();
                try { _duplicatedOutput?.ReleaseFrame(); } catch { }
            }
        }

        public void Pause()
        {
            _isRecording = false;
            _cts?.Cancel();
        }

        public async Task<string> SaveClipAsync()
        {
            if (_frameBuffer.IsEmpty)
            {
                Console.WriteLine("! No frames in buffer");
                return string.Empty;
            }

            var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filepath = Path.Combine(_savePath, filename);
            Directory.CreateDirectory(_savePath);

            var frames = _frameBuffer.ToArray();
            Console.WriteLine($"💾 Saving {frames.Length} frames...");
            
            await Task.Run(() => SaveClipDirect(filepath, frames));
            
            return filename;
        }

        private void SaveClipDirect(string outputPath, RawFrame[] frames)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                
                // Build FFmpeg command for direct encoding
                string codecArgs = _useHardwareEncoding
                    ? $"-c:v h264_nvenc -preset p4 -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate * 2}"
                    : $"-c:v libx264 -preset {_preset} -crf {_crf} -b:v {_bitrate} -maxrate {_bitrate} -bufsize {_bitrate * 2}";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                              $"-framerate {_fps} -i pipe:0 {codecArgs} " +
                              $"-movflags +faststart -pix_fmt yuv420p -y \"{outputPath}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("❌ Failed to start FFmpeg");
                    return;
                }

                using (var stdin = process.StandardInput.BaseStream)
                {
                    foreach (var frame in frames)
                    {
                        stdin.Write(frame.Data, 0, frame.Data.Length);
                    }
                    stdin.Flush();
                }

                // Wait for FFmpeg to finish encoding
                bool completed = process.WaitForExit(30000); // 30 second timeout
                
                if (!completed)
                {
                    Console.WriteLine("⚠ FFmpeg encoding timeout, killing process");
                    try { process.Kill(); } catch { }
                    return;
                }

                sw.Stop();

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Clip saved in {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"  Duration: {frames.Length / (double)_fps:F1}s | " +
                                    $"Size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
                }
                else
                {
                    Console.WriteLine($"❌ FFmpeg failed with exit code: {process.ExitCode}");
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
            
            // Clear buffer to free memory
            while (_frameBuffer.TryDequeue(out _)) { }
            
            // Cleanup DirectX resources
            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
            
            Console.WriteLine("✓ ScreenRecorder disposed");
        }
    }

    public class RawFrame
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long FrameNumber { get; set; }
        public DateTime Timestamp { get; set; }
    }
}