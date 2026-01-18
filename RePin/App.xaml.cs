using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using NAudio.Wave;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ReBuffer
{
    public partial class App : Application
    {
        private NotifyIcon? _notifyIcon;
        private ScreenRecorder? _recorder;
        private GlobalHotKeyManager? _hotKeyManager;
        private MainWindow? _mainWindow;
        private Settings _settings;
        private bool _isRecording = true;
        private Mutex? _instanceMutex;
        private const string MutexName = "ReBuffer_SingleInstance_Mutex_9F8A2E1D";

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Single instance check
            bool createdNew;
            _instanceMutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show("ReBuffer is already running!", "Already Running", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Load settings
            _settings = Settings.Load();

            // Check FFmpeg availability
            if (!ScreenRecorder.IsFfmpegAvailable())
            {
                var result = MessageBox.Show(
                    "FFmpeg was not found in your system PATH.\n\n" +
                    "ReBuffer requires FFmpeg to record and save clips.\n\n" +
                    "Please install FFmpeg and add it to your PATH, then restart ReBuffer.\n\n" +
                    "Would you like to open the FFmpeg download page?",
                    "FFmpeg Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error);

                if (result == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://ffmpeg.org/download.html",
                        UseShellExecute = true
                    });
                }

                _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
                Shutdown();
                return;
            }

            var ffmpegVersion = ScreenRecorder.GetFfmpegVersion();
            Console.WriteLine($"✓ FFmpeg found: {ffmpegVersion}");

            // Create system tray icon with custom icon
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Visible = true,
                Text = "ReBuffer - Recording Active"
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

            // Setup hotkey
            _hotKeyManager = new GlobalHotKeyManager();
            _hotKeyManager.RegisterHotKey(_settings.HotKeyCode, OnHotKeyPressed);

            string audioStatus = _settings.RecordAudio 
                ? $"(Audio: {GetAudioStatusText()})" 
                : "(Audio: Off)";
            LogToTray($"ReBuffer started {audioStatus} - Press {_settings.GetHotKeyName()} to save clip");
        }

        private Icon LoadAppIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images", "icon.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load custom icon: {ex.Message}");
            }

            // Fallback to simple red dot icon
            return CreateDefaultIcon();
        }

        private Icon CreateDefaultIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.FillEllipse(Brushes.Red, 2, 2, 12, 12);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }

        private string GetAudioStatusText()
        {
            if (!_settings.RecordAudio) return "Off";
            
            var sources = new System.Collections.Generic.List<string>();
            if (_settings.RecordDesktopAudio) sources.Add("Desktop");
            if (_settings.RecordMicrophone) sources.Add("Mic");
            
            return sources.Count > 0 ? string.Join(" + ", sources) : "None";
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
                    savePath: _settings.SavePath,
                    recordAudio: _settings.RecordAudio,
                    desktopAudioDevice: _settings.DesktopAudioDevice,
                    microphoneDevice: _settings.MicrophoneDevice,
                    desktopVolume: _settings.DesktopVolume,
                    micVolume: _settings.MicrophoneVolume,
                    recordDesktop: _settings.RecordDesktopAudio,
                    recordMic: _settings.RecordMicrophone
                );

                await _recorder.StartAsync();
                Console.WriteLine($"✓ ReBuffer recording started");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start recording: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ToggleRecording()
        {
            if (_recorder == null) return;

            _isRecording = !_isRecording;

            if (_isRecording)
            {
                await _recorder.StartAsync();
                _notifyIcon!.Text = "ReBuffer - Recording Active";
                _notifyIcon.Icon = LoadAppIcon();
                LogToTray("Recording resumed");
            }
            else
            {
                _recorder.Pause();
                _notifyIcon!.Text = "ReBuffer - Paused";
                _notifyIcon.Icon = CreatePausedIcon();
                LogToTray("Recording paused");
            }
        }

        private async void OnHotKeyPressed()
        {
            if (_recorder == null || !_isRecording) return;

            try
            {
                var filename = await _recorder.SaveClipAsync();
                
                if (!string.IsNullOrEmpty(filename))
                {
                    // Play clip sound
                    PlayClipSound();
                    
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

        private void PlayClipSound()
        {
            try
            {
                var soundPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clipSFX", "clip.mp3");
                if (File.Exists(soundPath))
                {
                    // Play sound in background thread
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using var audioFile = new AudioFileReader(soundPath);
                            using var outputDevice = new WaveOutEvent();
                            outputDevice.Init(audioFile);
                            outputDevice.Play();
                            while (outputDevice.PlaybackState == PlaybackState.Playing)
                            {
                                Thread.Sleep(100);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to play clip sound: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load clip sound: {ex.Message}");
            }
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null || !_mainWindow.IsLoaded)
            {
                _mainWindow = new MainWindow(_recorder, _settings, _isRecording);
                _mainWindow.RecordingToggled += async (isRecording) =>
                {
                    _isRecording = isRecording;
                    if (_recorder != null)
                    {
                        if (isRecording)
                            await _recorder.StartAsync();
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

        private async void ShowSettings()
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings);
                var result = settingsWindow.ShowDialog();

                if (result == true && settingsWindow.SettingsChanged)
                {
                    await ReinitializeRecorder();
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

            // Re-register hotkey with new key code
            if (_hotKeyManager != null)
            {
                _hotKeyManager.RegisterHotKey(_settings.HotKeyCode, OnHotKeyPressed);
            }

            InitializeRecorder();

            if (wasRecording && _recorder != null)
            {
                await _recorder.StartAsync();
            }

            string audioStatus = _settings.RecordAudio 
                ? $"(Audio: {GetAudioStatusText()})" 
                : "(Audio: Off)";
            LogToTray($"Settings applied {audioStatus}");
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
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            Shutdown();
        }

        private void LogToTray(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
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
