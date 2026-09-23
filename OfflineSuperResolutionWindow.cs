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
    internal sealed class OfflineSuperResolutionWindow : Window
    {
        private readonly Func<ResourcePlan> _getResourcePlan;
        private readonly TextBox _inputPathBox;
        private readonly TextBox _outputFolderBox;
        private readonly Slider _scaleSlider;
        private readonly TextBlock _scaleText;
        private readonly Slider _detailSlider;
        private readonly TextBlock _detailText;
        private readonly ComboBox _deviceBox;
        private readonly ComboBox _frameRateBox;
        private readonly TextBlock _statusText;
        private readonly TextBlock _percentText;
        private readonly ProgressBar _progressBar;
        private readonly Button _startButton;
        private readonly Button _cancelButton;

        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isRendering;

        public OfflineSuperResolutionWindow(Func<ResourcePlan>? getResourcePlan = null)
        {
            _getResourcePlan = getResourcePlan ?? (() => ResourcePlan.CreateFallback());
            Title = "Render Super Resolution";
            Width = 720;
            Height = 450;
            MinWidth = 640;
            MinHeight = 410;
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
                Text = "Render Video File",
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
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(optionsGrid, 3);
            root.Children.Add(optionsGrid);

            optionsGrid.Children.Add(Label("Scale", 0));
            _scaleSlider = new Slider
            {
                Minimum = 1,
                Maximum = 4,
                Value = 2,
                TickFrequency = 0.25,
                IsSnapToTickEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_scaleSlider, 1);
            optionsGrid.Children.Add(_scaleSlider);

            _scaleText = ValueText(FormatScale(_scaleSlider.Value));
            Grid.SetColumn(_scaleText, 2);
            optionsGrid.Children.Add(_scaleText);
            _scaleSlider.ValueChanged += (_, _) => _scaleText.Text = FormatScale(_scaleSlider.Value);

            optionsGrid.Children.Add(Label("Engine", 4));
            _deviceBox = new ComboBox { Height = 32 };
            AddComboItem(_deviceBox, "Fast FFmpeg", AiSuperResolutionDeviceMode.Cpu);
            AddComboItem(_deviceBox, "AI Real-ESRGAN integrated GPU", AiSuperResolutionDeviceMode.IntegratedGpu);
            AddComboItem(_deviceBox, "AI Real-ESRGAN discrete GPU", AiSuperResolutionDeviceMode.DiscreteGpu);
            _deviceBox.SelectedIndex = 0;
            Grid.SetColumn(_deviceBox, 5);
            optionsGrid.Children.Add(_deviceBox);

            var detailGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            Grid.SetRow(detailGrid, 4);
            root.Children.Add(detailGrid);

            detailGrid.Children.Add(Label("Detail", 0));
            _detailSlider = new Slider
            {
                Minimum = 0,
                Maximum = 2,
                Value = 2,
                TickFrequency = 0.05,
                IsSnapToTickEnabled = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_detailSlider, 1);
            detailGrid.Children.Add(_detailSlider);

            _detailText = ValueText(_detailSlider.Value.ToString("0.00", CultureInfo.InvariantCulture));
            Grid.SetColumn(_detailText, 2);
            detailGrid.Children.Add(_detailText);
            _detailSlider.ValueChanged += (_, _) => _detailText.Text = _detailSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

            detailGrid.Children.Add(Label("FPS", 4));
            _frameRateBox = new ComboBox { Height = 32 };
            AddComboItem(_frameRateBox, "Same", 1);
            AddComboItem(_frameRateBox, "2x", 2);
            AddComboItem(_frameRateBox, "3x", 3);
            AddComboItem(_frameRateBox, "4x", 4);
            _frameRateBox.SelectedIndex = 0;
            Grid.SetColumn(_frameRateBox, 5);
            detailGrid.Children.Add(_frameRateBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 18, 0, 0)
            };
            Grid.SetRow(buttons, 5);
            root.Children.Add(buttons);

            _startButton = CreateButton("Start Render", StartRender);
            buttons.Children.Add(_startButton);

            _cancelButton = CreateButton("Cancel", CancelRender);
            _cancelButton.IsEnabled = false;
            buttons.Children.Add(_cancelButton);

            var progressGrid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            Grid.SetRow(progressGrid, 6);
            root.Children.Add(progressGrid);

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
            Grid.SetRow(_statusText, 7);
            root.Children.Add(_statusText);

            Closing += (_, e) =>
            {
                if (_isRendering)
                {
                    e.Cancel = true;
                    CancelRender();
                }
            };
        }

        private async void StartRender()
        {
            if (_isRendering)
                return;

            string inputPath = _inputPathBox.Text.Trim();
            string outputFolder = _outputFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                MessageBox.Show("Choose a video file to render.", "Render Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                return;
            }
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                MessageBox.Show("Choose an output folder.", "Render Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                return;
            }

            Directory.CreateDirectory(outputFolder);
            var deviceMode = (AiSuperResolutionDeviceMode)((_deviceBox.SelectedItem as ComboBoxItem)?.Tag ?? AiSuperResolutionDeviceMode.IntegratedGpu);
            int frameRateMultiplier = (int)((_frameRateBox.SelectedItem as ComboBoxItem)?.Tag ?? 1);
            ResourcePlan resourcePlan = _getResourcePlan();

            _cancellationTokenSource = new CancellationTokenSource();
            SetRenderingState(true);
            SetProgressStatus(AiSuperResolutionProgress.Format(0.0, "Starting render with " + resourcePlan.FormatOfflineStatus() + "..."));

            var progress = new Progress<string>(SetProgressStatus);
            try
            {
                var service = new AiSuperResolutionService();
                var request = new AiSuperResolutionRequest
                {
                    InputPath = inputPath,
                    OutputDirectory = outputFolder,
                    StartSeconds = 0.0,
                    DurationSeconds = null,
                    TargetMode = "Custom",
                    CustomScale = _scaleSlider.Value,
                    DetailStrength = _detailSlider.Value,
                    DeviceMode = deviceMode,
                    UseFrameByFrameAi = deviceMode != AiSuperResolutionDeviceMode.Cpu,
                    FrameRateMultiplier = frameRateMultiplier,
                    FrameInterpolationMode = deviceMode == AiSuperResolutionDeviceMode.Cpu
                        ? AiFrameInterpolationMode.FfmpegMotion
                        : AiFrameInterpolationMode.RifeAi,
                    ResourcePlan = resourcePlan
                };

                AiSuperResolutionResult result = await service.CreateEnhancedVideoAsync(request, progress, _cancellationTokenSource.Token);
                SetProgressStatus(AiSuperResolutionProgress.Format(100.0, "Complete: " + result.Summary + "\n" + result.OutputPath));
                MessageBox.Show("Super-resolution render complete.\n\n" + result.Summary + "\n\n" + result.OutputPath, "Render Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            }
            catch (OperationCanceledException)
            {
                SetProgressStatus("Canceled");
            }
            catch (Exception ex)
            {
                SetProgressStatus("Failed: " + ex.Message);
                MessageBox.Show("Render failed.\n\n" + ex.Message, "Render Super Resolution", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                SetRenderingState(false);
            }
        }

        private void CancelRender()
        {
            _cancelButton.IsEnabled = false;
            SetProgressStatus("Canceling...");
            _cancellationTokenSource?.Cancel();
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

        private void BrowseInputFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose video file",
                Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.3gp;*.mxf|All files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                _inputPathBox.Text = dialog.FileName;
                if (string.IsNullOrWhiteSpace(_outputFolderBox.Text))
                    _outputFolderBox.Text = Path.GetDirectoryName(dialog.FileName) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
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

        private void SetRenderingState(bool isRendering)
        {
            _isRendering = isRendering;
            _startButton.IsEnabled = !isRendering;
            _cancelButton.IsEnabled = isRendering;
            _inputPathBox.IsEnabled = !isRendering;
            _outputFolderBox.IsEnabled = !isRendering;
            _scaleSlider.IsEnabled = !isRendering;
            _detailSlider.IsEnabled = !isRendering;
            _deviceBox.IsEnabled = !isRendering;
            _frameRateBox.IsEnabled = !isRendering;
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

        private static TextBlock ValueText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0)
            };
        }

        private static string FormatScale(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
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
