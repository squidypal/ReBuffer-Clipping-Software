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
            bool useHardwareEncoding = true)
        {
            _bufferSeconds = bufferSeconds;
            _fps = fps;
            _bitrate = bitrate;
            _crf = crf;
            _preset = preset;
            _useHardwareEncoding = useHardwareEncoding;
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
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var sw = Stopwatch.StartNew();
            long frameCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var targetTime = TimeSpan.FromTicks(frameCount * frameInterval.Ticks);
                    var elapsed = sw.Elapsed;

                    if (elapsed < targetTime)
                    {
                        var sleepTime = targetTime - elapsed;
                        if (sleepTime.TotalMilliseconds > 1)
                        {
                            Thread.Sleep((int)sleepTime.TotalMilliseconds);
                        }
                        continue;
                    }

                    CaptureFrame();
                    frameCount++;

                    // Maintain buffer size
                    while (_frameBuffer.Count > _maxFrames)
                    {
                        if (_frameBuffer.TryDequeue(out var oldFrame))
                        {
                            oldFrame.Dispose();
                        }
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    // No new frame, continue
                    continue;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code)
                {
                    // Display mode changed, reinitialize
                    ReinitializeCapture();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Capture error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }

        private void CaptureFrame()
        {
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return;

            SharpDX.DXGI.Resource? screenResource = null;
            OutputDuplicateFrameInformation frameInfo;
            
            try
            {
                // Try to get duplicated frame with 0ms timeout (non-blocking)
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out frameInfo, out screenResource);
                
                if (result.Failure || screenResource == null)
                    return;

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
                        Timestamp = DateTime.Now,
                        Width = _width,
                        Height = _height,
                        Data = new byte[_height * dataBox.RowPitch]
                    };

                    Marshal.Copy(dataBox.DataPointer, frameData.Data, 0, frameData.Data.Length);
                    frameData.Stride = dataBox.RowPitch;

                    _frameBuffer.Enqueue(frameData);
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
                var filepath = Path.Combine("clips", filename);
                Directory.CreateDirectory("clips");

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
                    // Try NVENC first, fallback to software if not available
                    codecArgs = $"-c:v h264_nvenc -preset {_preset} -cq {_crf}";
                }
                else
                {
                    // Software encoding with libx264
                    codecArgs = $"-c:v libx264 -preset {_preset} -crf {_crf}";
                }

                var ffmpegArgs = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                                $"-framerate {_fps} -i - " +
                                $"{codecArgs} " +
                                $"-pix_fmt yuv420p -movflags +faststart " +
                                $"-y \"{outputPath}\"";

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

                using var stdin = process.StandardInput.BaseStream;

                // Write frames to FFmpeg
                foreach (var frame in frames)
                {
                    stdin.Write(frame.Data, 0, frame.Data.Length);
                }

                stdin.Close();
                
                // Read any errors
                var errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                // If hardware encoding failed, try software encoding as fallback
                if (process.ExitCode != 0 && _useHardwareEncoding && errors.Contains("h264_nvenc"))
                {
                    Console.WriteLine("Hardware encoding failed, falling back to software encoding...");
                    
                    // Retry with software encoding
                    var softwareArgs = $"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} " +
                                      $"-framerate {_fps} -i - " +
                                      $"-c:v libx264 -preset {_preset} -crf {_crf} " +
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
                    }
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
