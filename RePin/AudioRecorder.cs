using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ReBuffer.Core.Interfaces;

namespace ReBuffer
{
    public class AudioRecorder : IAudioCapture
    {
        private WasapiLoopbackCapture? _desktopCapture;
        private WaveInEvent? _micCapture;
        private WaveFileWriter? _desktopWriter;
        private WaveFileWriter? _micWriter;
        private string? _desktopFilePath;
        private string? _micFilePath;
        private bool _isRecording;

        // Buffered streams for reduced I/O overhead (4.3)
        private FileStream? _desktopFileStream;
        private FileStream? _micFileStream;
        private const int AudioBufferSize = 65536; // 64KB buffer

        private readonly string _tempFolder;
        private readonly bool _recordDesktop;
        private readonly bool _recordMic;
        private readonly float _desktopVolume;
        private readonly float _micVolume;

        // Synchronization timing
        private readonly Stopwatch _recordingTimer = new();
        private long _desktopStartTicks;
        private long _micStartTicks;

        public bool IsRecording => _isRecording;

        /// <summary>
        /// Gets the elapsed recording time.
        /// </summary>
        public TimeSpan RecordingElapsed => _recordingTimer.Elapsed;

        /// <summary>
        /// Gets the offset between desktop audio start and recording start (for sync).
        /// </summary>
        public double DesktopAudioOffsetMs => _desktopStartTicks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Gets the offset between mic audio start and recording start (for sync).
        /// </summary>
        public double MicAudioOffsetMs => _micStartTicks * 1000.0 / Stopwatch.Frequency;

        // Events for decoupled communication
        public event EventHandler<AudioStateChangedEventArgs>? StateChanged;
        public event EventHandler<AudioErrorEventArgs>? ErrorOccurred;

        public AudioRecorder(
            string tempFolder,
            bool recordDesktop = true,
            bool recordMic = true,
            float desktopVolume = 1.0f,
            float micVolume = 1.0f)
        {
            _tempFolder = tempFolder;
            _recordDesktop = recordDesktop;
            _recordMic = recordMic;
            _desktopVolume = Math.Clamp(desktopVolume, 0f, 2f);
            _micVolume = Math.Clamp(micVolume, 0f, 2f);
            
            Directory.CreateDirectory(_tempFolder);
            Console.WriteLine("✓ AudioRecorder initialized");
        }

