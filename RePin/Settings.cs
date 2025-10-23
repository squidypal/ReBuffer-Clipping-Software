using System;
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
}
