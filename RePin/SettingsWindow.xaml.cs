using System;
using System.IO;
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

                UpdateMemoryEstimate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                
                // Note: With adaptive pool, actual memory will be much lower
                // This shows theoretical maximum if all frames were unique
                int maxFrames = 100; // Adaptive pool limit
                long bytesPerFrame = 1920 * 1080 * 4;
                double memoryMB = (bytesPerFrame * maxFrames) / (1024.0 * 1024.0);

                MemoryEstimateText.Text = $"Max memory: ~{memoryMB:F0} MB (adaptive pool)";
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

            // Workaround to select folder using SaveFileDialog
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
