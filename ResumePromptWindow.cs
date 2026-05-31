using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AXVideoPlayer
{
    internal sealed class ResumePromptWindow : Window
    {
        private readonly DispatcherTimer _timer;

        public ResumePromptWindow(string timeText, Action resume)
        {
            Title = "Resume Playback";
            Width = 360;
            Height = 150;
            MinWidth = 360;
            MinHeight = 150;
            MaxWidth = 360;
            MaxHeight = 150;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Topmost = true;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Resume from " + timeText + "?",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var yesButton = new Button
            {
                Content = "Yes",
                Width = 82,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0)
            };
            yesButton.Click += (_, _) =>
            {
                _timer.Stop();
                resume();
                Close();
            };

            var noButton = new Button
            {
                Content = "No",
                Width = 82,
                Height = 30
            };
            noButton.Click += (_, _) =>
            {
                _timer.Stop();
                Close();
            };

            buttons.Children.Add(yesButton);
            buttons.Children.Add(noButton);
            panel.Children.Add(buttons);
            Content = panel;

            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Close();
            };
            Loaded += (_, _) => _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }
    }
}
