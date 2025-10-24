using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace ReBuffer
{
    public partial class SettingsWindow : Window
    {
        private Settings _settings;
        public bool SettingsChanged { get; private set; }

        public SettingsWindow(Settings settings)
        {
            InitializeComponent();
            _settings = settings;
            
            // Load settings after all components are initialized
            Loaded += (s, e) => LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                // Save path
                SavePathText.Text = _settings.SavePath;

                // Hotkey
                foreach (ComboBoxItem item in HotKeyCombo.Items)
                {
                    if (item.Tag != null && int.Parse(item.Tag.ToString()!) == _settings.HotKeyCode)
                    {
                        HotKeyCombo.SelectedItem = item;
                        break;
                    }
                }

                // Buffer duration
                BufferSlider.Value = _settings.BufferSeconds;
                BufferValueText.Text = $"{_settings.BufferSeconds}s";

                // Frame rate
                foreach (ComboBoxItem item in FrameRateCombo.Items)
                {
                    if (item.Tag != null && int.Parse(item.Tag.ToString()!) == _settings.FrameRate)
                    {
                        FrameRateCombo.SelectedItem = item;
                        break;
                    }
                }

                // Quality
                foreach (ComboBoxItem item in QualityCombo.Items)
                {
                    if (item.Tag != null && item.Tag.ToString() == _settings.Quality.ToString())
                    {
                        QualityCombo.SelectedItem = item;
                        break;
                    }
                }

                // Custom settings
                BitrateText.Text = _settings.Bitrate.ToString();
                CRFText.Text = _settings.CRF.ToString();

                // Hardware encoding
                HardwareEncodingCheck.IsChecked = _settings.UseHardwareEncoding;

                // Audio settings
                RecordAudioCheck.IsChecked = _settings.RecordAudio;
                RecordDesktopCheck.IsChecked = _settings.RecordDesktopAudio;
                RecordMicCheck.IsChecked = _settings.RecordMicrophone;
                
                DesktopVolumeSlider.Value = _settings.DesktopVolume * 100;
                MicVolumeSlider.Value = _settings.MicrophoneVolume * 100;

                // Load audio devices
                LoadAudioDevices();

                // Startup setting
                RunAtStartupCheck.IsChecked = _settings.RunAtStartup;

                UpdateMemoryEstimate();
                UpdateAudioPanelVisibility();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAudioDevices()
        {
            try
            {
                // Desktop audio devices
                var desktopDevices = AudioRecorder.GetDesktopAudioDevices();
                DesktopDeviceCombo.Items.Clear();
                
                foreach (var device in desktopDevices)
                {
                    var parts = device.Split('|');
                    var item = new ComboBoxItem 
                    { 
                        Content = parts[0],
                        Tag = parts.Length > 1 ? parts[1] : ""
                    };
                    DesktopDeviceCombo.Items.Add(item);
                    
                    // Select saved device
                    if (_settings.DesktopAudioDevice == null && parts[0].Contains("Default"))
                    {
                        DesktopDeviceCombo.SelectedItem = item;
                    }
                    else if (_settings.DesktopAudioDevice != null && 
                             parts.Length > 1 && 
                             parts[1] == _settings.DesktopAudioDevice)
                    {
                        DesktopDeviceCombo.SelectedItem = item;
                    }
                }

                if (DesktopDeviceCombo.SelectedItem == null && DesktopDeviceCombo.Items.Count > 0)
                {
                    DesktopDeviceCombo.SelectedIndex = 0;
                }

                // Microphone devices
                var micDevices = AudioRecorder.GetMicrophoneDevices();
                MicDeviceCombo.Items.Clear();
                
                foreach (var device in micDevices)
                {
                    var parts = device.Split('|');
                    var item = new ComboBoxItem 
                    { 
                        Content = parts[0],
                        Tag = parts.Length > 1 ? parts[1] : ""
                    };
                    MicDeviceCombo.Items.Add(item);
                    
                    // Select saved device
                    if (_settings.MicrophoneDevice == null && parts[0].Contains("Default"))
                    {
                        MicDeviceCombo.SelectedItem = item;
                    }
                    else if (_settings.MicrophoneDevice != null && 
                             parts.Length > 1 && 
                             parts[1] == _settings.MicrophoneDevice)
                    {
                        MicDeviceCombo.SelectedItem = item;
                    }
                }

                if (MicDeviceCombo.SelectedItem == null && MicDeviceCombo.Items.Count > 0)
                {
                    MicDeviceCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load audio devices: {ex.Message}");
            }
        }

        private void UpdateAudioPanelVisibility()
        {
            // Null checks to prevent errors during initialization
            if (AudioOptionsPanel == null || RecordAudioCheck == null) return;
            if (DesktopAudioPanel == null || RecordDesktopCheck == null) return;
            if (MicrophonePanel == null || RecordMicCheck == null) return;
            
            AudioOptionsPanel.IsEnabled = RecordAudioCheck.IsChecked ?? false;
            DesktopAudioPanel.IsEnabled = RecordDesktopCheck.IsChecked ?? false;
            MicrophonePanel.IsEnabled = RecordMicCheck.IsChecked ?? false;
        }

        private void RecordAudioCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAudioPanelVisibility();
        }

        private void AudioSourceCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAudioPanelVisibility();
        }

        private void BufferSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BufferValueText != null)
            {
                int value = (int)e.NewValue;
                BufferValueText.Text = $"{value}s";
                UpdateMemoryEstimate();
            }
        }

        private void FrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMemoryEstimate();
        }

        private void QualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (QualityCombo == null || CustomPanel == null)
                return;
                
            if (QualityCombo.SelectedItem is ComboBoxItem item)
            {
                CustomPanel.Visibility = item.Tag?.ToString() == "Custom" 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
        }

        private void DesktopVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DesktopVolumeText != null)
            {
                DesktopVolumeText.Text = $"{(int)e.NewValue}%";
            }
        }

        private void MicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MicVolumeText != null)
            {
                MicVolumeText.Text = $"{(int)e.NewValue}%";
            }
        }

        private void UpdateMemoryEstimate()
        {
            if (MemoryEstimateText == null || BufferSlider == null || FrameRateCombo == null)
                return;

            if (FrameRateCombo.SelectedItem == null)
                return;

            try
            {
                int bufferSeconds = (int)BufferSlider.Value;
                var selectedItem = FrameRateCombo.SelectedItem as ComboBoxItem;
                if (selectedItem == null || selectedItem.Tag == null)
                    return;

                int fps = int.Parse(selectedItem.Tag.ToString()!);
                
                // Video memory estimate
                int maxFrames = 100;
                long bytesPerFrame = 1920 * 1080 * 4;
                double videoMemoryMB = (bytesPerFrame * maxFrames) / (1024.0 * 1024.0);

                // Audio memory estimate (48kHz, 16-bit, stereo)
                double audioMemoryMB = (bufferSeconds * 48000 * 2 * 2) / (1024.0 * 1024.0);

                double totalMemoryMB = videoMemoryMB + audioMemoryMB;

                MemoryEstimateText.Text = $"Est. memory: ~{totalMemoryMB:F0} MB (video: {videoMemoryMB:F0} MB + audio: {audioMemoryMB:F0} MB)";
                MemoryEstimateText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
            }
            catch
            {
                // Silently fail if something goes wrong during initialization
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Save path
                _settings.SavePath = SavePathText.Text;
                
                // Validate save path exists or can be created
                try
                {
                    Directory.CreateDirectory(_settings.SavePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Invalid save path: {ex.Message}", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save hotkey
                var hotKeyItem = HotKeyCombo.SelectedItem as ComboBoxItem;
                if (hotKeyItem?.Tag == null)
                {
                    MessageBox.Show("Please select a hotkey.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _settings.HotKeyCode = int.Parse(hotKeyItem.Tag.ToString()!);

                // Save buffer settings
                _settings.BufferSeconds = (int)BufferSlider.Value;
                
                var frameRateItem = FrameRateCombo.SelectedItem as ComboBoxItem;
                if (frameRateItem?.Tag == null)
                {
                    MessageBox.Show("Please select a frame rate.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _settings.FrameRate = int.Parse(frameRateItem.Tag.ToString()!);

                // Save quality settings
                var qualityItem = QualityCombo.SelectedItem as ComboBoxItem;
                if (qualityItem?.Tag == null)
                {
                    MessageBox.Show("Please select a quality preset.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                var qualityTag = qualityItem.Tag.ToString()!;
                _settings.Quality = Enum.Parse<VideoQuality>(qualityTag);

                // Save custom settings if applicable
                if (_settings.Quality == VideoQuality.Custom)
                {
                    if (int.TryParse(BitrateText.Text, out int bitrate))
                    {
                        _settings.Bitrate = bitrate;
                    }
                    else
                    {
                        MessageBox.Show("Invalid bitrate value. Please enter a number.", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (int.TryParse(CRFText.Text, out int crf))
                    {
                        if (crf < 0 || crf > 51)
                        {
                            MessageBox.Show("CRF must be between 0 and 51.", "Validation Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        _settings.CRF = crf;
                    }
                    else
                    {
                        MessageBox.Show("Invalid CRF value. Please enter a number between 0 and 51.", "Validation Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                // Save hardware encoding preference
                _settings.UseHardwareEncoding = HardwareEncodingCheck.IsChecked ?? true;

                // Save audio settings
                _settings.RecordAudio = RecordAudioCheck.IsChecked ?? true;
                _settings.RecordDesktopAudio = RecordDesktopCheck.IsChecked ?? true;
                _settings.RecordMicrophone = RecordMicCheck.IsChecked ?? true;
                
                _settings.DesktopVolume = (float)(DesktopVolumeSlider.Value / 100.0);
                _settings.MicrophoneVolume = (float)(MicVolumeSlider.Value / 100.0);

                // Save selected audio devices
                var desktopItem = DesktopDeviceCombo.SelectedItem as ComboBoxItem;
                if (desktopItem != null)
                {
                    var deviceId = desktopItem.Tag?.ToString();
                    _settings.DesktopAudioDevice = string.IsNullOrEmpty(deviceId) ? null : deviceId;
                }

                var micItem = MicDeviceCombo.SelectedItem as ComboBoxItem;
                if (micItem != null)
                {
                    var deviceId = micItem.Tag?.ToString();
                    _settings.MicrophoneDevice = string.IsNullOrEmpty(deviceId) ? null : deviceId;
                }

                // Validate audio settings
                if (_settings.RecordAudio && !_settings.RecordDesktopAudio && !_settings.RecordMicrophone)
                {
                    MessageBox.Show("Please enable at least one audio source or disable audio recording.", 
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save startup setting
                bool runAtStartup = RunAtStartupCheck.IsChecked ?? false;
                if (runAtStartup != _settings.RunAtStartup)
                {
                    if (!StartupManager.SetStartup(runAtStartup))
                    {
                        MessageBox.Show("Failed to update Windows startup settings. You may need to run ReBuffer as administrator.", 
                            "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    _settings.RunAtStartup = runAtStartup;
                }

                // Save to file
                _settings.Save();

                SettingsChanged = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SettingsChanged = false;
            DialogResult = false;
            Close();
        }

        private void BrowseSavePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Select Clips Folder",
                FileName = "SelectFolder",
                Filter = "Folder|*.none",
                CheckFileExists = false,
                CheckPathExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                var folderPath = System.IO.Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    SavePathText.Text = folderPath;
                }
            }
        }
    }
}
