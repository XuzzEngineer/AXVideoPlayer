using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AXVideoPlayer
{
    internal sealed class AiSuperResolutionProgressWindow : Window
    {
        private readonly TextBlock _statusText;
        private readonly TextBlock _percentText;
        private readonly ProgressBar _progressBar;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public AiSuperResolutionProgressWindow(CancellationTokenSource cancellationTokenSource)
        {
            _cancellationTokenSource = cancellationTokenSource;
            Title = "AI Super Resolution";
            Width = 520;
            Height = 190;
            MinWidth = 460;
            MinHeight = 170;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Foreground = Brushes.White;

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            root.Children.Add(new TextBlock
            {
                Text = "AI Super Resolution",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _statusText = new TextBlock
            {
                Text = "Preparing...",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(_statusText, 1);
            root.Children.Add(_statusText);

            var bottom = new DockPanel { LastChildFill = true };
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 92,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            cancel.Click += (_, _) =>
            {
                cancel.IsEnabled = false;
                SetStatus("Canceling...");
                _cancellationTokenSource.Cancel();
            };
            DockPanel.SetDock(cancel, Dock.Right);
            bottom.Children.Add(cancel);

            _percentText = new TextBlock
            {
                Text = "0%",
                Width = 58,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 5, 12, 0)
            };
            DockPanel.SetDock(_percentText, Dock.Right);
            bottom.Children.Add(_percentText);

            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                IsIndeterminate = false,
                Height = 14,
                Margin = new Thickness(0, 9, 14, 0)
            };
            bottom.Children.Add(_progressBar);
        }

        public void SetStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                if (AiSuperResolutionProgress.TryParse(status, out double percent, out string message))
                {
                    _progressBar.IsIndeterminate = false;
                    _progressBar.Value = percent;
                    _percentText.Text = percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                    _statusText.Text = message;
                }
                else
                {
                    _statusText.Text = string.IsNullOrWhiteSpace(status) ? "Working..." : status;
                }
            }
            else
            {
                Dispatcher.BeginInvoke((Action)(() => SetStatus(status)));
            }
        }
    }
}
