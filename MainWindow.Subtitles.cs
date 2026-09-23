using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;

namespace AXVideoPlayer;

public partial class MainWindow
{
	private void LoadSubtitle_Click(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		_singleClickTimer.Stop();
		_lastVideoClickTime = DateTime.MinValue;
		if (_currentIndex < 0 || _currentIndex >= _playlist.Count)
		{
			return;
		}
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = "Load Subtitle File",
			Filter = "Subtitle Files|*.srt;*.ass;*.ssa;*.vtt|All Files|*.*"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			if (!File.Exists(openFileDialog.FileName))
			{
				MessageBox.Show("The selected subtitle file does not exist.", "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			_selectedSubtitlePath = openFileDialog.FileName;
			_selectedEmbeddedSubtitleTrackId = -1;
			_lastSubtitlePath = openFileDialog.FileName;
			TryApplySubtitleToCurrentPlayer(showError: true);
		}
	}

	private void TryApplySubtitleToCurrentPlayer(bool showError)
	{
		if (_mediaPlayer == null)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(_selectedSubtitlePath))
		{
			if (_selectedEmbeddedSubtitleTrackId >= 0)
				ApplySelectedEmbeddedSubtitleTrack(showError: false);
			else
				ForceDisableSubtitles();
			return;
		}
		try
		{
			if (File.Exists(_selectedSubtitlePath))
			{
				string absoluteUri = new Uri(_selectedSubtitlePath).AbsoluteUri;
				bool flag = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, absoluteUri, select: true);
				if (!flag)
				{
					App.LogException(new InvalidOperationException("Could not load subtitle file: " + _selectedSubtitlePath));
					if (showError)
						MessageBox.Show("The subtitle file could not be loaded. Try converting it to UTF-8 .srt or .vtt.", "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				}
			}
			else
			{
				App.LogException(new FileNotFoundException("Subtitle file is missing.", _selectedSubtitlePath));
				if (showError)
					MessageBox.Show("The subtitle file no longer exists.", "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
		catch (Exception ex)
		{
			App.LogException(ex);
			if (showError)
			{
				MessageBox.Show("The subtitle file could not be loaded.\n\n" + ex.Message, "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
	}

	private void ForceDisableSubtitles()
	{
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Expected O, but got Unknown
		if (_mediaPlayer == null)
		{
			return;
		}
		TrySetSubtitleTrack(-1);
		int attempts = 0;
		DispatcherTimer timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(150L)
		};
		timer.Tick += delegate
		{
			if (_selectedEmbeddedSubtitleTrackId >= 0 || !string.IsNullOrWhiteSpace(_selectedSubtitlePath))
			{
				timer.Stop();
				return;
			}
			attempts++;
			TrySetSubtitleTrack(-1);
			if (attempts >= 8)
			{
				timer.Stop();
			}
		};
		timer.Start();
	}

	private bool TrySetSubtitleTrack(int trackId)
	{
		if (_mediaPlayer == null)
		{
			return false;
		}
		try
		{
			return _mediaPlayer.SetSpu(trackId);
		}
		catch (Exception ex)
		{
			App.LogException(ex);
			return false;
		}
	}

	private void SubtitleLocationBottom_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting("bottom", _subtitleSize, _subtitleColor);
	}

	private void SubtitleLocationMiddle_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting("middle", _subtitleSize, _subtitleColor);
	}

	private void SubtitleLocationTop_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting("top", _subtitleSize, _subtitleColor);
	}

	private void SubtitleSizeSmall_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting(_subtitleLocation, "small", _subtitleColor);
	}

	private void SubtitleSizeMedium_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting(_subtitleLocation, "medium", _subtitleColor);
	}

	private void SubtitleSizeLarge_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting(_subtitleLocation, "large", _subtitleColor);
	}

	private void SubtitleColorWhite_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting(_subtitleLocation, _subtitleSize, "white");
	}

	private void SubtitleColorYellow_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting(_subtitleLocation, _subtitleSize, "yellow");
	}

	private void SubtitleColorBlue_Click(object sender, RoutedEventArgs e)
	{
		ApplySubtitleSetting(_subtitleLocation, _subtitleSize, "blue");
	}

	private void ApplySubtitleSetting(string location, string size, string color)
	{
		_subtitleLocation = location;
		_subtitleSize = size;
		_subtitleColor = color;
		SaveUserSettings();
		if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
		{
			long resumeTime = _mediaPlayer?.Time ?? 0;
			bool shouldPlay = _mediaPlayer?.IsPlaying ?? true;
			RecreatePlaybackEngineAndReplay(resumeTime, shouldPlay);
		}
	}

	private void DisableSubtitles_Click(object sender, RoutedEventArgs e)
	{
		if (!string.IsNullOrWhiteSpace(_selectedSubtitlePath))
		{
		_lastSubtitlePath = _selectedSubtitlePath;
		}
		_selectedSubtitlePath = null;
		_selectedEmbeddedSubtitleTrackId = -1;
		_subtitleTrackSelectionTimer?.Stop();
		ForceDisableSubtitles();
	}

	private void EnableSubtitles_Click(object sender, RoutedEventArgs e)
	{
		if (HasLoadedVideo())
		{
			int firstEmbeddedSubtitleTrackId = GetFirstEmbeddedSubtitleTrackId();
			if (firstEmbeddedSubtitleTrackId >= 0)
			{
				_selectedSubtitlePath = null;
				_selectedEmbeddedSubtitleTrackId = firstEmbeddedSubtitleTrackId;
				ApplySelectedEmbeddedSubtitleTrack(showError: true);
			}
			else
			{
				MessageBox.Show("No embedded subtitle track was found in this video.", "Subtitles", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
		}
	}

	private int GetFirstEmbeddedSubtitleTrackId()
	{
		if (_mediaPlayer?.SpuDescription == null)
		{
			return -1;
		}
		TrackDescription[] spuDescription = _mediaPlayer.SpuDescription;
		for (int i = 0; i < spuDescription.Length; i++)
		{
			TrackDescription trackDescription = spuDescription[i];
			if (trackDescription.Id >= 0)
			{
				return trackDescription.Id;
			}
		}
		return -1;
	}

}
