using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuffer.Core.Logging
{
    /// <summary>
    /// Simple structured logging infrastructure for ReBuffer.
    /// Thread-safe with async file writing support.
    /// </summary>
    public sealed class Logger : IDisposable
    {
        private static Logger? _instance;
        private static readonly object _lock = new();

        private readonly ConcurrentQueue<LogEntry> _logQueue = new();
        private readonly string? _logFilePath;
        private readonly LogLevel _minimumLevel;
        private readonly bool _writeToConsole;
        private readonly bool _writeToFile;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task? _writerTask;
        private bool _disposed;

        /// <summary>
        /// Gets the singleton logger instance.
        /// </summary>
        public static Logger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new Logger();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Configures the global logger instance.
        /// </summary>
        public static void Configure(LogLevel minimumLevel = LogLevel.Info,
                                      bool writeToConsole = true,
                                      bool writeToFile = true,
                                      string? logDirectory = null)
        {
            lock (_lock)
            {
                _instance?.Dispose();
                _instance = new Logger(minimumLevel, writeToConsole, writeToFile, logDirectory);
            }
        }

        private Logger(LogLevel minimumLevel = LogLevel.Info,
                       bool writeToConsole = true,
                       bool writeToFile = true,
                       string? logDirectory = null)
        {
            _minimumLevel = minimumLevel;
            _writeToConsole = writeToConsole;
            _writeToFile = writeToFile;

            if (_writeToFile)
            {
                var logDir = logDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ReBuffer", "logs");

                Directory.CreateDirectory(logDir);
                _logFilePath = Path.Combine(logDir, $"rebuffer_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                // Start background writer task
                _writerTask = Task.Run(WriteLogsAsync);

                // Clean up old logs (keep last 10)
                CleanupOldLogs(logDir, 10);
            }
        }

        /// <summary>
        /// Logs a message at the specified level.
        /// </summary>
        public void Log(LogLevel level, string message, string? source = null, Exception? exception = null)
        {
            if (level < _minimumLevel || _disposed) return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message,
                Source = source ?? "ReBuffer",
                Exception = exception
            };

            _logQueue.Enqueue(entry);

            if (_writeToConsole)
            {
                WriteToConsole(entry);
            }
        }

        public void Debug(string message, string? source = null) => Log(LogLevel.Debug, message, source);
        public void Info(string message, string? source = null) => Log(LogLevel.Info, message, source);
        public void Warning(string message, string? source = null) => Log(LogLevel.Warning, message, source);
        public void Error(string message, string? source = null, Exception? ex = null) => Log(LogLevel.Error, message, source, ex);
        public void Fatal(string message, string? source = null, Exception? ex = null) => Log(LogLevel.Fatal, message, source, ex);

        private void WriteToConsole(LogEntry entry)
        {
            var prefix = entry.Level switch
            {
                LogLevel.Debug => "🔍",
                LogLevel.Info => "ℹ️",
                LogLevel.Warning => "⚠",
                LogLevel.Error => "❌",
                LogLevel.Fatal => "💀",
                _ => "  "
            };

            var color = entry.Level switch
            {
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Fatal => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };

            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{prefix} [{entry.Timestamp:HH:mm:ss}] [{entry.Source}] {entry.Message}");

            if (entry.Exception != null)
            {
                Console.WriteLine($"   Exception: {entry.Exception.Message}");
            }

            Console.ForegroundColor = originalColor;
        }

        private async Task WriteLogsAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_logQueue.TryDequeue(out var entry) && _logFilePath != null)
                    {
                        var line = FormatLogEntry(entry);
                        await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, _cts.Token);
                    }
                    else
                    {
                        await Task.Delay(100, _cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore logging errors
                }
            }

            // Flush remaining entries
            while (_logQueue.TryDequeue(out var entry) && _logFilePath != null)
            {
                try
                {
                    var line = FormatLogEntry(entry);
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
                catch { }
            }
        }

        private static string FormatLogEntry(LogEntry entry)
        {
            var sb = new StringBuilder();
            sb.Append($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] ");
            sb.Append($"[{entry.Level,-7}] ");
            sb.Append($"[{entry.Source}] ");
            sb.Append(entry.Message);

            if (entry.Exception != null)
            {
                sb.AppendLine();
                sb.Append($"  Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}");
                if (entry.Exception.StackTrace != null)
                {
                    sb.AppendLine();
                    sb.Append($"  StackTrace: {entry.Exception.StackTrace}");
                }
            }

            return sb.ToString();
        }

        private static void CleanupOldLogs(string logDirectory, int keepCount)
        {
            try
            {
                var logFiles = Directory.GetFiles(logDirectory, "rebuffer_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(keepCount)
                    .ToList();

                foreach (var file in logFiles)
                {
                    try { file.Delete(); } catch { }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _writerTask?.Wait(2000);
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Log entry structure.
    /// </summary>
    public struct LogEntry
    {
        public DateTime Timestamp { get; init; }
        public LogLevel Level { get; init; }
        public string Message { get; init; }
        public string Source { get; init; }
        public Exception? Exception { get; init; }
    }

    /// <summary>
    /// Log severity levels.
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Fatal = 4
    }
}
