using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ReBuffer
{
    public class AudioRecorder : IDisposable
    {
        private WasapiLoopbackCapture? _desktopCapture;
        private WaveInEvent? _micCapture;
        private WaveFileWriter? _desktopWriter;
        private WaveFileWriter? _micWriter;
        private string? _desktopFilePath;
        private string? _micFilePath;
        private bool _isRecording;
        
        private readonly string _tempFolder;
        private readonly bool _recordDesktop;
        private readonly bool _recordMic;
        private readonly float _desktopVolume;
        private readonly float _micVolume;

        public bool IsRecording => _isRecording;

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
            
            try
            {
                CleanupOldAudioFiles();
                
                if (_recordDesktop)
                {
                    StartDesktopCapture();
                }
                
                if (_recordMic)
                {
                    StartMicrophoneCapture();
                }
                
                Console.WriteLine("✓ Audio recording started");
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
                
                _desktopWriter = new WaveFileWriter(_desktopFilePath, _desktopCapture.WaveFormat);
                
                _desktopCapture.DataAvailable += (s, e) =>
                {
                    if (!_isRecording || _desktopWriter == null) return;
                    
                    try
                    {
                        if (Math.Abs(_desktopVolume - 1.0f) > 0.01f)
                        {
                            byte[] buffer = new byte[e.BytesRecorded];
                            Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                            ApplyVolume(buffer, _desktopVolume, _desktopCapture.WaveFormat);
                            _desktopWriter.Write(buffer, 0, buffer.Length);
                        }
                        else
                        {
                            _desktopWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                    }
                    catch { }
                };
                
                _desktopCapture.StartRecording();
                Console.WriteLine("✓ Desktop audio capture started");
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
                _micWriter = new WaveFileWriter(_micFilePath, _micCapture.WaveFormat);
                
                _micCapture.DataAvailable += (s, e) =>
                {
                    if (!_isRecording || _micWriter == null) return;
                    
                    try
                    {
                        if (Math.Abs(_micVolume - 1.0f) > 0.01f)
                        {
                            byte[] buffer = new byte[e.BytesRecorded];
                            Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                            ApplyVolume(buffer, _micVolume, _micCapture.WaveFormat);
                            _micWriter.Write(buffer, 0, buffer.Length);
                        }
                        else
                        {
                            _micWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                    }
                    catch { }
                };
                
                _micCapture.StartRecording();
                Console.WriteLine("✓ Microphone capture started");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Microphone failed: {ex.Message}");
                _micCapture?.Dispose();
                _micCapture = null;
            }
        }

        private void ApplyVolume(byte[] buffer, float volume, WaveFormat format)
        {
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i < buffer.Length - 1; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    sample = (short)Math.Clamp(sample * volume, short.MinValue, short.MaxValue);
                    buffer[i] = (byte)(sample & 0xFF);
                    buffer[i + 1] = (byte)((sample >> 8) & 0xFF);
                }
            }
            else if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i < buffer.Length - 3; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    sample = Math.Clamp(sample * volume, -1f, 1f);
                    byte[] sampleBytes = BitConverter.GetBytes(sample);
                    Array.Copy(sampleBytes, 0, buffer, i, 4);
                }
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
                
                _desktopWriter?.Flush();
                _desktopWriter?.Dispose();
                _micWriter?.Flush();
                _micWriter?.Dispose();
                
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