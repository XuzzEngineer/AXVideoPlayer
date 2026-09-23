using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AXVideoPlayer;

public partial class MainWindow
{
    private DispatcherTimer? _subtitleTrackSelectionTimer;

    private void PopulateSubtitleTracksMenu(MenuItem menu)
    {
        menu.Items.Clear();
        if (_mediaPlayer == null || !HasLoadedVideo())
        {
            menu.Items.Add(new MenuItem { Header = "Open a video to see its tracks", IsEnabled = false });
            return;
        }

        var off = new MenuItem { Header = "Off", IsCheckable = true, IsChecked = _selectedEmbeddedSubtitleTrackId < 0 && string.IsNullOrWhiteSpace(_selectedSubtitlePath) };
        off.Click += DisableSubtitles_Click;
        menu.Items.Add(off);
        menu.Items.Add(new Separator());

        bool found = false;
        try
        {
            var tracks = _mediaPlayer.SpuDescription;
            if (tracks != null)
            foreach (var track in tracks)
            {
                if (track.Id < 0) continue;
                found = true;
                int id = track.Id;
                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(track.Name) ? $"Track {id}" : $"{track.Name} (Track {id})",
                    IsCheckable = true,
                    IsChecked = _selectedEmbeddedSubtitleTrackId == id
                };
                item.Click += (_, _) =>
                {
                    _selectedSubtitlePath = null;
                    _selectedEmbeddedSubtitleTrackId = id;
                    ApplySelectedEmbeddedSubtitleTrack(showError: true);
                };
                menu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            App.LogException(ex);
        }
        if (!found)
            menu.Items.Add(new MenuItem { Header = "No embedded tracks found", IsEnabled = false });
    }

    private void ApplySelectedEmbeddedSubtitleTrack(bool showError)
    {
        _subtitleTrackSelectionTimer?.Stop();
        _subtitleTrackSelectionTimer = null;
        if (_mediaPlayer == null || _selectedEmbeddedSubtitleTrackId < 0) return;

        var player = _mediaPlayer;
        int id = _selectedEmbeddedSubtitleTrackId;
        if (TrySetSubtitleTrack(id)) return;

        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _subtitleTrackSelectionTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_mediaPlayer, player) || _selectedEmbeddedSubtitleTrackId != id)
            {
                timer.Stop();
                return;
            }
            attempts++;
            if (TrySetSubtitleTrack(id))
            {
                timer.Stop();
                _subtitleTrackSelectionTimer = null;
            }
            else if (attempts >= 10)
            {
                timer.Stop();
                _subtitleTrackSelectionTimer = null;
                App.LogException(new InvalidOperationException($"Could not select subtitle track {id}."));
                _selectedEmbeddedSubtitleTrackId = -1;
                if (showError)
                    System.Windows.MessageBox.Show("This subtitle track could not be selected.", "Subtitles");
            }
        };
        timer.Start();
    }
}
