using System;
using System.Collections.Generic;
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
        
        // SMALL ADAPTIVE POOL - Only allocates unique frames
        private readonly Dictionary<int, byte[]> _framePool;
        private readonly Queue<int> _poolLRU;
        private int _nextFrameId = 0;
        private readonly int _maxPoolSlots;
        
        // Circular buffer stores frame IDs
        private readonly int[] _frameIds;
        private readonly int _maxFrames;
        private int _writeIndex = 0;
        private int _bufferCount = 0;
        private readonly object _bufferLock = new();
        
        // Capture state
        private readonly byte[] _captureBuffer;
        private int _lastFrameId = -1;
        private readonly HashSet<int> _activeFrameIds;
        
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
        private long _uniqueFrames = 0;
        private long _duplicateFrames = 0;

        public int Width => _width;
        public int Height => _height;
        public int BufferedFrames => _bufferCount;
        public double BufferedSeconds => (double)_bufferCount / _fps;

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
            
            InitializeCapture();
            
            // CRITICAL: Limit pool to reasonable size
            // Typical content has 50-150 unique frames in 30s
            // Allocate for 100 unique frames maximum
            _maxPoolSlots = 100;
            _framePool = new Dictionary<int, byte[]>(_maxPoolSlots);
            _poolLRU = new Queue<int>(_maxPoolSlots);
            _activeFrameIds = new HashSet<int>();
            
            _captureBuffer = new byte[_bytesPerFrame];
            _frameIds = new int[_maxFrames];
            Array.Fill(_frameIds, -1);
            
            double maxMemoryMB = (_maxPoolSlots * _bytesPerFrame) / (1024.0 * 1024.0);
            Console.WriteLine($"✓ Adaptive pool: Max {_maxPoolSlots} unique frames = {maxMemoryMB:F0} MB maximum");
            Console.WriteLine($"✓ Buffer: {_maxFrames} frame slots (stores frame IDs only)");
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

            Console.WriteLine($"✓ Capture: {_width}×{_height} = {_bytesPerFrame / (1024.0 * 1024.0):F2} MB per frame");
        }

        public double EstimateMemoryMB()
        {
            return (_bytesPerFrame * _maxPoolSlots) / (1024.0 * 1024.0);
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _cts = new CancellationTokenSource();
            _totalFrames = 0;
            _uniqueFrames = 0;
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
        private unsafe bool IsFrameIdentical(byte[] captured, byte[] stored)
        {
            // Fast comparison using 64-bit chunks
            fixed (byte* pCaptured = captured)
            fixed (byte* pStored = stored)
            {
                long* p1 = (long*)pCaptured;
                long* p2 = (long*)pStored;
                int longCount = _bytesPerFrame / 8;
                
                // Sample every 16th long for speed
                for (int i = 0; i < longCount; i += 16)
                {
                    if (p1[i] != p2[i])
                        return false;
                }
                
                // If sample matches, do full check
                for (int i = 0; i < longCount; i++)
                {
                    if (p1[i] != p2[i])
                        return false;
                }
            }
            
            return true;
        }

        private void CleanupOldFrames()
        {
            lock (_bufferLock)
            {
                // Build set of currently used frame IDs
                _activeFrameIds.Clear();
                for (int i = 0; i < _bufferCount; i++)
                {
                    int idx = (_writeIndex - _bufferCount + i + _maxFrames) % _maxFrames;
                    int frameId = _frameIds[idx];
                    if (frameId >= 0)
                    {
                        _activeFrameIds.Add(frameId);
                    }
                }
                
                // Remove frames not in use
                var toRemove = new List<int>();
                foreach (var frameId in _framePool.Keys)
                {
                    if (!_activeFrameIds.Contains(frameId) && frameId != _lastFrameId)
                    {
                        toRemove.Add(frameId);
                    }
                }
                
                foreach (var frameId in toRemove)
                {
                    _framePool.Remove(frameId);
                }
                
                if (toRemove.Count > 0)
                {
                    Console.WriteLine($"🧹 Cleaned {toRemove.Count} unused frames, {_framePool.Count} remain");
                }
            }
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            var frameIntervalMs = 1000.0 / _fps;
            var sw = Stopwatch.StartNew();
            long frameNumber = 0;

            Console.WriteLine($"🎬 Capture started: {_fps} FPS");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Precise timing
                    var targetTimeMs = frameNumber * frameIntervalMs;
                    var sleepTimeMs = targetTimeMs - sw.Elapsed.TotalMilliseconds;

                    if (sleepTimeMs > 1)
                    {
                        Thread.Sleep((int)sleepTimeMs);
                    }
                    else if (sleepTimeMs > 0)
                    {
                        SpinWait.SpinUntil(() => sw.Elapsed.TotalMilliseconds >= targetTimeMs);
                    }

                    // Capture frame
                    bool captured = TryCaptureFrameToBuffer();
                    
                    int frameIdToStore;
                    
                    if (captured)
                    {
                        // Check if identical to last frame
                        bool isDuplicate = false;
                        
                        if (_lastFrameId >= 0 && _framePool.ContainsKey(_lastFrameId))
                        {
                            isDuplicate = IsFrameIdentical(_captureBuffer, _framePool[_lastFrameId]);
                        }
                        
                        if (isDuplicate)
                        {
                            // Reuse last frame ID
                            frameIdToStore = _lastFrameId;
                            _duplicateFrames++;
                        }
                        else
                        {
                            // Allocate new frame
                            frameIdToStore = _nextFrameId++;
                            
                            // Check pool size limit
                            if (_framePool.Count >= _maxPoolSlots)
                            {
                                // Remove least recently used
                                CleanupOldFrames();
                                
                                // If still full, force remove oldest
                                if (_framePool.Count >= _maxPoolSlots && _poolLRU.Count > 0)
                                {
                                    int oldId = _poolLRU.Dequeue();
                                    if (oldId != _lastFrameId)
                                    {
                                        _framePool.Remove(oldId);
                                    }
                                }
                            }
                            
                            // Store new frame
                            byte[] newFrame = new byte[_bytesPerFrame];
                            Buffer.BlockCopy(_captureBuffer, 0, newFrame, 0, _bytesPerFrame);
                            _framePool[frameIdToStore] = newFrame;
                            _poolLRU.Enqueue(frameIdToStore);
                            
                            _lastFrameId = frameIdToStore;
                            _uniqueFrames++;
                        }
                    }
                    else
                    {
                        // No new frame - reuse last
                        if (_lastFrameId < 0)
                        {
                            frameNumber++;
                            continue;
                        }
                        frameIdToStore = _lastFrameId;
                        _duplicateFrames++;
                    }

                    // Store in circular buffer
                    lock (_bufferLock)
                    {
                        _frameIds[_writeIndex] = frameIdToStore;
                        _writeIndex = (_writeIndex + 1) % _maxFrames;
                        
                        if (_bufferCount < _maxFrames)
                        {
                            _bufferCount++;
                        }
                    }

                    _totalFrames++;
                    frameNumber++;

                    // Stats every 5 seconds
                    if (frameNumber % (_fps * 5) == 0)
                    {
                        var actualFps = frameNumber / sw.Elapsed.TotalSeconds;
                        var dupRate = (_duplicateFrames / (double)_totalFrames) * 100;
                        var memoryMB = (_framePool.Count * _bytesPerFrame) / (1024.0 * 1024.0);
                        
                        Console.WriteLine($"📊 Frame {frameNumber} | FPS: {actualFps:F1} | " +
                                        $"Pool: {_framePool.Count}/{_maxPoolSlots} | " +
                                        $"Unique: {_uniqueFrames} | Dup: {_duplicateFrames} ({dupRate:F0}%) | " +
                                        $"RAM: {memoryMB:F0} MB");
                    }
                    
                    // Cleanup every 30 seconds
                    if (frameNumber % (_fps * 30) == 0)
                    {
                        CleanupOldFrames();
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    frameNumber++;
                    continue;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code)
                {
                    Console.WriteLine("⚠️ Display changed, reinitializing...");
                    ReinitializeCapture();
                    sw.Restart();
                    frameNumber = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Capture error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }

            Console.WriteLine($"⏹️  Stopped: {frameNumber} frames | Unique: {_uniqueFrames} | Dup: {_duplicateFrames}");
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
            // Snapshot frame IDs
            int[] frameIdSnapshot;
            
            lock (_bufferLock)
            {
                if (_bufferCount == 0)
                {
                    Console.WriteLine("❌ No frames in buffer");
                    return string.Empty;
                }
                
                frameIdSnapshot = new int[_bufferCount];
                int readIdx = (_writeIndex - _bufferCount + _maxFrames) % _maxFrames;
                
                for (int i = 0; i < _bufferCount; i++)
                {
                    frameIdSnapshot[i] = _frameIds[readIdx];
                    readIdx = (readIdx + 1) % _maxFrames;
                }
            }

            var filename = $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filepath = Path.Combine(_savePath, filename);
            Directory.CreateDirectory(_savePath);

            Console.WriteLine($"💾 Saving {frameIdSnapshot.Length} frames...");

            _ = Task.Run(() => EncodeToMp4(frameIdSnapshot, filepath));

            return filename;
        }

        private void EncodeToMp4(int[] frameIds, string outputPath)
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
                
                lock (_bufferLock)
                {
                    for (int i = 0; i < frameIds.Length; i++)
                    {
                        int frameId = frameIds[i];
                        if (_framePool.ContainsKey(frameId))
                        {
                            stdin.Write(_framePool[frameId], 0, _bytesPerFrame);
                        }
                    }
                }

                stdin.Close();
                
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                sw.Stop();

                if (process.ExitCode == 0)
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"✓ Saved in {sw.ElapsedMilliseconds}ms → {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
                }
                else
                {
                    Console.WriteLine($"❌ Encoding failed: {errors}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Encoding error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _isRecording = false;
            _cts?.Cancel();
            _captureTask?.Wait(2000);

            lock (_bufferLock)
            {
                _framePool.Clear();
                _poolLRU.Clear();
                _activeFrameIds.Clear();
                Array.Fill(_frameIds, -1);
                _bufferCount = 0;
            }

            _duplicatedOutput?.Dispose();
            _stagingTexture?.Dispose();
            _device?.Dispose();
            _cts?.Dispose();
            
            GC.Collect(2, GCCollectionMode.Forced, true);
            
            Console.WriteLine("✓ Disposed");
        }
    }
}
