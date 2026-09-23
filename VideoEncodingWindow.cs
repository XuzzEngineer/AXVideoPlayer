using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace AXVideoPlayer
{
    internal sealed class VideoEncodingWindow : Window
    {
        private readonly Func<ResourcePlan> _getResourcePlan;
        private readonly TextBox _inputPathBox;
        private readonly TextBox _outputFolderBox;
        private readonly ComboBox _formatBox;
        private readonly ComboBox _resolutionBox;
        private readonly ComboBox _qualityBox;
        private readonly TextBox _customWidthBox;
        private readonly TextBox _customHeightBox;
        private readonly CheckBox _overwriteBox;
        private readonly ProgressBar _progressBar;
        private readonly TextBlock _percentText;
        private readonly TextBlock _statusText;
        private readonly Button _startButton;
        private readonly Button _cancelButton;

        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isEncoding;

        public VideoEncodingWindow(string? initialInputPath = null, Func<ResourcePlan>? getResourcePlan = null)
        {
            _getResourcePlan = getResourcePlan ?? (() => ResourcePlan.CreateFallback());
            Title = "Video Encoder";
            Width = 760;
            Height = 540;
            MinWidth = 680;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush(30, 30, 30);
            Foreground = Brushes.White;
            FontSize = 14;

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Content = root;

            root.Children.Add(new TextBlock
            {
                Text = "Video Encoder",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 16)
            });

            _inputPathBox = new TextBox { IsReadOnly = true };
            AddPathRow(root, 1, "Video file", _inputPathBox, BrowseInputFile);

            _outputFolderBox = new TextBox { IsReadOnly = true };
            AddPathRow(root, 2, "Output folder", _outputFolderBox, BrowseOutputFolder);

            var optionsGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(optionsGrid, 3);
            root.Children.Add(optionsGrid);

            optionsGrid.Children.Add(Label("Format", 0));
            _formatBox = new ComboBox { Height = 32 };
            AddComboItem(_formatBox, "MP4", "MP4");
            AddComboItem(_formatBox, "MKV", "MKV");
            AddComboItem(_formatBox, "MOV", "MOV");
            AddComboItem(_formatBox, "WebM", "WebM");
            AddComboItem(_formatBox, "AVI", "AVI");
            _formatBox.SelectedIndex = 0;
            Grid.SetColumn(_formatBox, 1);
            optionsGrid.Children.Add(_formatBox);

            optionsGrid.Children.Add(Label("Resolution", 3));
            _resolutionBox = new ComboBox { Height = 32 };
            AddComboItem(_resolutionBox, "Same", "Same");
            AddComboItem(_resolutionBox, "2160p", "2160p");
            AddComboItem(_resolutionBox, "1440p", "1440p");
            AddComboItem(_resolutionBox, "1080p", "1080p");
            AddComboItem(_resolutionBox, "720p", "720p");
            AddComboItem(_resolutionBox, "480p", "480p");
            AddComboItem(_resolutionBox, "360p", "360p");
            AddComboItem(_resolutionBox, "Custom", "Custom");
            _resolutionBox.SelectedIndex = 0;
            _resolutionBox.SelectionChanged += (_, _) => UpdateCustomSizeState();
            Grid.SetColumn(_resolutionBox, 4);
            optionsGrid.Children.Add(_resolutionBox);

            var customGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            customGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(customGrid, 4);
            root.Children.Add(customGrid);

            customGrid.Children.Add(Label("Custom max", 0));
            _customWidthBox = CreateInputBox("1920");
            Grid.SetColumn(_customWidthBox, 1);
            customGrid.Children.Add(_customWidthBox);

            customGrid.Children.Add(new TextBlock
            {
                Text = "x",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(customGrid.Children[customGrid.Children.Count - 1], 2);

            _customHeightBox = CreateInputBox("1080");
            Grid.SetColumn(_customHeightBox, 3);
            customGrid.Children.Add(_customHeightBox);

            customGrid.Children.Add(Label("Quality", 5));
            _qualityBox = new ComboBox { Height = 32 };
            AddComboItem(_qualityBox, "High", "High");
            AddComboItem(_qualityBox, "Balanced", "Balanced");
            AddComboItem(_qualityBox, "Small", "Small");
            _qualityBox.SelectedIndex = 1;
            Grid.SetColumn(_qualityBox, 6);
            customGrid.Children.Add(_qualityBox);

            _overwriteBox = new CheckBox
            {
                Content = "Overwrite if output file already exists",
                Foreground = Brushes.White,
                Margin = new Thickness(0, 14, 0, 0)
            };
            Grid.SetRow(_overwriteBox, 5);
            root.Children.Add(_overwriteBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 18, 0, 0)
            };
            Grid.SetRow(buttons, 6);
            root.Children.Add(buttons);

            _startButton = CreateButton("Start Encode", StartEncode);
            buttons.Children.Add(_startButton);

            _cancelButton = CreateButton("Cancel", CancelEncode);
            _cancelButton.IsEnabled = false;
            buttons.Children.Add(_cancelButton);

            var progressPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            Grid.SetRow(progressPanel, 7);
            root.Children.Add(progressPanel);

            var progressGrid = new Grid();
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            progressPanel.Children.Add(progressGrid);

            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 16,
                IsIndeterminate = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            progressGrid.Children.Add(_progressBar);

            _percentText = new TextBlock
            {
                Text = "0%",
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(_percentText, 1);
            progressGrid.Children.Add(_percentText);

            _statusText = new TextBlock
            {
                Text = "Ready. " + _getResourcePlan().FormatOfflineStatus(),
                Foreground = Brush(215, 215, 215),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 18, 0, 0)
            };
            progressPanel.Children.Add(_statusText);

            if (!string.IsNullOrWhiteSpace(initialInputPath) && File.Exists(initialInputPath))
                SetInputPath(initialInputPath);

            UpdateCustomSizeState();
            Closing += (_, e) =>
            {
                if (_isEncoding)
                {
                    e.Cancel = true;
                    CancelEncode();
                }
            };
        }

        private async void StartEncode()
        {
            if (_isEncoding)
                return;

            string inputPath = _inputPathBox.Text.Trim();
            string outputFolder = _outputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                MessageBox.Show("Choose a video file to encode.", "Video Encoder", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                return;
            }

            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                MessageBox.Show("Choose an output folder.", "Video Encoder", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                return;
            }

            if (!TryReadCustomSize(out int customWidth, out int customHeight))
                return;

            Directory.CreateDirectory(outputFolder);
            ResourcePlan resourcePlan = _getResourcePlan();
            _cancellationTokenSource = new CancellationTokenSource();
            SetEncodingState(true);
            SetProgressStatus(AiSuperResolutionProgress.Format(0.0, "Starting encode with " + resourcePlan.FormatOfflineStatus() + "..."));

            var request = new VideoEncodeRequest
            {
                InputPath = inputPath,
                OutputDirectory = outputFolder,
                OutputFormat = GetSelectedTag(_formatBox, "MP4"),
                ResolutionMode = GetSelectedTag(_resolutionBox, "Same"),
                CustomWidth = customWidth,
                CustomHeight = customHeight,
                QualityPreset = GetSelectedTag(_qualityBox, "Balanced"),
                Overwrite = _overwriteBox.IsChecked == true,
                ResourcePlan = resourcePlan
            };

            try
            {
                var service = new VideoEncodingService();
                VideoEncodeResult result = await service.EncodeAsync(request, new Progress<string>(SetProgressStatus), _cancellationTokenSource.Token);
                SetProgressStatus(AiSuperResolutionProgress.Format(100.0, "Complete: " + result.Summary + "\n" + result.OutputPath));
                MessageBox.Show("Video encode complete.\n\n" + result.Summary + "\n\n" + result.OutputPath, "Video Encoder", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            }
            catch (OperationCanceledException)
            {
                SetProgressStatus("Canceled");
            }
            catch (Exception ex)
            {
                SetProgressStatus("Failed: " + ex.Message);
                MessageBox.Show("Encode failed.\n\n" + ex.Message, "Video Encoder", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                SetEncodingState(false);
            }
        }

        private void CancelEncode()
        {
            _cancelButton.IsEnabled = false;
            SetProgressStatus("Canceling...");
            _cancellationTokenSource?.Cancel();
        }

        private bool TryReadCustomSize(out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!string.Equals(GetSelectedTag(_resolutionBox, "Same"), "Custom", StringComparison.OrdinalIgnoreCase))
                return true;

            bool hasWidth = int.TryParse(_customWidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width) && width > 0;
            bool hasHeight = int.TryParse(_customHeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height) && height > 0;
            if (hasWidth || hasHeight)
                return true;

            MessageBox.Show("Enter a custom width or height greater than zero.", "Video Encoder", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            return false;
        }

        private void BrowseInputFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose video file",
                Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.3gp;*.mxf;*.ogv;*.vob|All files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
                SetInputPath(dialog.FileName);
        }

        private void BrowseOutputFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose output folder",
                Multiselect = false
            };

            if (!string.IsNullOrWhiteSpace(_outputFolderBox.Text) && Directory.Exists(_outputFolderBox.Text))
                dialog.InitialDirectory = _outputFolderBox.Text;

            if (dialog.ShowDialog(this) == true)
                _outputFolderBox.Text = dialog.FolderName;
        }

        private void SetInputPath(string inputPath)
        {
            _inputPathBox.Text = inputPath;
            if (string.IsNullOrWhiteSpace(_outputFolderBox.Text))
                _outputFolderBox.Text = Path.GetDirectoryName(inputPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private void SetProgressStatus(string status)
        {
            if (AiSuperResolutionProgress.TryParse(status, out double percent, out string message))
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = percent;
                _percentText.Text = percent.ToString("0", CultureInfo.InvariantCulture) + "%";
                _statusText.Text = message;
            }
            else
            {
                _statusText.Text = string.IsNullOrWhiteSpace(status) ? "Working..." : status;
            }
        }

        private void SetEncodingState(bool isEncoding)
        {
            _isEncoding = isEncoding;
            _startButton.IsEnabled = !isEncoding;
            _cancelButton.IsEnabled = isEncoding;
            _inputPathBox.IsEnabled = !isEncoding;
            _outputFolderBox.IsEnabled = !isEncoding;
            _formatBox.IsEnabled = !isEncoding;
            _resolutionBox.IsEnabled = !isEncoding;
            _qualityBox.IsEnabled = !isEncoding;
            _customWidthBox.IsEnabled = !isEncoding && IsCustomResolutionSelected();
            _customHeightBox.IsEnabled = !isEncoding && IsCustomResolutionSelected();
            _overwriteBox.IsEnabled = !isEncoding;
        }

        private void UpdateCustomSizeState()
        {
            bool enabled = !_isEncoding && IsCustomResolutionSelected();
            _customWidthBox.IsEnabled = enabled;
            _customHeightBox.IsEnabled = enabled;
        }

        private bool IsCustomResolutionSelected()
        {
            return string.Equals(GetSelectedTag(_resolutionBox, "Same"), "Custom", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSelectedTag(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
        }

        private static void AddPathRow(Grid root, int row, string label, TextBox textBox, Action browseAction)
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            Grid.SetRow(grid, row);
            root.Children.Add(grid);

            grid.Children.Add(Label(label, 0));
            textBox.Height = 32;
            textBox.VerticalContentAlignment = VerticalAlignment.Center;
            textBox.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(textBox, 1);
            grid.Children.Add(textBox);

            Button browse = CreateButton("Browse", browseAction);
            browse.Margin = new Thickness(0);
            Grid.SetColumn(browse, 2);
            grid.Children.Add(browse);
        }

        private static TextBox CreateInputBox(string text)
        {
            return new TextBox
            {
                Text = text,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock Label(string text, int column)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(label, column);
            return label;
        }

        private static Button CreateButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 34,
                MinWidth = 92,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(12, 0, 12, 0)
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static void AddComboItem(ComboBox comboBox, string text, object tag)
        {
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = text,
                Tag = tag
            });
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }
}
