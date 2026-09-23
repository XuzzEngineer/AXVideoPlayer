using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AXVideoPlayer;

public partial class MainWindow
{
	private void SaveCurrentPlaybackPosition()
	{
		if (_mediaPlayer != null && _currentIndex >= 0 && _currentIndex < _playlist.Count && _mediaPlayer.Length > 0)
		{
			long num = Math.Max(0L, _mediaPlayer.Time);
			long num2 = _mediaPlayer.Length - num;
			string videoPath = _playlist[_currentIndex];
			if (num < 5000 || num2 < 5000)
			{
				if (IsResumeRestorePending(videoPath))
				{
					return;
				}
				_playbackHistory.SetPosition(videoPath, 0L);
			}
			else
			{
				_playbackHistory.SetPosition(videoPath, num);
			}
		}
	}

	private void SaveCurrentPlaybackPositionThrottled()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!((utcNow - _lastPlaybackHistorySaveUtc).TotalSeconds < 2.0))
		{
			_lastPlaybackHistorySaveUtc = utcNow;
			SaveCurrentPlaybackPosition();
		}
	}

	private int GetResumePromptTimeoutSeconds()
	{
		return Math.Max(1, Math.Min(60, _userSettings.ResumePromptTimeoutSeconds));
	}

	private void SetResumePromptTimeoutSeconds(int seconds)
	{
		int clampedSeconds = Math.Max(1, Math.Min(60, seconds));
		if (_userSettings.ResumePromptTimeoutSeconds == clampedSeconds)
		{
			return;
		}
		_userSettings.ResumePromptTimeoutSeconds = clampedSeconds;
		SaveUserSettings();
	}

	private void ShowResumePromptIfNeeded(string filePath)
	{
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Expected O, but got Unknown
		if (!_playbackHistory.RememberEnabled || _mediaPlayer == null)
		{
			return;
		}
		long position = _playbackHistory.GetPosition(filePath);
		if (position < 5000)
		{
			return;
		}
		_resumePromptFilePath = filePath;
		_resumePromptTime = position;
		ResumePromptText.Text = "Resume from " + FormatTime(position) + "?";
		SuspendVideoClickShortcuts(1200);
		ResumePromptPanel.BeginAnimation(UIElement.OpacityProperty, null);
		ResumePromptPanel.Opacity = 1.0;
		PositionResumePromptPopup();
		ResumePromptPopup.IsOpen = true;
		KeepResumePromptInPlayerLayer();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(delegate
		{
			PositionResumePromptPopup();
			KeepResumePromptInPlayerLayer();
		}), (DispatcherPriority)6, Array.Empty<object>());
		DispatcherTimer? resumePromptTimer = _resumePromptTimer;
		if (resumePromptTimer != null)
		{
			resumePromptTimer.Stop();
		}
		_resumePromptTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(GetResumePromptTimeoutSeconds())
		};
		_resumePromptTimer.Tick += delegate
		{
			DispatcherTimer? resumePromptTimer2 = _resumePromptTimer;
			if (resumePromptTimer2 != null)
			{
				resumePromptTimer2.Stop();
			}
			FadeResumePrompt();
		};
		_resumePromptTimer.Start();
	}

	private void ResumePromptYes_Click(object sender, RoutedEventArgs e)
	{
		SuspendVideoClickShortcuts(1200);
		e.Handled = true;
		if (!string.IsNullOrWhiteSpace(_resumePromptFilePath))
		{
			SeekToResumeTime(_resumePromptFilePath, _resumePromptTime);
		}
		HideResumePrompt();
	}

	private void ResumePromptNo_Click(object sender, RoutedEventArgs e)
	{
		SuspendVideoClickShortcuts(1200);
		e.Handled = true;
		HideResumePrompt();
	}

	private void FadeResumePrompt()
	{
		DoubleAnimation doubleAnimation = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(350L));
		doubleAnimation.Completed += delegate
		{
			HideResumePrompt();
		};
		ResumePromptPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
	}

	private void HideResumePrompt()
	{
		SuspendVideoClickShortcuts(500);
		DispatcherTimer? resumePromptTimer = _resumePromptTimer;
		if (resumePromptTimer != null)
		{
			resumePromptTimer.Stop();
		}
		ResumePromptPanel.BeginAnimation(UIElement.OpacityProperty, null);
		ResumePromptPanel.Opacity = 0.0;
		ResumePromptPopup.IsOpen = false;
		_resumePromptFilePath = null;
		_resumePromptTime = 0L;
	}

	private void PositionResumePromptPopup()
	{
		if (ResumePromptPopup == null || ResumePromptPanel == null || RootGrid == null || MainVideoArea == null)
		{
			return;
		}
		double left = 18.0;
		double top = 58.0;
		double videoHeight = MainVideoArea.ActualHeight;
		try
		{
			Point origin = MainVideoArea.TransformToAncestor(RootGrid).Transform(new Point(0.0, 0.0));
			left = Math.Max(8.0, origin.X + 18.0);
			double panelHeight = ResumePromptPanel.ActualHeight > 1.0 ? ResumePromptPanel.ActualHeight : 86.0;
			if (videoHeight > 1.0)
			{
				top = Math.Max(8.0, origin.Y + videoHeight - panelHeight - 16.0);
			}
			else
			{
				top = Math.Max(8.0, RootGrid.ActualHeight - panelHeight - 16.0);
			}
		}
		catch
		{
		}
		ResumePromptPopup.HorizontalOffset = left;
		ResumePromptPopup.VerticalOffset = top;
	}

	private bool IsResumePromptOpen()
	{
		return ResumePromptPopup != null && ResumePromptPopup.IsOpen;
	}

	private void KeepResumePromptInPlayerLayer()
	{
		if (ResumePromptPanel == null)
		{
			return;
		}

		if (PresentationSource.FromVisual(ResumePromptPanel) is HwndSource source && source.Handle != IntPtr.Zero)
		{
			SetWindowPos(source.Handle, HwndNotTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
		}
	}

	private void SeekToResumeTime(string filePath, long resumeTime)
	{
		SeekToMediaTime(filePath, resumeTime, minimumTime: 5000);
	}

	private void SeekToMediaTime(string filePath, long resumeTime, long minimumTime)
	{
		if (string.IsNullOrWhiteSpace(filePath) || resumeTime < minimumTime)
		{
			return;
		}

		CancelPendingResumeSeek();
		_pendingResumeSeekFilePath = filePath;
		_pendingResumeSeekTime = resumeTime;
		_pendingResumeSeekAttempts = 0;
		ApplyPendingResumeSeek();
		_pendingResumeSeekTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(200L)
		};
		_pendingResumeSeekTimer.Tick += delegate
		{
			ApplyPendingResumeSeek();
		};
		_pendingResumeSeekTimer.Start();
	}

	private void ApplyPendingResumeSeek()
	{
		if (string.IsNullOrWhiteSpace(_pendingResumeSeekFilePath) || _mediaPlayer == null || _currentIndex < 0 || _currentIndex >= _playlist.Count || !PlaybackRules.IsSameMedia(_playlist[_currentIndex], _pendingResumeSeekFilePath))
		{
			CancelPendingResumeSeek();
			return;
		}
		_pendingResumeSeekAttempts++;
		long length = _mediaPlayer.Length;
		if (length > 0)
		{
			long targetTime = PlaybackRules.ClampSeekTime(length, _pendingResumeSeekTime);
			try
			{
				_mediaPlayer.Time = targetTime;
			}
			catch (Exception ex)
			{
				if (_pendingResumeSeekAttempts == 1) App.LogException(ex);
			}
			try
			{
				_mediaPlayer.Position = (float)ClampDouble((double)targetTime / length, 0.0, 0.9999);
			}
			catch (Exception ex)
			{
				if (_pendingResumeSeekAttempts == 1) App.LogException(ex);
			}
			CurrentTimeText.Text = FormatTime(targetTime);
			PositionSlider.Value = ClampDouble((double)targetTime / length, 0.0, 1.0) * PositionSlider.Maximum;
			if (Math.Abs(_mediaPlayer.Time - targetTime) <= 1500 || _pendingResumeSeekAttempts >= 60)
			{
				CancelPendingResumeSeek();
			}
		}
		else if (_pendingResumeSeekAttempts >= 60)
		{
			CancelPendingResumeSeek();
		}
	}

	private void CancelPendingResumeSeek()
	{
		DispatcherTimer? timer = _pendingResumeSeekTimer;
		if (timer != null)
		{
			timer.Stop();
		}
		_pendingResumeSeekTimer = null;
		_pendingResumeSeekFilePath = null;
		_pendingResumeSeekTime = 0L;
		_pendingResumeSeekAttempts = 0;
	}

}
