using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RePin
{
    public partial class MainWindow : Window
    {
        private ScreenRecorder? _recorder;
        private GlobalHotKeyManager? _hotKeyManager;
        private bool _isRecording = true;
        private readonly DispatcherTimer _updateTimer;

        public MainWindow()
        {
            InitializeComponent();
            
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LogMessage("Initializing RePin...");

                // Initialize recorder with hardware acceleration
                _recorder = new ScreenRecorder(
                    bufferSeconds: 30,
                    fps: 60,
                    bitrate: 8_000_000
                );

                await _recorder.StartAsync();
                LogMessage("✓ Screen capture started (Hardware accelerated)");
                LogMessage($"✓ Resolution: {_recorder.Width}x{_recorder.Height} @ 60 FPS");
                LogMessage($"✓ Buffer size: ~{_recorder.EstimateMemoryMB():F1} MB");

                // Setup F8 hotkey
                _hotKeyManager = new GlobalHotKeyManager();
                _hotKeyManager.RegisterF8Callback(OnF8Pressed);
                LogMessage("✓ F8 hotkey registered");
                LogMessage("");
                LogMessage("Ready! Press F8 to save the last 30 seconds.");

                _updateTimer.Start();
            }
            catch (Exception ex)
            {
                LogMessage($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_recorder != null && _isRecording)
            {
                InfoText.Text = $"Buffer: {_recorder.BufferedFrames} frames ({_recorder.BufferedSeconds:F1}s)";
            }
        }

        private async void OnF8Pressed()
        {
            if (_recorder == null || !_isRecording) return;

            await Dispatcher.InvokeAsync(async () =>
            {
                var sw = Stopwatch.StartNew();
                LogMessage($"[{DateTime.Now:HH:mm:ss}] F8 pressed - Saving clip...");

                try
                {
                    var filename = await _recorder.SaveClipAsync();
                    sw.Stop();

                    if (!string.IsNullOrEmpty(filename))
                    {
                        LogMessage($"✓ Saved: {filename} ({sw.ElapsedMilliseconds}ms)");
                        
                        // Flash the window
                        FlashWindow();
                    }
                    else
                    {
                        LogMessage("⚠ No frames in buffer to save");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"✗ Error saving clip: {ex.Message}");
                }
            });
        }

        private async void FlashWindow()
        {
            var originalBg = Background;
            Background = System.Windows.Media.Brushes.Green;
            await Task.Delay(100);
            Background = originalBg;
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogText.Text += $"{message}\n";
                
                // Auto-scroll to bottom
                var parent = LogText.Parent;
                if (parent is System.Windows.Controls.ScrollViewer scrollViewer)
                {
                    scrollViewer.ScrollToEnd();
                }
            });
        }

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recorder == null) return;

            _isRecording = !_isRecording;

            if (_isRecording)
            {
                await _recorder.StartAsync();
                StartStopButton.Content = "⏸ Pause Recording";
                StatusText.Text = "⏺ Recording Active";
                StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
                LogMessage("Recording resumed");
            }
            else
            {
                _recorder.Pause();
                StartStopButton.Content = "▶ Resume Recording";
                StatusText.Text = "⏸ Recording Paused";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                LogMessage("Recording paused");
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var clipsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clips");
            Directory.CreateDirectory(clipsFolder);
            
            Process.Start("explorer.exe", clipsFolder);
            LogMessage($"Opened: {clipsFolder}");
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _updateTimer?.Stop();
            _hotKeyManager?.Dispose();
            _recorder?.Dispose();
        }
    }
}
