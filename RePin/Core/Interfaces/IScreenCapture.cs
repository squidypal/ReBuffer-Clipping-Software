using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuffer.Core.Interfaces
{
    /// <summary>
    /// Interface for screen capture functionality.
    /// Enables dependency injection and testability.
    /// </summary>
    public interface IScreenCapture : IDisposable
    {
        /// <summary>
        /// Gets the width of the captured screen in pixels.
        /// </summary>
        int Width { get; }

        /// <summary>
        /// Gets the height of the captured screen in pixels.
        /// </summary>
        int Height { get; }

        /// <summary>
        /// Gets the number of frames currently buffered.
        /// </summary>
        int BufferedFrames { get; }

        /// <summary>
        /// Gets the buffered time in seconds.
        /// </summary>
        double BufferedSeconds { get; }

        /// <summary>
        /// Gets whether the recorder is currently recording.
        /// </summary>
        bool IsRecording { get; }

        /// <summary>
        /// Gets the total number of dropped frames.
        /// </summary>
        long DroppedFrames { get; }

        /// <summary>
        /// Gets the frame drop rate as a percentage.
        /// </summary>
        double DropRate { get; }

        /// <summary>
        /// Starts the screen capture asynchronously.
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// Pauses the screen capture.
        /// </summary>
        void Pause();

        /// <summary>
        /// Saves the current buffer to a clip file.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>The filename of the saved clip, or empty string on failure.</returns>
        Task<string> SaveClipAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Event raised when recording state changes.
        /// </summary>
        event EventHandler<RecordingStateChangedEventArgs>? RecordingStateChanged;

        /// <summary>
        /// Event raised when a clip is saved.
        /// </summary>
        event EventHandler<ClipSavedEventArgs>? ClipSaved;

        /// <summary>
        /// Event raised when an error occurs.
        /// </summary>
        event EventHandler<RecordingErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Event raised periodically with performance statistics.
        /// </summary>
        event EventHandler<PerformanceStatsEventArgs>? PerformanceUpdated;
    }

    /// <summary>
    /// Event args for recording state changes.
    /// </summary>
    public class RecordingStateChangedEventArgs : EventArgs
    {
        public bool IsRecording { get; init; }
        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// Event args for clip saved events.
    /// </summary>
    public class ClipSavedEventArgs : EventArgs
    {
        public string Filename { get; init; } = "";
        public string FullPath { get; init; } = "";
        public long FileSizeBytes { get; init; }
        public TimeSpan Duration { get; init; }
        public TimeSpan SaveDuration { get; init; }
    }

    /// <summary>
    /// Event args for recording errors.
    /// </summary>
    public class RecordingErrorEventArgs : EventArgs
    {
        public string Message { get; init; } = "";
        public Exception? Exception { get; init; }
        public bool IsFatal { get; init; }
    }

    /// <summary>
    /// Event args for performance statistics.
    /// </summary>
    public class PerformanceStatsEventArgs : EventArgs
    {
        public double ActualFps { get; init; }
        public double TargetFps { get; init; }
        public double CaptureRate { get; init; }
        public long TotalFrames { get; init; }
        public long DroppedFrames { get; init; }
        public int BufferedFrames { get; init; }
        public double BufferedSeconds { get; init; }
    }
}
