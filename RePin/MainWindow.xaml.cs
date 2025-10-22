using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace RePin
{
    public partial class MainWindow : Window
    {
        private ScreenRecorder? _recorder;
        private Settings _settings;
        private bool _isRecording;
        private readonly DispatcherTimer _updateTimer;

        public event Action<bool>? RecordingToggled;
        public event Action? SettingsChanged;

        public MainWindow(ScreenRecorder? recorder, Settings settings, bool isRecording)
        {
            InitializeComponent();
            
            _recorder = recorder;
            _settings = settings;
            _isRecording = isRecording;
            
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateUI();
            
            LogMessage("Dashboard opened");
            LogMessage($"Buffer: {_settings.BufferSeconds}s @ {_settings.FrameRate} FPS");
            LogMessage($"Quality: {_settings.Quality}");
            LogMessage("");
            LogMessage("Press F8 to save clips");
            LogMessage("Minimize to system tray to run in background");

            _updateTimer.Start();
        }

        private void UpdateUI()
        {
            if (_isRecording)
            {
                StartStopButton.Content = "⏸ Pause Recording";
                StatusText.Text = "⏺ Recording Active";
                StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
            else
            {
                StartStopButton.Content = "▶ Resume Recording";
                StatusText.Text = "⏸ Recording Paused";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            }

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
            }
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
            
            LogMessage(_isRecording ? "Recording resumed" : "Recording paused");
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings);
                var result = settingsWindow.ShowDialog();

                if (result == true && settingsWindow.SettingsChanged)
                {
                    LogMessage("Settings changed - restarting recorder...");
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
            LogMessage($"Opened: {clipsFolder}");
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
