using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AXVideoPlayer;

public partial class MainWindow
{
	private void Open_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = "Open Music or Video File",
			Filter = "Music and Video Files|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.rm;*.rmvb;*.mpg;*.mpeg;*.mpe;*.m2v;*.ts;*.m2ts;*.mts;*.vob;*.ogv;*.ogm;*.asf;*.divx;*.f4v;*.3gp;*.3g2;*.mxf;*.dv|All Files|*.*",
			Multiselect = true
		};
		if (openFileDialog.ShowDialog() == true)
		{
			int num = AddFilesToPlaylist(openFileDialog.FileNames);
			if (num >= 0)
			{
				PlayFromPlaylist(num, keepCurrentSubtitle: false);
			}
		}
	}

	private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = "Add Music or Videos to Playlist",
			Filter = "Music and Video Files|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.rm;*.rmvb;*.mpg;*.mpeg;*.mpe;*.m2v;*.ts;*.m2ts;*.mts;*.vob;*.ogv;*.ogm;*.asf;*.divx;*.f4v;*.3gp;*.3g2;*.mxf;*.dv|All Files|*.*",
			Multiselect = true
		};
		if (openFileDialog.ShowDialog() == true)
		{
			AddFilesToPlaylist(openFileDialog.FileNames);
		}
	}

	private int AddFilesToPlaylist(IEnumerable<string> files)
	{
		int num = -1;
		foreach (string file in files)
		{
			if (File.Exists(file) && IsSupportedMediaFile(file) && !_playlist.Any((string existing) => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
			{
				_playlist.Add(file);
				_playlistItems[file] = MediaPlaylistItem.Create(file, IsSupportedAudioFile(file));
				if (num < 0)
				{
					num = _playlist.Count - 1;
				}
			}
		}
		UpdatePlaylistViews();
		PersistPlaylistIfEnabled();
		return num;
	}

	private void ScanPlaylistFolder_Click(object sender, RoutedEventArgs e)
	{
		if (!HasLoadedVideo() || _currentIndex < 0 || _currentIndex >= _playlist.Count)
		{
			return;
		}
		string directoryName = Path.GetDirectoryName(_playlist[_currentIndex]);
		if (!string.IsNullOrWhiteSpace(directoryName) && Directory.Exists(directoryName))
		{
			string[] files = Directory.EnumerateFiles(directoryName).Where(IsSupportedMediaFile).OrderBy<string, string>(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			int num = AddFilesToPlaylist(files);
			if (num >= 0)
			{
				PlaylistBox.SelectedIndex = _currentIndex;
			}
			UpdateScanButtonState();
		}
	}

	private void UpdateScanButtonState()
	{
		if (ScanPlaylistFolderButton != null)
		{
			ScanPlaylistFolderButton.IsEnabled = HasLoadedVideo();
		}
		if (RemovePlaylistItemButton != null)
		{
			Button removePlaylistItemButton = RemovePlaylistItemButton;
			ListBox playlistBox = PlaylistBox;
			removePlaylistItemButton.IsEnabled = playlistBox != null && playlistBox.SelectedIndex >= 0;
		}
	}

	public void OpenFilesFromCommandLine(IEnumerable<string> files)
	{
		int firstAddedIndex = AddFilesToPlaylist(files);
		int firstIndex = firstAddedIndex >= 0 ? firstAddedIndex : FindFirstPlaylistIndex(files);
		if (firstIndex >= 0)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				PlayFromPlaylist(firstIndex, keepCurrentSubtitle: false);
			}, (DispatcherPriority)4, Array.Empty<object>());
		}
	}

	private static bool IsSupportedVideoFile(string filePath)
	{
		switch (Path.GetExtension(filePath).ToLowerInvariant())
		{
		case ".mp4":
		case ".mkv":
		case ".avi":
		case ".mov":
		case ".wmv":
		case ".flv":
		case ".webm":
		case ".m4v":
		case ".rm":
		case ".rmvb":
		case ".mpg":
		case ".mpeg":
		case ".mpe":
		case ".m2v":
		case ".ts":
		case ".m2ts":
		case ".mts":
		case ".vob":
		case ".ogv":
		case ".ogm":
		case ".asf":
		case ".divx":
		case ".f4v":
		case ".3gp":
		case ".3g2":
		case ".mxf":
		case ".dv":
			return true;
		default:
			return false;
		}
	}

	private static bool IsSupportedAudioFile(string filePath)
	{
		switch (Path.GetExtension(filePath).ToLowerInvariant())
		{
		case ".mp3":
		case ".flac":
		case ".wav":
		case ".m4a":
		case ".aac":
		case ".ogg":
		case ".opus":
		case ".wma":
			return true;
		default:
			return false;
		}
	}

	private static bool IsSupportedMediaFile(string filePath) => IsSupportedVideoFile(filePath) || IsSupportedAudioFile(filePath);

	private int FindFirstPlaylistIndex(IEnumerable<string> files)
	{
		foreach (string file in files)
		{
			int index = _playlist.FindIndex(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase));
			if (index >= 0)
				return index;
		}
		return -1;
	}

	private void PersistPlaylistIfEnabled() => _playbackHistory.SetPlaylist(_playlist);

	private void UpdatePlaylistViews()
	{
		if (PlaylistBox == null || MusicTrackList == null)
			return;

		_isSynchronizingPlaylistSelection = true;
		try
		{
			PlaylistBox.Items.Clear();
			MusicTrackList.Items.Clear();
			foreach (string path in _playlist)
			{
				if (!_playlistItems.TryGetValue(path, out MediaPlaylistItem? item))
				{
					item = MediaPlaylistItem.Create(path, IsSupportedAudioFile(path));
					_playlistItems[path] = item;
				}
				PlaylistBox.Items.Add(item);
				MusicTrackList.Items.Add(item);
			}
			PlaylistBox.SelectedIndex = _currentIndex;
			MusicTrackList.SelectedIndex = _currentIndex;
		}
		finally
		{
			_isSynchronizingPlaylistSelection = false;
		}
	}

	private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
	{
		SaveCurrentPlaybackPosition();
		if (_currentIndex >= 0 && _currentIndex < _playlist.Count && _currentMedia != null)
		{
			string text = _playlist[_currentIndex];
			_playlist.Clear();
			_playlistItems.Clear();
			_playlist.Add(text);
			_playlistItems[text] = MediaPlaylistItem.Create(text, IsSupportedAudioFile(text));
			_currentIndex = 0;
			UpdatePlaylistViews();
			PersistPlaylistIfEnabled();
			UpdateScanButtonState();
			UpdateLayoutState();
		}
		else
		{
			_playlist.Clear();
			_playlistItems.Clear();
			_currentIndex = -1;
			_selectedSubtitlePath = null;
			base.Title = AppDisplayName;
			EmptyStatePanel.Visibility = Visibility.Visible;
			UpdatePlaylistViews();
			PersistPlaylistIfEnabled();
			UpdateScanButtonState();
			UpdateLayoutState();
		}
	}

	private void RemovePlaylistItem_Click(object sender, RoutedEventArgs e)
	{
		int selectedIndex = PlaylistBox.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _playlist.Count)
		{
			return;
		}
		SaveCurrentPlaybackPosition();
		bool flag = selectedIndex == _currentIndex;
		_playlistItems.Remove(_playlist[selectedIndex]);
		_playlist.RemoveAt(selectedIndex);
		UpdatePlaylistViews();
		PersistPlaylistIfEnabled();
		if (!flag)
		{
			if (selectedIndex < _currentIndex)
			{
				_currentIndex--;
			}
			PlaylistBox.SelectedIndex = ((_currentIndex >= 0 && _currentIndex < PlaylistBox.Items.Count) ? _currentIndex : (-1));
			UpdateScanButtonState();
			return;
		}
		_currentIndex = -1;
		if (_playlist.Count > 0)
		{
			int index = Math.Min(selectedIndex, _playlist.Count - 1);
			PlayFromPlaylist(index, keepCurrentSubtitle: false);
			return;
		}
		StopPlayback();
		_selectedSubtitlePath = null;
		base.Title = AppDisplayName;
		EmptyStatePanel.Visibility = Visibility.Visible;
		UpdateScanButtonState();
		UpdateLayoutState();
	}

	private void PlaylistBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_isSynchronizingPlaylistSelection && PlaylistBox.SelectedIndex >= 0)
		{
			_isSynchronizingPlaylistSelection = true;
			MusicTrackList.SelectedIndex = PlaylistBox.SelectedIndex;
			_isSynchronizingPlaylistSelection = false;
		}
		UpdateScanButtonState();
	}

	private void MusicTrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_isSynchronizingPlaylistSelection && MusicTrackList.SelectedIndex >= 0)
		{
			_isSynchronizingPlaylistSelection = true;
			PlaylistBox.SelectedIndex = MusicTrackList.SelectedIndex;
			_isSynchronizingPlaylistSelection = false;
		}
		UpdateScanButtonState();
	}

	private void MusicTrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (MusicTrackList.SelectedIndex >= 0)
			PlayFromPlaylist(MusicTrackList.SelectedIndex, keepCurrentSubtitle: false);
	}

	private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (PlaylistBox.SelectedIndex >= 0)
		{
			PlayFromPlaylist(PlaylistBox.SelectedIndex, keepCurrentSubtitle: false);
		}
	}

	private void PlayFromPlaylist(int index, bool keepCurrentSubtitle)
	{
		if (_libVLC == null || _mediaPlayer == null || index < 0 || index >= _playlist.Count)
		{
			return;
		}
		SaveCurrentPlaybackPosition();
		CancelPendingResumeSeek();
		HideResumePrompt();
		if (PlaybackRules.ShouldClearSubtitle(_currentIndex, index, keepCurrentSubtitle))
		{
			_selectedSubtitlePath = null;
			_selectedEmbeddedSubtitleTrackId = -1;
		}
		_currentIndex = index;
		_playbackErrorShown = false;
		bool isAudio = IsSupportedAudioFile(_playlist[index]);
		_superResolutionWarningDismissed = false;
		PlaylistBox.SelectedIndex = index;
		MusicTrackList.SelectedIndex = index;
		UpdateScanButtonState();
		string text = _playlist[index];
		LoadEqualizerSettingsForCurrentVideo();
		_audioTrackManager?.ResetForNewMedia();
		_singleClickTimer.Stop();
		_lastVideoClickTime = DateTime.MinValue;
		Mouse.Capture(null);
		_currentMedia?.Dispose();
		_currentMedia = CreateMedia(text);
		_hasReachedEnd = false;
		_mediaPlayer.Media = _currentMedia;
		ApplyPlaybackOutputSettings();
		if (isAudio)
			UpdateMusicMode();
		else
			PrepareEmbeddedVideoSurface();
		_mediaPlayer.Play();
		_isPlaybackPaused = false;
		SetPlaybackTimerActive(active: true);
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (_mediaPlayer?.Media == _currentMedia && _mediaPlayer.Length > 0 && _mediaPlayer.Time <= 0)
			{
				_mediaPlayer.Time = 1L;
			}
		}, (DispatcherPriority)4, Array.Empty<object>());
		TryApplySubtitleToCurrentPlayer(showError: false);
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(BeginAudioTrackMenuRefresh), (DispatcherPriority)4, Array.Empty<object>());
		EmptyStatePanel.Visibility = Visibility.Collapsed;
		UpdateRenderSurfaceVisibility();
		PlayPauseButton.Content = IconPause;
		base.Title = AppDisplayName + " - " + Path.GetFileName(text);
		_isUiVisible = true;
		UpdateLayoutState();
		ShowResumePromptIfNeeded(text);
		Focus();
	}

}
