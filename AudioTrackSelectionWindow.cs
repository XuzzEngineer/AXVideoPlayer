using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AXVideoPlayer
{
    internal sealed class AudioTrackSelectionWindow : Window
    {
        private readonly Func<IReadOnlyList<AudioTrackOption>> _loadTracks;
        private readonly Func<bool> _isAudioDisabled;
        private readonly Action<int> _selectTrack;
        private readonly StackPanel _trackList = new();
        private bool _isRefreshing;

        public AudioTrackSelectionWindow(
            Func<IReadOnlyList<AudioTrackOption>> loadTracks,
            Func<bool> isAudioDisabled,
            Action<int> selectTrack)
        {
            _loadTracks = loadTracks;
            _isAudioDisabled = isAudioDisabled;
            _selectTrack = selectTrack;

            Title = "Audio Track";
            Width = 420;
            Height = 360;
            MinWidth = 360;
            MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));

            Content = BuildContent();
            RefreshTracks();
        }

        public void RefreshTracks()
        {
            _isRefreshing = true;
            _trackList.Children.Clear();

            AddRadioButton("Disable audio", -1, _isAudioDisabled());

            IReadOnlyList<AudioTrackOption> tracks = _loadTracks();
            if (tracks.Count == 0)
            {
                _trackList.Children.Add(new TextBlock
                {
                    Text = "No audio tracks found yet.",
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 12, 0, 0)
                });
            }
            else
            {
                foreach (AudioTrackOption track in tracks)
                    AddRadioButton(track.Name, track.Id, track.IsSelected);
            }

            _isRefreshing = false;
        }

        private UIElement BuildContent()
        {
            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new TextBlock
            {
                Text = "Audio Track",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var refreshButton = new Button
            {
                Content = "Refresh",
                Width = 92,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0)
            };
            refreshButton.Click += (_, _) => RefreshTracks();
            DockPanel.SetDock(refreshButton, Dock.Bottom);
            root.Children.Add(refreshButton);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _trackList
            };
            root.Children.Add(scrollViewer);

            return root;
        }

        private void AddRadioButton(string label, int trackId, bool isSelected)
        {
            var radioButton = new RadioButton
            {
                Content = label,
                Tag = trackId,
                GroupName = "AudioTracks",
                Foreground = Brushes.White,
                IsChecked = isSelected,
                Margin = new Thickness(0, 5, 0, 5)
            };

            radioButton.Checked += (_, _) =>
            {
                if (_isRefreshing || radioButton.Tag is not int selectedTrackId)
                    return;

                _selectTrack(selectedTrackId);
            };

            _trackList.Children.Add(radioButton);
        }
    }
}
