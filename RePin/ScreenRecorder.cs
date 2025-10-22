using System;
using System.Collections.Generic;
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
        
        // Circular buffer with reference tracking
        private readonly FrameReference[] _frameBuffer;
        private readonly int _maxFrames;
        private int _writeIndex = 0;
        private int _bufferCount = 0;
        private readonly object _bufferLock = new();
        
        // Track unique byte arrays
        private readonly Dictionary<byte[], int> _arrayRefCount = new();
        
        private Device? _device;
        private OutputDuplication? _duplicatedOutput;
        private Texture2D? _stagingTexture;
        
        private Task? _captureTask;
        private CancellationTokenSource? _cts;
        
        private int _width;
        private int _height;
        private int _bytesPerFrame;
        private bool _isRecording;
        private readonly object _saveLock = new();

        // Diagnostics
        private long _totalFramesCaptured = 0;
        private long _totalFramesAttempted = 0;
        private Stopwatch _captureStopwatch = new Stopwatch();

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames 
        { 
            get 
            { 
                lock (_bufferLock) 
                { 
                    return _bufferCount; 
                } 
            } 
        }
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
            _maxFrames = bufferSeconds * fps;
            
            // Pre-allocate circular buffer
            _frameBuffer = new FrameReference[_maxFrames];
            for (int i = 0; i < _maxFrames; i++)
            {
                _frameBuffer[i] = new FrameReference();
            }

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

            // Calculate bytes per frame
            _bytesPerFrame = _width * _height * 4; // BGRA format

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
            Console.WriteLine($"✓ Bytes per frame: {_bytesPerFrame:N0} ({_bytesPerFrame / (1024.0 * 1024.0):F2} MB)");
            Console.WriteLine($"✓ Maximum memory: ~{EstimateMemoryMB():F1} MB");
        }

        public double EstimateMemoryMB()
        {
            return (_bytesPerFrame * _maxFrames) / (1024.0 * 1024.0);
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

        private void AddArrayReference(byte[] array)
        {
            lock (_arrayRefCount)
            {
                if (_arrayRefCount.ContainsKey(array))
                {
                    _arrayRefCount[array]++;
                }
                else
                {
                    _arrayRefCount[array] = 1;
                }
            }
        }

        private void ReleaseArrayReference(byte[] array)
        {
            lock (_arrayRefCount)
            {
                if (_arrayRefCount.ContainsKey(array))
                {
                    _arrayRefCount[array]--;
                    if (_arrayRefCount[array] <= 0)
                    {
                        _arrayRefCount.Remove(array);
                        // Array is no longer referenced, can be GC'd
                        Array.Clear(array, 0, array.Length);
                    }
                }
            }
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            // Use high-resolution timing
            var frameIntervalMs = 1000.0 / _fps;
            var sw = Stopwatch.StartNew();
            long frameNumber = 0;
            long missedFrames = 0;
            long duplicatedFrames = 0;
            
            // Keep last captured frame data reference
            byte[]? lastFrameDataRef = null;

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

                    // Try to capture new frame
                    bool capturedNewFrame = TryCaptureFrame(out byte[]? newFrameData);
                    
                    byte[] dataToStore;
                    
                    if (capturedNewFrame && newFrameData != null)
                    {
                        // New frame captured
                        dataToStore = newFrameData;
                        lastFrameDataRef = newFrameData;
                        _totalFramesCaptured++;
                    }
                    else if (lastFrameDataRef != null)
                    {
                        // No new frame - reuse last frame reference
                        dataToStore = lastFrameDataRef;
                        duplicatedFrames++;
                        
                        if (duplicatedFrames % 300 == 0)
                        {
                            Console.WriteLine($"⚠ Duplicated {duplicatedFrames} frames (no new screen content)");
                        }
                    }
                    else
                    {
                        // No frame available at all
                        missedFrames++;
                        frameNumber++;
                        continue;
                    }

                    // Store frame in circular buffer
                    lock (_bufferLock)
                    {
                        var frameRef = _frameBuffer[_writeIndex];
                        
                        // Release old array reference if being overwritten
                        if (frameRef.Data != null)
                        {
                            ReleaseArrayReference(frameRef.Data);
                        }
                        
                        // Store new reference
                        frameRef.Timestamp = DateTime.UtcNow;
                        frameRef.Data = dataToStore;
                        AddArrayReference(dataToStore);
                        
                        // Advance write position
                        _writeIndex = (_writeIndex + 1) % _maxFrames;
                        
                        if (_bufferCount < _maxFrames)
                        {
                            _bufferCount++;
                        }
                    }

                    frameNumber++;

                    // Log timing stats every 5 seconds
                    if (frameNumber % (_fps * 5) == 0)
                    {
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var actualFps = frameNumber / elapsed;
                        var captureRate = (_totalFramesCaptured / (double)_totalFramesAttempted) * 100;
                        
                        int uniqueArrays;
                        lock (_arrayRefCount)
                        {
                            uniqueArrays = _arrayRefCount.Count;
                        }
                        
                        lock (_bufferLock)
                        {
                            var estimatedMB = (uniqueArrays * _bytesPerFrame) / (1024.0 * 1024.0);
                            Console.WriteLine($"📊 Stats: {frameNumber} frames in {elapsed:F1}s | " +
                                            $"Actual FPS: {actualFps:F1} | " +
                                            $"Captured: {_totalFramesCaptured} ({captureRate:F1}%) | " +
                                            $"Duplicated: {duplicatedFrames} | " +
                                            $"Buffer: {_bufferCount}/{_maxFrames} | " +
                                            $"Unique arrays: {uniqueArrays} (~{estimatedMB:F0} MB)");
                        }
                        
                        // Gentle GC every 20 seconds
                        if (frameNumber % (_fps * 20) == 0)
                        {
                            GC.Collect(1, GCCollectionMode.Optimized, false);
                        }
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
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

        private bool TryCaptureFrame(out byte[]? frameData)
        {
            frameData = null;
            
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return false;

            SharpDX.DXGI.Resource? screenResource = null;
            OutputDuplicateFrameInformation frameInfo;
            
            try
            {
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out frameInfo, out screenResource);
                
                if (result.Failure || screenResource == null || frameInfo.LastPresentTime == 0)
                {
                    return false;
                }

                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                var dataBox = _device.ImmediateContext.MapSubresource(
                    _stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    frameData = new byte[_bytesPerFrame];
                    Marshal.Copy(dataBox.DataPointer, frameData, 0, _bytesPerFrame);
                    return true;
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
                FrameData[] framesToEncode;
                
                lock (_bufferLock)
                {
                    if (_bufferCount == 0)
                    {
                        Console.WriteLine("! No frames in buffer");
                        return string.Empty;
                    }
                    
                    framesToEncode = new FrameData[_bufferCount];
                    int readIndex = (_writeIndex - _bufferCount + _maxFrames) % _maxFrames;
                    
                    for (int i = 0; i < _bufferCount; i++)
                    {
                        int index = (readIndex + i) % _maxFrames;
                        var frameRef = _frameBuffer[index];
                        
                        framesToEncode[i] = new FrameData
                        {
                            Timestamp = frameRef.Timestamp,
                            Data = frameRef.Data,
                            Width = _width,
                            Height = _height,
                            Stride = _width * 4
                        };
                    }
                }

                var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                var filepath = Path.Combine(_savePath, filename);
                Directory.CreateDirectory(_savePath);

                Console.WriteLine($"📹 Encoding {framesToEncode.Length} frames at {_fps} FPS...");
                Console.WriteLine($"   Expected duration: {framesToEncode.Length / (double)_fps:F2} seconds");

                Task.Run(() => EncodeToMp4(framesToEncode, filepath));

                return filename;
            }
        }

        private void EncodeToMp4(FrameData[] frames, string outputPath)
        {
            try
            {
                string codecArgs = _useHardwareEncoding
                    ? $"-c:v h264_nvenc -preset {_preset} -cq {_crf} -r {_fps}"
                    : $"-c:v libx264 -preset {_preset} -crf {_crf} -r {_fps}";

                var ffmpegArgs = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                                $"-framerate {_fps} -i - {codecArgs} " +
                                $"-vsync 1 -fps_mode cfr -pix_fmt yuv420p -movflags +faststart " +
                                $"-loglevel warning -y \"{outputPath}\"";

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
                if (process == null) return;

                var sw = Stopwatch.StartNew();
                var errorThread = new Thread(() => process.StandardError.ReadToEnd());
                errorThread.Start();

                using var stdin = process.StandardInput.BaseStream;
                int frameCount = 0;
                foreach (var frame in frames)
                {
                    stdin.Write(frame.Data, 0, frame.Data.Length);
                    frameCount++;
                    if (frameCount % 300 == 0)
                        Console.WriteLine($"   Writing frame {frameCount}/{frames.Length}...");
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Encoding error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _isRecording = false;
            _cts?.Cancel();
            _captureTask?.Wait(1000);

            lock (_bufferLock)
            {
                for (int i = 0; i < _frameBuffer.Length; i++)
                {
                    if (_frameBuffer[i]?.Data != null)
                    {
                        ReleaseArrayReference(_frameBuffer[i].Data);
                        _frameBuffer[i].Data = null;
                    }
                }
                _bufferCount = 0;
                _writeIndex = 0;
            }

            lock (_arrayRefCount)
            {
                _arrayRefCount.Clear();
            }

            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
            
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true);
            
            Console.WriteLine("✓ ScreenRecorder disposed");
        }
    }

    public class FrameReference
    {
        public DateTime Timestamp { get; set; }
        public byte[]? Data { get; set; }
    }

    public class FrameData
    {
        public DateTime Timestamp { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
    }
}