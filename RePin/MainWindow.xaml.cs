using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace ReBuffer
{
    public partial class MainWindow : Window
    {
        private ScreenRecorder? _recorder;
        private Settings _settings;
        private bool _isRecording;
        private readonly DispatcherTimer _updateTimer;
        private readonly Stopwatch _fpsTimer = new();
        private long _lastFrameCount;

        public event Action<bool>? RecordingToggled;
        public event Action? SettingsChanged;

        // Color brushes for stats
        private static readonly SolidColorBrush GreenBrush = new(System.Windows.Media.Color.FromRgb(57, 194, 15));
        private static readonly SolidColorBrush YellowBrush = new(System.Windows.Media.Color.FromRgb(255, 193, 7));
        private static readonly SolidColorBrush RedBrush = new(System.Windows.Media.Color.FromRgb(220, 53, 69));
        private static readonly SolidColorBrush GrayBrush = new(System.Windows.Media.Color.FromRgb(128, 128, 128));

        public MainWindow(ScreenRecorder? recorder, Settings settings, bool isRecording)
        {
            InitializeComponent();

            _recorder = recorder;
            _settings = settings;
            _isRecording = isRecording;

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500) // Update twice per second for smoother stats
            };
            _updateTimer.Tick += UpdateTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateUI();
            UpdatePerformanceStats();

            LogMessage("Dashboard opened");
            LogMessage($"Buffer: {_settings.BufferSeconds}s @ {_settings.FrameRate} FPS");
            LogMessage($"Quality: {_settings.Quality}");
            LogMessage($"Hotkey: {_settings.GetHotKeyName()}");
            LogMessage("");
            LogMessage($"Press {_settings.GetHotKeyName()} to save clips");
            LogMessage("Minimize to system tray to run in background");

            _fpsTimer.Start();
            _lastFrameCount = _recorder?.BufferedFrames ?? 0;
            _updateTimer.Start();
        }

        private void UpdateUI()
        {
            if (_isRecording)
            {
                StartStopButton.Content = "⏸  Pause Recording";
                StatusText.Text = "Recording Active";
            }
            else
            {
                StartStopButton.Content = "▶  Resume Recording";
                StatusText.Text = "Recording Paused";
            }

            // Update hotkey display
            HotKeyText.Text = _settings.GetHotKeyName();

            if (_recorder != null)
            {
                InfoText.Text = $"Buffer: {_settings.BufferSeconds} seconds @ {_settings.FrameRate} FPS";
            }
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_recorder != null && _isRecording)
            {
                InfoText.Text = $"Buffer: {_recorder.BufferedFrames} frames ({_recorder.BufferedSeconds:F1}s)";
                UpdatePerformanceStats();
            }
            else
            {
                // Show idle stats when paused
                FpsText.Text = "—";
                FpsText.Foreground = GrayBrush;
                CaptureRateText.Text = "—";
                CaptureRateText.Foreground = GrayBrush;
            }
        }

        private void UpdatePerformanceStats()
        {
            if (_recorder == null) return;

            // Calculate real-time FPS based on frame count change
            double elapsedSeconds = _fpsTimer.Elapsed.TotalSeconds;
            if (elapsedSeconds > 0.1) // Only update if enough time has passed
            {
                long currentFrames = _recorder.BufferedFrames;
                double fps = (currentFrames - _lastFrameCount) / elapsedSeconds;

                // Clamp to reasonable range
                fps = Math.Max(0, Math.Min(fps, _settings.FrameRate * 1.5));

                FpsText.Text = fps.ToString("F1");
                FpsText.Foreground = fps >= _settings.FrameRate * 0.95 ? GreenBrush
                    : fps >= _settings.FrameRate * 0.8 ? YellowBrush
                    : RedBrush;

                _lastFrameCount = currentFrames;
                _fpsTimer.Restart();
            }

            // Capture rate (percentage of frames successfully captured)
            double captureRate = 100.0 - _recorder.DropRate;
            CaptureRateText.Text = $"{captureRate:F0}%";
            CaptureRateText.Foreground = captureRate >= 99 ? GreenBrush
                : captureRate >= 95 ? YellowBrush
                : RedBrush;

            // Dropped frames
            long dropped = _recorder.DroppedFrames;
            DroppedText.Text = dropped.ToString();
            DroppedText.Foreground = dropped == 0 ? GrayBrush
                : dropped < 100 ? YellowBrush
                : RedBrush;

            // Buffer health (percentage of buffer filled)
            int maxBufferFrames = _settings.BufferSeconds * _settings.FrameRate;
            double bufferHealth = maxBufferFrames > 0
                ? (double)_recorder.BufferedFrames / maxBufferFrames * 100
                : 0;
            bufferHealth = Math.Min(100, bufferHealth);

            BufferHealthText.Text = $"{bufferHealth:F0}%";
            BufferHealthText.Foreground = bufferHealth >= 90 ? GreenBrush
                : bufferHealth >= 50 ? YellowBrush
                : GrayBrush;
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.Text += $"{message}\n";
                
                var parent = LogText.Parent;
                if (parent is System.Windows.Controls.ScrollViewer scrollViewer)
                {
                    scrollViewer.ScrollToEnd();
                }
            });
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            _isRecording = !_isRecording;
            UpdateUI();
            RecordingToggled?.Invoke(_isRecording);
            
            LogMessage(_isRecording ? "✓ Recording resumed" : "⏸ Recording paused");
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings);
                var result = settingsWindow.ShowDialog();

                if (result == true && settingsWindow.SettingsChanged)
                {
                    LogMessage("⚙ Settings changed - restarting recorder...");
                    SettingsChanged?.Invoke();
                    UpdateUI();
                    LogMessage("✓ Settings applied successfully");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error opening settings: {ex.Message}");
                MessageBox.Show($"Failed to open settings: {ex.Message}", 
                    "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var clipsFolder = _settings.SavePath;
            Directory.CreateDirectory(clipsFolder);
            
            Process.Start("explorer.exe", clipsFolder);
            LogMessage($"📁 Opened: {clipsFolder}");
        }

        private void GitHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/squidypal/ReBuffer-Clipping-Software",
                    UseShellExecute = true
                });
                LogMessage("🌐 Opened GitHub repository");
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Failed to open GitHub: {ex.Message}");
                MessageBox.Show($"Failed to open GitHub: {ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _updateTimer?.Stop();
            
            // Don't actually close, just hide
            e.Cancel = true;
            Hide();
            LogMessage("Dashboard minimized to system tray");
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }

            base.OnStateChanged(e);
        }
    }
}