        public async Task StartAsync()
        {
            if (_isRecording) return;

            _isRecording = true;
            _recordingTimer.Restart();

            try
            {
                CleanupOldAudioFiles();

                // Start both captures as close together as possible for sync
                if (_recordDesktop)
                {
                    StartDesktopCapture();
                }

                if (_recordMic)
                {
                    StartMicrophoneCapture();
                }

                Console.WriteLine($"✓ Audio recording started (Desktop offset: {DesktopAudioOffsetMs:F1}ms, Mic offset: {MicAudioOffsetMs:F1}ms)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Audio recording failed: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private void CleanupOldAudioFiles()
        {
            try
            {
                if (!Directory.Exists(_tempFolder)) return;
                
                var oldDesktopFiles = Directory.GetFiles(_tempFolder, "desktop_*.wav");
                foreach (var file in oldDesktopFiles)
                {
                    try { File.Delete(file); } catch { }
                }
                
                var oldMicFiles = Directory.GetFiles(_tempFolder, "mic_*.wav");
                foreach (var file in oldMicFiles)
                {
                    try { File.Delete(file); } catch { }
                }
                
                if (oldDesktopFiles.Length + oldMicFiles.Length > 0)
                {
                    Console.WriteLine($"✓ Cleaned up {oldDesktopFiles.Length + oldMicFiles.Length} old audio files");
                }
            }
            catch { }
        }

        private void StartDesktopCapture()
        {
            try
            {
                _desktopCapture = new WasapiLoopbackCapture();
                _desktopFilePath = Path.Combine(_tempFolder, $"desktop_{Guid.NewGuid():N}.wav");

                // Use buffered file stream for better I/O performance
                _desktopFileStream = new FileStream(
                    _desktopFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    AudioBufferSize,
                    FileOptions.SequentialScan);

                _desktopWriter = new WaveFileWriter(_desktopFileStream, _desktopCapture.WaveFormat);

                _desktopCapture.DataAvailable += (s, e) =>
                {
                    if (!_isRecording || _desktopWriter == null) return;

                    try
                    {
                        if (Math.Abs(_desktopVolume - 1.0f) > 0.01f)
                        {
                            // Use ArrayPool to reduce GC pressure
                            byte[] buffer = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
                            try
                            {
                                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                                ApplyVolumeOptimized(buffer, _desktopVolume, _desktopCapture.WaveFormat, e.BytesRecorded);
                                _desktopWriter.Write(buffer, 0, e.BytesRecorded);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        else
                        {
                            _desktopWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                    }
                    catch { }
                };

                _desktopStartTicks = _recordingTimer.ElapsedTicks;
                _desktopCapture.StartRecording();
                Console.WriteLine("✓ Desktop audio capture started (buffered I/O)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Desktop audio failed: {ex.Message}");
                _desktopCapture?.Dispose();
                _desktopCapture = null;
            }
        }

        private void StartMicrophoneCapture()
        {
            try
            {
                _micCapture = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(48000, 16, 1),
                    BufferMilliseconds = 50
                };

                _micFilePath = Path.Combine(_tempFolder, $"mic_{Guid.NewGuid():N}.wav");

                // Use buffered file stream for better I/O performance
                _micFileStream = new FileStream(
                    _micFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    AudioBufferSize,
                    FileOptions.SequentialScan);

                _micWriter = new WaveFileWriter(_micFileStream, _micCapture.WaveFormat);

                _micCapture.DataAvailable += (s, e) =>
                {
                    if (!_isRecording || _micWriter == null) return;

                    try
                    {
                        if (Math.Abs(_micVolume - 1.0f) > 0.01f)
                        {
                            // Use ArrayPool to reduce GC pressure
                            byte[] buffer = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
                            try
                            {
                                Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                                ApplyVolumeOptimized(buffer, _micVolume, _micCapture.WaveFormat, e.BytesRecorded);
                                _micWriter.Write(buffer, 0, e.BytesRecorded);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        else
                        {
                            _micWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                    }
                    catch { }
                };

                _micStartTicks = _recordingTimer.ElapsedTicks;
                _micCapture.StartRecording();
                Console.WriteLine("✓ Microphone capture started (buffered I/O)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Microphone failed: {ex.Message}");
                _micCapture?.Dispose();
                _micCapture = null;
            }
        }

        /// <summary>
        /// Optimized volume processing using unsafe pointers.
        /// Eliminates per-sample allocations and byte manipulation overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void ApplyVolumeOptimized(byte[] buffer, float volume, WaveFormat format, int length)
        {
            fixed (byte* ptr = buffer)
            {
                if (format.BitsPerSample == 16)
                {
                    ApplyVolume16Bit(ptr, volume, length);
                }
                else if (format.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    ApplyVolumeFloat(ptr, volume, length);
                }
            }
        }

        /// <summary>
        /// Applies volume to 16-bit PCM audio using direct pointer manipulation.
        /// ~4x faster than byte-by-byte processing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyVolume16Bit(byte* buffer, float volume, int length)
        {
            short* samples = (short*)buffer;
            int sampleCount = length / 2;

            for (int i = 0; i < sampleCount; i++)
            {
                int scaled = (int)(samples[i] * volume);
                // Clamp to short range
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                samples[i] = (short)scaled;
            }
        }

        /// <summary>
        /// Applies volume to IEEE float audio using direct pointer manipulation.
        /// Eliminates BitConverter allocations (48,000 allocations/second at 48kHz).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyVolumeFloat(byte* buffer, float volume, int length)
        {
            float* samples = (float*)buffer;
            int sampleCount = length / 4;

            for (int i = 0; i < sampleCount; i++)
            {
                float scaled = samples[i] * volume;
                // Clamp to [-1, 1] range
                if (scaled > 1f) scaled = 1f;
                else if (scaled < -1f) scaled = -1f;
                samples[i] = scaled;
            }
        }

        public void Stop()
        {
            if (!_isRecording) return;

            _isRecording = false;

            try
            {
                _desktopCapture?.StopRecording();
                _micCapture?.StopRecording();

                Thread.Sleep(100);

                // Flush and dispose writers (which also flushes underlying streams)
                _desktopWriter?.Flush();
                _desktopWriter?.Dispose();
                _desktopWriter = null;

                _micWriter?.Flush();
                _micWriter?.Dispose();
                _micWriter = null;

                // Dispose file streams (WaveFileWriter should handle this, but be explicit)
                _desktopFileStream?.Dispose();
                _desktopFileStream = null;

                _micFileStream?.Dispose();
                _micFileStream = null;

                Console.WriteLine("✓ Audio stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Audio stop error: {ex.Message}");
            }
        }

        public string? GetDesktopAudioPath() => _desktopFilePath;
        public string? GetMicAudioPath() => _micFilePath;

        public void Dispose()
        {
            Stop();
            _desktopCapture?.Dispose();
            _micCapture?.Dispose();
            Console.WriteLine("✓ AudioRecorder disposed");
        }

        public static string[] GetDesktopAudioDevices()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                return new[] { "Default|" }.Concat(
                    devices.Select(d => $"{d.FriendlyName}|{d.ID}")
                ).ToArray();
            }
            catch
            {
                return new[] { "Default|" };
            }
        }

        public static string[] GetMicrophoneDevices()
        {
            try
            {
                var devices = new string[WaveInEvent.DeviceCount + 1];
                devices[0] = "Default|";
                
                for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                {
                    var caps = WaveInEvent.GetCapabilities(i);
                    devices[i + 1] = $"{caps.ProductName}|{caps.ProductName}";
                }
                
                return devices;
            }
            catch
            {
                return new[] { "Default|" };
            }
        }
    }
}