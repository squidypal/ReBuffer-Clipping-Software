using System;
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
    /// <summary>
    /// Screen recorder with audio support
    /// Video: Real-time FFmpeg segments (circular buffer)
    /// Audio: WAV files, mixed during save
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
        
        // Audio
        private AudioRecorder? _audioRecorder;
        private readonly bool _recordAudio;
        
        // FFmpeg video recording
        private Process? _ffmpegProcess;
        private Stream? _ffmpegInput;
        private string? _segmentBasePath;
        
        // Timing & threading
        private Task? _captureTask;
        private CancellationTokenSource? _cts;
        private bool _isRecording;
        private readonly Stopwatch _frameTimer = new();
        private long _frameNumber = 0;
        
        // Performance monitoring
        private long _totalFramesCaptured = 0;

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => (int)Math.Min(_frameNumber, _bufferSeconds * _fps);
        public double BufferedSeconds => BufferedFrames / (double)_fps;

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
            
            // Initialize audio if enabled
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
            // D3D11 device with hardware acceleration
            _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None);

            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;
            using var output = adapter.GetOutput(0);
            using var output1 = output.QueryInterface<Output1>();

            var outputDesc = output.Description;
            _width = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            _height = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;

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
            Console.WriteLine($"✓ Buffer: {_bufferSeconds} seconds");
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            
            // Start audio recording
            if (_audioRecorder != null)
            {
                await _audioRecorder.StartAsync();
            }
            
            // Start FFmpeg video recording
            StartFFmpegRecording();
            
            // Start capture loop
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
                
                _segmentBasePath = Path.Combine(tempDir, $"buffer_{Guid.NewGuid():N}");
                
                // Video only - audio mixed later
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
                Console.WriteLine($"✓ FFmpeg recording started (video only, segments: {maxSegments} x {segmentDuration}s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed to start FFmpeg: {ex.Message}");
                throw;
            }
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            byte[]? lastValidFrame = null;
            int bytesPerFrame = _width * _height * 4;
            
            long targetTicks = Stopwatch.Frequency / _fps;
            long nextFrameTicks = _frameTimer.ElapsedTicks + targetTicks;
            
            int consecutiveDrops = 0;
            const int MAX_CONSECUTIVE_DROPS = 3;

            Console.WriteLine($"🎬 Capture started: {_fps} FPS");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    long currentTicks = _frameTimer.ElapsedTicks;
                    
                    if (currentTicks < nextFrameTicks)
                    {
                        long ticksToWait = nextFrameTicks - currentTicks;
                        if (ticksToWait > targetTicks / 10)
                        {
                            Thread.Sleep((int)((ticksToWait * 1000) / Stopwatch.Frequency));
                        }
                        SpinWait.SpinUntil(() => _frameTimer.ElapsedTicks >= nextFrameTicks);
                    }
                    
                    byte[]? frameData = TryCaptureFrame(bytesPerFrame);
                    
                    if (frameData != null)
                    {
                        lastValidFrame = frameData;
                        _totalFramesCaptured++;
                        consecutiveDrops = 0;
                        
                        if (_ffmpegInput != null && _ffmpegInput.CanWrite)
                        {
                            try
                            {
                                _ffmpegInput.Write(frameData, 0, frameData.Length);
                            }
                            catch (IOException)
                            {
                                Console.WriteLine("⚠ FFmpeg pipe closed");
                                break;
                            }
                        }
                    }
                    else if (lastValidFrame != null && consecutiveDrops < MAX_CONSECUTIVE_DROPS)
                    {
                        consecutiveDrops++;
                        if (_ffmpegInput != null && _ffmpegInput.CanWrite)
                        {
                            try
                            {
                                _ffmpegInput.Write(lastValidFrame, 0, lastValidFrame.Length);
                            }
                            catch (IOException)
                            {
                                break;
                            }
                        }
                    }
                    
                    _frameNumber++;
                    nextFrameTicks += targetTicks;
                    
                    if (nextFrameTicks < currentTicks - (targetTicks * 5))
                    {
                        nextFrameTicks = currentTicks + targetTicks;
                    }

                    if (_frameNumber % (_fps * 10) == 0)
                    {
                        double elapsed = _frameTimer.Elapsed.TotalSeconds;
                        double actualFps = _frameNumber / elapsed;
                        double captureRate = (_totalFramesCaptured / (double)_frameNumber) * 100;
                        
                        Console.WriteLine($"📊 Frames: {_frameNumber} | FPS: {actualFps:F1} | Capture: {captureRate:F1}%");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Capture error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }

            Console.WriteLine($"✓ Capture stopped: {_frameNumber} frames");
        }

        private byte[]? TryCaptureFrame(int bytesPerFrame)
        {
            if (_duplicatedOutput == null || _device == null || _stagingTexture == null)
                return null;

            SharpDX.DXGI.Resource? screenResource = null;
            
            try
            {
                var result = _duplicatedOutput.TryAcquireNextFrame(0, out var frameInfo, out screenResource);
                
                if (result.Failure || screenResource == null)
                    return null;

                using var screenTexture = screenResource.QueryInterface<Texture2D>();
                _device.ImmediateContext.CopyResource(screenTexture, _stagingTexture);

                var dataBox = _device.ImmediateContext.MapSubresource(
                    _stagingTexture, 0, MapMode.Read, MapFlags.None);

                try
                {
                    byte[] buffer = new byte[bytesPerFrame];
                    Marshal.Copy(dataBox.DataPointer, buffer, 0, bytesPerFrame);
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
            if (!_isRecording) return;
            
            _isRecording = false;
            _cts?.Cancel();
            
            // Stop audio
            _audioRecorder?.Stop();
            
            // Stop FFmpeg
            try
            {
                _ffmpegInput?.Close();
                _ffmpegProcess?.WaitForExit(3000);
                if (_ffmpegProcess?.HasExited == false)
                {
                    _ffmpegProcess.Kill();
                }
            }
            catch { }
        }

        public async Task<string> SaveClipAsync()
        {
            if (_segmentBasePath == null)
            {
                Console.WriteLine("! No recording active");
                return string.Empty;
            }

            var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filepath = Path.Combine(_savePath, filename);
            
            Console.WriteLine($"💾 Saving clip...");
            
            await Task.Run(() => SaveCurrentBuffer(filepath));
            
            return filename;
        }

        private void SaveCurrentBuffer(string outputPath)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                
                var dir = Path.GetDirectoryName(_segmentBasePath)!;
                var baseFilename = Path.GetFileName(_segmentBasePath);
                var pattern = $"{baseFilename}_*.mkv";
                
                var segmentFiles = Directory.GetFiles(dir, pattern)
                    .OrderBy(f => File.GetCreationTime(f))
                    .ToArray();
                
                if (segmentFiles.Length == 0)
                {
                    Console.WriteLine("❌ No segments found");
                    return;
                }
                
                Console.WriteLine($"  Found {segmentFiles.Length} video segments");
                
                // Concat video
                string concatFile = Path.Combine(dir, $".concat_{Guid.NewGuid():N}.txt");
                var concatLines = segmentFiles.Select(f => $"file '{Path.GetFileName(f)}'");
                File.WriteAllLines(concatFile, concatLines);
                
                // Get audio files
                string? desktopAudio = _audioRecorder?.GetDesktopAudioPath();
                string? micAudio = _audioRecorder?.GetMicAudioPath();
                
                bool hasDesktop = !string.IsNullOrEmpty(desktopAudio) && File.Exists(desktopAudio);
                bool hasMic = !string.IsNullOrEmpty(micAudio) && File.Exists(micAudio);
                
                string ffmpegArgs;
                
                if (hasDesktop && hasMic)
                {
                    // Mix both audio sources
                    Console.WriteLine("  Mixing desktop + mic audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"-i \"{desktopAudio}\" -i \"{micAudio}\" " +
                               $"-filter_complex \"[1:a][2:a]amix=inputs=2:duration=first[a]\" " +
                               $"-map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else if (hasDesktop)
                {
                    // Desktop audio only
                    Console.WriteLine("  Adding desktop audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"-i \"{desktopAudio}\" " +
                               $"-map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else if (hasMic)
                {
                    // Mic audio only
                    Console.WriteLine("  Adding microphone audio");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" " +
                               $"-i \"{micAudio}\" " +
                               $"-map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                else
                {
                    // Video only
                    Console.WriteLine("  No audio - video only");
                    ffmpegArgs = $"-f concat -safe 0 -i \"{concatFile}\" -c copy " +
                               $"-movflags +faststart -y \"{outputPath}\"";
                }
                
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

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("❌ Failed to start FFmpeg");
                    File.Delete(concatFile);
                    return;
                }
                
                bool completed = process.WaitForExit(30000);
                
                if (!completed)
                {
                    Console.WriteLine("⚠ FFmpeg timeout");
                    try { process.Kill(); } catch { }
                }
                
                File.Delete(concatFile);
                
                sw.Stop();

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Clip saved in {sw.ElapsedMilliseconds}ms");
                    Console.WriteLine($"  Size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
                }
                else
                {
                    Console.WriteLine($"❌ FFmpeg failed: {process.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Save error: {ex.Message}");
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
            
            // Stop audio
            _audioRecorder?.Dispose();
            
            // Stop capture
            _captureTask?.Wait(2000);
            
            // Stop FFmpeg
            try
            {
                _ffmpegInput?.Close();
                _ffmpegProcess?.WaitForExit(2000);
                if (_ffmpegProcess?.HasExited == false)
                {
                    _ffmpegProcess.Kill();
                }
                _ffmpegProcess?.Dispose();
            }
            catch { }
            
            CleanupTempFiles();
            
            // Cleanup DirectX
            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
            
            Console.WriteLine("✓ ScreenRecorder disposed");
        }
    }
}
