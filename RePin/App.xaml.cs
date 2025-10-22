using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace RePin
{
    public partial class App : Application
    {
        private NotifyIcon? _notifyIcon;
        private ScreenRecorder? _recorder;
        private GlobalHotKeyManager? _hotKeyManager;
        private MainWindow? _mainWindow;
        private Settings _settings;
        private bool _isRecording = true;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Load settings
            _settings = Settings.Load();

            // Create system tray icon
            _notifyIcon = new NotifyIcon
            {
                Icon = CreateIcon(),
                Visible = true,
                Text = "RePin - Recording Active"
            };

            // Context menu
            var contextMenu = new ContextMenuStrip();
            
            contextMenu.Items.Add("Open Dashboard", null, (s, ev) => ShowMainWindow());
            contextMenu.Items.Add(new ToolStripSeparator());
            
            var pauseItem = new ToolStripMenuItem("⏸ Pause Recording");
            pauseItem.Click += (s, ev) => ToggleRecording();
            contextMenu.Items.Add(pauseItem);
            
            contextMenu.Items.Add("⚙️ Settings", null, (s, ev) => ShowSettings());
            contextMenu.Items.Add("📁 Open Clips Folder", null, (s, ev) => OpenClipsFolder());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, (s, ev) => ExitApplication());
            
            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, ev) => ShowMainWindow();

            // Initialize recorder
            InitializeRecorder();

            // Setup F8 hotkey
            _hotKeyManager = new GlobalHotKeyManager();
            _hotKeyManager.RegisterF8Callback(OnF8Pressed);

            LogToTray("RePin started - Press F8 to save clip");
        }

        private async void InitializeRecorder()
        {
            try
            {
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
                Console.WriteLine($"✓ RePin recording started");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start recording: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleRecording()
        {
            if (_recorder == null) return;

            _isRecording = !_isRecording;

            if (_isRecording)
            {
                _recorder.StartAsync();
                _notifyIcon!.Text = "RePin - Recording Active";
                _notifyIcon.Icon = CreateIcon();
                LogToTray("Recording resumed");
            }
            else
            {
                _recorder.Pause();
                _notifyIcon!.Text = "RePin - Paused";
                _notifyIcon.Icon = CreatePausedIcon();
                LogToTray("Recording paused");
            }
        }

        private async void OnF8Pressed()
        {
            if (_recorder == null || !_isRecording) return;

            try
            {
                var filename = await _recorder.SaveClipAsync();
                
                if (!string.IsNullOrEmpty(filename))
                {
                    // Show notification balloon
                    _notifyIcon?.ShowBalloonTip(
                        2000,
                        "Clip Saved!",
                        $"Saved: {filename}",
                        ToolTipIcon.Info
                    );
                    
                    Console.WriteLine($"✓ Saved: {filename}");
                }
            }
            catch (Exception ex)
            {
                _notifyIcon?.ShowBalloonTip(
                    3000,
                    "Save Failed",
                    ex.Message,
                    ToolTipIcon.Error
                );
            }
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null || !_mainWindow.IsLoaded)
            {
                _mainWindow = new MainWindow(_recorder, _settings, _isRecording);
                _mainWindow.RecordingToggled += (isRecording) => 
                {
                    _isRecording = isRecording;
                    if (_recorder != null)
                    {
                        if (isRecording)
                            _recorder.StartAsync();
                        else
                            _recorder.Pause();
                    }
                };
                _mainWindow.SettingsChanged += async () =>
                {
                    await ReinitializeRecorder();
                };
                _mainWindow.Closed += (s, e) => _mainWindow = null;
            }

            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        private void ShowSettings()
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings);
                var result = settingsWindow.ShowDialog();

                if (result == true && settingsWindow.SettingsChanged)
                {
                    ReinitializeRecorder();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async System.Threading.Tasks.Task ReinitializeRecorder()
        {
            var wasRecording = _isRecording;
            
            if (_recorder != null)
            {
                _recorder.Pause();
                _recorder.Dispose();
            }

            InitializeRecorder();

            if (wasRecording && _recorder != null)
            {
                await _recorder.StartAsync();
            }

            LogToTray("Settings applied");
        }

        private void OpenClipsFolder()
        {
            var clipsFolder = _settings.SavePath;
            System.IO.Directory.CreateDirectory(clipsFolder);
            System.Diagnostics.Process.Start("explorer.exe", clipsFolder);
        }

        private void ExitApplication()
        {
            _recorder?.Dispose();
            _hotKeyManager?.Dispose();
            _notifyIcon?.Dispose();
            Shutdown();
        }

        private void LogToTray(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private Icon CreateIcon()
        {
            // Create a simple red dot icon
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.FillEllipse(Brushes.Red, 2, 2, 12, 12);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }

        private Icon CreatePausedIcon()
        {
            // Create a simple gray icon
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.FillEllipse(Brushes.Gray, 2, 2, 12, 12);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }
}
