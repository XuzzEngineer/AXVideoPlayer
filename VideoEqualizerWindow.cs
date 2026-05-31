using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AXVideoPlayer
{
    internal sealed class VideoEqualizerWindow : Window
    {
        private readonly Action<bool> _setEnabled;
        private readonly Action<string, double> _setValue;
        private readonly Action _reset;
        private bool _isUpdating;

        public VideoEqualizerWindow(
            bool enabled,
            double brightness,
            double contrast,
            double saturation,
            double gamma,
            double hue,
            Action<bool> setEnabled,
            Action<string, double> setValue,
            Action reset)
        {
            _setEnabled = setEnabled;
            _setValue = setValue;
            _reset = reset;

            Title = "Video Equalizer";
            Width = 520;
            Height = 430;
            MinWidth = 440;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));

            Content = BuildContent(enabled, brightness, contrast, saturation, gamma, hue);
        }

        private UIElement BuildContent(bool enabled, double brightness, double contrast, double saturation, double gamma, double hue)
        {
            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = "Video Equalizer",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var enableBox = new CheckBox
            {
                Content = "Enable video equalizer",
                Foreground = Brushes.White,
                IsChecked = enabled,
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setEnabled(true);
            enableBox.Unchecked += (_, _) => _setEnabled(false);
            panel.Children.Add(enableBox);

            Slider brightnessSlider = AddSlider(panel, "Brightness", "brightness", brightness, 0, 2);
            Slider contrastSlider = AddSlider(panel, "Contrast", "contrast", contrast, 0, 2);
            Slider saturationSlider = AddSlider(panel, "Saturation", "saturation", saturation, 0, 3);
            Slider gammaSlider = AddSlider(panel, "Gamma", "gamma", gamma, 0.1, 3);
            Slider hueSlider = AddSlider(panel, "Hue", "hue", hue, -180, 180);

            var resetButton = new Button
            {
                Content = "Reset",
                Width = 92,
                Height = 32,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            resetButton.Click += (_, _) =>
            {
                _isUpdating = true;
                brightnessSlider.Value = 1.0;
                contrastSlider.Value = 1.0;
                saturationSlider.Value = 1.0;
                gammaSlider.Value = 1.0;
                hueSlider.Value = 0.0;
                _isUpdating = false;
                _reset();
            };
            panel.Children.Add(resetButton);

            return panel;
        }

        private Slider AddSlider(Panel parent, string label, string setting, double value, double minimum, double maximum)
        {
            Slider slider = CreateSlider(label, value, minimum, maximum, parent, out TextBlock valueText);
            slider.ValueChanged += (_, _) =>
            {
                valueText.Text = FormatValue(slider.Value);
                if (!_isUpdating)
                    _setValue(setting, slider.Value);
            };

            return slider;
        }

        private static Slider CreateSlider(string label, double value, double minimum, double maximum, Panel parent, out TextBlock valueText)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });

            grid.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
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
                Text = FormatValue(value),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            parent.Children.Add(grid);
            return slider;
        }

        private static string FormatValue(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
