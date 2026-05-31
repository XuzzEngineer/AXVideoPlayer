using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AXVideoPlayer
{
    internal sealed class ToolsWindow : Window
    {
        private readonly Action _takeScreenshot;
        private readonly Func<bool> _getAudioEqualizerEnabled;
        private readonly Action<bool> _setAudioEqualizerEnabled;
        private readonly Func<double> _getAudioPreamp;
        private readonly Func<int, double> _getAudioBand;
        private readonly Action<double> _setAudioPreamp;
        private readonly Action<int, double> _setAudioBand;
        private readonly Action _resetAudioEqualizer;
        private readonly Func<bool> _getVideoEqualizerEnabled;
        private readonly Action<bool> _setVideoEqualizerEnabled;
        private readonly Func<string, double> _getVideoValue;
        private readonly Action<string, double> _setVideoValue;
        private readonly Action _resetVideoEqualizer;
        private readonly Func<IReadOnlyList<AudioTrackOption>> _loadAudioTracks;
        private readonly Func<bool> _isAudioDisabled;
        private readonly Action<int> _selectAudioTrack;
        private readonly Func<bool> _getResumeEnabled;
        private readonly Action<bool> _setResumeEnabled;
        private readonly Action _clearPlaybackHistory;
        private readonly Func<string> _getAspectRatio;
        private readonly Action<string> _setAspectRatio;
        private readonly Func<bool> _getAlwaysOnTop;
        private readonly Action<bool> _setAlwaysOnTop;
        private readonly Func<long> _getAudioDelayMs;
        private readonly Action<long> _setAudioDelayMs;

        private readonly ListBox _navigation = new();
        private readonly ContentControl _content = new();
        private StackPanel? _audioTrackList;
        private bool _isUpdating;

        public ToolsWindow(
            Action takeScreenshot,
            Func<bool> getAudioEqualizerEnabled,
            Action<bool> setAudioEqualizerEnabled,
            Func<double> getAudioPreamp,
            Func<int, double> getAudioBand,
            Action<double> setAudioPreamp,
            Action<int, double> setAudioBand,
            Action resetAudioEqualizer,
            Func<bool> getVideoEqualizerEnabled,
            Action<bool> setVideoEqualizerEnabled,
            Func<string, double> getVideoValue,
            Action<string, double> setVideoValue,
            Action resetVideoEqualizer,
            Func<IReadOnlyList<AudioTrackOption>> loadAudioTracks,
            Func<bool> isAudioDisabled,
            Action<int> selectAudioTrack,
            Func<bool> getResumeEnabled,
            Action<bool> setResumeEnabled,
            Action clearPlaybackHistory,
            Func<string> getAspectRatio,
            Action<string> setAspectRatio,
            Func<bool> getAlwaysOnTop,
            Action<bool> setAlwaysOnTop,
            Func<long> getAudioDelayMs,
            Action<long> setAudioDelayMs)
        {
            _takeScreenshot = takeScreenshot;
            _getAudioEqualizerEnabled = getAudioEqualizerEnabled;
            _setAudioEqualizerEnabled = setAudioEqualizerEnabled;
            _getAudioPreamp = getAudioPreamp;
            _getAudioBand = getAudioBand;
            _setAudioPreamp = setAudioPreamp;
            _setAudioBand = setAudioBand;
            _resetAudioEqualizer = resetAudioEqualizer;
            _getVideoEqualizerEnabled = getVideoEqualizerEnabled;
            _setVideoEqualizerEnabled = setVideoEqualizerEnabled;
            _getVideoValue = getVideoValue;
            _setVideoValue = setVideoValue;
            _resetVideoEqualizer = resetVideoEqualizer;
            _loadAudioTracks = loadAudioTracks;
            _isAudioDisabled = isAudioDisabled;
            _selectAudioTrack = selectAudioTrack;
            _getResumeEnabled = getResumeEnabled;
            _setResumeEnabled = setResumeEnabled;
            _clearPlaybackHistory = clearPlaybackHistory;
            _getAspectRatio = getAspectRatio;
            _setAspectRatio = setAspectRatio;
            _getAlwaysOnTop = getAlwaysOnTop;
            _setAlwaysOnTop = setAlwaysOnTop;
            _getAudioDelayMs = getAudioDelayMs;
            _setAudioDelayMs = setAudioDelayMs;

            Title = "Tools";
            Width = 700;
            Height = 520;
            MinWidth = 620;
            MinHeight = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush(32, 32, 32);
            FontSize = 14;
            ShowInTaskbar = false;

            Content = BuildLayout();
            _navigation.SelectedIndex = 0;
        }

        public void RefreshAudioTracks()
        {
            if (_audioTrackList == null)
                return;

            PopulateAudioTrackList(_audioTrackList);
        }

        private UIElement BuildLayout()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new Border
            {
                Background = Brush(24, 24, 24),
                BorderBrush = Brush(50, 50, 50),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(10)
            };
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var leftPanel = new DockPanel();
            left.Child = leftPanel;

            _navigation.BorderThickness = new Thickness(0);
            _navigation.Background = Brushes.Transparent;
            _navigation.Foreground = Brushes.White;
            _navigation.FontSize = 14;
            _navigation.ItemContainerStyle = CreateNavigationItemStyle();
            _navigation.Items.Add("Screenshot");
            _navigation.Items.Add("Audio Equalizer");
            _navigation.Items.Add("Video Equalizer");
            _navigation.Items.Add("Audio Track");
            _navigation.Items.Add("Aspect Ratio");
            _navigation.Items.Add("Playback");
            _navigation.Items.Add("Resume");
            _navigation.SelectionChanged += (_, _) => ShowSelectedPage();
            DockPanel.SetDock(_navigation, Dock.Bottom);
            leftPanel.Children.Add(_navigation);

            var right = new Border
            {
                Background = Brush(32, 32, 32),
                Padding = new Thickness(18)
            };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            right.Child = _content;

            return grid;
        }

        private void ShowSelectedPage()
        {
            string selected = _navigation.SelectedItem?.ToString() ?? "Screenshot";
            _audioTrackList = null;

            _content.Content = selected switch
            {
                "Audio Equalizer" => BuildAudioEqualizerPage(),
                "Video Equalizer" => BuildVideoEqualizerPage(),
                "Audio Track" => BuildAudioTrackPage(),
                "Aspect Ratio" => BuildAspectRatioPage(),
                "Playback" => BuildPlaybackPage(),
                "Resume" => BuildResumePage(),
                _ => BuildScreenshotPage()
            };
        }

        private UIElement BuildScreenshotPage()
        {
            var panel = CreatePage("Screenshot");
            panel.Children.Add(new TextBlock
            {
                Text = "Save a snapshot beside the currently playing video file.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 14)
            });
            panel.Children.Add(CreateActionButton("Take Screenshot", _takeScreenshot));
            return panel;
        }

        private UIElement BuildAudioEqualizerPage()
        {
            var panel = CreatePage("Audio Equalizer");

            var enableBox = new CheckBox
            {
                Content = "Enable audio equalizer",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getAudioEqualizerEnabled(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setAudioEqualizerEnabled(true);
            enableBox.Unchecked += (_, _) => _setAudioEqualizerEnabled(false);
            panel.Children.Add(enableBox);

            Slider preamp = CreateSlider("Preamp", _getAudioPreamp(), -20, 20, "0.0 dB", panel, out TextBlock preampText);
            preamp.ValueChanged += (_, _) =>
            {
                preampText.Text = FormatDb(preamp.Value);
                if (!_isUpdating)
                    _setAudioPreamp(preamp.Value);
            };

            string[] labels = { "60 Hz", "170 Hz", "310 Hz", "600 Hz", "1 kHz", "3 kHz", "6 kHz", "12 kHz", "14 kHz", "16 kHz" };
            var sliders = new List<Slider> { preamp };

            for (int i = 0; i < labels.Length; i++)
            {
                int bandIndex = i;
                Slider slider = CreateSlider(labels[i], _getAudioBand(i), -20, 20, "0.0 dB", panel, out TextBlock valueText);
                slider.ValueChanged += (_, _) =>
                {
                    valueText.Text = FormatDb(slider.Value);
                    if (!_isUpdating)
                        _setAudioBand(bandIndex, slider.Value);
                };
                sliders.Add(slider);
            }

            panel.Children.Add(CreateActionButton("Reset", () =>
            {
                _isUpdating = true;
                foreach (Slider slider in sliders)
                    slider.Value = 0;
                _isUpdating = false;
                _resetAudioEqualizer();
            }));

            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private UIElement BuildVideoEqualizerPage()
        {
            var panel = CreatePage("Video Equalizer");

            var enableBox = new CheckBox
            {
                Content = "Enable video equalizer",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getVideoEqualizerEnabled(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setVideoEqualizerEnabled(true);
            enableBox.Unchecked += (_, _) => _setVideoEqualizerEnabled(false);
            panel.Children.Add(enableBox);

            Slider brightness = AddVideoSlider(panel, "Brightness", "brightness", 0, 2);
            Slider contrast = AddVideoSlider(panel, "Contrast", "contrast", 0, 2);
            Slider saturation = AddVideoSlider(panel, "Saturation", "saturation", 0, 3);
            Slider gamma = AddVideoSlider(panel, "Gamma", "gamma", 0.1, 3);
            Slider hue = AddVideoSlider(panel, "Hue", "hue", -180, 180);

            panel.Children.Add(CreateActionButton("Reset", () =>
            {
                _isUpdating = true;
                brightness.Value = 1.0;
                contrast.Value = 1.0;
                saturation.Value = 1.0;
                gamma.Value = 1.0;
                hue.Value = 0.0;
                _isUpdating = false;
                _resetVideoEqualizer();
            }));

            return panel;
        }

        private UIElement BuildAudioTrackPage()
        {
            var root = CreatePage("Audio Track");
            root.Children.Add(new TextBlock
            {
                Text = "Select the audio track for the current video.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _audioTrackList = new StackPanel();
            PopulateAudioTrackList(_audioTrackList);
            root.Children.Add(_audioTrackList);
            root.Children.Add(CreateActionButton("Refresh", RefreshAudioTracks));

            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        }

        private UIElement BuildAspectRatioPage()
        {
            var panel = CreatePage("Aspect Ratio");
            panel.Children.Add(new TextBlock
            {
                Text = "Choose how the video is shaped inside the player.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var combo = new ComboBox
            {
                Width = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8)
            };

            string[] options = { "Default", "16:9", "4:3", "1:1", "21:9" };
            foreach (string option in options)
                combo.Items.Add(option);

            combo.SelectedItem = options.Contains(_getAspectRatio()) ? _getAspectRatio() : "Default";
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string selected)
                    _setAspectRatio(selected);
            };

            panel.Children.Add(combo);
            return panel;
        }

        private UIElement BuildPlaybackPage()
        {
            var panel = CreatePage("Playback");

            var topMostBox = new CheckBox
            {
                Content = "Always on top",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getAlwaysOnTop(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            topMostBox.Checked += (_, _) => _setAlwaysOnTop(true);
            topMostBox.Unchecked += (_, _) => _setAlwaysOnTop(false);
            panel.Children.Add(topMostBox);

            Slider delaySlider = CreateSlider("Audio sync", _getAudioDelayMs(), -2000, 2000, "0 ms", panel, out TextBlock delayText);
            delaySlider.TickFrequency = 100;
            delaySlider.IsSnapToTickEnabled = true;
            delaySlider.ValueChanged += (_, _) =>
            {
                long delay = (long)Math.Round(delaySlider.Value);
                delayText.Text = FormatMs(delay);
                if (!_isUpdating)
                    _setAudioDelayMs(delay);
            };

            panel.Children.Add(CreateActionButton("Reset audio sync", () =>
            {
                _isUpdating = true;
                delaySlider.Value = 0;
                delayText.Text = FormatMs(0);
                _isUpdating = false;
                _setAudioDelayMs(0);
            }));

            return panel;
        }

        private UIElement BuildResumePage()
        {
            var panel = CreatePage("Resume");

            var enableBox = new CheckBox
            {
                Content = "Ask to resume last playback time",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getResumeEnabled(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setResumeEnabled(true);
            enableBox.Unchecked += (_, _) => _setResumeEnabled(false);
            panel.Children.Add(enableBox);

            panel.Children.Add(new TextBlock
            {
                Text = "When this is on, reopening a video shows a 3-second prompt to resume from its last saved time.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(CreateActionButton("Clear Watched History", _clearPlaybackHistory));
            return panel;
        }

        private void PopulateAudioTrackList(StackPanel target)
        {
            _isUpdating = true;
            target.Children.Clear();

            AddAudioTrackRadio(target, "Disable audio", -1, _isAudioDisabled());

            IReadOnlyList<AudioTrackOption> tracks = _loadAudioTracks();
            if (tracks.Count == 0)
            {
                target.Children.Add(new TextBlock
                {
                    Text = "No audio tracks found yet.",
                    Foreground = Brush(210, 210, 210),
                    FontSize = 14,
                    Margin = new Thickness(0, 10, 0, 10)
                });
            }
            else
            {
                foreach (AudioTrackOption track in tracks)
                    AddAudioTrackRadio(target, track.Name, track.Id, track.IsSelected);
            }

            _isUpdating = false;
        }

        private void AddAudioTrackRadio(Panel parent, string label, int trackId, bool selected)
        {
            var radio = new RadioButton
            {
                Content = label,
                Tag = trackId,
                GroupName = "AudioTracks",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = selected,
                Margin = new Thickness(0, 5, 0, 5)
            };
            radio.Checked += (_, _) =>
            {
                if (_isUpdating || radio.Tag is not int selectedTrackId)
                    return;

                _selectAudioTrack(selectedTrackId);
            };
            parent.Children.Add(radio);
        }

        private Slider AddVideoSlider(Panel parent, string label, string setting, double minimum, double maximum)
        {
            Slider slider = CreateSlider(label, _getVideoValue(setting), minimum, maximum, "0.00", parent, out TextBlock valueText);
            slider.ValueChanged += (_, _) =>
            {
                valueText.Text = slider.Value.ToString("0.00", CultureInfo.InvariantCulture);
                if (!_isUpdating)
                    _setVideoValue(setting, slider.Value);
            };
            return slider;
        }

        private static StackPanel CreatePage(string title)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            });
            return panel;
        }

        private static Button CreateActionButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 34,
                MinWidth = 110,
                FontSize = 14,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static Style CreateNavigationItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 9, 10, 9)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        private static Slider CreateSlider(string label, double value, double minimum, double maximum, string format, Panel parent, out TextBlock valueText)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            grid.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });

            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            valueText = new TextBlock
            {
                Text = format.EndsWith("dB", StringComparison.Ordinal)
                    ? FormatDb(value)
                    : format.EndsWith("ms", StringComparison.Ordinal)
                        ? FormatMs(value)
                        : value.ToString(format, CultureInfo.InvariantCulture),
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            parent.Children.Add(grid);
            return slider;
        }

        private static string FormatDb(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        private static string FormatMs(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        private static SolidColorBrush Brush(byte red, byte green, byte blue)
        {
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }
    }
}
