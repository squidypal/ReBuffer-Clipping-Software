using System;
using System.Threading.Tasks;

namespace ReBuffer.Core.Interfaces
{
    /// <summary>
    /// Interface for audio capture functionality.
    /// Enables dependency injection and testability.
    /// </summary>
    public interface IAudioCapture : IDisposable
    {
        /// <summary>
        /// Gets whether the recorder is currently recording.
        /// </summary>
        bool IsRecording { get; }

        /// <summary>
        /// Gets the elapsed recording time.
        /// </summary>
        TimeSpan RecordingElapsed { get; }

        /// <summary>
        /// Gets the desktop audio offset in milliseconds (for sync).
        /// </summary>
        double DesktopAudioOffsetMs { get; }

        /// <summary>
        /// Gets the microphone audio offset in milliseconds (for sync).
        /// </summary>
        double MicAudioOffsetMs { get; }

        /// <summary>
        /// Starts the audio capture asynchronously.
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// Stops the audio capture.
        /// </summary>
        void Stop();

        /// <summary>
        /// Gets the path to the desktop audio file.
        /// </summary>
        string? GetDesktopAudioPath();

        /// <summary>
        /// Gets the path to the microphone audio file.
        /// </summary>
        string? GetMicAudioPath();

        /// <summary>
        /// Event raised when audio capture state changes.
        /// </summary>
        event EventHandler<AudioStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// Event raised when an audio error occurs.
        /// </summary>
        event EventHandler<AudioErrorEventArgs>? ErrorOccurred;
    }

    /// <summary>
    /// Event args for audio state changes.
    /// </summary>
    public class AudioStateChangedEventArgs : EventArgs
    {
        public bool IsRecording { get; init; }
        public bool DesktopActive { get; init; }
        public bool MicrophoneActive { get; init; }
    }

    /// <summary>
    /// Event args for audio errors.
    /// </summary>
    public class AudioErrorEventArgs : EventArgs
    {
        public string Source { get; init; } = ""; // "Desktop" or "Microphone"
        public string Message { get; init; } = "";
        public Exception? Exception { get; init; }
    }
}
