using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ReBuffer
{
    public class Settings
    {
        public int BufferSeconds { get; set; } = 30;
        public int FrameRate { get; set; } = 60;
        public int Bitrate { get; set; } = 8_000_000;
        public VideoQuality Quality { get; set; } = VideoQuality.High;
        public int CRF { get; set; } = 23;
        public bool UseHardwareEncoding { get; set; } = true;
        public string EncodingPreset { get; set; } = "ultrafast";
        public string SavePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clips");

        // Hotkey settings
        public int HotKeyCode { get; set; } = 0x77; // F8 by default

        // Audio settings
        public bool RecordAudio { get; set; } = true;
        public bool RecordDesktopAudio { get; set; } = true;
        public bool RecordMicrophone { get; set; } = true;
        public string? DesktopAudioDevice { get; set; } = null; // null = default
        public string? MicrophoneDevice { get; set; } = null; // null = default
        public float DesktopVolume { get; set; } = 1.0f; // 0.0 to 2.0
        public float MicrophoneVolume { get; set; } = 1.0f; // 0.0 to 2.0
        public int AudioDelayMs { get; set; } = 0; // Audio delay in milliseconds (-500 to 500)

        // Monitor settings
        public int MonitorIndex { get; set; } = 0; // 0 = primary monitor

        // Codec settings
        public VideoCodec Codec { get; set; } = VideoCodec.H264;
        public HardwareEncoder HardwareEncoderType { get; set; } = HardwareEncoder.Auto;

        // Startup settings
        public bool RunAtStartup { get; set; } = true;
        
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ReBuffer",
            "settings.json"
        );

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load settings: {ex.Message}");
            }
            
            return new Settings();
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public string GetHotKeyName()
        {
            return HotKeyCode switch
            {
                0x70 => "F1",
                0x71 => "F2",
                0x72 => "F3",
                0x73 => "F4",
                0x74 => "F5",
                0x75 => "F6",
                0x76 => "F7",
                0x77 => "F8",
                0x78 => "F9",
                0x79 => "F10",
                0x7A => "F11",
                0x7B => "F12",
                _ => $"Key{HotKeyCode:X}"
            };
        }

        public int GetBitrateForQuality()
        {
            return Quality switch
            {
                VideoQuality.Low => 2_000_000,      // 2 Mbps
                VideoQuality.Medium => 5_000_000,   // 5 Mbps
                VideoQuality.High => 8_000_000,     // 8 Mbps
                VideoQuality.VeryHigh => 12_000_000, // 12 Mbps
                VideoQuality.Ultra => 20_000_000,   // 20 Mbps
                VideoQuality.Custom => Bitrate,
                _ => 8_000_000
            };
        }

        public int GetCRFForQuality()
        {
            return Quality switch
            {
                VideoQuality.Low => 28,
                VideoQuality.Medium => 25,
                VideoQuality.High => 23,
                VideoQuality.VeryHigh => 20,
                VideoQuality.Ultra => 18,
                VideoQuality.Custom => CRF,
                _ => 23
            };
        }

        public string GetPresetForQuality()
        {
            if (UseHardwareEncoding)
            {
                return "p4"; // NVENC preset
            }

            return Quality switch
            {
                VideoQuality.Low => "veryfast",
                VideoQuality.Medium => "faster",
                VideoQuality.High => "fast",
                VideoQuality.VeryHigh => "medium",
                VideoQuality.Ultra => "slow",
                VideoQuality.Custom => EncodingPreset,
                _ => "ultrafast"
            };
        }

        /// <summary>
        /// Gets the FFmpeg encoder string based on codec and hardware encoder settings.
        /// </summary>
        public string GetEncoderString()
        {
            if (!UseHardwareEncoding)
            {
                return Codec switch
                {
                    VideoCodec.H264 => "libx264",
                    VideoCodec.H265 => "libx265",
                    VideoCodec.VP9 => "libvpx-vp9",
                    VideoCodec.AV1 => "libaom-av1",
                    _ => "libx264"
                };
            }

            // Hardware encoding
            return (Codec, HardwareEncoderType) switch
            {
                (VideoCodec.H264, HardwareEncoder.NVIDIA) => "h264_nvenc",
                (VideoCodec.H264, HardwareEncoder.AMD) => "h264_amf",
                (VideoCodec.H264, HardwareEncoder.Intel) => "h264_qsv",
                (VideoCodec.H264, HardwareEncoder.Auto) => "h264_nvenc", // Default to NVIDIA
                (VideoCodec.H265, HardwareEncoder.NVIDIA) => "hevc_nvenc",
                (VideoCodec.H265, HardwareEncoder.AMD) => "hevc_amf",
                (VideoCodec.H265, HardwareEncoder.Intel) => "hevc_qsv",
                (VideoCodec.H265, HardwareEncoder.Auto) => "hevc_nvenc",
                // VP9 and AV1 hardware encoding is limited
                (VideoCodec.VP9, _) => "libvpx-vp9",
                (VideoCodec.AV1, HardwareEncoder.NVIDIA) => "av1_nvenc",
                (VideoCodec.AV1, _) => "libaom-av1",
                _ => "h264_nvenc"
            };
        }

        /// <summary>
        /// Gets available monitors.
        /// </summary>
        public static List<MonitorInfo> GetAvailableMonitors()
        {
            var monitors = new List<MonitorInfo>();
            var allScreens = System.Windows.Forms.Screen.AllScreens;

            for (int i = 0; i < allScreens.Length; i++)
            {
                var screen = allScreens[i];
                monitors.Add(new MonitorInfo
                {
                    Index = i,
                    Name = screen.Primary ? $"Monitor {i + 1} (Primary)" : $"Monitor {i + 1}",
                    DeviceName = screen.DeviceName,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height,
                    IsPrimary = screen.Primary
                });
            }

            return monitors;
        }
    }

    public class MonitorInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }

        public override string ToString() => $"{Name} ({Width}x{Height})";
    }

    public enum VideoQuality
    {
        Low,
        Medium,
        High,
        VeryHigh,
        Ultra,
        Custom
    }

    public enum VideoCodec
    {
        H264,
        H265,
        VP9,
        AV1
    }

    public enum HardwareEncoder
    {
        Auto,
        NVIDIA,
        AMD,
        Intel
    }
}