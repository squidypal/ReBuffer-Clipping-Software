using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Buffer = System.Buffer;

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
        
        // DISK-BASED CIRCULAR BUFFER
        private string _tempBufferFile;
        private FileStream? _bufferFileStream;
        private long _maxBufferSize;
        private long _currentWritePosition = 0;
        private int _framesWritten = 0;
        private readonly object _fileLock = new();
        
        // Capture buffer (reused)
        private readonly byte[] _captureBuffer;
        private readonly byte[] _lastFrameBuffer;
        private bool _lastFrameValid = false;
        
        private Device? _device;
        private OutputDuplication? _duplicatedOutput;
        private Texture2D? _stagingTexture;
        
        private Task? _captureTask;
        private CancellationTokenSource? _cts;
        
        private int _width;
        private int _height;
        private int _bytesPerFrame;
        private bool _isRecording;

        // Stats
        private long _totalFrames = 0;
        private long _duplicateFrames = 0;

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => Math.Min(_framesWritten, _bufferSeconds * _fps);
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
            
            InitializeCapture();
            
            // Allocate capture buffers (only ~16 MB in RAM)
            _captureBuffer = new byte[_bytesPerFrame];
            _lastFrameBuffer = new byte[_bytesPerFrame];
            
            // Create temp file for circular buffer
            var tempPath = Path.Combine(Path.GetTempPath(), "RePin");
            Directory.CreateDirectory(tempPath);
            _tempBufferFile = Path.Combine(tempPath, $"buffer_{Process.GetCurrentProcess().Id}.tmp");
            
            // Calculate max buffer size (store all frames on disk)
            _maxBufferSize = (long)_bytesPerFrame * _bufferSeconds * _fps;
            
            // Pre-allocate file
            _bufferFileStream = new FileStream(_tempBufferFile, FileMode.Create, FileAccess.ReadWrite, 
                FileShare.None, 65536, FileOptions.WriteThrough);
            _bufferFileStream.SetLength(_maxBufferSize);
            
            double bufferMB = _maxBufferSize / (1024.0 * 1024.0);
            double ramMB = (_bytesPerFrame * 2) / (1024.0 * 1024.0); // 2 buffers only
            
            Console.WriteLine($"✓ DISK BUFFER: {bufferMB:F0} MB on disk");
            Console.WriteLine($"✓ RAM: ~{ramMB:F0} MB (buffers only)");
            Console.WriteLine($"✓ Temp: {_tempBufferFile}");
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
            _bytesPerFrame = _width * _height * 4;

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

            Console.WriteLine($"✓ Capture: {_width}×{_height}");
        }

        public double EstimateMemoryMB()
        {
            return (_bytesPerFrame * 2) / (1024.0 * 1024.0);
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            _totalFrames = 0;
            _duplicateFrames = 0;
            
            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
            await Task.CompletedTask;
        }

        public void Pause()
        {
            _isRecording = false;
            _cts?.Cancel();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool IsFrameIdentical(byte[] frame1, byte[] frame2)
        {
            fixed (byte* p1 = frame1)
            fixed (byte* p2 = frame2)
            {
                long* l1 = (long*)p1;
                long* l2 = (long*)p2;
                int longCount = _bytesPerFrame / 8;
                
                for (int i = 0; i < longCount; i += 64)
                {
                    if (l1[i] != l2[i])
                        return false;
                }
                
                for (int i = 0; i < longCount; i++)
                {
                    if (l1[i] != l2[i])
                        return false;
                }
            }
            return true;
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            var frameIntervalMs = 1000.0 / _fps;
            var sw = Stopwatch.StartNew();
            long frameNumber = 0;

            Console.WriteLine($"🎬 Disk-based capture: {_fps} FPS");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var targetTimeMs = frameNumber * frameIntervalMs;
                    var sleepTimeMs = targetTimeMs - sw.Elapsed.TotalMilliseconds;

                    if (sleepTimeMs > 1)
                        Thread.Sleep((int)sleepTimeMs);
                    else if (sleepTimeMs > 0)
                        SpinWait.SpinUntil(() => sw.Elapsed.TotalMilliseconds >= targetTimeMs);

                    bool captured = TryCaptureFrameToBuffer();
                    
                    if (captured)
                    {
                        bool isDuplicate = _lastFrameValid && IsFrameIdentical(_captureBuffer, _lastFrameBuffer);
                        
                        if (!isDuplicate)
                        {
                            WriteFrameToDisk(_captureBuffer);
                            Buffer.BlockCopy(_captureBuffer, 0, _lastFrameBuffer, 0, _bytesPerFrame);
                            _lastFrameValid = true;
                        }
                        else
                        {
                            WriteFrameToDisk(_lastFrameBuffer);
                            _duplicateFrames++;
                        }
                        
                        _totalFrames++;
                    }
                    else if (_lastFrameValid)
                    {
                        WriteFrameToDisk(_lastFrameBuffer);
                        _duplicateFrames++;
                        _totalFrames++;
                    }

                    frameNumber++;

                    if (frameNumber % (_fps * 5) == 0)
                    {
                        var actualFps = frameNumber / sw.Elapsed.TotalSeconds;
                        var dupRate = (_duplicateFrames / (double)_totalFrames) * 100;
                        var gcMemoryMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                        
                        Console.WriteLine($"📊 {frameNumber} frames | {actualFps:F1} FPS | " +
                                        $"Buf: {BufferedFrames} | Dup: {dupRate:F0}% | RAM: {gcMemoryMB:F0} MB");
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    frameNumber++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        private void WriteFrameToDisk(byte[] frameData)
        {
            lock (_fileLock)
            {
                try
                {
                    long writePosition = _currentWritePosition % _maxBufferSize;
                    
                    _bufferFileStream!.Seek(writePosition, SeekOrigin.Begin);
                    _bufferFileStream.Write(frameData, 0, _bytesPerFrame);
                    
                    _currentWritePosition += _bytesPerFrame;
                    _framesWritten++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Write error: {ex.Message}");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryCaptureFrameToBuffer()
        {
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return false;

            SharpDX.DXGI.Resource? screenResource = null;
            
            try
            {
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out var frameInfo, out screenResource);
                
                if (result.Failure || screenResource == null || frameInfo.LastPresentTime == 0)
                    return false;

                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                var dataBox = _device.ImmediateContext.MapSubresource(
                    _stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    Marshal.Copy(dataBox.DataPointer, _captureBuffer, 0, _bytesPerFrame);
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
                try { _duplicatedOutput?.ReleaseFrame(); } catch { }
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
            int framesToSave = BufferedFrames;
            
            if (framesToSave == 0)
            {
                Console.WriteLine("❌ No frames");
                return string.Empty;
            }

            var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filepath = Path.Combine(_savePath, filename);
            Directory.CreateDirectory(_savePath);

            Console.WriteLine($"💾 Encoding {framesToSave} frames...");

            _ = Task.Run(() => EncodeFromDisk(framesToSave, filepath));

            return filename;
        }

        private void EncodeFromDisk(int frameCount, string outputPath)
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
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return;

                var sw = Stopwatch.StartNew();
                
                using var stdin = process.StandardInput.BaseStream;
                
                byte[] frameBuffer = new byte[_bytesPerFrame];
                
                lock (_fileLock)
                {
                    long totalFramesInBuffer = Math.Min(_framesWritten, _bufferSeconds * _fps);
                    long startFrame = _framesWritten - totalFramesInBuffer;
                    long startPosition = (startFrame * _bytesPerFrame) % _maxBufferSize;
                    
                    for (int i = 0; i < frameCount; i++)
                    {
                        long readPosition = (startPosition + (i * _bytesPerFrame)) % _maxBufferSize;
                        
                        _bufferFileStream!.Seek(readPosition, SeekOrigin.Begin);
                        int bytesRead = _bufferFileStream.Read(frameBuffer, 0, _bytesPerFrame);
                        
                        if (bytesRead == _bytesPerFrame)
                        {
                            stdin.Write(frameBuffer, 0, _bytesPerFrame);
                        }
                    }
                }

                stdin.Close();
                process.WaitForExit();
                sw.Stop();

                if (process.ExitCode == 0)
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Saved in {sw.ElapsedMilliseconds}ms → {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Encode error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _isRecording = false;
            _cts?.Cancel();
            _captureTask?.Wait(2000);

            lock (_fileLock)
            {
                _bufferFileStream?.Close();
                _bufferFileStream?.Dispose();
                
                try
                {
                    if (File.Exists(_tempBufferFile))
                        File.Delete(_tempBufferFile);
                }
                catch { }
            }

            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
            
            Console.WriteLine("✓ Disposed");
        }
    }
}