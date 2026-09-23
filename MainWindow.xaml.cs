using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using LibVLCSharp.WPF;
using Microsoft.Win32;

namespace AXVideoPlayer;

public partial class MainWindow : Window
{
	private const string AppDisplayName = "AX Video Player V2.3";

	private enum PlaybackOrderMode
	{
		Single,
		Shuffle,
		RepeatPlaylist,
		RepeatOne
	}

	private enum SubtitleVerticalPosition
	{
		Bottom,
		Middle,
		Top
	}

	private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;

		public uint mouseData;

		public uint flags;

		public uint time;

		public nint dwExtraInfo;
	}

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MONITORINFO
	{
		public int cbSize;

		public RECT rcMonitor;

		public RECT rcWork;

		public uint dwFlags;
	}

	private LibVLC? _libVLC;

	private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;

	private Media? _currentMedia;

	private readonly DispatcherTimer _timer;

	private readonly DispatcherTimer _fullscreenHideTimer;

	private readonly DispatcherTimer _singleClickTimer;

	private readonly List<string> _playlist = new List<string>();

	private readonly Dictionary<string, MediaPlaylistItem> _playlistItems = new(StringComparer.OrdinalIgnoreCase);

	private bool _isSynchronizingPlaylistSelection;

	private static readonly TimeSpan ActivePlaybackTimerInterval = TimeSpan.FromMilliseconds(300L);

	private static readonly TimeSpan IdlePlaybackTimerInterval = TimeSpan.FromMilliseconds(1000L);

	private const double AiSuperResolutionPreviewSeconds = 1.0;

	private const string VideoUpscalingModeCpu = "CPU";

	private const string VideoUpscalingModeGpu = "GPU";

	private const string IconPlaylist = "\u2630";

	private const string IconPlay = "\u25B6";

	private const string IconPause = "\u23F8";

	private const string IconPrevious = "\u23EE";

	private const string IconNext = "\u23ED";

	private const string IconStop = "\u23F9";

	private const string IconSingle = "\u2192";

	private const string IconShuffle = "\u21C4";

	private const string IconRepeatPlaylist = "\u21BB";

	private const string IconRepeatOne = "\u21BB\u00B9";

	private const string IconFullscreen = "\u26F6";

	private bool _isDraggingSlider = false;

	private bool _isFullscreen = false;

	private bool _isPlaylistVisible = false;

	private bool _manualUiHidden = false;

	private bool _isUiVisible = true;

	private bool _hasReachedEnd = false;

	private bool _isPlaybackPaused = false;

	private PlaybackOrderMode _playOrderMode = PlaybackOrderMode.Single;

	private readonly Random _shuffleRandom = new Random();

	private AudioTrackManager? _audioTrackManager;

	private ImageProcessingService? _imageProcessingService;

	private LiveSuperResolutionPipeline? _liveSuperResolutionPipeline;

	private AudioEqualizerService? _audioEqualizerService;

	private VideoAdjustmentService? _videoAdjustmentService;

	private readonly PlaybackHistoryService _playbackHistory = new PlaybackHistoryService();

	private readonly VideoEqualizerSettingsService _videoEqualizerSettings = new VideoEqualizerSettingsService();

	private readonly UserSettingsService _userSettingsService = new UserSettingsService();

	private UserSettings _userSettings = UserSettings.Default;

	private readonly HardwareCapabilityService _hardwareCapabilityService = new HardwareCapabilityService();

	private HardwareProfile _hardwareProfile = HardwareProfile.CreateFallback();

	private ResourcePlan _resourcePlan = ResourcePlan.CreateFallback();

	private AudioEqualizerWindow? _audioEqualizerWindow;

	private VideoEqualizerWindow? _videoEqualizerWindow;

	private AudioTrackSelectionWindow? _audioTrackSelectionWindow;

	private ToolsWindow? _toolsWindow;

	private OfflineSuperResolutionWindow? _offlineSuperResolutionWindow;

	private VideoEncodingWindow? _videoEncodingWindow;

	private bool _showProcessingStatistics;

	private bool _showVideoInfoOverlay;

	private bool _superResolutionWarningDismissed;

	private float _playbackSpeed = 1f;

	private bool _isLoadingUserSettings;

	private DispatcherTimer? _resumePromptTimer;

	private readonly DispatcherTimer _videoFilterRestartTimer;

	private DispatcherTimer? _pendingResumeSeekTimer;

	private string? _resumePromptFilePath;

	private long _resumePromptTime;

	private string? _pendingResumeSeekFilePath;

	private long _pendingResumeSeekTime;

	private int _pendingResumeSeekAttempts;

	private bool _playlistWasVisibleBeforeManualHide = false;

	private bool _playlistWasVisibleBeforeFullscreen = false;

	private int _currentIndex = -1;

	private DateTime _lastVideoClickTime = DateTime.MinValue;

	private Point _lastVideoClickPoint;

	private DateTime _lastVideoShortcutDownUtc = DateTime.MinValue;

	private Point _lastVideoShortcutDownScreenPoint;

	private DateTime _suppressVideoShortcutDownUntilUtc = DateTime.MinValue;

	private Point _lastMousePosition;

	private DateTime _lastFullscreenToggleUtc = DateTime.MinValue;

	private DateTime _videoClickShortcutsSuspendedUntilUtc = DateTime.MinValue;

	private DateTime _lastPlaybackHistorySaveUtc = DateTime.MinValue;

	private bool _isVideoCursorHidden;

	private int _volumePercent = 100;

	private int _volumeBeforeMute = 100;

	private bool _isUpdatingVolumeSlider;

	private bool _isLoadingEqualizerSettings;

	private bool _isUpdatingSuperResolutionControls;

	private bool _superResolutionControlsReady;

	private WindowStyle _windowStyleBeforeManualHide = WindowStyle.SingleBorderWindow;

	private WindowStyle _windowStyleBeforeFullscreen = WindowStyle.SingleBorderWindow;

	private WindowState _windowStateBeforeFullscreen = WindowState.Normal;

	private ResizeMode _resizeModeBeforeFullscreen = ResizeMode.CanResize;

	private Rect _windowBoundsBeforeFullscreen = Rect.Empty;

	private bool _subtitleMenuOpen = false;

	private nint _mouseHookHandle = IntPtr.Zero;

	private nint _mainWindowHandle = IntPtr.Zero;

	private LowLevelMouseProc? _mouseHookProc;

	private string? _selectedSubtitlePath = null;
	private int _selectedEmbeddedSubtitleTrackId = -1;
	private bool _playbackErrorShown;

	private string? _lastSubtitlePath = null;

	private string _subtitleLocation = "bottom";

	private string _subtitleSize = "medium";

	private string _subtitleColor = "white";

	private string _aspectRatio = "Default";

	private long _audioDelayMs = 0L;

	private bool _videoUpscalingEnabled;

	private bool _rtxVideoEnhancementEnabled;

	private string _aiSuperResolutionStatus = "AI SR ready";

	private bool _isAiSuperResolutionRunning;

	private bool _liveFrameGenerationEnabled;

	private int _aiSuperResolutionFrameRateMultiplier = 2;

	private bool _rememberUpscalingAndFrameGeneration;

	private string _resourceProfile = ResourceProfileNames.BalancedAuto;

	private readonly HashSet<string> _temporaryAiSuperResolutionFiles = new(StringComparer.OrdinalIgnoreCase);

	private string _videoUpscalingTarget = "Custom";

	private string _videoUpscalingMode = VideoUpscalingModeGpu;

	private double _videoUpscalingScale = 1.3;

	private double _videoUpscalingSharpness = 0.35;

	private const int WH_MOUSE_LL = 14;

	private const int WM_LBUTTONDOWN_LL = 513;

	private const int WM_NCHITTEST = 132;

	private const int HTCLIENT = 1;

	private const uint MONITOR_DEFAULTTONEAREST = 2;

	private static readonly nint HwndNotTopmost = new IntPtr(-2);

	private const uint SWP_NOSIZE = 1;

	private const uint SWP_NOMOVE = 2;

	private const uint SWP_NOACTIVATE = 16;


	public MainWindow()
	{
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Expected O, but got Unknown
		//IL_0198: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b0: Expected O, but got Unknown
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e5: Expected O, but got Unknown
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_020e: Expected O, but got Unknown
		_isLoadingUserSettings = true;
		_isUpdatingSuperResolutionControls = true;
		InitializeComponent();
		_isUpdatingSuperResolutionControls = false;
		_isLoadingUserSettings = false;
		LoadUserSettings();
		CleanupTemporaryAiSuperResolutionFiles();
		SyncSuperResolutionControlsFromSettings();
		ConfigureVolumeControl();
		LoadVideoEqualizerSettings();
		Core.Initialize();
		CreatePlaybackEngine();
		_timer = new DispatcherTimer
		{
			Interval = ActivePlaybackTimerInterval
		};
		_timer.Tick += Timer_Tick;
		_timer.Start();
		_fullscreenHideTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2L)
		};
		_fullscreenHideTimer.Tick += FullscreenHideTimer_Tick;
		_singleClickTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(520L)
		};
		_singleClickTimer.Tick += SingleClickTimer_Tick;
		_videoFilterRestartTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(350L)
		};
		_videoFilterRestartTimer.Tick += VideoFilterRestartTimer_Tick;
		ComponentDispatcher.ThreadFilterMessage += new ThreadMessageEventHandler(ComponentDispatcher_ThreadFilterMessage);
		_mainWindowHandle = new WindowInteropHelper(this).EnsureHandle();
		InstallMouseHook();
		base.Focusable = true;
		base.SizeChanged += delegate
		{
			PositionShowUiPopup();
			UpdatePopupSize();
			UpdateCompactControls();
		};
		Focus();
		base.Loaded += delegate
		{
			RestoreRememberedPlaylist();
			SyncSuperResolutionControlsFromSettings();
			ConfigureTopToolsMenu();
			ConfigureBottomSubtitleMenu();
			UpdatePlayOrderButton();
			ApplyPlaybackSpeedToComboBox();
			UpdateScanButtonState();
			UpdateCompactControls();
			NormalizeReadableUiLabels();
			_superResolutionControlsReady = true;
		};
	}

	private void RestoreRememberedPlaylist()
	{
		IReadOnlyList<string> rememberedPaths = _playbackHistory.GetPlaylist();
		if (rememberedPaths.Count == 0)
			return;

		AddFilesToPlaylist(rememberedPaths.Where(path => File.Exists(path) && IsSupportedMediaFile(path)));
		UpdatePlaylistViews();
	}

	private void ConfigureTopToolsMenu()
	{
	}

	private void ConfigureBottomSubtitleMenu()
	{
		MenuItem menuItem = FindMenuItemByHeader((DependencyObject)(object)ControlPanel, "Subtitles");
		if (menuItem == null)
		{
			return;
		}
		RemoveMenuItemByHeader(menuItem, "Enable subtitles");
		RemoveMenuItemByHeader(menuItem, "Disable subtitles");
		RemoveMenuItemByHeader(menuItem, "Subtitle track");
		RemoveTrailingSeparators(menuItem);
		MenuItem tracksMenu = new MenuItem { Header = "Subtitle track" };
		tracksMenu.Items.Add(new MenuItem { Header = "Open a video to see its tracks", IsEnabled = false });
		menuItem.Items.Add(tracksMenu);
		menuItem.Items.Add(new Separator());
		MenuItem menuItem2 = new MenuItem
		{
			Header = "Enable subtitles"
		};
		menuItem2.Click += EnableSubtitles_Click;
		menuItem.Items.Add(menuItem2);
		MenuItem menuItem3 = new MenuItem
		{
			Header = "Disable subtitles"
		};
		menuItem3.Click += DisableSubtitles_Click;
		menuItem.Items.Add(menuItem3);
		menuItem.SubmenuOpened += delegate(object sender, RoutedEventArgs e)
		{
			if (!ReferenceEquals(e.OriginalSource, menuItem)) return;
			PopulateSubtitleTracksMenu(tracksMenu);
			_subtitleMenuOpen = true;
			_singleClickTimer.Stop();
			_lastVideoClickTime = DateTime.MinValue;
		};
		menuItem.SubmenuClosed += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_subtitleMenuOpen = false;
			}, (DispatcherPriority)4, Array.Empty<object>());
		};
	}

	private void LoadUserSettings()
	{
		_isLoadingUserSettings = true;
		_userSettings = _userSettingsService.Load();
		_volumePercent = _userSettings.VolumePercent;
		_volumeBeforeMute = _userSettings.VolumeBeforeMute;
		_aspectRatio = _userSettings.AspectRatio;
		_audioDelayMs = _userSettings.AudioDelayMs;
		_playbackSpeed = _userSettings.PlaybackSpeed;
		_subtitleLocation = _userSettings.SubtitleLocation;
		_subtitleSize = _userSettings.SubtitleSize;
		_subtitleColor = _userSettings.SubtitleColor;
		_rememberUpscalingAndFrameGeneration = _userSettings.RememberUpscalingAndFrameGeneration;
		_videoUpscalingEnabled = _rememberUpscalingAndFrameGeneration && _userSettings.VideoUpscalingEnabled;
		_videoUpscalingMode = _rememberUpscalingAndFrameGeneration ? NormalizeVideoUpscalingMode(_userSettings.VideoUpscalingMode) : VideoUpscalingModeGpu;
		_videoUpscalingTarget = "Custom";
		_videoUpscalingScale = _rememberUpscalingAndFrameGeneration ? ClampDouble(_userSettings.VideoUpscalingScale, 1.0, 4.0) : 1.3;
		_videoUpscalingSharpness = Math.Max(1.0, ClampDouble(_userSettings.VideoUpscalingSharpness, 0.0, 2.0));
		_rtxVideoEnhancementEnabled = _userSettings.RtxVideoEnhancementEnabled;
		_liveFrameGenerationEnabled = _rememberUpscalingAndFrameGeneration && _userSettings.FrameGenerationEnabled;
		_aiSuperResolutionFrameRateMultiplier = _rememberUpscalingAndFrameGeneration ? Math.Max(1, Math.Min(4, _userSettings.FrameGenerationMultiplier)) : 2;
		_resourceProfile = ResourceProfileNames.Normalize(_userSettings.ResourceProfile);
		LoadHardwareProfile();
		_showVideoInfoOverlay = _userSettings.ShowVideoInfoOverlay;
		base.Topmost = _userSettings.AlwaysOnTop;
		if (Enum.TryParse<PlaybackOrderMode>(_userSettings.PlayOrderMode, ignoreCase: true, out var result))
		{
			_playOrderMode = result;
		}
		_isLoadingUserSettings = false;
	}

	private void SaveUserSettings()
	{
		if (!_isLoadingUserSettings)
		{
			_userSettings.VolumePercent = _volumePercent;
			_userSettings.VolumeBeforeMute = _volumeBeforeMute;
			_userSettings.PlayOrderMode = _playOrderMode.ToString();
			_userSettings.AspectRatio = _aspectRatio;
			_userSettings.AudioDelayMs = _audioDelayMs;
			_userSettings.PlaybackSpeed = _playbackSpeed;
			_userSettings.SubtitleLocation = _subtitleLocation;
			_userSettings.SubtitleSize = _subtitleSize;
			_userSettings.SubtitleColor = _subtitleColor;
			_userSettings.AlwaysOnTop = base.Topmost;
			_userSettings.VideoUpscalingEnabled = _videoUpscalingEnabled;
			_userSettings.VideoUpscalingMode = _videoUpscalingMode;
			_userSettings.VideoUpscalingTarget = _videoUpscalingTarget;
			_userSettings.VideoUpscalingScale = _videoUpscalingScale;
			_userSettings.VideoUpscalingSharpness = _videoUpscalingSharpness;
			_userSettings.RtxVideoEnhancementEnabled = _rtxVideoEnhancementEnabled;
			_userSettings.FrameGenerationEnabled = _liveFrameGenerationEnabled;
			_userSettings.FrameGenerationMultiplier = _aiSuperResolutionFrameRateMultiplier;
			_userSettings.RememberUpscalingAndFrameGeneration = _rememberUpscalingAndFrameGeneration;
			_userSettings.ResourceProfile = _resourceProfile;
			_userSettings.ShowVideoInfoOverlay = _showVideoInfoOverlay;
			_userSettingsService.Save(_userSettings);
		}
	}

	private void LoadHardwareProfile()
	{
		try
		{
			_hardwareProfile = _hardwareCapabilityService.LoadOrScan(_resourceProfile);
			_resourcePlan = _hardwareProfile.RecommendedPlan ?? ResourcePlan.CreateFallback(_resourceProfile);
		}
		catch
		{
			_hardwareProfile = HardwareProfile.CreateFallback(_resourceProfile);
			_resourcePlan = _hardwareProfile.RecommendedPlan ?? ResourcePlan.CreateFallback(_resourceProfile);
		}
	}

	private void RescanHardwareProfile()
	{
		try
		{
			_hardwareProfile = _hardwareCapabilityService.Rescan(_resourceProfile);
			_resourcePlan = _hardwareProfile.RecommendedPlan ?? ResourcePlan.CreateFallback(_resourceProfile);
		}
		catch (Exception ex)
		{
			_hardwareProfile = HardwareProfile.CreateFallback(_resourceProfile);
			_resourcePlan = _hardwareProfile.RecommendedPlan ?? ResourcePlan.CreateFallback(_resourceProfile);
			MessageBox.Show("Hardware scan failed.\n\n" + ex.Message, "Performance", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}

		_toolsWindow?.RefreshPerformanceStatus();
		UpdateProcessingStatsPanel();
		UpdateVideoInfoOverlayPanel();
	}

	private ResourcePlan GetCurrentResourcePlan()
	{
		return _resourcePlan ?? ResourcePlan.CreateFallback(_resourceProfile);
	}

	private string GetResourcePlanStatusText()
	{
		return GetCurrentResourcePlan().FormatDetailedStatus() +
			"\nProfile cache: " + _hardwareCapabilityService.FilePath;
	}

	private static MenuItem? FindMenuItemByHeader(DependencyObject root, string headerText)
	{
		foreach (object child in LogicalTreeHelper.GetChildren(root))
		{
			if (child is MenuItem { Header: var header } menuItem)
			{
				if (string.Equals(header?.ToString(), headerText, StringComparison.OrdinalIgnoreCase))
				{
					return menuItem;
				}
				MenuItem menuItem2 = FindMenuItemByHeader((DependencyObject)(object)menuItem, headerText);
				if (menuItem2 != null)
				{
					return menuItem2;
				}
				continue;
			}
			DependencyObject val = (DependencyObject)((child is DependencyObject) ? child : null);
			if (val != null)
			{
				MenuItem menuItem3 = FindMenuItemByHeader(val, headerText);
				if (menuItem3 != null)
				{
					return menuItem3;
				}
			}
		}
		return null;
	}

	private static void RemoveMenuItemByHeader(MenuItem parent, string headerText)
	{
		for (int num = parent.Items.Count - 1; num >= 0; num--)
		{
			if (parent.Items[num] is MenuItem { Header: var header } && string.Equals(header?.ToString(), headerText, StringComparison.OrdinalIgnoreCase))
			{
				parent.Items.RemoveAt(num);
			}
		}
	}

	private static void RemoveTrailingSeparators(MenuItem parent)
	{
		while (parent.Items.Count > 0 && parent.Items[parent.Items.Count - 1] is Separator)
		{
			parent.Items.RemoveAt(parent.Items.Count - 1);
		}
	}

	private void CreatePlaybackEngine()
	{
		_libVLC = new LibVLC(BuildLibVlcOptions());
		_mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
		_mediaPlayer.Volume = Math.Min(100, _volumePercent);
		_audioTrackManager = new AudioTrackManager(_mediaPlayer);
		_imageProcessingService = new ImageProcessingService(_mediaPlayer);
		_audioEqualizerService = new AudioEqualizerService(_mediaPlayer);
		_videoAdjustmentService = new VideoAdjustmentService(_mediaPlayer);
		SyncEqualizerServicesFromUi();
		ApplyCurrentVolume();
		AttachMediaPlayerEvents(_mediaPlayer);
		ApplyPlaybackOutputSettings();
		ApplyPlaybackSpeed();
		ConfigureRenderPathForCurrentPlayer();
		UpdateVolumeSliderDisplay();
	}

	private void AttachMediaPlayerEvents(LibVLCSharp.Shared.MediaPlayer player)
	{
		player.EncounteredError += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Action)delegate
			{
				if (!ReferenceEquals(_mediaPlayer, player) || _playbackErrorShown) return;
				_playbackErrorShown = true;
				string file = _currentIndex >= 0 && _currentIndex < _playlist.Count ? _playlist[_currentIndex] : "unknown media";
				App.LogException(new InvalidOperationException("LibVLC could not play: " + file));
				SetPlaybackTimerActive(active: false);
				PlayPauseButton.Content = IconPlay;
				MessageBox.Show("This file could not be played. Details were saved to:\n" + AppStoragePaths.GetUserDataFilePath("AXVideoPlayer.error.log"), "Playback error", MessageBoxButton.OK, MessageBoxImage.Warning);
			});
		};
		player.EndReached += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_hasReachedEnd = true;
				_isPlaybackPaused = false;
				SetPlaybackTimerActive(active: false);
				PlayPauseButton.Content = IconPlay;
				_isUiVisible = true;
				UpdateLayoutState();
			}, Array.Empty<object>());
		};
		player.Playing += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_hasReachedEnd = false;
				_isPlaybackPaused = false;
				SetPlaybackTimerActive(active: true);
				PlayPauseButton.Content = IconPause;
				if (string.IsNullOrWhiteSpace(_selectedSubtitlePath) && _selectedEmbeddedSubtitleTrackId < 0)
				{
					ForceDisableSubtitles();
				}
				else if (_selectedEmbeddedSubtitleTrackId >= 0)
				{
					ApplySelectedEmbeddedSubtitleTrack(showError: false);
				}
				_audioTrackManager?.ReapplyPreferredState(Math.Max(1, Math.Min(100, _volumePercent)), BeginAudioTrackMenuRefresh);
				_audioEqualizerService?.Apply();
				ApplyCurrentVolume();
				_videoAdjustmentService?.Apply();
				ApplyPlaybackOutputSettings();
				ApplyPlaybackSpeed();
				BeginAudioTrackMenuRefresh();
			}, Array.Empty<object>());
		};
		player.Paused += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_isPlaybackPaused = true;
				SetPlaybackTimerActive(active: false);
				PlayPauseButton.Content = IconPlay;
			}, Array.Empty<object>());
		};
		player.Stopped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_isPlaybackPaused = false;
				if (!_hasReachedEnd)
				{
					PlayPauseButton.Content = IconPlay;
				}
			}, Array.Empty<object>());
		};
	}

	private void SetPlaybackTimerActive(bool active)
	{
		TimeSpan timeSpan = (active ? ActivePlaybackTimerInterval : IdlePlaybackTimerInterval);
		if (_timer.Interval != timeSpan)
		{
			_timer.Interval = timeSpan;
		}
	}

	private string[] BuildLibVlcOptions()
	{
		string subtitleSize = _subtitleSize;
		if (1 == 0)
		{
		}
		string text = ((subtitleSize == "small") ? "--freetype-rel-fontsize=28" : ((!(subtitleSize == "large")) ? "--freetype-rel-fontsize=22" : "--freetype-rel-fontsize=16"));
		if (1 == 0)
		{
		}
		string text2 = text;
		string subtitleColor = _subtitleColor;
		if (1 == 0)
		{
		}
		text = ((subtitleColor == "yellow") ? "--freetype-color=16776960" : ((!(subtitleColor == "blue")) ? "--freetype-color=16777215" : "--freetype-color=255"));
		if (1 == 0)
		{
		}
		string text3 = text;
		string subtitleLocation = _subtitleLocation;
		if (1 == 0)
		{
		}
		text = ((subtitleLocation == "top") ? ("--sub-margin=" + GetSubtitleMarginPixels(SubtitleVerticalPosition.Top).ToString(CultureInfo.InvariantCulture)) : ((!(subtitleLocation == "middle")) ? ("--sub-margin=" + GetSubtitleMarginPixels(SubtitleVerticalPosition.Bottom).ToString(CultureInfo.InvariantCulture)) : ("--sub-margin=" + GetSubtitleMarginPixels(SubtitleVerticalPosition.Middle).ToString(CultureInfo.InvariantCulture))));
		if (1 == 0)
		{
		}
		string text4 = text;
		bool useRtxNativeVideoPath = IsRtxVideoEnhancementEnabled();
		string hardwareDecodeOption = IsLiveSuperResolutionEnabled() ? "--avcodec-hw=none" : "--avcodec-hw=any";
		List<string> options = new List<string> { text2, text3, text4, hardwareDecodeOption, "--drop-late-frames", "--skip-frames", "--no-video-title-show", "--file-caching=500", "--network-caching=1000", "--live-caching=300" };
		if (useRtxNativeVideoPath)
		{
			options.Add("--vout=direct3d11");
		}
		return options.ToArray();
	}

	private int GetSubtitleMarginPixels(SubtitleVerticalPosition position)
	{
		Grid mainVideoArea = MainVideoArea;
		double num;
		if (mainVideoArea == null || !(mainVideoArea.ActualHeight > 120.0))
		{
			VideoView videoView = VideoView;
			num = ((videoView != null && videoView.ActualHeight > 120.0) ? VideoView.ActualHeight : Math.Max(360.0, base.ActualHeight - 150.0));
		}
		else
		{
			num = MainVideoArea.ActualHeight;
		}
		double num2 = num;
		if (1 == 0)
		{
		}
		int result = position switch
		{
			SubtitleVerticalPosition.Top => Math.Max(24, (int)Math.Round(num2 - 52.0)), 
			SubtitleVerticalPosition.Middle => Math.Max(24, (int)Math.Round(num2 * 0.5)), 
			_ => 24, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void ConfigureRenderPathForCurrentPlayer()
	{
		if (_mediaPlayer != null)
		{
			DisposeLiveSuperResolutionPipeline();
			if (IsLiveSuperResolutionEnabled())
			{
				VideoView.MediaPlayer = null;
				VideoView.Visibility = Visibility.Collapsed;
				SuperResolutionImage.Visibility = HasLoadedVideo() ? Visibility.Visible : Visibility.Collapsed;
				ApplySuperResolutionAspectRatio();
				_liveSuperResolutionPipeline = new LiveSuperResolutionPipeline(_mediaPlayer, SuperResolutionImage, Dispatcher, CreateLiveSuperResolutionSettings, GetCurrentVisibleVideoSizeForSuperResolution, IsGpuLiveUpscalingEnabled(), GetCurrentResourcePlan());
				_liveSuperResolutionPipeline.Attach();
			}
			else
			{
				SuperResolutionImage.Source = null;
				SuperResolutionImage.Visibility = Visibility.Collapsed;
				ResetSuperResolutionAspectRatio();
				VideoView.MediaPlayer = _mediaPlayer;
			}
			UpdateRenderSurfaceVisibility();
			UpdateProcessingStatsPanel();
			UpdateVideoInfoOverlayPanel();
			UpdateSuperResolutionWarningPanel();
		}
	}

	private void UpdateRenderSurfaceVisibility()
	{
		if (IsCurrentMediaAudio())
		{
			VideoView.Visibility = Visibility.Collapsed;
			SuperResolutionImage.Visibility = Visibility.Collapsed;
			MusicModePanel.Visibility = Visibility.Visible;
			return;
		}
		MusicModePanel.Visibility = Visibility.Collapsed;
		bool flag = HasLoadedVideo();
		if (IsLiveSuperResolutionEnabled())
		{
			VideoView.Visibility = Visibility.Collapsed;
			SuperResolutionImage.Visibility = flag ? Visibility.Visible : Visibility.Collapsed;
			ApplySuperResolutionAspectRatio();
		}
		else
		{
			SuperResolutionImage.Visibility = Visibility.Collapsed;
			ResetSuperResolutionAspectRatio();
			VideoView.Visibility = flag ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void PrepareEmbeddedVideoSurface()
	{
		if (_mediaPlayer == null || VideoView == null)
		{
			return;
		}
		if (IsCurrentMediaAudio())
		{
			UpdateMusicMode();
			return;
		}
		EmptyStatePanel.Visibility = Visibility.Collapsed;
		if (IsLiveSuperResolutionEnabled())
		{
			VideoView.MediaPlayer = null;
			VideoView.Visibility = Visibility.Collapsed;
			SuperResolutionImage.Visibility = Visibility.Visible;
			ApplySuperResolutionAspectRatio();
		}
		else
		{
			SuperResolutionImage.Visibility = Visibility.Collapsed;
			ResetSuperResolutionAspectRatio();
			VideoView.Visibility = Visibility.Visible;
			VideoView.MediaPlayer = _mediaPlayer;
		}
		UpdateLayout();
		((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
		{
		}, DispatcherPriority.Render);
	}

	private bool IsCurrentMediaAudio() => _currentIndex >= 0 && _currentIndex < _playlist.Count && IsSupportedAudioFile(_playlist[_currentIndex]);

	private void UpdateMusicMode()
	{
		if (!IsCurrentMediaAudio())
		{
			MusicModePanel.Visibility = Visibility.Collapsed;
			return;
		}

		string path = _playlist[_currentIndex];
		if (!_playlistItems.TryGetValue(path, out MediaPlaylistItem? item))
		{
			item = MediaPlaylistItem.Create(path, isAudio: true);
			_playlistItems[path] = item;
		}

		MusicModePanel.Visibility = Visibility.Visible;
		VideoView.Visibility = Visibility.Collapsed;
		SuperResolutionImage.Visibility = Visibility.Collapsed;
		NowPlayingTitle.Text = item.Title;
		NowPlayingArtist.Text = string.IsNullOrWhiteSpace(item.Artist) ? item.Album : item.Artist;
		NowPlayingArtwork.Source = item.Artwork;
		MusicTrackList.SelectedIndex = _currentIndex;
	}

	private void SetShowProcessingStatistics(bool show)
	{
		_showProcessingStatistics = show;
		UpdateProcessingStatsPanel();
	}

	private void UpdateProcessingStatsPanel()
	{
		if (ProcessingStatsPanel != null && ProcessingStatsText != null && ProcessingStatsPopup != null && ProcessingStatsPopupText != null)
		{
			bool showProcessingStatistics = _showProcessingStatistics;
			ProcessingStatsPanel.Visibility = Visibility.Collapsed;
			ProcessingStatsPopup.IsOpen = showProcessingStatistics;
			if (showProcessingStatistics)
			{
				string renderPath = IsRtxVideoEnhancementEnabled()
					? "RTX Video native VLC Direct3D11 path"
					: (IsGpuLiveUpscalingEnabled()
						? "Live GPU SR D3D11 compute callbacks"
						: (IsCpuLiveUpscalingEnabled()
							? "Live CPU SR memory callbacks"
							: (IsRealtimeFrameGenerationEnabled() ? "Realtime Frame Generation callbacks" : "Standard VLC VideoView")));
				string equalizerText = IsLiveSuperResolutionEnabled() ? "source-side VLC adjustments before SR" : "VLC adjustment controls";
				string text = "Render Path: " + renderPath + "\nUpscaling: " + GetUpscalingStatusText() + "\nVideo Equalizer: " + equalizerText;
				ProcessingStatsText.Text = text;
				ProcessingStatsPopupText.Text = text;
			}
		}
	}

	private void SetShowVideoInfoOverlay(bool show)
	{
		_showVideoInfoOverlay = show;
		SaveUserSettings();
		UpdateVideoInfoOverlayPanel();
	}

	private void UpdateVideoInfoOverlayPanel()
	{
		if (VideoInfoOverlayPopup == null || VideoInfoOverlayPanel == null || VideoInfoOverlayText == null)
		{
			return;
		}

		if (!_showVideoInfoOverlay || !HasLoadedVideo() || _mediaPlayer == null || _mediaPlayer.Length <= 0)
		{
			VideoInfoOverlayPopup.IsOpen = false;
			return;
		}

		VideoInfoOverlayText.Text =
			"FPS " + FormatFps(GetCurrentDisplayFps()) + "\n" +
			"Resolution " + GetCurrentResolutionText() + "\n" +
			"SR " + GetVideoInfoUpscalingText() + "\n" +
			"Bitrate " + FormatBitrate(GetCurrentBitrateKbps());
		PositionVideoInfoOverlayPopup();
		VideoInfoOverlayPopup.IsOpen = true;
	}

	private void PositionVideoInfoOverlayPopup()
	{
		if (VideoInfoOverlayPopup == null || VideoInfoOverlayPanel == null || RootGrid == null || MainVideoArea == null)
		{
			return;
		}
		double left = Math.Max(8.0, RootGrid.ActualWidth - 230.0);
		double top = 58.0;
		try
		{
			VideoInfoOverlayPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			double panelWidth = VideoInfoOverlayPanel.DesiredSize.Width > 1.0 ? VideoInfoOverlayPanel.DesiredSize.Width : 230.0;
			Point origin = MainVideoArea.TransformToAncestor(RootGrid).Transform(new Point(0.0, 0.0));
			double videoWidth = MainVideoArea.ActualWidth;
			left = Math.Max(8.0, origin.X + videoWidth - panelWidth - 8.0);
			top = Math.Max(8.0, origin.Y + 8.0);
		}
		catch
		{
		}
		VideoInfoOverlayPopup.HorizontalOffset = left;
		VideoInfoOverlayPopup.VerticalOffset = top;
	}

	private double GetCurrentDisplayFps()
	{
		if (IsLiveSuperResolutionEnabled() && _liveSuperResolutionPipeline != null)
		{
			LiveSuperResolutionStats stats = _liveSuperResolutionPipeline.GetStats();
			if (IsRealtimeFrameGenerationEnabled() && stats.PresentedFps > 0.001)
			{
				return stats.PresentedFps;
			}
			if (stats.ProcessedFps > 0.001)
			{
				return stats.ProcessedFps;
			}
			if (stats.DecodedFps > 0.001)
			{
				return stats.DecodedFps;
			}
		}

		return TryReadNumericProperty(_mediaPlayer, "Fps", "FPS", "FrameRate");
	}

	private string GetCurrentResolutionText()
	{
		(uint Width, uint Height) source = TryGetCurrentVideoSize();
		if (IsLiveSuperResolutionEnabled() && _liveSuperResolutionPipeline != null)
		{
			LiveSuperResolutionStats stats = _liveSuperResolutionPipeline.GetStats();
			if (stats.SourceWidth > 0 && stats.SourceHeight > 0)
			{
				source = ((uint)stats.SourceWidth, (uint)stats.SourceHeight);
			}
			if (stats.TargetWidth > 0 && stats.TargetHeight > 0)
			{
				return FormatResolution(source.Width, source.Height) + " -> " + FormatResolution((uint)stats.TargetWidth, (uint)stats.TargetHeight);
			}
		}
		return FormatResolution(source.Width, source.Height);
	}

	private string GetVideoInfoUpscalingText()
	{
		if (IsRtxVideoEnhancementEnabled())
		{
			return "RTX";
		}
		if (!_videoUpscalingEnabled)
		{
			return IsRealtimeFrameGenerationEnabled()
				? "FG " + _aiSuperResolutionFrameRateMultiplier.ToString(CultureInfo.InvariantCulture) + "x"
				: "Off";
		}
		string engine = IsGpuLiveUpscalingEnabled() ? "GPU" : "CPU";
		return IsRealtimeFrameGenerationEnabled()
			? engine + " + FG " + _aiSuperResolutionFrameRateMultiplier.ToString(CultureInfo.InvariantCulture) + "x"
			: engine;
	}

	private string FormatResolution(uint width, uint height)
	{
		if (width == 0 || height == 0)
		{
			return "--";
		}
		return width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
	}

	private double GetCurrentBitrateKbps()
	{
		double liveBitrate = TryGetVlcStatsBitrateKbps();
		if (liveBitrate > 1.0)
		{
			return liveBitrate;
		}

		string? path = GetCurrentVideoPath();
		if (string.IsNullOrWhiteSpace(path) || _mediaPlayer == null || _mediaPlayer.Length <= 0)
		{
			return 0.0;
		}

		try
		{
			if (File.Exists(path))
			{
				long bytes = new FileInfo(path).Length;
				return bytes * 8.0 / _mediaPlayer.Length;
			}
		}
		catch
		{
		}

		return 0.0;
	}

	private double TryGetVlcStatsBitrateKbps()
	{
		if (_mediaPlayer == null)
		{
			return 0.0;
		}

		try
		{
			PropertyInfo? statsProperty = _mediaPlayer.GetType().GetProperty("Stats");
			object? stats = statsProperty?.GetValue(_mediaPlayer);
			double raw = TryReadNumericProperty(stats, "DemuxBitrate", "InputBitrate", "ReadBytesRate", "Bitrate");
			if (raw <= 0.0)
			{
				return 0.0;
			}

			return raw < 10000.0 ? raw * 8.0 : raw / 125.0;
		}
		catch
		{
			return 0.0;
		}
	}

	private static double TryReadNumericProperty(object? source, params string[] propertyNames)
	{
		if (source == null)
		{
			return 0.0;
		}

		Type type = source.GetType();
		foreach (string propertyName in propertyNames)
		{
			try
			{
				PropertyInfo? property = type.GetProperty(propertyName);
				object? value = property?.GetValue(source);
				if (value == null)
				{
					continue;
				}
				double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
				if (!double.IsNaN(number) && !double.IsInfinity(number) && number > 0.0)
				{
					return number;
				}
			}
			catch
			{
			}
		}

		return 0.0;
	}

	private static string FormatFps(double fps)
	{
		return fps > 0.001 ? fps.ToString("0.0", CultureInfo.InvariantCulture) : "--";
	}

	private static string FormatBitrate(double kbps)
	{
		if (kbps <= 0.001)
		{
			return "--";
		}
		if (kbps >= 1000.0)
		{
			return (kbps / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " Mbps";
		}
		return kbps.ToString("0", CultureInfo.InvariantCulture) + " kbps";
	}

	private void UpdateSuperResolutionWarningPanel()
	{
		if (SuperResolutionWarningPanel == null || SuperResolutionWarningText == null)
		{
			return;
		}

		if (!IsLiveSuperResolutionEnabled() || _liveSuperResolutionPipeline == null || !HasLoadedVideo() || _superResolutionWarningDismissed)
		{
			SuperResolutionWarningPanel.Visibility = Visibility.Collapsed;
			return;
		}

		LiveSuperResolutionStats stats = _liveSuperResolutionPipeline.GetStats();
		if (stats.SourceHeight <= 0 || stats.TargetHeight <= 0 || stats.DecodedFrames < 60)
		{
			SuperResolutionWarningPanel.Visibility = Visibility.Collapsed;
			return;
		}

		double scale = stats.TargetHeight / (double)stats.SourceHeight;
		long missingFrames = Math.Max(0L, stats.DecodedFrames - stats.ProcessedFrames);
		long droppedFrames = Math.Max(stats.DroppedFrames, missingFrames);
		double dropRatio = droppedFrames / (double)Math.Max(1L, stats.DecodedFrames);
		bool frameRateBehind = stats.DecodedFps >= 12.0 && stats.ProcessedFps > 0.001 && stats.ProcessedFps < stats.DecodedFps * 0.85;
		bool tooManyDrops = droppedFrames >= 8 && dropRatio >= 0.08;

		if (scale <= 1.05 || (!frameRateBehind && !tooManyDrops))
		{
			SuperResolutionWarningPanel.Visibility = Visibility.Collapsed;
			return;
		}

		SuperResolutionWarningText.Text =
			"SR scale is too high: " +
			stats.ProcessedFps.ToString("0.0", CultureInfo.InvariantCulture) +
			" fps processed / " +
			stats.DecodedFps.ToString("0.0", CultureInfo.InvariantCulture) +
			" fps source, " +
			droppedFrames.ToString(CultureInfo.InvariantCulture) +
			" frames dropped. Lower scale.";
		SuperResolutionWarningPanel.Visibility = Visibility.Visible;
	}

	private LiveSuperResolutionSettings CreateLiveSuperResolutionSettings()
	{
		double scale = _videoUpscalingEnabled && !IsRtxVideoEnhancementEnabled() ? _videoUpscalingScale : 1.0;
		double sharpness = _videoUpscalingEnabled && !IsRtxVideoEnhancementEnabled() ? _videoUpscalingSharpness : 0.0;
		return new LiveSuperResolutionSettings(
			"Custom",
			scale,
			sharpness,
			IsRealtimeFrameGenerationEnabled(),
			_aiSuperResolutionFrameRateMultiplier);
	}

	private (int Width, int Height) GetCurrentVisibleVideoSizeForSuperResolution()
	{
		(uint Width, uint Height) size = TryGetCurrentVideoSize();
		return (
			size.Width > int.MaxValue ? int.MaxValue : (int)size.Width,
			size.Height > int.MaxValue ? int.MaxValue : (int)size.Height);
	}

	private void ApplySuperResolutionAspectRatio()
	{
		if (SuperResolutionImage == null)
		{
			return;
		}
		if (!IsLiveSuperResolutionEnabled() || string.Equals(_aspectRatio, "Default", StringComparison.OrdinalIgnoreCase))
		{
			ResetSuperResolutionAspectRatio();
			return;
		}
		if (!TryParseAspectRatio(_aspectRatio, out double targetRatio))
		{
			ResetSuperResolutionAspectRatio();
			return;
		}
		double availableWidth = MainVideoArea?.ActualWidth ?? 0.0;
		double availableHeight = MainVideoArea?.ActualHeight ?? 0.0;
		if (availableWidth <= 1.0 || availableHeight <= 1.0)
		{
			return;
		}
		double areaRatio = availableWidth / availableHeight;
		double width;
		double height;
		if (areaRatio > targetRatio)
		{
			height = availableHeight;
			width = height * targetRatio;
		}
		else
		{
			width = availableWidth;
			height = width / targetRatio;
		}
		SuperResolutionImage.HorizontalAlignment = HorizontalAlignment.Center;
		SuperResolutionImage.VerticalAlignment = VerticalAlignment.Center;
		SuperResolutionImage.Width = Math.Max(1.0, width);
		SuperResolutionImage.Height = Math.Max(1.0, height);
		SuperResolutionImage.Stretch = Stretch.Fill;
	}

	private void ResetSuperResolutionAspectRatio()
	{
		if (SuperResolutionImage == null)
		{
			return;
		}
		SuperResolutionImage.HorizontalAlignment = HorizontalAlignment.Stretch;
		SuperResolutionImage.VerticalAlignment = VerticalAlignment.Stretch;
		SuperResolutionImage.Width = double.NaN;
		SuperResolutionImage.Height = double.NaN;
		SuperResolutionImage.Stretch = Stretch.Uniform;
	}

	private static bool TryParseAspectRatio(string aspectRatio, out double ratio)
	{
		ratio = 0.0;
		string[] parts = (aspectRatio ?? string.Empty).Split(':', 2);
		if (parts.Length != 2 ||
			!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
			!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
			width <= 0.0 ||
			height <= 0.0)
		{
			return false;
		}
		ratio = width / height;
		return ratio > 0.0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio);
	}

	private void DisposeLiveSuperResolutionPipeline()
	{
		_liveSuperResolutionPipeline?.Dispose();
		_liveSuperResolutionPipeline = null;
	}

	private bool HasLoadedVideo()
	{
		return _mediaPlayer?.Media != null && _currentMedia != null && _currentIndex >= 0;
	}

	private bool IsVideoActivelyPlaying()
	{
		return HasLoadedVideo() && (_mediaPlayer?.IsPlaying ?? false);
	}

	private void ConfigureVolumeControl()
	{
		VolumeSlider.Minimum = 0.0;
		VolumeSlider.Maximum = 100.0;
		VolumeSlider.SmallChange = 5.0;
		VolumeSlider.LargeChange = 10.0;
		VolumeSlider.TickFrequency = 5.0;
		_volumePercent = Math.Max(0, Math.Min(200, _volumePercent));
		UpdateVolumeSliderDisplay();
	}

	private void UpdatePopupSize()
	{
		if (FullscreenTopPopup != null && FullscreenBottomPopup != null && FullscreenPlaylistPopup != null && FullscreenTopHost != null && FullscreenBottomHost != null && FullscreenPlaylistHost != null)
		{
			double num = ((base.ActualWidth > 0.0) ? base.ActualWidth : RootGrid.ActualWidth);
			double num2 = ((base.ActualHeight > 0.0) ? base.ActualHeight : RootGrid.ActualHeight);
			if (!(num <= 0.0) && !(num2 <= 0.0))
			{
				double num3 = ((TopBar.ActualHeight > 1.0) ? TopBar.ActualHeight : 46.0);
				double num4 = ((ControlPanel.ActualHeight > 1.0) ? ControlPanel.ActualHeight : 104.0);
				double width = 320.0;
				FullscreenTopPopup.HorizontalOffset = 0.0;
				FullscreenTopPopup.VerticalOffset = 0.0;
				FullscreenTopPopup.Width = num;
				FullscreenTopPopup.Height = num3;
				FullscreenTopHost.Width = num;
				FullscreenTopHost.Height = num3;
				FullscreenBottomPopup.HorizontalOffset = 0.0;
				FullscreenBottomPopup.VerticalOffset = Math.Max(0.0, num2 - num4);
				FullscreenBottomPopup.Width = num;
				FullscreenBottomPopup.Height = num4;
				FullscreenBottomHost.Width = num;
				FullscreenBottomHost.Height = num4;
				FullscreenPlaylistPopup.HorizontalOffset = 0.0;
				FullscreenPlaylistPopup.VerticalOffset = num3;
				FullscreenPlaylistPopup.Width = width;
				FullscreenPlaylistPopup.Height = Math.Max(0.0, num2 - num3 - num4);
				FullscreenPlaylistHost.Width = width;
				FullscreenPlaylistHost.Height = Math.Max(0.0, num2 - num3 - num4);
			}
		}
	}

	private void UpdateCompactControls()
	{
		bool flag = IsFirstNarrowLayout();
		bool flag2 = IsNarrowLayout();
		bool flag3 = IsCompactLayout();
		bool flag4 = IsTinyLayout();
		bool flag5 = flag3 && !_isFullscreen && !_manualUiHidden;
		TopLayoutRow.Height = (flag5 ? new GridLength(0.0) : new GridLength(46.0));
		if (!_isFullscreen && !_manualUiHidden)
		{
			TopBar.Visibility = (flag5 ? Visibility.Collapsed : Visibility.Visible);
			TopBar.IsHitTestVisible = !flag5;
		}
		TimelineControls.Visibility = Visibility.Visible;
		TimelineControlRow.Height = (flag3 ? new GridLength(20.0) : new GridLength(34.0));
		ControlLayoutRow.Height = (flag3 ? new GridLength(70.0) : new GridLength(104.0));
		TransportControlRow.Height = (flag3 ? new GridLength(42.0) : new GridLength(54.0));
		ControlPanel.Padding = (flag3 ? new Thickness(6.0, 4.0, 6.0, 4.0) : new Thickness(12.0));
		CurrentTimeText.FontSize = (flag3 ? 10 : 12);
		TotalTimeText.FontSize = (flag3 ? 10 : 12);
		CurrentTimeText.Visibility = (flag4 ? Visibility.Collapsed : Visibility.Visible);
		TotalTimeText.Visibility = (flag4 ? Visibility.Collapsed : Visibility.Visible);
		CurrentTimeColumn.Width = (flag4 ? new GridLength(0.0) : (flag3 ? new GridLength(50.0) : new GridLength(90.0)));
		TotalTimeColumn.Width = (flag4 ? new GridLength(0.0) : (flag3 ? new GridLength(50.0) : new GridLength(90.0)));
		SubtitleMenu.Visibility = (flag3 ? Visibility.Collapsed : Visibility.Visible);
		SetTransportChildVisibility(0, flag2 ? Visibility.Collapsed : Visibility.Visible);
		SetTransportChildVisibility(2, flag2 ? Visibility.Collapsed : Visibility.Visible);
		SetTransportChildVisibility(3, flag3 ? Visibility.Collapsed : Visibility.Visible);
		SpeedLabel.Visibility = (flag2 ? Visibility.Collapsed : Visibility.Visible);
		SpeedComboBox.Visibility = (flag2 ? Visibility.Collapsed : Visibility.Visible);
		SetVolumeChildVisibilityFromEnd(0, flag3 ? Visibility.Collapsed : Visibility.Visible);
		VolumePercentText.Visibility = (flag4 ? Visibility.Collapsed : Visibility.Visible);
		VolumeSlider.Width = (flag4 ? 64 : (flag3 ? 78 : 100));
		PlaylistToggleButton.MinWidth = (flag3 ? 42 : 42);
		PlayOrderButton.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		CompactHideUiButton.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
	}

	private bool IsFirstNarrowLayout()
	{
		return base.ActualWidth > 0.0 && base.ActualWidth < 950.0;
	}

	private bool IsNarrowLayout()
	{
		return base.ActualWidth > 0.0 && base.ActualWidth < 850.0;
	}

	private bool IsCompactLayout()
	{
		return base.ActualWidth > 0.0 && base.ActualWidth < 700.0;
	}

	private bool IsTinyLayout()
	{
		return base.ActualWidth > 0.0 && base.ActualWidth < 500.0;
	}

	private void SetTransportChildVisibility(int index, Visibility visibility)
	{
		if (TransportControls != null && index >= 0 && index < TransportControls.Children.Count)
		{
			UIElement uIElement = TransportControls.Children[index];
			if (uIElement != null)
			{
				uIElement.Visibility = visibility;
			}
		}
	}

	private void SetVolumeChildVisibilityFromEnd(int reverseIndex, Visibility visibility)
	{
		if (!(VolumeControlGroup.Child is Panel panel))
		{
			return;
		}
		int num = panel.Children.Count - 1 - reverseIndex;
		if (num >= 0 && num < panel.Children.Count)
		{
			UIElement uIElement = panel.Children[num];
			if (uIElement != null)
			{
				uIElement.Visibility = visibility;
			}
		}
	}

	private void NormalizeReadableUiLabels()
	{
		if (PlaylistToggleButton != null)
		{
			SetButtonContentIfChanged(PlaylistToggleButton, IconPlaylist);
		}

		if (PlayOrderButton != null)
		{
			SetButtonContentIfChanged(PlayOrderButton, GetPlayOrderLabel());
			PlayOrderButton.MinWidth = 42.0;
			PlayOrderButton.Width = 42.0;
			PlayOrderButton.Padding = new Thickness(0.0);
		}

		SetTransportButtonLabel(0, IconPrevious);
		SetTransportButtonLabel(1, (_mediaPlayer?.IsPlaying == true && !_isPlaybackPaused) ? IconPause : IconPlay);
		SetTransportButtonLabel(2, IconNext);
		SetTransportButtonLabel(3, IconStop);
		SetFullscreenButtonLabel();
	}

	private string GetPlayOrderLabel()
	{
		return _playOrderMode switch
		{
			PlaybackOrderMode.Shuffle => IconShuffle,
			PlaybackOrderMode.RepeatPlaylist => IconRepeatPlaylist,
			PlaybackOrderMode.RepeatOne => IconRepeatOne,
			_ => IconSingle
		};
	}

	private void SetTransportButtonLabel(int index, string label)
	{
		if (TransportControls == null || index < 0 || index >= TransportControls.Children.Count)
		{
			return;
		}

		if (TransportControls.Children[index] is Button button)
		{
			SetButtonContentIfChanged(button, label);
		}
	}

	private void SetFullscreenButtonLabel()
	{
		if (VolumeControlGroup?.Child is not Panel panel)
		{
			return;
		}

		foreach (Button button in panel.Children.OfType<Button>())
		{
			if (!ReferenceEquals(button, CompactHideUiButton))
			{
				SetButtonContentIfChanged(button, IconFullscreen);
			}
		}
	}

	private static void SetButtonContentIfChanged(Button button, string label)
	{
		if (!Equals(button.Content, label))
			button.Content = label;
	}

	private void EnsureUiInPopup()
	{
		if ((object)TopBar.Parent == RootGrid)
		{
			RootGrid.Children.Remove(TopBar);
			RootGrid.Children.Remove(ControlPanel);
			FullscreenTopHost.Children.Add(TopBar);
			FullscreenBottomHost.Children.Add(ControlPanel);
			TopBar.HorizontalAlignment = HorizontalAlignment.Stretch;
			TopBar.VerticalAlignment = VerticalAlignment.Stretch;
			ControlPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
			ControlPanel.VerticalAlignment = VerticalAlignment.Stretch;
		}
	}

	private void EnsureUiInGrid()
	{
		if ((object)TopBar.Parent == FullscreenTopHost)
		{
			FullscreenTopHost.Children.Remove(TopBar);
			FullscreenBottomHost.Children.Remove(ControlPanel);
			RootGrid.Children.Add(TopBar);
			RootGrid.Children.Add(ControlPanel);
			Grid.SetRow(TopBar, 0);
			Grid.SetColumn(TopBar, 1);
			Grid.SetRow(ControlPanel, 2);
			Grid.SetColumn(ControlPanel, 1);
			TopBar.HorizontalAlignment = HorizontalAlignment.Stretch;
			TopBar.VerticalAlignment = VerticalAlignment.Stretch;
			ControlPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
			ControlPanel.VerticalAlignment = VerticalAlignment.Stretch;
		}
		EnsurePlaylistInGrid();
	}

	private void EnsurePlaylistInPopup()
	{
		if ((object)PlaylistPanel.Parent != FullscreenPlaylistHost)
		{
			if ((object)PlaylistPanel.Parent == RootGrid)
			{
				RootGrid.Children.Remove(PlaylistPanel);
			}
			FullscreenPlaylistHost.Children.Add(PlaylistPanel);
			PlaylistPanel.Width = 320.0;
			PlaylistPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
			PlaylistPanel.VerticalAlignment = VerticalAlignment.Stretch;
		}
	}

	private void EnsurePlaylistInGrid()
	{
		if ((object)PlaylistPanel.Parent == FullscreenPlaylistHost)
		{
			FullscreenPlaylistHost.Children.Remove(PlaylistPanel);
			RootGrid.Children.Add(PlaylistPanel);
		}
		Grid.SetRow(PlaylistPanel, 0);
		Grid.SetRowSpan(PlaylistPanel, 3);
		Grid.SetColumn(PlaylistPanel, 0);
		((DependencyObject)PlaylistPanel).ClearValue(FrameworkElement.WidthProperty);
		PlaylistPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
		PlaylistPanel.VerticalAlignment = VerticalAlignment.Stretch;
	}

	private void SetUiVisibility(bool visible)
	{
		_isUiVisible = visible;
		if (_isFullscreen)
		{
			TopBar.Visibility = Visibility.Visible;
			ControlPanel.Visibility = Visibility.Visible;
			TopBar.IsHitTestVisible = visible;
			ControlPanel.IsHitTestVisible = visible;
			FullscreenTopHost.IsHitTestVisible = true;
			FullscreenBottomHost.IsHitTestVisible = true;
			FullscreenTopPopup.IsOpen = true;
			FullscreenBottomPopup.IsOpen = true;
			FullscreenPlaylistPopup.IsOpen = _isPlaylistVisible;
			UpdatePopupSize();
			double toValue = (visible ? 1.0 : 0.0);
			DoubleAnimation doubleAnimation = new DoubleAnimation(toValue, TimeSpan.FromMilliseconds(140L));
			TopBar.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
			ControlPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation.Clone());
			FullscreenTopHost.BeginAnimation(UIElement.OpacityProperty, null);
			FullscreenBottomHost.BeginAnimation(UIElement.OpacityProperty, null);
			FullscreenTopHost.Opacity = 1.0;
			FullscreenBottomHost.Opacity = 1.0;
			SetShowUiButtonVisible(visible: false);
			UpdateVideoCursorVisibility();
			return;
		}
		TopBar.BeginAnimation(UIElement.OpacityProperty, null);
		ControlPanel.BeginAnimation(UIElement.OpacityProperty, null);
		TopBar.Opacity = 1.0;
		ControlPanel.Opacity = 1.0;
		FullscreenTopHost.BeginAnimation(UIElement.OpacityProperty, null);
		FullscreenBottomHost.BeginAnimation(UIElement.OpacityProperty, null);
		FullscreenTopHost.Opacity = 1.0;
		FullscreenBottomHost.Opacity = 1.0;
		FullscreenTopHost.IsHitTestVisible = true;
		FullscreenBottomHost.IsHitTestVisible = true;
		if (visible)
		{
			bool flag = IsCompactLayout();
			TopLayoutRow.Height = (flag ? new GridLength(0.0) : new GridLength(46.0));
			ControlLayoutRow.Height = new GridLength(104.0);
			TopBar.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
			ControlPanel.Visibility = Visibility.Visible;
			TopBar.IsHitTestVisible = !flag;
			ControlPanel.IsHitTestVisible = true;
			SetShowUiButtonVisible(visible: false);
		}
		else
		{
			TopLayoutRow.Height = new GridLength(0.0);
			ControlLayoutRow.Height = new GridLength(0.0);
			TopBar.Visibility = Visibility.Collapsed;
			ControlPanel.Visibility = Visibility.Collapsed;
			SetShowUiButtonVisible(_manualUiHidden);
		}
		UpdateVideoCursorVisibility();
	}

	private void UpdateVideoCursorVisibility()
	{
		bool shouldHide = HasLoadedVideo() &&
			IsVideoActivelyPlaying() &&
			!AreVideoClickShortcutsSuspended() &&
			(_manualUiHidden || (_isFullscreen && !_isUiVisible));
		if (shouldHide == _isVideoCursorHidden)
			return;

		_isVideoCursorHidden = shouldHide;
		Cursor cursor = shouldHide ? Cursors.None : Cursors.Arrow;
		if (MainVideoArea != null)
			MainVideoArea.Cursor = cursor;
		if (VideoView != null)
			VideoView.Cursor = cursor;
		if (SuperResolutionImage != null)
			SuperResolutionImage.Cursor = cursor;
		if (!shouldHide && Mouse.OverrideCursor == Cursors.None)
			Mouse.OverrideCursor = null;
	}

	private void UpdateLayoutState()
	{
		UpdateScanButtonState();
		if (!HasLoadedVideo())
		{
			EnsureUiInGrid();
			Grid.SetRow(MainVideoArea, 1);
			Grid.SetRowSpan(MainVideoArea, 1);
			VideoView.Visibility = Visibility.Collapsed;
			MusicModePanel.Visibility = Visibility.Collapsed;
			EmptyStatePanel.Visibility = Visibility.Visible;
			FullscreenTopPopup.IsOpen = false;
			FullscreenBottomPopup.IsOpen = false;
			FullscreenPlaylistPopup.IsOpen = false;
			_fullscreenHideTimer.Stop();
			SetUiVisibility(visible: true);
			NormalizeReadableUiLabels();
			return;
		}
		UpdateRenderSurfaceVisibility();
		EmptyStatePanel.Visibility = Visibility.Collapsed;
		if (_isFullscreen)
		{
			Grid.SetRow(MainVideoArea, 0);
			Grid.SetRowSpan(MainVideoArea, 3);
			EnsureUiInPopup();
			FullscreenTopPopup.IsOpen = true;
			FullscreenBottomPopup.IsOpen = true;
			FullscreenPlaylistPopup.IsOpen = _isPlaylistVisible;
			if (_isPlaylistVisible)
			{
				EnsurePlaylistInPopup();
			}
			UpdatePopupSize();
			if (_manualUiHidden)
			{
				SetUiVisibility(visible: false);
				if (IsVideoActivelyPlaying())
				{
					_fullscreenHideTimer.Stop();
					_fullscreenHideTimer.Start();
				}
				else
				{
					_fullscreenHideTimer.Stop();
				}
			}
			else if (!IsVideoActivelyPlaying())
			{
				SetUiVisibility(visible: true);
				_fullscreenHideTimer.Stop();
			}
			else
			{
				SetUiVisibility(_isUiVisible);
				if (_isUiVisible && !_fullscreenHideTimer.IsEnabled)
				{
					_fullscreenHideTimer.Start();
				}
			}
		}
		else
		{
			EnsureUiInGrid();
			FullscreenTopPopup.IsOpen = false;
			FullscreenBottomPopup.IsOpen = false;
			FullscreenPlaylistPopup.IsOpen = false;
			UpdateCompactControls();
			if (_manualUiHidden)
			{
				Grid.SetRow(MainVideoArea, 0);
				Grid.SetRowSpan(MainVideoArea, 3);
				SetUiVisibility(visible: false);
			}
			else
			{
				Grid.SetRow(MainVideoArea, 1);
				Grid.SetRowSpan(MainVideoArea, 1);
				SetUiVisibility(visible: true);
			}
		}
		NormalizeReadableUiLabels();
	}

	private void RecreatePlaybackEngineAndReplay(long resumeTime, bool shouldPlay)
	{
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c3: Expected O, but got Unknown
		if (_currentIndex < 0 || _currentIndex >= _playlist.Count)
		{
			return;
		}
		string filePath = _playlist[_currentIndex];
		resumeTime = Math.Max(0L, resumeTime);
		int volumePercent = _volumePercent;
		_mediaPlayer?.Stop();
		_isPlaybackPaused = false;
		DisposeLiveSuperResolutionPipeline();
		_mediaPlayer?.Dispose();
		_currentMedia?.Dispose();
		_libVLC?.Dispose();
		_libVLC = new LibVLC(BuildLibVlcOptions());
		_mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
		_mediaPlayer.Volume = Math.Min(100, volumePercent);
		_audioTrackManager = new AudioTrackManager(_mediaPlayer);
		_imageProcessingService = new ImageProcessingService(_mediaPlayer);
		_audioEqualizerService = new AudioEqualizerService(_mediaPlayer);
		_videoAdjustmentService = new VideoAdjustmentService(_mediaPlayer);
		SyncEqualizerServicesFromUi();
		ApplyCurrentVolume();
		AttachMediaPlayerEvents(_mediaPlayer);
		ConfigureRenderPathForCurrentPlayer();
		_currentMedia = CreateMedia(filePath);
		_mediaPlayer.Media = _currentMedia;
		ApplyPlaybackOutputSettings();
		PrepareEmbeddedVideoSurface();
		if (shouldPlay || resumeTime > 0)
		{
			_mediaPlayer.Play();
			if (resumeTime > 0)
			{
				try
				{
					_mediaPlayer.Time = resumeTime;
				}
				catch
				{
				}
			}
			_isPlaybackPaused = false;
			SetPlaybackTimerActive(shouldPlay);
			PlayPauseButton.Content = shouldPlay ? IconPause : IconPlay;
		}
		else
		{
			PlayPauseButton.Content = IconPlay;
		}
		if (resumeTime > 0)
		{
			SeekToMediaTime(filePath, resumeTime, minimumTime: 1);
		}
		DispatcherTimer restoreTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250L)
		};
		restoreTimer.Tick += delegate
		{
			restoreTimer.Stop();
			TryApplySubtitleToCurrentPlayer(showError: false);
			_audioTrackManager?.ReapplyPreferredState(Math.Max(1, Math.Min(100, _volumePercent)), BeginAudioTrackMenuRefresh);
			ApplyCurrentVolume();
			if (!shouldPlay && _mediaPlayer != null)
			{
				try
				{
					_mediaPlayer.Pause();
				}
				catch
				{
				}
				_isPlaybackPaused = true;
				SetPlaybackTimerActive(active: false);
				PlayPauseButton.Content = IconPlay;
			}
		};
		restoreTimer.Start();
		UpdateLayoutState();
	}

	private void RestartPlaybackEngineForRenderMode()
	{
		if (_mediaPlayer == null)
		{
			return;
		}

		if (HasLoadedVideo())
		{
			long resumeTime = Math.Max(0L, _mediaPlayer.Time);
			bool shouldPlay = _mediaPlayer.IsPlaying;
			RecreatePlaybackEngineAndReplay(resumeTime, shouldPlay);
			return;
		}

		RecreatePlaybackEngineWithoutMedia();
	}

	private void RecreatePlaybackEngineWithoutMedia()
	{
		_mediaPlayer?.Stop();
		DisposeLiveSuperResolutionPipeline();
		VideoView.MediaPlayer = null;
		_mediaPlayer?.Dispose();
		_currentMedia?.Dispose();
		_currentMedia = null;
		_libVLC?.Dispose();
		_libVLC = null;
		_mediaPlayer = null;

		CreatePlaybackEngine();
		UpdateLayoutState();
	}

	private Media CreateMedia(string filePath)
	{
		if (_libVLC == null)
		{
			throw new InvalidOperationException("LibVLC is not initialized.");
		}
		Media media = new Media(_libVLC, new Uri(filePath));
		media.AddOption(":no-sub-autodetect-file");
		media.AddOption(":sub-autodetect-fuzzy=0");
		media.AddOption(":no-video-title-show");
		AddVideoEnhancementOptions(media);
		if (string.IsNullOrWhiteSpace(_selectedSubtitlePath) && _selectedEmbeddedSubtitleTrackId < 0)
		{
			media.AddOption(":sub-track=-1");
		}
		return media;
	}

	private void AddVideoEnhancementOptions(Media media)
	{
		if (IsRtxVideoEnhancementEnabled())
		{
			return;
		}

		float sigma = IsNativeUpscalingEnabled() ? GetNativeUpscalingSigma() : 0f;
		if (sigma > 0.001f)
		{
			media.AddOption(":video-filter=sharpen");
			media.AddOption(":sharpen-sigma=" + sigma.ToString("0.###", CultureInfo.InvariantCulture));
		}
	}

	private bool IsNativeUpscalingEnabled()
	{
		return VideoEqualizerEnableCheckBox != null && VideoEqualizerEnableCheckBox.IsChecked == true && SharpnessSlider != null && SharpnessSlider.Value > 0.001;
	}

	private float GetNativeUpscalingSigma()
	{
		double value = SharpnessSlider?.Value ?? 0.0;
		return (float)Math.Max(0.0, Math.Min(2.0, value));
	}

	private float GetCombinedSharpenSigma()
	{
		double sigma = IsNativeUpscalingEnabled() ? GetNativeUpscalingSigma() : 0.0;
		if (_videoUpscalingEnabled && !IsRtxVideoEnhancementEnabled())
		{
			sigma = Math.Max(sigma, _videoUpscalingSharpness);
		}
		return (float)ClampDouble(sigma, 0.0, 2.0);
	}

	private string GetUpscalingStatusText()
	{
		if (IsRtxVideoEnhancementEnabled())
		{
			return GetRtxVideoEnhancementStatusText();
		}
		if (!_videoUpscalingEnabled && !IsNativeUpscalingEnabled())
		{
			return IsRealtimeFrameGenerationEnabled() ? GetRealtimeFrameGenerationStatusText() : "SR is off";
		}
		if (!_videoUpscalingEnabled)
		{
			string nativeText = "VLC sharpen " + GetNativeUpscalingSigma().ToString("0.##", CultureInfo.InvariantCulture);
			return IsRealtimeFrameGenerationEnabled() ? nativeText + ". " + GetRealtimeFrameGenerationStatusText() : nativeText;
		}
		if (_liveSuperResolutionPipeline != null)
		{
			return _liveSuperResolutionPipeline.GetStatusText();
		}
		(uint Width, uint Height) size = TryGetCurrentVideoSize();
		double scale = GetEffectiveVideoUpscalingScale(size.Height);
		if (scale <= 0.0)
		{
			return "Live CPU SR: waiting for video size. " + GetCurrentResourcePlan().FormatLiveStatus();
		}
		string sourceText = size.Height > 0 ? size.Height.ToString(CultureInfo.InvariantCulture) + "p" : "source";
		string targetText = size.Height > 0 ? Math.Round(size.Height * scale).ToString(CultureInfo.InvariantCulture) + "p" : _videoUpscalingScale.ToString("0.00", CultureInfo.InvariantCulture) + "x";
		string engineText = IsGpuLiveUpscalingEnabled() ? "Live GPU SR: D3D11 compute" : "Live CPU SR";
		string frameGenerationText = IsRealtimeFrameGenerationEnabled()
			? " Realtime FG " + _aiSuperResolutionFrameRateMultiplier.ToString(CultureInfo.InvariantCulture) + "x."
			: string.Empty;
		return engineText + ": scale " + _videoUpscalingScale.ToString("0.##", CultureInfo.InvariantCulture) + "x, detail " + _videoUpscalingSharpness.ToString("0.##", CultureInfo.InvariantCulture) + ". Frame target: " + sourceText + " -> " + targetText + "." + frameGenerationText + " " + GetCurrentResourcePlan().FormatLiveStatus() + ".";
	}

	private string GetRealtimeFrameGenerationStatusText()
	{
		int multiplier = Math.Max(1, Math.Min(4, _aiSuperResolutionFrameRateMultiplier));
		if (!_liveFrameGenerationEnabled)
		{
			return "Realtime frame generation is off.";
		}
		if (multiplier <= 1)
		{
			return "Realtime frame generation is on; choose 2x, 3x, or 4x.";
		}
		if (_liveSuperResolutionPipeline != null)
		{
			return _liveSuperResolutionPipeline.GetStatusText();
		}
		return "Realtime frame generation " + multiplier.ToString(CultureInfo.InvariantCulture) + "x is ready. " + GetCurrentResourcePlan().FormatLiveStatus() + ".";
	}

	private bool IsLiveSuperResolutionEnabled()
	{
		return IsLiveUpscalingEnabled() || IsRealtimeFrameGenerationEnabled();
	}

	private bool IsLiveUpscalingEnabled()
	{
		return !IsRtxVideoEnhancementEnabled() && _videoUpscalingEnabled && (string.Equals(_videoUpscalingMode, VideoUpscalingModeCpu, StringComparison.OrdinalIgnoreCase) || string.Equals(_videoUpscalingMode, VideoUpscalingModeGpu, StringComparison.OrdinalIgnoreCase));
	}

	private bool IsRealtimeFrameGenerationEnabled()
	{
		return !IsRtxVideoEnhancementEnabled() && _liveFrameGenerationEnabled && _aiSuperResolutionFrameRateMultiplier > 1;
	}

	private bool IsCpuLiveUpscalingEnabled()
	{
		return !IsRtxVideoEnhancementEnabled() && _videoUpscalingEnabled && string.Equals(_videoUpscalingMode, VideoUpscalingModeCpu, StringComparison.OrdinalIgnoreCase);
	}

	private bool IsGpuLiveUpscalingEnabled()
	{
		return !IsRtxVideoEnhancementEnabled() && _videoUpscalingEnabled && string.Equals(_videoUpscalingMode, VideoUpscalingModeGpu, StringComparison.OrdinalIgnoreCase);
	}

	private bool IsRtxVideoEnhancementEnabled()
	{
		return _rtxVideoEnhancementEnabled;
	}

	private string NormalizeVideoUpscalingMode(string? mode)
	{
		return string.Equals(mode, VideoUpscalingModeGpu, StringComparison.OrdinalIgnoreCase) ? VideoUpscalingModeGpu : VideoUpscalingModeCpu;
	}

	private void ApplyVideoUpscalingScale()
	{
		if (_mediaPlayer == null)
		{
			return;
		}
		try
		{
			_mediaPlayer.Scale = 0f;
		}
		catch
		{
			// Scale is a playback enhancement; keep the video running if this VLC build rejects it.
		}
	}

	private double GetEffectiveVideoUpscalingScale()
	{
		(uint Width, uint Height) size = TryGetCurrentVideoSize();
		return GetEffectiveVideoUpscalingScale(size.Height);
	}

	private double GetEffectiveVideoUpscalingScale(uint sourceHeight)
	{
		if (!_videoUpscalingEnabled || IsRtxVideoEnhancementEnabled())
		{
			return 0.0;
		}
		if (string.Equals(_videoUpscalingTarget, "Custom", StringComparison.OrdinalIgnoreCase))
		{
			return ClampDouble(_videoUpscalingScale, 1.0, 4.0);
		}
		if (sourceHeight == 0)
		{
			return 0.0;
		}
		int targetHeight = ResolveVideoUpscalingTargetHeight(sourceHeight);
		if (targetHeight <= 0 || targetHeight <= sourceHeight)
		{
			return 1.0;
		}
		return ClampDouble((double)targetHeight / sourceHeight, 1.0, 4.0);
	}

	private int ResolveVideoUpscalingTargetHeight(uint sourceHeight)
	{
		string target = _videoUpscalingTarget;
		if (string.Equals(target, "1080p", StringComparison.OrdinalIgnoreCase))
		{
			return 1080;
		}
		if (string.Equals(target, "1440p", StringComparison.OrdinalIgnoreCase))
		{
			return 1440;
		}
		if (string.Equals(target, "2160p", StringComparison.OrdinalIgnoreCase))
		{
			return 2160;
		}
		if (sourceHeight < 1080)
		{
			return 1080;
		}
		return (int)sourceHeight;
	}

	private (uint Width, uint Height) TryGetCurrentVideoSize()
	{
		if (_mediaPlayer == null)
		{
			return (0u, 0u);
		}
		try
		{
			Type playerType = _mediaPlayer.GetType();
			foreach (MethodInfo method in playerType.GetMethods())
			{
				if (method.Name != "Size")
				{
					continue;
				}
				ParameterInfo[] parameters = method.GetParameters();
				if (parameters.Length != 3)
				{
					continue;
				}
				object?[] args = { 0u, 0u, 0u };
				object? result = method.Invoke(_mediaPlayer, args);
				bool success = result is bool b && b;
				uint width = args[1] is uint w ? w : 0u;
				uint height = args[2] is uint h ? h : 0u;
				if (success && width > 0 && height > 0)
				{
					return (width, height);
				}
			}
		}
		catch
		{
			// The player may not know the video dimensions until playback starts.
		}
		return (0u, 0u);
	}

	private static double ClampDouble(double value, double min, double max)
	{
		if (double.IsNaN(value) || double.IsInfinity(value))
		{
			return min;
		}
		return Math.Max(min, Math.Min(max, value));
	}

	private void SyncSuperResolutionControlsFromSettings()
	{
		CheckBox? enableBox = FindSuperResolutionControl<CheckBox>("SuperResolutionEnableCheckBox");
		Slider? scaleSlider = FindSuperResolutionControl<Slider>("SuperResolutionScaleSlider");
		Slider? sharpnessSlider = FindSuperResolutionControl<Slider>("SuperResolutionSharpnessSlider");
		if (enableBox == null || scaleSlider == null || sharpnessSlider == null)
		{
			return;
		}
		_isUpdatingSuperResolutionControls = true;
		enableBox.IsChecked = _videoUpscalingEnabled;
		scaleSlider.Value = ClampDouble(_videoUpscalingScale, scaleSlider.Minimum, scaleSlider.Maximum);
		sharpnessSlider.Value = ClampDouble(_videoUpscalingSharpness, sharpnessSlider.Minimum, sharpnessSlider.Maximum);
		_isUpdatingSuperResolutionControls = false;
		UpdateSuperResolutionStatus();
	}

	private void SelectSuperResolutionTargetComboItem(string target)
	{
		ComboBox? comboBox = FindSuperResolutionControl<ComboBox>("SuperResolutionTargetComboBox");
		if (comboBox == null)
		{
			return;
		}
		foreach (object item in comboBox.Items)
		{
			if (item is ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
			{
				comboBox.SelectedItem = comboBoxItem;
				return;
			}
		}
		comboBox.SelectedIndex = 0;
	}

	private void UpdateSuperResolutionStatus()
	{
		TextBlock? scaleText = FindSuperResolutionControl<TextBlock>("SuperResolutionScaleText");
		if (scaleText != null)
		{
			scaleText.Text = _videoUpscalingScale.ToString("0.00", CultureInfo.InvariantCulture) + "x";
		}
		TextBlock? sharpnessText = FindSuperResolutionControl<TextBlock>("SuperResolutionSharpnessText");
		if (sharpnessText != null)
		{
			sharpnessText.Text = _videoUpscalingSharpness.ToString("0.00", CultureInfo.InvariantCulture);
		}
		TextBlock? statusText = FindSuperResolutionControl<TextBlock>("SuperResolutionStatusText");
		if (statusText != null)
		{
			statusText.Text = GetUpscalingStatusText();
		}
		UpdateProcessingStatsPanel();
	}

	private T? FindSuperResolutionControl<T>(string name) where T : FrameworkElement
	{
		return SuperResolutionPanel == null ? null : FindNamedDescendant<T>(SuperResolutionPanel, name);
	}

	private static T? FindNamedDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
	{
		if (parent is T element && string.Equals(element.Name, name, StringComparison.Ordinal))
		{
			return element;
		}
		foreach (object child in LogicalTreeHelper.GetChildren(parent))
		{
			if (child is DependencyObject dependencyObject)
			{
				T? result = FindNamedDescendant<T>(dependencyObject, name);
				if (result != null)
				{
					return result;
				}
			}
		}
		return null;
	}

	private static bool IsSupportedSubtitleFile(string filePath)
	{
		switch (Path.GetExtension(filePath).ToLowerInvariant())
		{
		case ".srt":
		case ".ass":
		case ".ssa":
		case ".vtt":
			return true;
		default:
			return false;
		}
	}

	private void PlaylistToggleButton_Click(object sender, RoutedEventArgs e)
	{
		_isPlaylistVisible = !_isPlaylistVisible;
		_isUiVisible = true;
		ApplyPlaylistVisibility();
		UpdateLayoutState();
	}

	private void ApplyPlaylistVisibility()
	{
		if (_isPlaylistVisible && _isFullscreen)
		{
			EnsurePlaylistInPopup();
			PlaylistColumn.Width = new GridLength(0.0);
			PlaylistPanel.Visibility = Visibility.Visible;
			FullscreenPlaylistPopup.IsOpen = true;
			PlaylistToggleButton.Content = IconPlaylist;
		}
		else if (_isPlaylistVisible && !_manualUiHidden)
		{
			EnsurePlaylistInGrid();
			PlaylistColumn.Width = new GridLength(280.0);
			PlaylistPanel.Visibility = Visibility.Visible;
			FullscreenPlaylistPopup.IsOpen = false;
			PlaylistToggleButton.Content = IconPlaylist;
		}
		else
		{
			PlaylistPanel.Visibility = Visibility.Collapsed;
			PlaylistColumn.Width = new GridLength(0.0);
			FullscreenPlaylistPopup.IsOpen = false;
			PlaylistToggleButton.Content = IconPlaylist;
		}
	}

	private void SetShowUiButtonVisible(bool visible)
	{
		if (ShowUiPopup != null && ShowUiButton != null)
		{
			ShowUiButton.Visibility = ((!visible) ? Visibility.Collapsed : Visibility.Visible);
			ShowUiPopup.IsOpen = visible;
			if (visible)
			{
				PositionShowUiPopup();
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(PositionShowUiPopup), (DispatcherPriority)6, Array.Empty<object>());
			}
		}
	}

	private bool IsShowUiButtonVisible()
	{
		return ShowUiPopup != null && ShowUiPopup.IsOpen;
	}

	private void PositionShowUiPopup()
	{
		if (ShowUiPopup != null && ShowUiButton != null)
		{
			double num = ((ShowUiButton.ActualWidth > 1.0) ? ShowUiButton.ActualWidth : 104.0);
			ShowUiPopup.HorizontalOffset = Math.Max(8.0, base.ActualWidth - num - 18.0);
			ShowUiPopup.VerticalOffset = 10.0;
		}
	}

	private void HideUiButton_Click(object sender, RoutedEventArgs e)
	{
		HideUiManually();
	}

	private void HideUiManually()
	{
		if (!IsVideoActivelyPlaying())
		{
			_manualUiHidden = false;
			UpdateLayoutState();
			return;
		}
		_manualUiHidden = true;
		_playlistWasVisibleBeforeManualHide = _isPlaylistVisible;
		_isPlaylistVisible = false;
		ApplyPlaylistVisibility();
		ApplyManualWindowChromeHidden(hidden: true);
		UpdateLayoutState();
	}

	private void ShowUiButton_Click(object sender, RoutedEventArgs e)
	{
		ShowUiManually();
	}

	private void ShowUiButton_MouseEnter(object sender, MouseEventArgs e)
	{
		if (_isFullscreen && _manualUiHidden)
		{
			ShowUiManually();
		}
	}

	private void ShowUiManually()
	{
		_manualUiHidden = false;
		_isPlaylistVisible = _playlistWasVisibleBeforeManualHide;
		ApplyPlaylistVisibility();
		ApplyManualWindowChromeHidden(hidden: false);
		_isUiVisible = true;
		UpdateLayoutState();
	}

	private void ApplyManualWindowChromeHidden(bool hidden)
	{
		if (_isFullscreen)
		{
			return;
		}
		if (hidden)
		{
			if (base.WindowStyle != WindowStyle.None)
			{
				_windowStyleBeforeManualHide = base.WindowStyle;
			}
			base.WindowStyle = WindowStyle.None;
		}
		else if (base.WindowStyle == WindowStyle.None)
		{
			base.WindowStyle = ((_windowStyleBeforeManualHide == WindowStyle.None) ? WindowStyle.SingleBorderWindow : _windowStyleBeforeManualHide);
		}
	}

	private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2 && HandleVideoSurfaceLeftButtonShortcut(PointToScreen(e.GetPosition(this)), isDoubleClick: true))
		{
			e.Handled = true;
		}
	}

	private bool IsPointInsideVisibleElement(MouseButtonEventArgs e, FrameworkElement element)
	{
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		if (_isFullscreen && !_isUiVisible && (element == TopBar || element == ControlPanel))
		{
			return false;
		}
		if (element.Visibility != Visibility.Visible || element.Opacity <= 0.05 || element.ActualWidth <= 0.0 || element.ActualHeight <= 0.0)
		{
			return false;
		}
		Point position = e.GetPosition(element);
		return position.X >= 0.0 && position.X <= element.ActualWidth && position.Y >= 0.0 && position.Y <= element.ActualHeight;
	}

	private void MainVideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (HandleVideoSurfaceLeftButtonShortcut(PointToScreen(e.GetPosition(this)), e.ClickCount == 2))
		{
			e.Handled = true;
		}
	}

	private bool HandleVideoSurfaceLeftButtonShortcut(Point screenPoint, bool isDoubleClick)
	{
		if (AreVideoClickShortcutsSuspended() || !IsScreenPointInsideMainViewingSurface(screenPoint))
		{
			return false;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (!isDoubleClick && ShouldIgnoreDuplicateVideoShortcutDown(screenPoint, utcNow))
		{
			return true;
		}
		RememberVideoShortcutDown(screenPoint, utcNow);
		Focus();
		Point windowPoint = PointFromScreen(screenPoint);
		if (isDoubleClick)
		{
			CancelPendingVideoSingleClick();
			_suppressVideoShortcutDownUntilUtc = utcNow.AddMilliseconds(260.0);
			ToggleFullscreenDebounced();
			return true;
		}
		double elapsedMilliseconds = (utcNow - _lastVideoClickTime).TotalMilliseconds;
		double movement = Distance(windowPoint, _lastVideoClickPoint);
		if (elapsedMilliseconds <= 500.0 && movement <= 20.0)
		{
			CancelPendingVideoSingleClick();
			_suppressVideoShortcutDownUntilUtc = utcNow.AddMilliseconds(260.0);
			ToggleFullscreenDebounced();
			return true;
		}
		_lastVideoClickTime = utcNow;
		_lastVideoClickPoint = windowPoint;
		_singleClickTimer.Stop();
		_singleClickTimer.Start();
		return true;
	}

	private bool ShouldIgnoreDuplicateVideoShortcutDown(Point screenPoint, DateTime utcNow)
	{
		double movement = Distance(screenPoint, _lastVideoShortcutDownScreenPoint);
		if (utcNow < _suppressVideoShortcutDownUntilUtc && movement <= 24.0)
		{
			return true;
		}
		return (utcNow - _lastVideoShortcutDownUtc).TotalMilliseconds <= 80.0 && movement <= 24.0;
	}

	private void RememberVideoShortcutDown(Point screenPoint, DateTime utcNow)
	{
		_lastVideoShortcutDownUtc = utcNow;
		_lastVideoShortcutDownScreenPoint = screenPoint;
	}

	private void CancelPendingVideoSingleClick()
	{
		_singleClickTimer.Stop();
		_lastVideoClickTime = DateTime.MinValue;
	}

	private void PlayPause_Click(object sender, RoutedEventArgs e)
	{
		TogglePlayPause();
	}

	private void TogglePlayPause()
	{
		if (_mediaPlayer == null)
		{
			return;
		}
		if (_mediaPlayer.Media == null && _playlist.Count > 0)
		{
			PlayFromPlaylist(0, keepCurrentSubtitle: false);
			return;
		}
		if (_mediaPlayer.IsPlaying)
		{
			_mediaPlayer.Pause();
			_isPlaybackPaused = true;
			SetPlaybackTimerActive(active: false);
			PlayPauseButton.Content = IconPlay;
		}
		else
		{
			if (_hasReachedEnd)
			{
				RestartCurrentMediaAtRatio(0.0, play: true);
			}
			else
			{
				PrepareEmbeddedVideoSurface();
				_mediaPlayer.Play();
				_isPlaybackPaused = false;
				SetPlaybackTimerActive(active: true);
			}
			PlayPauseButton.Content = IconPause;
			EmptyStatePanel.Visibility = Visibility.Collapsed;
			UpdateRenderSurfaceVisibility();
		}
		_isUiVisible = true;
		UpdateLayoutState();
		Focus();
	}

	private void RestartCurrentMediaAtRatio(double ratio, bool play)
	{
		if (_mediaPlayer != null && _currentIndex >= 0 && _currentIndex < _playlist.Count)
		{
			long targetTime = ((_mediaPlayer.Length > 0) ? ((long)((double)_mediaPlayer.Length * Math.Max(0.0, Math.Min(1.0, ratio)))) : 0);
			RestartCurrentMediaAtTime(targetTime, play);
		}
	}

	private void RestartCurrentMediaAtTime(long targetTime, bool play)
	{
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Expected O, but got Unknown
		if (_mediaPlayer == null || _currentIndex < 0 || _currentIndex >= _playlist.Count)
		{
			return;
		}
		SaveCurrentPlaybackPosition();
		string filePath = _playlist[_currentIndex];
		_currentMedia?.Dispose();
		_currentMedia = CreateMedia(filePath);
		_mediaPlayer.Media = _currentMedia;
		_hasReachedEnd = false;
		PrepareEmbeddedVideoSurface();
		if (play)
		{
			_mediaPlayer.Play();
			_isPlaybackPaused = false;
			SetPlaybackTimerActive(active: true);
			PlayPauseButton.Content = IconPause;
		}
		DispatcherTimer seekTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(180L)
		};
		seekTimer.Tick += delegate
		{
			seekTimer.Stop();
			if (_mediaPlayer != null && _mediaPlayer.Length > 0)
			{
				_mediaPlayer.Time = Math.Max(0L, Math.Min(_mediaPlayer.Length - 1, targetTime));
				PositionSlider.Value = ((_mediaPlayer.Length > 0) ? ((double)_mediaPlayer.Time / (double)_mediaPlayer.Length * PositionSlider.Maximum) : 0.0);
				CurrentTimeText.Text = FormatTime(_mediaPlayer.Time);
			}
			TryApplySubtitleToCurrentPlayer(showError: false);
			BeginAudioTrackMenuRefresh();
		};
		seekTimer.Start();
	}

	private void BeginAudioTrackMenuRefresh()
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Expected O, but got Unknown
		_audioTrackSelectionWindow?.RefreshTracks();
		_toolsWindow?.RefreshAudioTracks();
		if (!HasLoadedVideo())
		{
			return;
		}
		int attempts = 0;
		DispatcherTimer timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250L)
		};
		timer.Tick += delegate
		{
			attempts++;
			_audioTrackSelectionWindow?.RefreshTracks();
			_toolsWindow?.RefreshAudioTracks();
			if (HasAudioTracks() || attempts >= 8)
			{
				timer.Stop();
			}
		};
		timer.Start();
	}

	private bool HasAudioTracks()
	{
		try
		{
			if (_mediaPlayer?.AudioTrackDescription == null)
			{
				return false;
			}
			TrackDescription[] audioTrackDescription = _mediaPlayer.AudioTrackDescription;
			foreach (TrackDescription trackDescription in audioTrackDescription)
			{
				if (trackDescription.Id >= 0)
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private void SyncEqualizerServicesFromUi()
	{
		if (_audioEqualizerService != null && AudioEqualizerEnableCheckBox != null)
		{
			_audioEqualizerService.SetEnabled(AudioEqualizerEnableCheckBox.IsChecked == true);
			_audioEqualizerService.SetPreamp((float)AudioPreampSlider.Value);
			_audioEqualizerService.SetBand(0, (float)AudioBand0Slider.Value);
			_audioEqualizerService.SetBand(1, (float)AudioBand1Slider.Value);
			_audioEqualizerService.SetBand(2, (float)AudioBand2Slider.Value);
			_audioEqualizerService.SetBand(3, (float)AudioBand3Slider.Value);
			_audioEqualizerService.SetBand(4, (float)AudioBand4Slider.Value);
			_audioEqualizerService.SetBand(5, (float)AudioBand5Slider.Value);
			_audioEqualizerService.SetBand(6, (float)AudioBand6Slider.Value);
			_audioEqualizerService.SetBand(7, (float)AudioBand7Slider.Value);
			_audioEqualizerService.SetBand(8, (float)AudioBand8Slider.Value);
			_audioEqualizerService.SetBand(9, (float)AudioBand9Slider.Value);
		}
		if (_videoAdjustmentService != null && VideoEqualizerEnableCheckBox != null)
		{
			_videoAdjustmentService.SetEnabled(VideoEqualizerEnableCheckBox.IsChecked == true);
			_videoAdjustmentService.SetBrightness((float)BrightnessSlider.Value);
			_videoAdjustmentService.SetContrast((float)ContrastSlider.Value);
			_videoAdjustmentService.SetSaturation((float)SaturationSlider.Value);
			_videoAdjustmentService.SetGamma((float)GammaSlider.Value);
			_videoAdjustmentService.SetHue((float)HueSlider.Value);
			_videoAdjustmentService.SetSharpness((float)SharpnessSlider.Value);
		}
	}

	private string? GetCurrentVideoPath()
	{
		return (_currentIndex >= 0 && _currentIndex < _playlist.Count) ? _playlist[_currentIndex] : null;
	}

	private void LoadVideoEqualizerSettings()
	{
		LoadEqualizerSettingsForCurrentVideo();
	}

	private void LoadEqualizerSettingsForCurrentVideo()
	{
		string currentVideoPath = GetCurrentVideoPath();
		_isLoadingEqualizerSettings = true;
		VideoEqualizerSettings videoEqualizerSettings = _videoEqualizerSettings.LoadVideo(currentVideoPath);
		VideoEqualizerEnableCheckBox.IsChecked = videoEqualizerSettings.Enabled;
		BrightnessSlider.Value = videoEqualizerSettings.Brightness;
		ContrastSlider.Value = videoEqualizerSettings.Contrast;
		SaturationSlider.Value = videoEqualizerSettings.Saturation;
		GammaSlider.Value = videoEqualizerSettings.Gamma;
		HueSlider.Value = videoEqualizerSettings.Hue;
		SharpnessSlider.Value = videoEqualizerSettings.Sharpness;
		AudioEqualizerSettings audioEqualizerSettings = _videoEqualizerSettings.LoadAudio(currentVideoPath);
		AudioEqualizerEnableCheckBox.IsChecked = audioEqualizerSettings.Enabled;
		AudioPreampSlider.Value = audioEqualizerSettings.Preamp;
		for (int i = 0; i < 10; i++)
		{
			Slider audioBandSlider = GetAudioBandSlider(i);
			if (audioBandSlider != null)
			{
				audioBandSlider.Value = audioEqualizerSettings.Bands[i];
			}
		}
		_isLoadingEqualizerSettings = false;
		SyncEqualizerServicesFromUi();
	}

	private void SaveVideoEqualizerSettings()
	{
		if (!_isLoadingEqualizerSettings)
		{
			_videoEqualizerSettings.SaveVideo(GetCurrentVideoPath(), new VideoEqualizerSettings
			{
				Enabled = (VideoEqualizerEnableCheckBox.IsChecked == true),
				Brightness = BrightnessSlider.Value,
				Contrast = ContrastSlider.Value,
				Saturation = SaturationSlider.Value,
				Gamma = GammaSlider.Value,
				Hue = HueSlider.Value,
				Sharpness = SharpnessSlider.Value
			});
		}
	}

	private void SaveAudioEqualizerSettings()
	{
		if (!_isLoadingEqualizerSettings)
		{
			double[] array = new double[10];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = GetAudioBandSlider(i)?.Value ?? 0.0;
			}
			_videoEqualizerSettings.SaveAudio(GetCurrentVideoPath(), new AudioEqualizerSettings
			{
				Enabled = (AudioEqualizerEnableCheckBox.IsChecked == true),
				Preamp = AudioPreampSlider.Value,
				Bands = array
			});
		}
	}

	private void ResetVideoEqualizerValues()
	{
		BrightnessSlider.Value = 1.0;
		ContrastSlider.Value = 1.0;
		SaturationSlider.Value = 1.0;
		GammaSlider.Value = 1.0;
		HueSlider.Value = 0.0;
		SharpnessSlider.Value = 0.0;
		_videoAdjustmentService?.Reset();
		SaveVideoEqualizerSettings();
		ScheduleNativeVideoFilterRestart();
	}

	private void ClearVideoEqualizerHistory()
	{
		VideoEqualizerEnableCheckBox.IsChecked = false;
		BrightnessSlider.Value = 1.0;
		ContrastSlider.Value = 1.0;
		SaturationSlider.Value = 1.0;
		GammaSlider.Value = 1.0;
		HueSlider.Value = 0.0;
		SharpnessSlider.Value = 0.0;
		_videoAdjustmentService?.SetEnabled(enabled: false);
		_videoAdjustmentService?.Reset();
		_videoEqualizerSettings.ClearVideo(GetCurrentVideoPath());
		ScheduleNativeVideoFilterRestart();
	}

	private void Screenshot_Click(object sender, RoutedEventArgs e)
	{
		if (_mediaPlayer == null || _imageProcessingService == null || !HasLoadedVideo())
		{
			MessageBox.Show("Open a video before taking a screenshot.", "Screenshot", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		try
		{
			string currentVideoPath = _playlist[_currentIndex];
			bool isOpen = ProcessingStatsPopup.IsOpen;
			Visibility visibility = ProcessingStatsPanel.Visibility;
			bool isOpen2 = ShowUiPopup.IsOpen;
			Visibility visibility2 = AudioEqualizerPanel.Visibility;
			Visibility visibility3 = VideoEqualizerPanel.Visibility;
			bool isOpen3 = SuperResolutionPopup.IsOpen;
			bool flag = _toolsWindow?.IsVisible ?? false;
			bool flag2 = _audioEqualizerWindow?.IsVisible ?? false;
			bool flag3 = _videoEqualizerWindow?.IsVisible ?? false;
			ProcessingStatsPopup.IsOpen = false;
			ProcessingStatsPanel.Visibility = Visibility.Collapsed;
			ShowUiPopup.IsOpen = false;
			AudioEqualizerPanel.Visibility = Visibility.Collapsed;
			VideoEqualizerPanel.Visibility = Visibility.Collapsed;
			SuperResolutionPopup.IsOpen = false;
			if (flag)
			{
				_toolsWindow?.Hide();
			}
			if (flag2)
			{
				_audioEqualizerWindow?.Hide();
			}
			if (flag3)
			{
				_videoEqualizerWindow?.Hide();
			}
			UpdateLayout();
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
			}, (DispatcherPriority)7);
			Thread.Sleep(120);
			string text;
			try
			{
				text = _imageProcessingService.SaveCurrentFrameSnapshot(currentVideoPath, MainVideoArea);
			}
			finally
			{
				ProcessingStatsPopup.IsOpen = isOpen;
				ProcessingStatsPanel.Visibility = visibility;
				ShowUiPopup.IsOpen = isOpen2;
				AudioEqualizerPanel.Visibility = visibility2;
				VideoEqualizerPanel.Visibility = visibility3;
				SuperResolutionPopup.IsOpen = isOpen3;
				if (flag)
				{
					_toolsWindow?.Show();
				}
				if (flag2)
				{
					_audioEqualizerWindow?.Show();
				}
				if (flag3)
				{
					_videoEqualizerWindow?.Show();
				}
			}
			MessageBox.Show("Screenshot saved:\n" + text, "Screenshot", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			MessageBox.Show("Could not save screenshot.\n\n" + ex.Message, "Screenshot", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void Tools_Click(object sender, RoutedEventArgs e)
	{
		if (_toolsWindow != null && _toolsWindow.IsVisible)
		{
			_toolsWindow.Activate();
			return;
		}
		if (SuperResolutionPopup != null)
		{
			SuperResolutionPopup.IsOpen = false;
		}
		_toolsWindow = new ToolsWindow(delegate
		{
			Screenshot_Click(this, new RoutedEventArgs());
		}, () => AudioEqualizerEnableCheckBox.IsChecked == true, delegate(bool enabled)
		{
			AudioEqualizerEnableCheckBox.IsChecked = enabled;
			_audioEqualizerService?.SetEnabled(enabled);
			SaveAudioEqualizerSettings();
		}, () => AudioPreampSlider.Value, (int bandIndex) => GetAudioBandSlider(bandIndex)?.Value ?? 0.0, delegate(double value)
		{
			AudioPreampSlider.Value = value;
			_audioEqualizerService?.SetPreamp((float)value);
			SaveAudioEqualizerSettings();
		}, delegate(int bandIndex, double value)
		{
			Slider audioBandSlider = GetAudioBandSlider(bandIndex);
			if (audioBandSlider != null)
			{
				audioBandSlider.Value = value;
			}
			_audioEqualizerService?.SetBand(bandIndex, (float)value);
			SaveAudioEqualizerSettings();
		}, delegate
		{
			AudioPreampSlider.Value = 0.0;
			for (int i = 0; i < 10; i++)
			{
				Slider audioBandSlider = GetAudioBandSlider(i);
				if (audioBandSlider != null)
				{
					audioBandSlider.Value = 0.0;
				}
			}
			_audioEqualizerService?.Reset();
			SaveAudioEqualizerSettings();
		}, () => VideoEqualizerEnableCheckBox.IsChecked == true, delegate(bool enabled)
		{
			VideoEqualizerEnableCheckBox.IsChecked = enabled;
			_videoAdjustmentService?.SetEnabled(enabled);
			SaveVideoEqualizerSettings();
			ScheduleNativeVideoFilterRestartIfSharpnessSet();
		}, GetVideoEqualizerValue, SetVideoEqualizerValue, ResetVideoEqualizerValues, ClearVideoEqualizerHistory, () => _videoUpscalingEnabled, SetVideoUpscalingEnabled, GetVideoUpscalingMode, SetVideoUpscalingMode, () => _videoUpscalingTarget, SetVideoUpscalingTarget, () => _videoUpscalingScale, SetVideoUpscalingScale, () => _videoUpscalingSharpness, SetVideoUpscalingSharpness, GetUpscalingStatusText, GetRtxVideoEnhancementEnabled, SetRtxVideoEnhancementEnabled, GetRtxVideoEnhancementStatusText, OpenNvidiaVideoSettings, StartAiSuperResolutionPreview, StartAiSuperResolutionFullVideo, OpenOfflineSuperResolutionRenderer, OpenVideoEncoder, GetAiSuperResolutionStatus, GetAiSuperResolutionFrameRateMultiplier, SetAiSuperResolutionFrameRateMultiplier, GetLiveFrameGenerationEnabled, SetLiveFrameGenerationEnabled, GetRealtimeFrameGenerationStatusText, () => _audioTrackManager?.GetAudioTracks() ?? Array.Empty<AudioTrackOption>(), () => _audioTrackManager?.IsAudioDisabled ?? false, delegate(int trackId)
		{
			if (_mediaPlayer == null || _audioTrackManager == null || !HasLoadedVideo())
			{
				MessageBox.Show("Open a video before selecting an audio track.", "Audio Track", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
			else
			{
				_audioTrackManager.SelectTrack(trackId, Math.Max(1, Math.Min(100, _volumePercent)), delegate
				{
					BeginAudioTrackMenuRefresh();
					_toolsWindow?.RefreshAudioTracks();
					ApplyCurrentVolume();
				});
			}
		}, () => _playbackHistory.RememberEnabled, delegate(bool enabled)
		{
			_playbackHistory.SetRememberEnabled(enabled);
			if (enabled)
				PersistPlaylistIfEnabled();
		}, GetResumePromptTimeoutSeconds, SetResumePromptTimeoutSeconds, delegate
		{
			_playbackHistory.ClearHistory();
			MessageBox.Show("Remembered playlist and playback history cleared.", "Resume", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}, () => _aspectRatio, SetAspectRatio, () => base.Topmost, delegate(bool enabled)
		{
			base.Topmost = enabled;
			if (_isFullscreen)
			{
				base.Topmost = true;
			}
			SaveUserSettings();
		}, () => _audioDelayMs, SetAudioDelayMs, () => _showProcessingStatistics, SetShowProcessingStatistics, () => _showVideoInfoOverlay, SetShowVideoInfoOverlay, GetResourcePlanStatusText, RescanHardwareProfile, GetRememberUpscalingAndFrameGeneration, SetRememberUpscalingAndFrameGeneration)
		{
			Owner = this
		};
		_toolsWindow.Closed += delegate
		{
			_toolsWindow = null;
			UpdateVideoCursorVisibility();
		};
		_toolsWindow.Show();
		UpdateVideoCursorVisibility();
	}

	private void OpenOfflineSuperResolutionRenderer()
	{
		if (_offlineSuperResolutionWindow != null && _offlineSuperResolutionWindow.IsVisible)
		{
			_offlineSuperResolutionWindow.Activate();
			return;
		}

		_offlineSuperResolutionWindow = new OfflineSuperResolutionWindow(GetCurrentResourcePlan)
		{
			Owner = this
		};
		_offlineSuperResolutionWindow.Closed += delegate
		{
			_offlineSuperResolutionWindow = null;
		};
		_offlineSuperResolutionWindow.Show();
	}

	private void OpenVideoEncoder()
	{
		if (_videoEncodingWindow != null && _videoEncodingWindow.IsVisible)
		{
			_videoEncodingWindow.Activate();
			return;
		}

		string? currentVideoPath = GetCurrentVideoPath();
		_videoEncodingWindow = new VideoEncodingWindow(currentVideoPath, GetCurrentResourcePlan)
		{
			Owner = this
		};
		_videoEncodingWindow.Closed += delegate
		{
			_videoEncodingWindow = null;
		};
		_videoEncodingWindow.Show();
	}

	private bool GetRtxVideoEnhancementEnabled()
	{
		return _rtxVideoEnhancementEnabled;
	}

	private void SetRtxVideoEnhancementEnabled(bool enabled)
	{
		if (_rtxVideoEnhancementEnabled == enabled)
		{
			return;
		}

		_rtxVideoEnhancementEnabled = enabled;
		if (enabled)
		{
			_videoUpscalingEnabled = false;
			_liveFrameGenerationEnabled = false;
			_superResolutionWarningDismissed = false;
			SyncSuperResolutionControlsFromSettings();
		}

		SaveUserSettings();
		UpdateSuperResolutionStatus();
		UpdateSuperResolutionWarningPanel();
		UpdateProcessingStatsPanel();
		UpdateVideoInfoOverlayPanel();
		RestartPlaybackEngineForRenderMode();
	}

	private string GetRtxVideoEnhancementStatusText()
	{
		string modeText = IsRtxVideoEnhancementEnabled()
			? "RTX Video compatibility mode is on. Playback uses VLC native Direct3D11 output so NVIDIA RTX Video can engage when Super Resolution or HDR is enabled in NVIDIA settings."
			: "RTX Video compatibility mode is off.";

		return modeText + " " + GetDetectedRtxGpuStatusText();
	}

	private string GetDetectedRtxGpuStatusText()
	{
		HardwareGpuInfo? rtxGpu = _hardwareProfile.Gpus.FirstOrDefault(gpu => IsNvidiaRtxGpuName(gpu.Name));
		if (rtxGpu != null)
		{
			string driverText = string.IsNullOrWhiteSpace(rtxGpu.DriverVersion) ? string.Empty : ", driver " + rtxGpu.DriverVersion;
			return "Detected " + rtxGpu.Name + driverText + ".";
		}

		if (_hardwareProfile.Gpus.Count == 0)
		{
			return "No GPU was detected by the last hardware scan.";
		}

		return "Last hardware scan did not find an NVIDIA RTX GPU; RTX Video requires an NVIDIA RTX GPU and a supported Windows driver.";
	}

	private static bool IsNvidiaRtxGpuName(string? gpuName)
	{
		return !string.IsNullOrWhiteSpace(gpuName) &&
			gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 &&
			gpuName.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private void OpenNvidiaVideoSettings()
	{
		var launchErrors = new List<string>();
		foreach (string candidate in GetNvidiaControlPanelCandidates())
		{
			if (!string.Equals(candidate, "nvcplui.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(candidate))
			{
				continue;
			}

			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = candidate,
					UseShellExecute = true
				});
				return;
			}
			catch (Exception ex)
			{
				launchErrors.Add(candidate + ": " + ex.Message);
			}
		}

		string details = launchErrors.Count == 0
			? string.Empty
			: "\n\nLaunch attempts:\n" + string.Join("\n", launchErrors);
		MessageBox.Show(
			"Could not open NVIDIA Control Panel.\n\n" +
			GetDetectedRtxGpuStatusText() +
			"\n\nOpen NVIDIA App or NVIDIA Control Panel manually and enable RTX Video enhancements under video settings." +
			details,
			"RTX Video",
			MessageBoxButton.OK,
			MessageBoxImage.Information);
	}

	private static IEnumerable<string> GetNvidiaControlPanelCandidates()
	{
		yield return "nvcplui.exe";

		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		if (!string.IsNullOrWhiteSpace(programFiles))
		{
			yield return Path.Combine(programFiles, "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe");
		}

		string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		if (!string.IsNullOrWhiteSpace(programFilesX86))
		{
			yield return Path.Combine(programFilesX86, "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe");
		}
	}

	private string GetAiSuperResolutionStatus()
	{
		return _aiSuperResolutionStatus;
	}

	private int GetAiSuperResolutionFrameRateMultiplier()
	{
		return _aiSuperResolutionFrameRateMultiplier;
	}

	private void SetAiSuperResolutionFrameRateMultiplier(int multiplier)
	{
		bool wasLive = IsLiveSuperResolutionEnabled();
		_aiSuperResolutionFrameRateMultiplier = Math.Max(1, Math.Min(4, multiplier));
		SaveUserSettings();
		UpdateProcessingStatsPanel();
		UpdateVideoInfoOverlayPanel();
		UpdateSuperResolutionStatus();
		if (wasLive != IsLiveSuperResolutionEnabled())
		{
			ScheduleNativeVideoFilterRestart();
		}
	}

	private bool GetLiveFrameGenerationEnabled()
	{
		return _liveFrameGenerationEnabled;
	}

	private void SetLiveFrameGenerationEnabled(bool enabled)
	{
		bool wasLive = IsLiveSuperResolutionEnabled();
		bool wasRtx = IsRtxVideoEnhancementEnabled();
		if (enabled)
		{
			_rtxVideoEnhancementEnabled = false;
		}
		_liveFrameGenerationEnabled = enabled;
		SaveUserSettings();
		ApplyPlaybackOutputSettings();
		UpdateSuperResolutionStatus();
		UpdateSuperResolutionWarningPanel();
		UpdateProcessingStatsPanel();
		UpdateVideoInfoOverlayPanel();
		if (wasRtx)
		{
			RestartPlaybackEngineForRenderMode();
		}
		else if (wasLive != IsLiveSuperResolutionEnabled())
		{
			ScheduleNativeVideoFilterRestart();
		}
	}

	private bool GetRememberUpscalingAndFrameGeneration()
	{
		return _rememberUpscalingAndFrameGeneration;
	}

	private void SetRememberUpscalingAndFrameGeneration(bool remember)
	{
		_rememberUpscalingAndFrameGeneration = remember;
		SaveUserSettings();
	}

	private static string FormatAiSuperResolutionStatusForDisplay(string status)
	{
		if (AiSuperResolutionProgress.TryParse(status, out double percent, out string message))
		{
			return percent.ToString("0", CultureInfo.InvariantCulture) + "% " + message;
		}
		return string.IsNullOrWhiteSpace(status) ? "AI SR ready" : status;
	}

	private void StartAiSuperResolutionPreview()
	{
		_ = RunAiSuperResolutionAsync(fullVideo: false);
	}

	private void StartAiSuperResolutionFullVideo()
	{
		_ = RunAiSuperResolutionAsync(fullVideo: true);
	}

	private async Task RunAiSuperResolutionAsync(bool fullVideo)
	{
		if (_isAiSuperResolutionRunning)
		{
			MessageBox.Show("AI super-resolution is already running.", "AI Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		if (!HasLoadedVideo() || _mediaPlayer == null)
		{
			MessageBox.Show("Open a video before running AI super-resolution.", "AI Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		string? currentVideoPath = GetCurrentVideoPath();
		if (string.IsNullOrWhiteSpace(currentVideoPath) || !File.Exists(currentVideoPath))
		{
			MessageBox.Show("The current video file was not found.", "AI Super Resolution", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (fullVideo)
		{
			int fpsMultiplier = Math.Max(1, Math.Min(4, _aiSuperResolutionFrameRateMultiplier));
			MessageBoxResult result = MessageBox.Show(
				"Fast super-resolution will render the full video to a new MP4" +
				(fpsMultiplier > 1 ? " at " + fpsMultiplier.ToString(CultureInfo.InvariantCulture) + "x FPS" : string.Empty) +
				".\n\nContinue?",
				"AI Super Resolution",
				MessageBoxButton.YesNo,
				MessageBoxImage.Question);
			if (result != MessageBoxResult.Yes)
			{
				return;
			}
		}

		using var cancellationTokenSource = new CancellationTokenSource();
		var progressWindow = new AiSuperResolutionProgressWindow(cancellationTokenSource)
		{
			Owner = this
		};
		var progress = new Progress<string>(status =>
		{
			_aiSuperResolutionStatus = FormatAiSuperResolutionStatusForDisplay(status);
			progressWindow.SetStatus(status);
		});

		bool wasPlaying = _mediaPlayer.IsPlaying;
		long resumeTime = Math.Max(0L, _mediaPlayer.Time);
		_isAiSuperResolutionRunning = true;
		ResourcePlan resourcePlan = GetCurrentResourcePlan();
		_aiSuperResolutionStatus = (fullVideo ? "Starting fast full-video SR..." : "Starting AI SR preview...") + " " + resourcePlan.FormatOfflineStatus() + ".";
		progressWindow.SetStatus(AiSuperResolutionProgress.Format(0.0, _aiSuperResolutionStatus));
		progressWindow.Show();
		if (wasPlaying)
		{
			_mediaPlayer.Pause();
			_isPlaybackPaused = true;
			PlayPauseButton.Content = IconPlay;
		}

		try
		{
			var service = new AiSuperResolutionService();
			var request = new AiSuperResolutionRequest
			{
				InputPath = currentVideoPath,
				StartSeconds = fullVideo ? 0.0 : Math.Max(0.0, resumeTime / 1000.0),
				DurationSeconds = fullVideo ? null : AiSuperResolutionPreviewSeconds,
				TargetMode = "Custom",
				CustomScale = _videoUpscalingScale,
				DetailStrength = _videoUpscalingSharpness,
				UseFrameByFrameAi = !fullVideo,
				FrameRateMultiplier = fullVideo ? _aiSuperResolutionFrameRateMultiplier : 1,
				FrameInterpolationMode = fullVideo ? AiFrameInterpolationMode.FfmpegMotion : AiFrameInterpolationMode.None,
				ResourcePlan = resourcePlan
			};

			AiSuperResolutionResult aiResult = await service.CreateEnhancedVideoAsync(request, progress, cancellationTokenSource.Token);
			_aiSuperResolutionStatus = fullVideo ? "SR complete: " + aiResult.Summary : "AI SR complete: " + aiResult.Summary;
			progressWindow.SetStatus(AiSuperResolutionProgress.Format(100.0, _aiSuperResolutionStatus));
			TrackTemporaryAiSuperResolutionFile(aiResult.OutputPath);
			int index = AddFilesToPlaylist(new[] { aiResult.OutputPath });
			if (index >= 0)
			{
				PlayFromPlaylist(index, keepCurrentSubtitle: false);
			}
			MessageBox.Show("Super-resolution video created.\n\n" + aiResult.Summary + "\n\n" + aiResult.OutputPath, "AI Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (OperationCanceledException)
		{
			_aiSuperResolutionStatus = "AI SR canceled";
			if (wasPlaying && _mediaPlayer != null && HasLoadedVideo())
			{
				_mediaPlayer.Time = resumeTime;
				_mediaPlayer.Play();
				_isPlaybackPaused = false;
				PlayPauseButton.Content = IconPause;
			}
		}
		catch (Exception ex)
		{
			_aiSuperResolutionStatus = fullVideo ? "SR failed: " + ex.Message : "AI SR failed: " + ex.Message;
			MessageBox.Show("AI super-resolution failed.\n\n" + ex.Message, "AI Super Resolution", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		finally
		{
			_isAiSuperResolutionRunning = false;
			if (progressWindow.IsVisible)
			{
				progressWindow.Close();
			}
		}
	}

	private void TrackTemporaryAiSuperResolutionFile(string path)
	{
		if (!string.IsNullOrWhiteSpace(path))
			_temporaryAiSuperResolutionFiles.Add(Path.GetFullPath(path));
	}

	private void CleanupTemporaryAiSuperResolutionFiles()
	{
		try
		{
			string outputDirectory = Path.GetFullPath(AiSuperResolutionService.GetOutputDirectory());
			string appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
			if (!outputDirectory.StartsWith(appDirectory, StringComparison.OrdinalIgnoreCase))
				return;

			foreach (string path in _temporaryAiSuperResolutionFiles.ToArray())
				TryDeleteTemporaryAiSuperResolutionFile(path, outputDirectory);

			_temporaryAiSuperResolutionFiles.Clear();

			if (!Directory.Exists(outputDirectory))
				return;

			foreach (string path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
				TryDeleteTemporaryAiSuperResolutionFile(path, outputDirectory);

			foreach (string directory in Directory.EnumerateDirectories(outputDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
			{
				try
				{
					if (!Directory.EnumerateFileSystemEntries(directory).Any())
						Directory.Delete(directory);
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			App.LogException(ex);
		}
	}

	private static void TryDeleteTemporaryAiSuperResolutionFile(string path, string outputDirectory)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			if (!fullPath.StartsWith(outputDirectory, StringComparison.OrdinalIgnoreCase))
				return;

			if (File.Exists(fullPath))
				File.Delete(fullPath);
		}
		catch
		{
		}
	}

	private void AudioEqualizerPanel_Click(object sender, RoutedEventArgs e)
	{
		if (_audioEqualizerWindow != null && _audioEqualizerWindow.IsVisible)
		{
			_audioEqualizerWindow.Activate();
			return;
		}
		double[] bands = new double[10] { AudioBand0Slider.Value, AudioBand1Slider.Value, AudioBand2Slider.Value, AudioBand3Slider.Value, AudioBand4Slider.Value, AudioBand5Slider.Value, AudioBand6Slider.Value, AudioBand7Slider.Value, AudioBand8Slider.Value, AudioBand9Slider.Value };
		_audioEqualizerWindow = new AudioEqualizerWindow(AudioEqualizerEnableCheckBox.IsChecked == true, AudioPreampSlider.Value, bands, delegate(bool enabled)
		{
			AudioEqualizerEnableCheckBox.IsChecked = enabled;
			_audioEqualizerService?.SetEnabled(enabled);
			SaveAudioEqualizerSettings();
		}, delegate(double value)
		{
			AudioPreampSlider.Value = value;
			_audioEqualizerService?.SetPreamp((float)value);
			SaveAudioEqualizerSettings();
		}, delegate(int bandIndex, double value)
		{
			Slider audioBandSlider = GetAudioBandSlider(bandIndex);
			if (audioBandSlider != null)
			{
				audioBandSlider.Value = value;
			}
			_audioEqualizerService?.SetBand(bandIndex, (float)value);
			SaveAudioEqualizerSettings();
		}, delegate
		{
			AudioPreampSlider.Value = 0.0;
			for (int i = 0; i < 10; i++)
			{
				Slider audioBandSlider = GetAudioBandSlider(i);
				if (audioBandSlider != null)
				{
					audioBandSlider.Value = 0.0;
				}
			}
			_audioEqualizerService?.Reset();
			SaveAudioEqualizerSettings();
		})
		{
			Owner = this
		};
		_audioEqualizerWindow.Closed += delegate
		{
			_audioEqualizerWindow = null;
		};
		_audioEqualizerWindow.Show();
	}

	private void VideoEqualizerPanel_Click(object sender, RoutedEventArgs e)
	{
		if (_videoEqualizerWindow != null && _videoEqualizerWindow.IsVisible)
		{
			_videoEqualizerWindow.Activate();
			return;
		}
		_videoEqualizerWindow = new VideoEqualizerWindow(VideoEqualizerEnableCheckBox.IsChecked == true, BrightnessSlider.Value, ContrastSlider.Value, SaturationSlider.Value, GammaSlider.Value, HueSlider.Value, SharpnessSlider.Value, delegate(bool enabled)
		{
			VideoEqualizerEnableCheckBox.IsChecked = enabled;
			_videoAdjustmentService?.SetEnabled(enabled);
			SaveVideoEqualizerSettings();
			ScheduleNativeVideoFilterRestartIfSharpnessSet();
		}, delegate(string setting, double value)
		{
			SetVideoEqualizerValue(setting, value);
		}, ResetVideoEqualizerValues, ClearVideoEqualizerHistory)
		{
			Owner = this
		};
		_videoEqualizerWindow.Closed += delegate
		{
			_videoEqualizerWindow = null;
		};
		_videoEqualizerWindow.Show();
	}

	private void AudioTrackSelection_Click(object sender, RoutedEventArgs e)
	{
		if (_mediaPlayer == null || _audioTrackManager == null || !HasLoadedVideo())
		{
			MessageBox.Show("Open a video before selecting an audio track.", "Audio Track", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		BeginAudioTrackMenuRefresh();
		if (_audioTrackSelectionWindow != null && _audioTrackSelectionWindow.IsVisible)
		{
			_audioTrackSelectionWindow.RefreshTracks();
			_audioTrackSelectionWindow.Activate();
			return;
		}
		_audioTrackSelectionWindow = new AudioTrackSelectionWindow(() => _audioTrackManager?.GetAudioTracks() ?? Array.Empty<AudioTrackOption>(), () => _audioTrackManager?.IsAudioDisabled ?? false, delegate(int trackId)
		{
			_audioTrackManager?.SelectTrack(trackId, Math.Max(1, Math.Min(100, _volumePercent)), delegate
			{
				BeginAudioTrackMenuRefresh();
				_audioTrackSelectionWindow?.RefreshTracks();
				ApplyCurrentVolume();
			});
		})
		{
			Owner = this
		};
		_audioTrackSelectionWindow.Closed += delegate
		{
			_audioTrackSelectionWindow = null;
		};
		_audioTrackSelectionWindow.Show();
	}

	private Slider? GetAudioBandSlider(int bandIndex)
	{
		if (1 == 0)
		{
		}
		Slider result = bandIndex switch
		{
			0 => AudioBand0Slider, 
			1 => AudioBand1Slider, 
			2 => AudioBand2Slider, 
			3 => AudioBand3Slider, 
			4 => AudioBand4Slider, 
			5 => AudioBand5Slider, 
			6 => AudioBand6Slider, 
			7 => AudioBand7Slider, 
			8 => AudioBand8Slider, 
			9 => AudioBand9Slider, 
			_ => null, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void SetVideoEqualizerValue(string setting, double value)
	{
		switch (setting)
		{
		case "brightness":
			BrightnessSlider.Value = value;
			_videoAdjustmentService?.SetBrightness((float)value);
			break;
		case "contrast":
			ContrastSlider.Value = value;
			_videoAdjustmentService?.SetContrast((float)value);
			break;
		case "saturation":
			SaturationSlider.Value = value;
			_videoAdjustmentService?.SetSaturation((float)value);
			break;
		case "gamma":
			GammaSlider.Value = value;
			_videoAdjustmentService?.SetGamma((float)value);
			break;
		case "hue":
			HueSlider.Value = value;
			_videoAdjustmentService?.SetHue((float)value);
			break;
		case "sharpness":
			SharpnessSlider.Value = value;
			_videoAdjustmentService?.SetSharpness((float)value);
			ScheduleNativeVideoFilterRestart();
			break;
		}
		SaveVideoEqualizerSettings();
	}

	private double GetVideoEqualizerValue(string setting)
	{
		if (1 == 0)
		{
		}
		double result = setting switch
		{
			"brightness" => BrightnessSlider.Value, 
			"contrast" => ContrastSlider.Value, 
			"saturation" => SaturationSlider.Value, 
			"gamma" => GammaSlider.Value, 
			"hue" => HueSlider.Value, 
			"sharpness" => SharpnessSlider.Value, 
			_ => 0.0, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void CloseAudioEqualizerPanel_Click(object sender, RoutedEventArgs e)
	{
		AudioEqualizerPanel.Visibility = Visibility.Collapsed;
	}

	private void CloseVideoEqualizerPanel_Click(object sender, RoutedEventArgs e)
	{
		VideoEqualizerPanel.Visibility = Visibility.Collapsed;
	}

	private void CloseSuperResolutionPanel_Click(object sender, RoutedEventArgs e)
	{
		SuperResolutionPopup.IsOpen = false;
	}

	private void CloseSuperResolutionWarning_Click(object sender, RoutedEventArgs e)
	{
		_superResolutionWarningDismissed = true;
		if (SuperResolutionWarningPanel != null)
		{
			SuperResolutionWarningPanel.Visibility = Visibility.Collapsed;
		}
	}

	private void SuperResolutionEnable_Checked(object sender, RoutedEventArgs e)
	{
		if (!_superResolutionControlsReady || _isUpdatingSuperResolutionControls)
		{
			return;
		}
		bool enabled = sender is CheckBox checkBox && checkBox.IsChecked == true;
		SetVideoUpscalingEnabled(enabled);
		UpdateSuperResolutionStatus();
	}

	private void SuperResolutionTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_superResolutionControlsReady || _isUpdatingSuperResolutionControls || sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item)
		{
			return;
		}
		SetVideoUpscalingTarget(item.Tag?.ToString() ?? "Auto");
		UpdateSuperResolutionStatus();
	}

	private void SuperResolutionScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_superResolutionControlsReady || _isUpdatingSuperResolutionControls)
		{
			return;
		}
		if (sender is Slider slider)
		{
			SetVideoUpscalingScale(slider.Value);
		}
		UpdateSuperResolutionStatus();
	}

	private void SuperResolutionSharpnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_superResolutionControlsReady || _isUpdatingSuperResolutionControls)
		{
			return;
		}
		if (sender is Slider slider)
		{
			SetVideoUpscalingSharpness(slider.Value);
		}
		UpdateSuperResolutionStatus();
	}

	private void ResetSuperResolution_Click(object sender, RoutedEventArgs e)
	{
		_videoUpscalingEnabled = false;
		_videoUpscalingMode = VideoUpscalingModeGpu;
		_videoUpscalingTarget = "Custom";
		_videoUpscalingScale = 1.3;
		_videoUpscalingSharpness = 1.0;
		SaveUserSettings();
		SyncSuperResolutionControlsFromSettings();
		ScheduleNativeVideoFilterRestart();
	}

	private void SaveSuperResolutionComparison_Click(object sender, RoutedEventArgs e)
	{
		if (_imageProcessingService == null || !HasLoadedVideo())
		{
			MessageBox.Show("Open a video before saving an SR comparison.", "Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		try
		{
			bool wasOpen = SuperResolutionPopup.IsOpen;
			SuperResolutionPopup.IsOpen = false;
			UpdateLayout();
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
			}, (DispatcherPriority)7);
			Thread.Sleep(120);
			SuperResolutionFrameResult result;
			try
			{
				result = _imageProcessingService.SaveSuperResolutionComparison(
					GetCurrentVideoPath(),
					MainVideoArea,
					"Custom",
					_videoUpscalingScale,
					_videoUpscalingSharpness);
			}
			finally
			{
				SuperResolutionPopup.IsOpen = wasOpen;
			}
			MessageBox.Show("SR comparison saved.\n\n" + result.Summary + "\n\nEnhanced:\n" + result.EnhancedPath + "\n\nComparison:\n" + result.ComparisonPath, "Super Resolution", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			MessageBox.Show("Could not save SR comparison.\n\n" + ex.Message, "Super Resolution", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void AudioEqualizerEnable_Checked(object sender, RoutedEventArgs e)
	{
		_audioEqualizerService?.SetEnabled(AudioEqualizerEnableCheckBox.IsChecked == true);
		SaveAudioEqualizerSettings();
	}

	private void AudioEqualizerReset_Click(object sender, RoutedEventArgs e)
	{
		AudioPreampSlider.Value = 0.0;
		AudioBand0Slider.Value = 0.0;
		AudioBand1Slider.Value = 0.0;
		AudioBand2Slider.Value = 0.0;
		AudioBand3Slider.Value = 0.0;
		AudioBand4Slider.Value = 0.0;
		AudioBand5Slider.Value = 0.0;
		AudioBand6Slider.Value = 0.0;
		AudioBand7Slider.Value = 0.0;
		AudioBand8Slider.Value = 0.0;
		AudioBand9Slider.Value = 0.0;
		_audioEqualizerService?.Reset();
		SaveAudioEqualizerSettings();
	}

	private void AudioEqualizerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_audioEqualizerService != null && sender is Slider { Tag: not null } slider)
		{
			string text = slider.Tag.ToString() ?? string.Empty;
			float num = (float)slider.Value;
			int result;
			if (text == "preamp")
			{
				_audioEqualizerService.SetPreamp(num);
				SaveAudioEqualizerSettings();
			}
			else if (text.StartsWith("band", StringComparison.OrdinalIgnoreCase) && int.TryParse(text.Substring(4), out result))
			{
				_audioEqualizerService.SetBand(result, num);
				SaveAudioEqualizerSettings();
			}
		}
	}

	private void VideoEqualizerEnable_Checked(object sender, RoutedEventArgs e)
	{
		_videoAdjustmentService?.SetEnabled(VideoEqualizerEnableCheckBox.IsChecked == true);
		SaveVideoEqualizerSettings();
		ScheduleNativeVideoFilterRestartIfSharpnessSet();
	}

	private void VideoEqualizerReset_Click(object sender, RoutedEventArgs e)
	{
		ResetVideoEqualizerValues();
	}

	private void VideoEqualizerClearHistory_Click(object sender, RoutedEventArgs e)
	{
		ClearVideoEqualizerHistory();
	}

	private void VideoEqualizerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_videoAdjustmentService != null && sender is Slider { Tag: not null } slider)
		{
			float num = (float)slider.Value;
			switch (slider.Tag.ToString())
			{
			case "brightness":
				_videoAdjustmentService.SetBrightness(num);
				break;
			case "contrast":
				_videoAdjustmentService.SetContrast(num);
				break;
			case "saturation":
				_videoAdjustmentService.SetSaturation(num);
				break;
			case "gamma":
				_videoAdjustmentService.SetGamma(num);
				break;
			case "hue":
				_videoAdjustmentService.SetHue(num);
				break;
			case "sharpness":
				_videoAdjustmentService.SetSharpness(num);
				ScheduleNativeVideoFilterRestart();
				break;
			}
			SaveVideoEqualizerSettings();
		}
	}

	private void ScheduleNativeVideoFilterRestartIfSharpnessSet()
	{
		if (SharpnessSlider != null && SharpnessSlider.Value > 0.001)
		{
			ScheduleNativeVideoFilterRestart();
		}
	}

	private void ScheduleNativeVideoFilterRestart()
	{
		if (_isLoadingEqualizerSettings || !HasLoadedVideo() || _mediaPlayer == null)
		{
			UpdateProcessingStatsPanel();
			return;
		}
		_videoFilterRestartTimer.Stop();
		_videoFilterRestartTimer.Start();
		UpdateProcessingStatsPanel();
	}

	private void VideoFilterRestartTimer_Tick(object? sender, EventArgs e)
	{
		_videoFilterRestartTimer.Stop();
		if (_mediaPlayer == null || !HasLoadedVideo())
		{
			return;
		}
		long resumeTime = Math.Max(0L, _mediaPlayer.Time);
		bool shouldPlay = _mediaPlayer.IsPlaying;
		RecreatePlaybackEngineAndReplay(resumeTime, shouldPlay);
	}

	private void Stop_Click(object sender, RoutedEventArgs e)
	{
		StopPlayback();
	}

	private void StopPlayback()
	{
		SaveCurrentPlaybackPosition();
		CancelPendingResumeSeek();
		HideResumePrompt();
		_hasReachedEnd = false;
		_mediaPlayer?.Stop();
		_isPlaybackPaused = false;
		SetPlaybackTimerActive(active: false);
		PositionSlider.Value = 0.0;
		CurrentTimeText.Text = "00:00";
		TotalTimeText.Text = "00:00";
		PlayPauseButton.Content = IconPlay;
		_isUiVisible = true;
		UpdateLayoutState();
	}

	private void SeekRelative(long milliseconds)
	{
		if (_mediaPlayer != null && _mediaPlayer.Length > 0)
		{
			long num = Math.Max(0L, Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + milliseconds));
			if (_hasReachedEnd)
			{
				RestartCurrentMediaAtTime(num, play: true);
			}
			else
			{
				_mediaPlayer.Time = num;
			}
		}
	}

	private void Previous_Click(object sender, RoutedEventArgs e)
	{
		if (_playlist.Count != 0)
		{
			int num = _currentIndex - 1;
			if (num < 0)
			{
				num = _playlist.Count - 1;
			}
			PlayFromPlaylist(num, keepCurrentSubtitle: false);
		}
	}

	private void Next_Click(object sender, RoutedEventArgs e)
	{
		PlayNext();
	}

	private void PlayNext()
	{
		if (_playlist.Count != 0)
		{
			int? nextPlaylistIndex = GetNextPlaylistIndex(manualAdvance: true);
			if (nextPlaylistIndex.HasValue)
			{
				PlayFromPlaylist(nextPlaylistIndex.Value, keepCurrentSubtitle: false);
			}
		}
	}

	private int? GetNextPlaylistIndex(bool manualAdvance)
	{
		if (_playlist.Count == 0)
		{
			return null;
		}
		if (_playOrderMode == PlaybackOrderMode.RepeatOne && !manualAdvance)
		{
			return Math.Max(0, _currentIndex);
		}
		if (_playOrderMode == PlaybackOrderMode.Shuffle && _playlist.Count > 1)
		{
			int num;
			do
			{
				num = _shuffleRandom.Next(_playlist.Count);
			}
			while (num == _currentIndex);
			return num;
		}
		int num2 = _currentIndex + 1;
		if (num2 < _playlist.Count)
		{
			return num2;
		}
		return (_playOrderMode == PlaybackOrderMode.RepeatPlaylist || manualAdvance) ? new int?(0) : ((int?)null);
	}

	private void PlayOrderButton_Click(object sender, RoutedEventArgs e)
	{
		PlaybackOrderMode playOrderMode = _playOrderMode;
		if (1 == 0)
		{
		}
		PlaybackOrderMode playOrderMode2 = playOrderMode switch
		{
			PlaybackOrderMode.Single => PlaybackOrderMode.Shuffle, 
			PlaybackOrderMode.Shuffle => PlaybackOrderMode.RepeatPlaylist, 
			PlaybackOrderMode.RepeatPlaylist => PlaybackOrderMode.RepeatOne, 
			_ => PlaybackOrderMode.Single, 
		};
		if (1 == 0)
		{
		}
		_playOrderMode = playOrderMode2;
		UpdatePlayOrderButton();
		SaveUserSettings();
		Focus();
	}

	private void UpdatePlayOrderButton()
	{
		if (PlayOrderButton != null)
		{
			Button playOrderButton = PlayOrderButton;
			PlaybackOrderMode playOrderMode = _playOrderMode;
			if (1 == 0)
			{
			}
			string content = playOrderMode switch
			{
				PlaybackOrderMode.Shuffle => IconShuffle, 
				PlaybackOrderMode.RepeatPlaylist => IconRepeatPlaylist, 
				PlaybackOrderMode.RepeatOne => IconRepeatOne, 
				_ => IconSingle, 
			};
			if (1 == 0)
			{
			}
			playOrderButton.Content = content;
			Button playOrderButton2 = PlayOrderButton;
			PlaybackOrderMode playOrderMode2 = _playOrderMode;
			if (1 == 0)
			{
			}
			content = playOrderMode2 switch
			{
				PlaybackOrderMode.Shuffle => "Play order: shuffle", 
				PlaybackOrderMode.RepeatPlaylist => "Play order: repeat playlist", 
				PlaybackOrderMode.RepeatOne => "Play order: repeat one video", 
				_ => "Play order: single", 
			};
			if (1 == 0)
			{
			}
			playOrderButton2.ToolTip = content;
			NormalizeReadableUiLabels();
		}
	}

	private bool IsResumeRestorePending(string videoPath)
	{
		return IsSamePath(_resumePromptFilePath, videoPath) && _resumePromptTime >= 5000
			|| IsSamePath(_pendingResumeSeekFilePath, videoPath) && _pendingResumeSeekTime >= 5000;
	}

	private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		if (sender is Slider slider)
		{
			object originalSource = e.OriginalSource;
			if (!IsOriginalSourceInsideThumb((DependencyObject?)((originalSource is DependencyObject) ? originalSource : null)) && !(slider.ActualWidth <= 0.0))
			{
				Point position = e.GetPosition(slider);
				double num = Math.Max(0.0, Math.Min(1.0, position.X / slider.ActualWidth));
				slider.Value = (_volumePercent = (int)Math.Round(slider.Minimum + num * (slider.Maximum - slider.Minimum)));
				ApplyCurrentVolume();
				SaveUserSettings();
				e.Handled = true;
			}
		}
	}

	private static bool IsOriginalSourceInsideThumb(DependencyObject? source)
	{
		while (source != null)
		{
			if (source is Thumb)
			{
				return true;
			}
			source = VisualTreeHelper.GetParent(source);
		}
		return false;
	}

	private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_isUpdatingVolumeSlider)
		{
			_volumePercent = Math.Max(0, Math.Min(100, (int)VolumeSlider.Value));
			if (_volumePercent > 0)
			{
				_volumeBeforeMute = _volumePercent;
			}
			ApplyCurrentVolume();
			SaveUserSettings();
		}
	}

	private void VolumeLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (_volumePercent > 0)
		{
			_volumeBeforeMute = _volumePercent;
			_volumePercent = 0;
		}
		else
		{
			_volumePercent = Math.Max(1, Math.Min(200, _volumeBeforeMute));
		}
		ApplyCurrentVolume();
		SaveUserSettings();
		e.Handled = true;
	}

	private void ChangeVolumeBy(int delta)
	{
		_volumePercent = Math.Max(0, Math.Min(200, _volumePercent + delta));
		if (_volumePercent > 0)
		{
			_volumeBeforeMute = _volumePercent;
		}
		ApplyCurrentVolume();
		SaveUserSettings();
	}

	private void ApplyCurrentVolume()
	{
		int num = (_volumePercent = Math.Max(0, Math.Min(200, _volumePercent)));
		if (_mediaPlayer != null)
		{
			AudioTrackManager? audioTrackManager = _audioTrackManager;
			if (audioTrackManager != null && audioTrackManager.IsAudioDisabled)
			{
				_mediaPlayer.Volume = 0;
				_mediaPlayer.Mute = true;
				_audioEqualizerService?.SetVolumeBoost(0f);
			}
			else
			{
				_mediaPlayer.Volume = Math.Min(100, num);
				_mediaPlayer.Mute = num <= 0;
				_audioEqualizerService?.SetVolumeBoost(CalculateVolumeBoostDb(num));
			}
		}
		UpdateVolumeSliderDisplay();
	}

	private void UpdateVolumeSliderDisplay()
	{
		if (VolumeSlider != null)
		{
			_isUpdatingVolumeSlider = true;
			VolumeSlider.Value = Math.Min(100, _volumePercent);
			_isUpdatingVolumeSlider = false;
		}
		if (VolumePercentText != null)
		{
			VolumePercentText.Text = _volumePercent.ToString(CultureInfo.InvariantCulture) + "%";
		}
	}

	private static float CalculateVolumeBoostDb(int volume)
	{
		if (volume <= 100)
		{
			return 0f;
		}
		return (float)(20.0 * Math.Log10((double)volume / 100.0));
	}

	private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (SpeedComboBox.SelectedItem is ComboBoxItem comboBoxItem)
		{
			string text = comboBoxItem.Content.ToString() ?? "1.0x";
			text = text.Replace("x", "");
			if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
			{
				_playbackSpeed = result;
				ApplyPlaybackSpeed();
				SaveUserSettings();
			}
		}
		Focus();
	}

	private void ApplyPlaybackSpeed()
	{
		if (_mediaPlayer == null)
		{
			return;
		}
		try
		{
			_mediaPlayer.SetRate(_playbackSpeed);
		}
		catch
		{
		}
	}

	private void ApplyPlaybackSpeedToComboBox()
	{
		if (SpeedComboBox == null)
		{
			return;
		}
		foreach (object item in (IEnumerable)SpeedComboBox.Items)
		{
			if (item is ComboBoxItem { Content: var content } comboBoxItem)
			{
				string s = content?.ToString()?.Replace("x", "") ?? "";
				if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && Math.Abs(result - _playbackSpeed) < 0.001f)
				{
					SpeedComboBox.SelectedItem = comboBoxItem;
					break;
				}
			}
		}
	}

	private void SetAspectRatio(string aspectRatio)
	{
		_aspectRatio = (string.IsNullOrWhiteSpace(aspectRatio) ? "Default" : aspectRatio);
		ApplyPlaybackOutputSettings();
		SaveUserSettings();
	}

	private void MainVideoArea_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (IsLiveSuperResolutionEnabled())
		{
			ApplySuperResolutionAspectRatio();
		}
		if (IsResumePromptOpen())
		{
			PositionResumePromptPopup();
		}
		if (VideoInfoOverlayPopup != null && VideoInfoOverlayPopup.IsOpen)
		{
			PositionVideoInfoOverlayPopup();
		}
	}

	private void SetVideoUpscalingEnabled(bool enabled)
	{
		bool wasRtx = IsRtxVideoEnhancementEnabled();
		if (enabled)
		{
			_rtxVideoEnhancementEnabled = false;
		}
		_videoUpscalingEnabled = enabled;
		_superResolutionWarningDismissed = false;
		ApplyPlaybackOutputSettings();
		SaveUserSettings();
		UpdateSuperResolutionStatus();
		if (wasRtx)
		{
			RestartPlaybackEngineForRenderMode();
		}
		else
		{
			ScheduleNativeVideoFilterRestart();
		}
		UpdateSuperResolutionWarningPanel();
	}

	private string GetVideoUpscalingMode()
	{
		return _videoUpscalingMode;
	}

	private void SetVideoUpscalingMode(string mode)
	{
		string normalizedMode = NormalizeVideoUpscalingMode(mode);
		if (string.Equals(_videoUpscalingMode, normalizedMode, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_videoUpscalingMode = normalizedMode;
		_superResolutionWarningDismissed = false;
		ApplyPlaybackOutputSettings();
		SaveUserSettings();
		UpdateSuperResolutionStatus();
		if (_videoUpscalingEnabled)
		{
			ScheduleNativeVideoFilterRestart();
		}
		UpdateSuperResolutionWarningPanel();
		UpdateVideoInfoOverlayPanel();
	}

	private void SetVideoUpscalingTarget(string target)
	{
		_videoUpscalingTarget = "Custom";
		_superResolutionWarningDismissed = false;
		ApplyPlaybackOutputSettings();
		SaveUserSettings();
		UpdateSuperResolutionStatus();
		ScheduleNativeVideoFilterRestart();
		UpdateSuperResolutionWarningPanel();
	}

	private void SetVideoUpscalingScale(double scale)
	{
		_videoUpscalingScale = ClampDouble(scale, 1.0, 4.0);
		_videoUpscalingTarget = "Custom";
		_superResolutionWarningDismissed = false;
		ApplyPlaybackOutputSettings();
		SaveUserSettings();
		UpdateSuperResolutionStatus();
		UpdateSuperResolutionWarningPanel();
	}

	private void SetVideoUpscalingSharpness(double sharpness)
	{
		_videoUpscalingSharpness = ClampDouble(sharpness, 0.0, 2.0);
		_superResolutionWarningDismissed = false;
		SaveUserSettings();
		UpdateSuperResolutionStatus();
		ScheduleNativeVideoFilterRestart();
		UpdateSuperResolutionWarningPanel();
	}

	private void SetAudioDelayMs(long delayMs)
	{
		_audioDelayMs = PlaybackRules.AudioDelayMicroseconds(delayMs) / 1000;
		ApplyPlaybackOutputSettings();
		SaveUserSettings();
	}

	private void ApplyPlaybackOutputSettings()
	{
		if (_mediaPlayer != null)
		{
			_mediaPlayer.AspectRatio = ((_aspectRatio == "Default") ? null : _aspectRatio);
			ApplyVideoUpscalingScale();
			_mediaPlayer.SetAudioDelay(PlaybackRules.AudioDelayMicroseconds(_audioDelayMs));
		}
		ApplySuperResolutionAspectRatio();
		UpdateProcessingStatsPanel();
		UpdateVideoInfoOverlayPanel();
	}

	private void Timer_Tick(object? sender, EventArgs e)
	{
		if (_mediaPlayer == null)
		{
			UpdateVideoInfoOverlayPanel();
			SetPlaybackTimerActive(active: false);
			NormalizeReadableUiLabels();
			return;
		}
		if (_showProcessingStatistics)
		{
			UpdateProcessingStatsPanel();
		}
		if (IsResumePromptOpen())
		{
			PositionResumePromptPopup();
		}
		UpdateVideoInfoOverlayPanel();
		UpdateSuperResolutionWarningPanel();
		if (_mediaPlayer.Length <= 0)
		{
			SetPlaybackTimerActive(active: false);
			if (HasLoadedVideo() || EmptyStatePanel.Visibility != Visibility.Visible)
			{
				UpdateLayoutState();
			}
			NormalizeReadableUiLabels();
			return;
		}
		SetPlaybackTimerActive(_mediaPlayer.IsPlaying);
		if (!_isDraggingSlider)
		{
			PositionSlider.Value = (double)_mediaPlayer.Position * 1000.0;
		}
		CurrentTimeText.Text = FormatTime(_mediaPlayer.Time);
		TotalTimeText.Text = FormatTime(_mediaPlayer.Length);
		if (_mediaPlayer.IsPlaying)
		{
			SaveCurrentPlaybackPositionThrottled();
		}
		if (!_mediaPlayer.IsPlaying && _isFullscreen && !_manualUiHidden && !_isUiVisible)
		{
			_isUiVisible = true;
			UpdateLayoutState();
		}
		if (!_mediaPlayer.IsPlaying && _mediaPlayer.Position >= 0.99f)
		{
			int? nextPlaylistIndex = GetNextPlaylistIndex(manualAdvance: false);
			if (nextPlaylistIndex.HasValue)
			{
				PlayFromPlaylist(nextPlaylistIndex.Value, keepCurrentSubtitle: false);
			}
		}
		NormalizeReadableUiLabels();
	}

	private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		if (_mediaPlayer != null && _mediaPlayer.Length > 0 && !(PositionSlider.ActualWidth <= 0.0))
		{
			_isDraggingSlider = true;
			PositionSlider.CaptureMouse();
			PreviewSeekToMousePositionOnSlider(e.GetPosition(PositionSlider));
			e.Handled = true;
		}
	}

	private void PositionSlider_MouseMove(object sender, MouseEventArgs e)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		if (_isDraggingSlider && e.LeftButton == MouseButtonState.Pressed)
		{
			PreviewSeekToMousePositionOnSlider(e.GetPosition(PositionSlider));
		}
	}

	private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		if (_isDraggingSlider)
		{
			CommitSeekToMousePositionOnSlider(e.GetPosition(PositionSlider));
			_isDraggingSlider = false;
			PositionSlider.ReleaseMouseCapture();
			e.Handled = true;
		}
	}

	private double GetSliderRatio(Point point)
	{
		if (PositionSlider.ActualWidth <= 0.0)
		{
			return 0.0;
		}
		return Math.Max(0.0, Math.Min(1.0, point.X / PositionSlider.ActualWidth));
	}

	private void PreviewSeekToMousePositionOnSlider(Point point)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		if (_mediaPlayer != null && _mediaPlayer.Length > 0)
		{
			double sliderRatio = GetSliderRatio(point);
			long num = (long)((double)_mediaPlayer.Length * sliderRatio);
			PositionSlider.Value = sliderRatio * PositionSlider.Maximum;
			CurrentTimeText.Text = FormatTime(num);
			if (!_hasReachedEnd)
			{
				_mediaPlayer.Time = num;
			}
		}
	}

	private void CommitSeekToMousePositionOnSlider(Point point)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		if (_mediaPlayer != null && _mediaPlayer.Length > 0)
		{
			double sliderRatio = GetSliderRatio(point);
			long num = (long)((double)_mediaPlayer.Length * sliderRatio);
			PositionSlider.Value = sliderRatio * PositionSlider.Maximum;
			CurrentTimeText.Text = FormatTime(num);
			if (_hasReachedEnd)
			{
				RestartCurrentMediaAtTime(num, play: true);
			}
			else
			{
				_mediaPlayer.Time = num;
			}
		}
	}

	private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
	}

	private void Window_DragOver(object sender, DragEventArgs e)
	{
		e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None);
		e.Handled = true;
	}

	private void Window_Drop(object sender, DragEventArgs e)
	{
		if (!e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			return;
		}
		string[] array = (string[])e.Data.GetData(DataFormats.FileDrop);
		string text = array.FirstOrDefault((string file) => File.Exists(file) && IsSupportedSubtitleFile(file));
		if (text != null)
		{
			_selectedSubtitlePath = text;
			_lastSubtitlePath = text;
			if (HasLoadedVideo())
			{
				TryApplySubtitleToCurrentPlayer(showError: true);
			}
		}
		string? droppedMedia = array.FirstOrDefault(file => File.Exists(file) && IsSupportedMediaFile(file));
		AddFilesToPlaylist(array);
		int droppedIndex = droppedMedia == null ? -1 : _playlist.FindIndex(existing => string.Equals(existing, droppedMedia, StringComparison.OrdinalIgnoreCase));
		if (droppedIndex >= 0)
		{
			e.Handled = true;
			bool keepDroppedSubtitle = text != null;
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				PlayFromPlaylist(droppedIndex, keepDroppedSubtitle);
			}, (DispatcherPriority)4, Array.Empty<object>());
		}
		else
		{
			e.Handled = text != null;
		}
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Invalid comparison between Unknown and I4
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Invalid comparison between Unknown and I4
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Invalid comparison between Unknown and I4
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Invalid comparison between Unknown and I4
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Invalid comparison between Unknown and I4
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Invalid comparison between Unknown and I4
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Invalid comparison between Unknown and I4
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_0127: Invalid comparison between Unknown and I4
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_0150: Invalid comparison between Unknown and I4
		//IL_016c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Invalid comparison between Unknown and I4
		if (_isFullscreen)
		{
			_manualUiHidden = false;
			_isUiVisible = true;
			UpdateLayoutState();
		}
		if ((int)e.Key == 18 || (int)e.Key == 54)
		{
			TogglePlayPause();
			e.Handled = true;
		}
		else if ((int)e.Key == 23)
		{
			SeekRelative(-5000L);
			e.Handled = true;
		}
		else if ((int)e.Key == 25)
		{
			SeekRelative(5000L);
			e.Handled = true;
		}
		else if ((int)e.Key == 24)
		{
			ChangeVolumeBy(5);
			e.Handled = true;
		}
		else if ((int)e.Key == 26)
		{
			ChangeVolumeBy(-5);
			e.Handled = true;
		}
		else if ((int)e.Key == 53)
		{
			SeekRelative(-10000L);
			e.Handled = true;
		}
		else if ((int)e.Key == 55)
		{
			SeekRelative(10000L);
			e.Handled = true;
		}
		else if ((int)e.Key == 49)
		{
			ToggleFullscreen();
			e.Handled = true;
		}
		else if ((int)e.Key == 13 && _isFullscreen)
		{
			ToggleFullscreen();
			e.Handled = true;
		}
	}

	private static string FormatTime(long milliseconds)
	{
		if (milliseconds < 0)
		{
			milliseconds = 0L;
		}
		TimeSpan timeSpan = TimeSpan.FromMilliseconds(milliseconds);
		return (timeSpan.TotalHours >= 1.0) ? timeSpan.ToString("hh\\:mm\\:ss") : timeSpan.ToString("mm\\:ss");
	}

	private static bool IsSamePath(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
		{
			return false;
		}
		try
		{
			return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Expected O, but got Unknown
		SaveCurrentPlaybackPosition();
		PersistPlaylistIfEnabled();
		SaveUserSettings();
		_playbackHistory.Flush();
		_videoEqualizerSettings.Flush();
		_userSettingsService.Flush();
		ComponentDispatcher.ThreadFilterMessage -= new ThreadMessageEventHandler(ComponentDispatcher_ThreadFilterMessage);
		if (_mouseHookHandle != IntPtr.Zero)
		{
			UnhookWindowsHookEx(_mouseHookHandle);
			_mouseHookHandle = IntPtr.Zero;
		}
		_timer.Stop();
		_fullscreenHideTimer.Stop();
		_singleClickTimer.Stop();
		_videoFilterRestartTimer.Stop();
		CancelPendingResumeSeek();
		_mediaPlayer?.Stop();
		_isPlaybackPaused = false;
		DisposeLiveSuperResolutionPipeline();
		_mediaPlayer?.Dispose();
		_currentMedia?.Dispose();
		_libVLC?.Dispose();
		CleanupTemporaryAiSuperResolutionFiles();
		base.OnClosed(e);
	}

}
