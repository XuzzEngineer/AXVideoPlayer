using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AXVideoPlayer
{
    internal sealed class AudioEqualizerWindow : Window
    {
        private readonly Action<bool> _setEnabled;
        private readonly Action<double> _setPreamp;
        private readonly Action<int, double> _setBand;
        private readonly Action _reset;
        private bool _isUpdating;

        public AudioEqualizerWindow(
            bool enabled,
            double preamp,
            double[] bands,
            Action<bool> setEnabled,
            Action<double> setPreamp,
            Action<int, double> setBand,
            Action reset)
        {
            _setEnabled = setEnabled;
            _setPreamp = setPreamp;
            _setBand = setBand;
            _reset = reset;

            Title = "Audio Equalizer";
            Width = 560;
            Height = 520;
            MinWidth = 460;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));

            var root = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = BuildContent(enabled, preamp, bands)
            };

            Content = root;
        }

        private UIElement BuildContent(bool enabled, double preamp, double[] bands)
        {
            var panel = new StackPanel { Margin = new Thickness(16) };

            var heading = new TextBlock
            {
                Text = "Audio Equalizer",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(heading);

            var enableBox = new CheckBox
            {
                Content = "Enable audio equalizer",
                Foreground = Brushes.White,
                IsChecked = enabled,
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setEnabled(true);
            enableBox.Unchecked += (_, _) => _setEnabled(false);
            panel.Children.Add(enableBox);

            Slider preampSlider = CreateSlider("Preamp", preamp, -20, 20, panel, out TextBlock preampValueText);
            preampSlider.ValueChanged += (_, _) =>
            {
                UpdateValueText(preampValueText, preampSlider.Value);
                if (!_isUpdating)
                    _setPreamp(preampSlider.Value);
            };

            string[] labels = { "60 Hz", "170 Hz", "310 Hz", "600 Hz", "1 kHz", "3 kHz", "6 kHz", "12 kHz", "14 kHz", "16 kHz" };

            for (int i = 0; i < labels.Length; i++)
            {
                int bandIndex = i;
                double value = i < bands.Length ? bands[i] : 0;
                Slider slider = CreateSlider(labels[i], value, -20, 20, panel, out TextBlock valueText);
                slider.ValueChanged += (_, _) =>
                {
                    UpdateValueText(valueText, slider.Value);
                    if (!_isUpdating)
                        _setBand(bandIndex, slider.Value);
                };
            }

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
                preampSlider.Value = 0;
                foreach (Slider slider in FindSliders(panel))
                    slider.Value = 0;
                _isUpdating = false;
                _reset();
            };
            panel.Children.Add(resetButton);

            return panel;
        }

        private static Slider CreateSlider(string label, double value, double minimum, double maximum, Panel parent, out TextBlock valueText)
        {
            var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });

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
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            UpdateValueText(valueText, value);
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            parent.Children.Add(grid);
            return slider;
        }

        private static void UpdateValueText(TextBlock textBlock, double value)
        {
            textBlock.Text = value.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        private static IEnumerable<Slider> FindSliders(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is Slider slider)
                    yield return slider;

                foreach (Slider nested in FindSliders(child))
                    yield return nested;
            }
        }
    }
}
