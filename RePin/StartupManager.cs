using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ReBuffer
{
    public static class StartupManager
    {
        private const string AppName = "ReBuffer";
        private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
                var value = key?.GetValue(AppName) as string;
                
                if (string.IsNullOrEmpty(value))
                    return false;

                // Check if the path still points to the current executable
                var currentPath = GetExecutablePath();
                return value.Equals($"\"{currentPath}\"", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to check startup status: {ex.Message}");
                return false;
            }
        }

        public static bool SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
                
                if (key == null)
                {
                    Console.WriteLine("Failed to open registry key");
                    return false;
                }

                if (enable)
                {
                    var exePath = GetExecutablePath();
                    key.SetValue(AppName, $"\"{exePath}\"", RegistryValueKind.String);
                    Console.WriteLine($"✓ Startup enabled: {exePath}");
                    return true;
                }
                else
                {
                    key.DeleteValue(AppName, false);
                    Console.WriteLine("✓ Startup disabled");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set startup: {ex.Message}");
                return false;
            }
        }

        private static string GetExecutablePath()
        {
            // Get the executable path
            var assembly = Assembly.GetExecutingAssembly();
            return assembly.Location.Replace(".dll", ".exe");
        }
    }
}
