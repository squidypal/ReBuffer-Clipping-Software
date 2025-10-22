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
        private Settings _settings;

        public MainWindow()
        {
            InitializeComponent();
            
            // Load settings
            _settings = Settings.Load();
            
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

                // Initialize recorder with settings
                await InitializeRecorder();

                // Setup F8 hotkey
                _hotKeyManager = new GlobalHotKeyManager();
                _hotKeyManager.RegisterF8Callback(OnF8Pressed);
                LogMessage("✓ F8 hotkey registered");
                LogMessage("");
                LogMessage($"Ready! Press F8 to save the last {_settings.BufferSeconds} seconds.");

                _updateTimer.Start();
            }
            catch (Exception ex)
            {
                LogMessage($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async System.Threading.Tasks.Task InitializeRecorder()
        {
            // Dispose old recorder if exists
            if (_recorder != null)
            {
                _recorder.Dispose();
                _recorder = null;
            }

            _recorder = new ScreenRecorder(
                bufferSeconds: _settings.BufferSeconds,
                fps: _settings.FrameRate,
                bitrate: _settings.GetBitrateForQuality(),
                crf: _settings.GetCRFForQuality(),
                preset: _settings.GetPresetForQuality(),
                useHardwareEncoding: _settings.UseHardwareEncoding,
                savePath: _settings.SavePath
            );

            await _recorder.StartAsync();
            
            var encodingType = _settings.UseHardwareEncoding ? "Hardware accelerated" : "Software encoding";
            LogMessage($"✓ Screen capture started ({encodingType})");
            LogMessage($"✓ Resolution: {_recorder.Width}x{_recorder.Height} @ {_settings.FrameRate} FPS");
            LogMessage($"✓ Buffer size: ~{_recorder.EstimateMemoryMB():F1} MB");
            LogMessage($"✓ Quality: {_settings.Quality} ({_settings.GetBitrateForQuality() / 1_000_000} Mbps)");
            
            // Update status text
            InfoText.Text = $"Buffer: {_settings.BufferSeconds} seconds @ {_settings.FrameRate} FPS";
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
            await System.Threading.Tasks.Task.Delay(100);
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

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings);
                var result = settingsWindow.ShowDialog();

                if (result == true && settingsWindow.SettingsChanged)
                {
                    LogMessage("Settings changed - restarting recorder...");
                    
                    var wasRecording = _isRecording;
                    
                    // Stop recording temporarily
                    if (_isRecording)
                    {
                        _recorder?.Pause();
                        _isRecording = false;
                    }

                    // Reinitialize with new settings
                    await InitializeRecorder();

                    // Resume if it was recording before
                    if (wasRecording)
                    {
                        await _recorder!.StartAsync();
                        _isRecording = true;
                        StartStopButton.Content = "⏸ Pause Recording";
                        StatusText.Text = "⏺ Recording Active";
                        StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
                    }

                    LogMessage("✓ Settings applied successfully");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"✗ Error opening settings: {ex.Message}");
                MessageBox.Show($"Failed to open settings: {ex.Message}\n\nStack trace:\n{ex.StackTrace}", 
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
            _hotKeyManager?.Dispose();
            _recorder?.Dispose();
        }
    }
}
