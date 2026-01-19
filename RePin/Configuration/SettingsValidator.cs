using System;
using System.Collections.Generic;
using System.IO;

namespace ReBuffer.Configuration
{
    /// <summary>
    /// Validates settings and provides detailed error messages.
    /// </summary>
    public static class SettingsValidator
    {
        /// <summary>
        /// Validates the settings and returns any validation errors.
        /// </summary>
        /// <param name="settings">The settings to validate.</param>
        /// <returns>A list of validation errors. Empty if valid.</returns>
        public static List<ValidationError> Validate(Settings settings)
        {
            var errors = new List<ValidationError>();

            // Buffer settings
            if (settings.BufferSeconds < 5)
            {
                errors.Add(new ValidationError(
                    nameof(settings.BufferSeconds),
                    "Buffer must be at least 5 seconds.",
                    ValidationSeverity.Error));
            }
            else if (settings.BufferSeconds > 300)
            {
                errors.Add(new ValidationError(
                    nameof(settings.BufferSeconds),
                    "Buffer over 5 minutes may cause high memory usage.",
                    ValidationSeverity.Warning));
            }

            // Frame rate
            if (settings.FrameRate < 15)
            {
                errors.Add(new ValidationError(
                    nameof(settings.FrameRate),
                    "Frame rate must be at least 15 FPS.",
                    ValidationSeverity.Error));
            }
            else if (settings.FrameRate > 144)
            {
                errors.Add(new ValidationError(
                    nameof(settings.FrameRate),
                    "Frame rates above 144 FPS may cause performance issues.",
                    ValidationSeverity.Warning));
            }

            // Bitrate
            if (settings.Bitrate < 500_000)
            {
                errors.Add(new ValidationError(
                    nameof(settings.Bitrate),
                    "Bitrate must be at least 500 Kbps.",
                    ValidationSeverity.Error));
            }
            else if (settings.Bitrate > 50_000_000)
            {
                errors.Add(new ValidationError(
                    nameof(settings.Bitrate),
                    "Bitrate over 50 Mbps may cause disk I/O issues.",
                    ValidationSeverity.Warning));
            }

            // CRF (Constant Rate Factor)
            if (settings.CRF < 0 || settings.CRF > 51)
            {
                errors.Add(new ValidationError(
                    nameof(settings.CRF),
                    "CRF must be between 0 and 51.",
                    ValidationSeverity.Error));
            }

            // Save path
            if (string.IsNullOrWhiteSpace(settings.SavePath))
            {
                errors.Add(new ValidationError(
                    nameof(settings.SavePath),
                    "Save path cannot be empty.",
                    ValidationSeverity.Error));
            }
            else
            {
                try
                {
                    var fullPath = Path.GetFullPath(settings.SavePath);
                    var directory = Path.GetDirectoryName(fullPath);

                    // Check if we can write to the directory
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        // Try to get directory info to check permissions
                        var dirInfo = new DirectoryInfo(directory);
                        if (dirInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
                        {
                            errors.Add(new ValidationError(
                                nameof(settings.SavePath),
                                "Save directory is read-only.",
                                ValidationSeverity.Error));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new ValidationError(
                        nameof(settings.SavePath),
                        $"Invalid save path: {ex.Message}",
                        ValidationSeverity.Error));
                }
            }

            // Volume settings
            if (settings.DesktopVolume < 0 || settings.DesktopVolume > 2)
            {
                errors.Add(new ValidationError(
                    nameof(settings.DesktopVolume),
                    "Desktop volume must be between 0 and 2 (200%).",
                    ValidationSeverity.Error));
            }

            if (settings.MicrophoneVolume < 0 || settings.MicrophoneVolume > 2)
            {
                errors.Add(new ValidationError(
                    nameof(settings.MicrophoneVolume),
                    "Microphone volume must be between 0 and 2 (200%).",
                    ValidationSeverity.Error));
            }

            // Audio delay
            if (settings.AudioDelayMs < -1000 || settings.AudioDelayMs > 1000)
            {
                errors.Add(new ValidationError(
                    nameof(settings.AudioDelayMs),
                    "Audio delay must be between -1000ms and 1000ms.",
                    ValidationSeverity.Error));
            }

            // Monitor index
            var monitors = Settings.GetAvailableMonitors();
            if (settings.MonitorIndex < 0 || settings.MonitorIndex >= monitors.Count)
            {
                errors.Add(new ValidationError(
                    nameof(settings.MonitorIndex),
                    $"Invalid monitor index. Available monitors: 0-{monitors.Count - 1}.",
                    ValidationSeverity.Warning)); // Warning since we fall back to primary
            }

            // Hotkey
            if (settings.HotKeyCode < 0x70 || settings.HotKeyCode > 0x7B)
            {
                errors.Add(new ValidationError(
                    nameof(settings.HotKeyCode),
                    "Hotkey must be a function key (F1-F12).",
                    ValidationSeverity.Warning));
            }

            return errors;
        }

        /// <summary>
        /// Validates settings and throws if any errors are found.
        /// </summary>
        /// <param name="settings">The settings to validate.</param>
        /// <exception cref="SettingsValidationException">Thrown if validation fails.</exception>
        public static void ValidateOrThrow(Settings settings)
        {
            var errors = Validate(settings);
            var fatalErrors = errors.FindAll(e => e.Severity == ValidationSeverity.Error);

            if (fatalErrors.Count > 0)
            {
                throw new SettingsValidationException(fatalErrors);
            }
        }

        /// <summary>
        /// Checks if settings are valid (no errors, warnings are OK).
        /// </summary>
        public static bool IsValid(Settings settings)
        {
            var errors = Validate(settings);
            return !errors.Exists(e => e.Severity == ValidationSeverity.Error);
        }
    }

    /// <summary>
    /// Represents a validation error.
    /// </summary>
    public class ValidationError
    {
        public string PropertyName { get; }
        public string Message { get; }
        public ValidationSeverity Severity { get; }

        public ValidationError(string propertyName, string message, ValidationSeverity severity)
        {
            PropertyName = propertyName;
            Message = message;
            Severity = severity;
        }

        public override string ToString() => $"[{Severity}] {PropertyName}: {Message}";
    }

    /// <summary>
    /// Severity level for validation errors.
    /// </summary>
    public enum ValidationSeverity
    {
        /// <summary>Informational message.</summary>
        Info,
        /// <summary>Warning - settings will work but may cause issues.</summary>
        Warning,
        /// <summary>Error - settings are invalid and must be fixed.</summary>
        Error
    }

    /// <summary>
    /// Exception thrown when settings validation fails.
    /// </summary>
    public class SettingsValidationException : Exception
    {
        public List<ValidationError> Errors { get; }

        public SettingsValidationException(List<ValidationError> errors)
            : base($"Settings validation failed with {errors.Count} error(s): {string.Join("; ", errors)}")
        {
            Errors = errors;
        }
    }
}
