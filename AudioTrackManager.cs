using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace AXVideoPlayer
{
    internal sealed class AudioTrackOption
    {
        public AudioTrackOption(int id, string name, bool isSelected)
        {
            Id = id;
            Name = name;
            IsSelected = isSelected;
        }

        public int Id { get; }
        public string Name { get; }
        public bool IsSelected { get; }
    }

    internal sealed class AudioTrackManager
    {
        private readonly MediaPlayer _mediaPlayer;
        private int? _preferredTrackId;
        private bool _audioDisabled;

        public bool IsAudioDisabled => _audioDisabled;

        public AudioTrackManager(MediaPlayer mediaPlayer)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
        }

        public void ResetForNewMedia()
        {
            _preferredTrackId = null;
            _audioDisabled = false;
            _mediaPlayer.Mute = false;
        }

        public void PopulateAudioTracksMenu(MenuItem audioTracksMenu, RoutedEventHandler clickHandler, Func<int> getRestoreVolume)
        {
            audioTracksMenu.Items.Clear();

            try
            {
                int currentTrack = _mediaPlayer.AudioTrack;
                bool disabled = IsAudioDisabled;

                var disableItem = new MenuItem
                {
                    Header = "Disable audio",
                    Tag = -1,
                    IsCheckable = true,
                    IsChecked = disabled
                };
                disableItem.Click += clickHandler;
                audioTracksMenu.Items.Add(disableItem);
                audioTracksMenu.Items.Add(new Separator());

                bool addedAny = false;
                var tracks = _mediaPlayer.AudioTrackDescription;

                if (tracks != null)
                {
                    foreach (var track in tracks)
                    {
                        if (track.Id < 0)
                            continue;

                        string name = string.IsNullOrWhiteSpace(track.Name)
                            ? $"Track {track.Id}"
                            : track.Name;

                        var item = new MenuItem
                        {
                            Header = name,
                            Tag = track.Id,
                            IsCheckable = true,
                            IsChecked = !disabled && currentTrack == track.Id
                        };

                        item.Click += clickHandler;
                        audioTracksMenu.Items.Add(item);
                        addedAny = true;
                    }
                }

                if (!addedAny)
                    audioTracksMenu.Items.Add(new MenuItem { Header = "No audio tracks found", IsEnabled = false });
            }
            catch
            {
                audioTracksMenu.Items.Add(new MenuItem { Header = "Audio tracks not available yet", IsEnabled = false });
            }
        }

        public IReadOnlyList<AudioTrackOption> GetAudioTracks()
        {
            var options = new List<AudioTrackOption>();

            try
            {
                int currentTrack = _mediaPlayer.AudioTrack;
                var tracks = _mediaPlayer.AudioTrackDescription;

                if (tracks == null)
                    return options;

                foreach (var track in tracks)
                {
                    if (track.Id < 0)
                        continue;

                    string name = string.IsNullOrWhiteSpace(track.Name)
                        ? $"Track {track.Id}"
                        : track.Name;

                    options.Add(new AudioTrackOption(track.Id, name, !_audioDisabled && currentTrack == track.Id));
                }
            }
            catch
            {
            }

            return options;
        }

        public void SelectTrack(int trackId, int restoreVolume, Action refreshMenu)
        {
            restoreVolume = Math.Max(1, Math.Min(100, restoreVolume));

            if (trackId < 0)
            {
                DisableAudio(refreshMenu);
                return;
            }

            _audioDisabled = false;
            _preferredTrackId = trackId;

            _mediaPlayer.Mute = false;
            _mediaPlayer.Volume = restoreVolume;

            ApplyTrackRepeatedly(trackId, restoreVolume, refreshMenu);
        }

        public void ReapplyPreferredState(int restoreVolume, Action refreshMenu)
        {
            restoreVolume = Math.Max(1, Math.Min(100, restoreVolume));

            if (_audioDisabled)
            {
                DisableAudio(refreshMenu);
                return;
            }

            if (_preferredTrackId.HasValue && _preferredTrackId.Value >= 0)
            {
                _mediaPlayer.Mute = false;
                _mediaPlayer.Volume = restoreVolume;
                ApplyTrackRepeatedly(_preferredTrackId.Value, restoreVolume, refreshMenu);
            }
        }

        private void DisableAudio(Action refreshMenu)
        {
            _audioDisabled = true;
            _preferredTrackId = -1;

            _mediaPlayer.Volume = 0;
            _mediaPlayer.Mute = true;

            try
            {
                _mediaPlayer.SetAudioTrack(-1);
            }
            catch
            {
                // Mute is the reliable fallback.
            }

            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                attempts++;

                if (!_audioDisabled)
                {
                    timer.Stop();
                    refreshMenu();
                    return;
                }

                try
                {
                    _mediaPlayer.SetAudioTrack(-1);
                }
                catch
                {
                    // Mute and zero volume remain the fallback.
                }

                _mediaPlayer.Volume = 0;
                _mediaPlayer.Mute = true;

                if (attempts >= 15)
                {
                    timer.Stop();
                    refreshMenu();
                }
            };
            timer.Start();

            refreshMenu();
        }

        private void ApplyTrackRepeatedly(int trackId, int restoreVolume, Action refreshMenu)
        {
            int attempts = 0;

            void ApplyOnce()
            {
                try
                {
                    _mediaPlayer.Mute = false;
                    _mediaPlayer.Volume = restoreVolume;
                    _mediaPlayer.SetAudioTrack(trackId);
                }
                catch
                {
                    // VLC may reject the switch while track info is still initializing.
                }
            }

            ApplyOnce();

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            timer.Tick += (_, _) =>
            {
                attempts++;

                if (_audioDisabled || _preferredTrackId != trackId)
                {
                    timer.Stop();
                    refreshMenu();
                    return;
                }

                ApplyOnce();

                if (_mediaPlayer.AudioTrack == trackId || attempts >= 12)
                {
                    timer.Stop();
                    refreshMenu();
                }
            };

            timer.Start();
            refreshMenu();
        }
    }
}
