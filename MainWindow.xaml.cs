using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using LibVLCSharp.Shared;

namespace AXVideoPlayer
{
    public partial class MainWindow : Window
    {
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private Media? _currentMedia;

        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _fullscreenHideTimer;
        private readonly DispatcherTimer _singleClickTimer;
        private readonly List<string> _playlist = new();

        private bool _isDraggingSlider = false;
        private bool _isFullscreen = false;
        private bool _isPlaylistVisible = false;

        private bool _manualUiHidden = false;
        private bool _isUiVisible = true;
        private bool _hasReachedEnd = false;
        private PlaybackOrderMode _playOrderMode = PlaybackOrderMode.Single;
        private readonly Random _shuffleRandom = new();
        private AudioTrackManager? _audioTrackManager;
        private ImageProcessingService? _imageProcessingService;
        private AudioEqualizerService? _audioEqualizerService;
        private VideoAdjustmentService? _videoAdjustmentService;
        private readonly PlaybackHistoryService _playbackHistory = new();
        private AudioEqualizerWindow? _audioEqualizerWindow;
        private VideoEqualizerWindow? _videoEqualizerWindow;
        private AudioTrackSelectionWindow? _audioTrackSelectionWindow;
        private ToolsWindow? _toolsWindow;
        private DispatcherTimer? _resumePromptTimer;
        private string? _resumePromptFilePath;
        private long _resumePromptTime;

        private bool _playlistWasVisibleBeforeManualHide = false;
        private bool _playlistWasVisibleBeforeFullscreen = false;

        private int _currentIndex = -1;
        private DateTime _lastVideoClickTime = DateTime.MinValue;
        private Point _lastVideoClickPoint;
        private Point _lastMousePosition;
        private DateTime _lastFullscreenToggleUtc = DateTime.MinValue;
        private DateTime _lastPlaybackHistorySaveUtc = DateTime.MinValue;
        private int _volumeBeforeMute = 100;
        private WindowStyle _windowStyleBeforeManualHide = WindowStyle.SingleBorderWindow;
        private bool _subtitleMenuOpen = false;
        private IntPtr _mouseHookHandle = IntPtr.Zero;
        private IntPtr _mainWindowHandle = IntPtr.Zero;
        private LowLevelMouseProc? _mouseHookProc;

        private string? _selectedSubtitlePath = null;
        private string? _lastSubtitlePath = null;
        private string _subtitleLocation = "bottom";
        private string _subtitleSize = "medium";
        private string _subtitleColor = "white";
        private string _aspectRatio = "Default";
        private long _audioDelayMs = 0;

        private enum PlaybackOrderMode
        {
            Single,
            Shuffle,
            RepeatPlaylist,
            RepeatOne
        }

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize();
            CreatePlaybackEngine();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _fullscreenHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _fullscreenHideTimer.Tick += FullscreenHideTimer_Tick;

            _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(230) };
            _singleClickTimer.Tick += SingleClickTimer_Tick;

            ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
            _mainWindowHandle = new WindowInteropHelper(this).EnsureHandle();
            InstallMouseHook();

            Focusable = true;
            SizeChanged += (_, _) =>
            {
                PositionShowUiPopup();
                UpdatePopupSize();
                UpdateCompactControls();
            };
            Focus();

            Loaded += (_, _) =>
            {
                ConfigureTopToolsMenu();
                ConfigureBottomSubtitleMenu();
                UpdatePlayOrderButton();
                UpdateCompactControls();
            };
        }

        private void ConfigureTopToolsMenu()
        {
        }

        private void ConfigureBottomSubtitleMenu()
        {
            var subtitlesMenu = FindMenuItemByHeader(ControlPanel, "Subtitles");
            if (subtitlesMenu == null)
                return;

            RemoveMenuItemByHeader(subtitlesMenu, "Enable subtitles");
            RemoveMenuItemByHeader(subtitlesMenu, "Disable subtitles");
            RemoveTrailingSeparators(subtitlesMenu);

            subtitlesMenu.Items.Add(new Separator());

            var enableItem = new MenuItem { Header = "Enable subtitles" };
            enableItem.Click += EnableSubtitles_Click;
            subtitlesMenu.Items.Add(enableItem);

            var disableItem = new MenuItem { Header = "Disable subtitles" };
            disableItem.Click += DisableSubtitles_Click;
            subtitlesMenu.Items.Add(disableItem);

            subtitlesMenu.SubmenuOpened += (_, _) =>
            {
                _subtitleMenuOpen = true;
                _singleClickTimer.Stop();
                _lastVideoClickTime = DateTime.MinValue;
            };
            subtitlesMenu.SubmenuClosed += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() => _subtitleMenuOpen = false), DispatcherPriority.Background);
            };
        }

        private static MenuItem? FindMenuItemByHeader(DependencyObject root, string headerText)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is MenuItem menuItem)
                {
                    if (string.Equals(menuItem.Header?.ToString(), headerText, StringComparison.OrdinalIgnoreCase))
                        return menuItem;

                    var nested = FindMenuItemByHeader(menuItem, headerText);
                    if (nested != null)
                        return nested;
                }
                else if (child is DependencyObject dependencyObject)
                {
                    var nested = FindMenuItemByHeader(dependencyObject, headerText);
                    if (nested != null)
                        return nested;
                }
            }

            return null;
        }

        private static void RemoveMenuItemByHeader(MenuItem parent, string headerText)
        {
            for (int i = parent.Items.Count - 1; i >= 0; i--)
            {
                if (parent.Items[i] is MenuItem item &&
                    string.Equals(item.Header?.ToString(), headerText, StringComparison.OrdinalIgnoreCase))
                {
                    parent.Items.RemoveAt(i);
                }
            }
        }

        private static void RemoveTrailingSeparators(MenuItem parent)
        {
            while (parent.Items.Count > 0 && parent.Items[parent.Items.Count - 1] is Separator)
                parent.Items.RemoveAt(parent.Items.Count - 1);
        }

        private void CreatePlaybackEngine()
        {
            _libVLC = new LibVLC(BuildLibVlcOptions());
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.Volume = (int)VolumeSlider.Value;
            _audioTrackManager = new AudioTrackManager(_mediaPlayer);
            _imageProcessingService = new ImageProcessingService(_mediaPlayer);
            _audioEqualizerService = new AudioEqualizerService(_mediaPlayer);
            _videoAdjustmentService = new VideoAdjustmentService(_mediaPlayer);
            SyncEqualizerServicesFromUi();
            AttachMediaPlayerEvents(_mediaPlayer);
            ApplyPlaybackOutputSettings();

            VideoView.MediaPlayer = _mediaPlayer;
            VolumePercentText.Text = ((int)VolumeSlider.Value).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void AttachMediaPlayerEvents(MediaPlayer player)
        {
            player.EndReached += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _hasReachedEnd = true;
                PlayPauseButton.Content = "▶";
                _isUiVisible = true;
                UpdateLayoutState();
            }));

            player.Playing += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _hasReachedEnd = false;
                PlayPauseButton.Content = "⏸";

                if (string.IsNullOrWhiteSpace(_selectedSubtitlePath))
                    ForceDisableSubtitles();

                _audioTrackManager?.ReapplyPreferredState(Math.Max(1, (int)VolumeSlider.Value), BeginAudioTrackMenuRefresh);
                _audioEqualizerService?.Apply();
                _videoAdjustmentService?.Apply();
                ApplyPlaybackOutputSettings();
                BeginAudioTrackMenuRefresh();
            }));

            player.Stopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_hasReachedEnd)
                    PlayPauseButton.Content = "▶";
            }));
        }

        private string[] BuildLibVlcOptions()
        {
            string size = _subtitleSize switch
            {
                "small" => "--freetype-rel-fontsize=28",
                "large" => "--freetype-rel-fontsize=16",
                _ => "--freetype-rel-fontsize=22"
            };

            string color = _subtitleColor switch
            {
                "yellow" => "--freetype-color=16776960",
                "blue" => "--freetype-color=255",
                _ => "--freetype-color=16777215"
            };

            string margin = _subtitleLocation switch
            {
                "top" => "--sub-margin=" + GetSubtitleMarginPixels(SubtitleVerticalPosition.Top).ToString(CultureInfo.InvariantCulture),
                "middle" => "--sub-margin=" + GetSubtitleMarginPixels(SubtitleVerticalPosition.Middle).ToString(CultureInfo.InvariantCulture),
                _ => "--sub-margin=" + GetSubtitleMarginPixels(SubtitleVerticalPosition.Bottom).ToString(CultureInfo.InvariantCulture)
            };

            return new[] { size, color, margin };
        }

        private enum SubtitleVerticalPosition
        {
            Bottom,
            Middle,
            Top
        }

        private int GetSubtitleMarginPixels(SubtitleVerticalPosition position)
        {
            double videoHeight = MainVideoArea?.ActualHeight > 120
                ? MainVideoArea.ActualHeight
                : VideoView?.ActualHeight > 120
                    ? VideoView.ActualHeight
                    : Math.Max(360, ActualHeight - 150);

            return position switch
            {
                SubtitleVerticalPosition.Top => Math.Max(24, (int)Math.Round(videoHeight - 52)),
                SubtitleVerticalPosition.Middle => Math.Max(24, (int)Math.Round(videoHeight * 0.5)),
                _ => 24
            };
        }

        private bool HasLoadedVideo() => _mediaPlayer?.Media != null && _currentMedia != null && _currentIndex >= 0;
        private bool IsVideoActivelyPlaying() => HasLoadedVideo() && (_mediaPlayer?.IsPlaying ?? false);

        private void UpdatePopupSize()
        {
            if (FullscreenTopPopup == null || FullscreenBottomPopup == null ||
                FullscreenPlaylistPopup == null || FullscreenTopHost == null ||
                FullscreenBottomHost == null || FullscreenPlaylistHost == null) return;

            double w = ActualWidth > 0 ? ActualWidth : RootGrid.ActualWidth;
            double h = ActualHeight > 0 ? ActualHeight : RootGrid.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double topHeight = TopBar.ActualHeight > 1 ? TopBar.ActualHeight : 46;
            double bottomHeight = ControlPanel.ActualHeight > 1 ? ControlPanel.ActualHeight : 104;
            double playlistWidth = 320;

            FullscreenTopPopup.HorizontalOffset = 0;
            FullscreenTopPopup.VerticalOffset = 0;
            FullscreenTopPopup.Width = w;
            FullscreenTopPopup.Height = topHeight;
            FullscreenTopHost.Width = w;
            FullscreenTopHost.Height = topHeight;

            FullscreenBottomPopup.HorizontalOffset = 0;
            FullscreenBottomPopup.VerticalOffset = Math.Max(0, h - bottomHeight);
            FullscreenBottomPopup.Width = w;
            FullscreenBottomPopup.Height = bottomHeight;
            FullscreenBottomHost.Width = w;
            FullscreenBottomHost.Height = bottomHeight;

            FullscreenPlaylistPopup.HorizontalOffset = 0;
            FullscreenPlaylistPopup.VerticalOffset = topHeight;
            FullscreenPlaylistPopup.Width = playlistWidth;
            FullscreenPlaylistPopup.Height = Math.Max(0, h - topHeight - bottomHeight);
            FullscreenPlaylistHost.Width = playlistWidth;
            FullscreenPlaylistHost.Height = Math.Max(0, h - topHeight - bottomHeight);
        }

        private void UpdateCompactControls()
        {
            bool firstNarrow = IsFirstNarrowLayout();
            bool narrow = IsNarrowLayout();
            bool compact = IsCompactLayout();
            bool tiny = IsTinyLayout();
            bool hideTopBarForCompact = compact && !_isFullscreen && !_manualUiHidden;

            TopLayoutRow.Height = hideTopBarForCompact ? new GridLength(0) : new GridLength(46);
            if (!_isFullscreen && !_manualUiHidden)
            {
                TopBar.Visibility = hideTopBarForCompact ? Visibility.Collapsed : Visibility.Visible;
                TopBar.IsHitTestVisible = !hideTopBarForCompact;
            }

            TimelineControls.Visibility = Visibility.Visible;
            TimelineControlRow.Height = compact ? new GridLength(20) : new GridLength(34);
            ControlLayoutRow.Height = compact ? new GridLength(70) : new GridLength(104);
            TransportControlRow.Height = compact ? new GridLength(42) : new GridLength(54);
            ControlPanel.Padding = compact ? new Thickness(6, 4, 6, 4) : new Thickness(12);
            CurrentTimeText.FontSize = compact ? 10 : 12;
            TotalTimeText.FontSize = compact ? 10 : 12;
            CurrentTimeText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
            TotalTimeText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
            CurrentTimeColumn.Width = tiny ? new GridLength(0) : compact ? new GridLength(50) : new GridLength(90);
            TotalTimeColumn.Width = tiny ? new GridLength(0) : compact ? new GridLength(50) : new GridLength(90);

            SubtitleMenu.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            SetTransportChildVisibility(0, narrow ? Visibility.Collapsed : Visibility.Visible);
            SetTransportChildVisibility(2, narrow ? Visibility.Collapsed : Visibility.Visible);
            SetTransportChildVisibility(3, compact ? Visibility.Collapsed : Visibility.Visible);

            SpeedLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            SpeedComboBox.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            SetVolumeChildVisibilityFromEnd(0, compact ? Visibility.Collapsed : Visibility.Visible);

            VolumePercentText.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
            VolumeSlider.Width = tiny ? 64 : compact ? 78 : 100;
            PlaylistToggleButton.MinWidth = compact ? 42 : 42;
            PlayOrderButton.Visibility = firstNarrow ? Visibility.Collapsed : Visibility.Visible;
            CompactHideUiButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool IsFirstNarrowLayout() => ActualWidth > 0 && ActualWidth < 950;

        private bool IsNarrowLayout() => ActualWidth > 0 && ActualWidth < 850;

        private bool IsCompactLayout() => ActualWidth > 0 && ActualWidth < 700;

        private bool IsTinyLayout() => ActualWidth > 0 && ActualWidth < 500;

        private void SetTransportChildVisibility(int index, Visibility visibility)
        {
            if (TransportControls == null || index < 0 || index >= TransportControls.Children.Count)
                return;

            if (TransportControls.Children[index] is UIElement element)
                element.Visibility = visibility;
        }

        private void SetVolumeChildVisibilityFromEnd(int reverseIndex, Visibility visibility)
        {
            if (VolumeControlGroup.Child is not Panel panel)
                return;

            int index = panel.Children.Count - 1 - reverseIndex;
            if (index < 0 || index >= panel.Children.Count)
                return;

            if (panel.Children[index] is UIElement element)
                element.Visibility = visibility;
        }

        private void EnsureUiInPopup()
        {
            if (TopBar.Parent == RootGrid)
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
            if (TopBar.Parent == FullscreenTopHost)
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
            if (PlaylistPanel.Parent == FullscreenPlaylistHost) return;

            if (PlaylistPanel.Parent == RootGrid)
                RootGrid.Children.Remove(PlaylistPanel);

            FullscreenPlaylistHost.Children.Add(PlaylistPanel);
            PlaylistPanel.Width = 320;
            PlaylistPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            PlaylistPanel.VerticalAlignment = VerticalAlignment.Stretch;
        }

        private void EnsurePlaylistInGrid()
        {
            if (PlaylistPanel.Parent == FullscreenPlaylistHost)
            {
                FullscreenPlaylistHost.Children.Remove(PlaylistPanel);
                RootGrid.Children.Add(PlaylistPanel);
            }

            Grid.SetRow(PlaylistPanel, 0);
            Grid.SetRowSpan(PlaylistPanel, 3);
            Grid.SetColumn(PlaylistPanel, 0);
            PlaylistPanel.ClearValue(FrameworkElement.WidthProperty);
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

                double targetOpacity = visible ? 1.0 : 0.0;
                var anim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(140));
                TopBar.BeginAnimation(UIElement.OpacityProperty, anim);
                ControlPanel.BeginAnimation(UIElement.OpacityProperty, anim.Clone());

                FullscreenTopHost.BeginAnimation(UIElement.OpacityProperty, null);
                FullscreenBottomHost.BeginAnimation(UIElement.OpacityProperty, null);
                FullscreenTopHost.Opacity = 1.0;
                FullscreenBottomHost.Opacity = 1.0;
                SetShowUiButtonVisible(false);
            }
            else
            {
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
                    bool hideTopBarForCompact = IsCompactLayout();
                    TopLayoutRow.Height = hideTopBarForCompact ? new GridLength(0) : new GridLength(46);
                    TopBar.Visibility = hideTopBarForCompact ? Visibility.Collapsed : Visibility.Visible;
                    ControlPanel.Visibility = Visibility.Visible;
                    TopBar.IsHitTestVisible = !hideTopBarForCompact;
                    ControlPanel.IsHitTestVisible = true;
                    SetShowUiButtonVisible(false);
                }
                else
                {
                    TopBar.Visibility = Visibility.Collapsed;
                    ControlPanel.Visibility = Visibility.Collapsed;
                    SetShowUiButtonVisible(_manualUiHidden);
                }
            }
        }

        private void UpdateLayoutState()
        {
            if (!HasLoadedVideo())
            {
                EnsureUiInGrid();
                Grid.SetRow(MainVideoArea, 1);
                Grid.SetRowSpan(MainVideoArea, 1);
                VideoView.Visibility = Visibility.Collapsed;
                EmptyStatePanel.Visibility = Visibility.Visible;

                FullscreenTopPopup.IsOpen = false;
                FullscreenBottomPopup.IsOpen = false;
                FullscreenPlaylistPopup.IsOpen = false;
                _fullscreenHideTimer.Stop();
                SetUiVisibility(true);
                return;
            }

            VideoView.Visibility = Visibility.Visible;
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
                    EnsurePlaylistInPopup();
                UpdatePopupSize();

                if (_manualUiHidden)
                {
                    SetUiVisibility(false);
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
                    SetUiVisibility(true);
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
                    SetUiVisibility(false);
                }
                else
                {
                    Grid.SetRow(MainVideoArea, 1);
                    Grid.SetRowSpan(MainVideoArea, 1);
                    SetUiVisibility(true);
                }
            }
        }

        private void RecreatePlaybackEngineAndReplay(long resumeTime, bool shouldPlay)
        {
            if (_currentIndex < 0 || _currentIndex >= _playlist.Count) return;

            string filePath = _playlist[_currentIndex];
            int volume = (int)VolumeSlider.Value;

            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _currentMedia?.Dispose();
            _libVLC?.Dispose();

            _libVLC = new LibVLC(BuildLibVlcOptions());
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.Volume = volume;
            _audioTrackManager = new AudioTrackManager(_mediaPlayer);
            _imageProcessingService = new ImageProcessingService(_mediaPlayer);
            _audioEqualizerService = new AudioEqualizerService(_mediaPlayer);
            _videoAdjustmentService = new VideoAdjustmentService(_mediaPlayer);
            SyncEqualizerServicesFromUi();
            AttachMediaPlayerEvents(_mediaPlayer);
            VideoView.MediaPlayer = _mediaPlayer;

            _currentMedia = CreateMedia(filePath);
            _mediaPlayer.Media = _currentMedia;
            ApplyPlaybackOutputSettings();

            if (shouldPlay)
            {
                _mediaPlayer.Play();
                PlayPauseButton.Content = "⏸";
            }
            else
            {
                PlayPauseButton.Content = "▶";
            }

            var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            restoreTimer.Tick += (s, e) =>
            {
                restoreTimer.Stop();
                if (_mediaPlayer != null && resumeTime > 0)
                    _mediaPlayer.Time = resumeTime;

                TryApplySubtitleToCurrentPlayer(showError: false);
                _audioTrackManager?.ReapplyPreferredState(Math.Max(1, (int)VolumeSlider.Value), BeginAudioTrackMenuRefresh);
            };

            restoreTimer.Start();
            UpdateLayoutState();
        }

        private Media CreateMedia(string filePath)
        {
            if (_libVLC == null) throw new InvalidOperationException("LibVLC is not initialized.");

            var media = new Media(_libVLC, new Uri(filePath));
            media.AddOption(":no-sub-autodetect-file");
            media.AddOption(":sub-autodetect-fuzzy=0");

            if (string.IsNullOrWhiteSpace(_selectedSubtitlePath))
                media.AddOption(":sub-track=-1");

            return media;
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open Video File",
                Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v|All Files|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                int firstAddedIndex = AddFilesToPlaylist(dialog.FileNames);
                if (firstAddedIndex >= 0)
                    PlayFromPlaylist(firstAddedIndex, false);
            }
        }

        private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add Videos to Playlist",
                Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v|All Files|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
                AddFilesToPlaylist(dialog.FileNames);
        }

        private int AddFilesToPlaylist(IEnumerable<string> files)
        {
            int firstAddedIndex = -1;

            foreach (var file in files)
            {
                if (!File.Exists(file) || !IsSupportedVideoFile(file))
                    continue;

                _playlist.Add(file);
                PlaylistBox.Items.Add(Path.GetFileName(file));

                if (firstAddedIndex < 0)
                    firstAddedIndex = _playlist.Count - 1;
            }

            return firstAddedIndex;
        }

        public void OpenFilesFromCommandLine(IEnumerable<string> files)
        {
            int firstAddedIndex = AddFilesToPlaylist(files);
            if (firstAddedIndex < 0)
                return;

            Dispatcher.BeginInvoke(new Action(() => PlayFromPlaylist(firstAddedIndex, false)), DispatcherPriority.Background);
        }

        private static bool IsSupportedVideoFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v";
        }

        private static bool IsSupportedSubtitleFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension is ".srt" or ".ass" or ".ssa" or ".vtt";
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
                PlaylistColumn.Width = new GridLength(0);
                PlaylistPanel.Visibility = Visibility.Visible;
                FullscreenPlaylistPopup.IsOpen = true;
                PlaylistToggleButton.Content = "☰ Hide";
            }
            else if (_isPlaylistVisible && !_manualUiHidden)
            {
                EnsurePlaylistInGrid();
                PlaylistColumn.Width = new GridLength(280);
                PlaylistPanel.Visibility = Visibility.Visible;
                FullscreenPlaylistPopup.IsOpen = false;
                PlaylistToggleButton.Content = "☰ Hide";
            }
            else
            {
                PlaylistPanel.Visibility = Visibility.Collapsed;
                PlaylistColumn.Width = new GridLength(0);
                FullscreenPlaylistPopup.IsOpen = false;
                PlaylistToggleButton.Content = "☰ Playlist";
            }
        }

        private void SetShowUiButtonVisible(bool visible)
        {
            if (ShowUiPopup == null || ShowUiButton == null) return;

            ShowUiButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ShowUiPopup.IsOpen = visible;

            if (visible)
            {
                PositionShowUiPopup();
                Dispatcher.BeginInvoke(new Action(PositionShowUiPopup), DispatcherPriority.Loaded);
            }
        }

        private bool IsShowUiButtonVisible() => ShowUiPopup != null && ShowUiPopup.IsOpen;

        private void PositionShowUiPopup()
        {
            if (ShowUiPopup == null || ShowUiButton == null) return;

            double buttonWidth = ShowUiButton.ActualWidth > 1 ? ShowUiButton.ActualWidth : 104;
            ShowUiPopup.HorizontalOffset = Math.Max(8, ActualWidth - buttonWidth - 18);
            ShowUiPopup.VerticalOffset = 10;
        }

        private void HideUiButton_Click(object sender, RoutedEventArgs e) => HideUiManually();

        private void HideUiManually()
        {
            if (_isFullscreen && !HasLoadedVideo())
            {
                _manualUiHidden = false;
                UpdateLayoutState();
                return;
            }

            _manualUiHidden = true;
            _playlistWasVisibleBeforeManualHide = _isPlaylistVisible;
            _isPlaylistVisible = false;
            ApplyPlaylistVisibility();
            ApplyManualWindowChromeHidden(true);

            UpdateLayoutState();
        }

        private void ShowUiButton_Click(object sender, RoutedEventArgs e) => ShowUiManually();

        private void ShowUiButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isFullscreen && _manualUiHidden)
                ShowUiManually();
        }

        private void ShowUiManually()
        {
            _manualUiHidden = false;
            _isPlaylistVisible = _playlistWasVisibleBeforeManualHide;
            ApplyPlaylistVisibility();
            ApplyManualWindowChromeHidden(false);

            _isUiVisible = true;
            UpdateLayoutState();
        }

        private void ApplyManualWindowChromeHidden(bool hidden)
        {
            if (_isFullscreen)
                return;

            if (hidden)
            {
                if (WindowStyle != WindowStyle.None)
                    _windowStyleBeforeManualHide = WindowStyle;

                WindowStyle = WindowStyle.None;
            }
            else if (WindowStyle == WindowStyle.None)
            {
                WindowStyle = _windowStyleBeforeManualHide == WindowStyle.None
                    ? WindowStyle.SingleBorderWindow
                    : _windowStyleBeforeManualHide;
            }
        }

        private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentPlaybackPosition();
            StopPlayback();
            _playlist.Clear();
            PlaylistBox.Items.Clear();

            _currentIndex = -1;
            _selectedSubtitlePath = null;
            Title = "AX Video Player";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }

        private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlaylistBox.SelectedIndex >= 0)
                PlayFromPlaylist(PlaylistBox.SelectedIndex, false);
        }

        private void PlayFromPlaylist(int index, bool keepCurrentSubtitle)
        {
            if (_libVLC == null || _mediaPlayer == null || index < 0 || index >= _playlist.Count)
                return;

            SaveCurrentPlaybackPosition();

            if (index != _currentIndex && !keepCurrentSubtitle)
                _selectedSubtitlePath = null;

            _currentIndex = index;
            PlaylistBox.SelectedIndex = index;

            string filePath = _playlist[index];

            _audioTrackManager?.ResetForNewMedia();

            _singleClickTimer.Stop();
            _lastVideoClickTime = DateTime.MinValue;
            Mouse.Capture(null);

            _currentMedia?.Dispose();
            _currentMedia = CreateMedia(filePath);

            _hasReachedEnd = false;
            _mediaPlayer.Media = _currentMedia;
            ApplyPlaybackOutputSettings();
            _mediaPlayer.Play();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_mediaPlayer?.Media == _currentMedia && _mediaPlayer.Length > 0 && _mediaPlayer.Time <= 0)
                    _mediaPlayer.Time = 1;
            }), DispatcherPriority.Background);
            TryApplySubtitleToCurrentPlayer(showError: false);
            Dispatcher.BeginInvoke(new Action(BeginAudioTrackMenuRefresh), DispatcherPriority.Background);

            EmptyStatePanel.Visibility = Visibility.Collapsed;
            VideoView.Visibility = Visibility.Visible;
            PlayPauseButton.Content = "⏸";
            Title = "AX Video Player - " + Path.GetFileName(filePath);

            _isUiVisible = true;
            UpdateLayoutState();
            ShowResumePromptIfNeeded(filePath);
            Focus();
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AreVideoClickShortcutsSuspended())
                return;

            if (e.ClickCount != 2) return;

            if (IsPointInsideVisibleElement(e, TopBar) ||
                IsPointInsideVisibleElement(e, ControlPanel) ||
                IsPointInsideVisibleElement(e, PlaylistPanel) ||
                IsShowUiButtonVisible() && IsPointInsideVisibleElement(e, ShowUiButton))
            {
                return;
            }

            if (IsPointInsideVisibleElement(e, MainVideoArea))
            {
                Focus();
                ToggleFullscreenDebounced();
                e.Handled = true;
            }
        }

        private bool IsPointInsideVisibleElement(MouseButtonEventArgs e, FrameworkElement element)
        {
            if (_isFullscreen && !_isUiVisible && (ReferenceEquals(element, TopBar) || ReferenceEquals(element, ControlPanel)))
                return false;

            if (element.Visibility != Visibility.Visible || element.Opacity <= 0.05 || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            Point point = e.GetPosition(element);
            return point.X >= 0 && point.X <= element.ActualWidth && point.Y >= 0 && point.Y <= element.ActualHeight;
        }

        private void MainVideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AreVideoClickShortcutsSuspended())
                return;

            Focus();

            if (e.ClickCount == 2)
            {
                _singleClickTimer.Stop();
                ToggleFullscreenDebounced();
                e.Handled = true;
                return;
            }

            if (e.ClickCount == 1)
            {
                _singleClickTimer.Stop();
                _singleClickTimer.Start();
                e.Handled = true;
            }
        }

        private void LoadSubtitle_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _singleClickTimer.Stop();
            _lastVideoClickTime = DateTime.MinValue;

            if (_currentIndex < 0 || _currentIndex >= _playlist.Count)
                return;

            var dialog = new OpenFileDialog
            {
                Title = "Load Subtitle File",
                Filter = "Subtitle Files|*.srt;*.ass;*.ssa;*.vtt|All Files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                if (!File.Exists(dialog.FileName))
                {
                    MessageBox.Show("The selected subtitle file does not exist.", "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _selectedSubtitlePath = dialog.FileName;
                _lastSubtitlePath = dialog.FileName;
                TryApplySubtitleToCurrentPlayer(showError: true);
            }
        }

        private void TryApplySubtitleToCurrentPlayer(bool showError)
        {
            if (_mediaPlayer == null) return;

            if (string.IsNullOrWhiteSpace(_selectedSubtitlePath))
            {
                ForceDisableSubtitles();
                return;
            }

            try
            {
                if (!File.Exists(_selectedSubtitlePath)) return;

                string subtitleUri = new Uri(_selectedSubtitlePath).AbsoluteUri;
                bool added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, subtitleUri, true);

                if (!added && showError)
                    MessageBox.Show("The subtitle file could not be loaded. Try converting it to UTF-8 .srt or .vtt.", "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                if (showError)
                    MessageBox.Show("The subtitle file could not be loaded.\n\n" + ex.Message, "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ForceDisableSubtitles()
        {
            if (_mediaPlayer == null) return;

            TrySetMediaPlayerTrack("SetSpu", "Spu", -1);

            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                attempts++;
                TrySetMediaPlayerTrack("SetSpu", "Spu", -1);

                if (attempts >= 8)
                    timer.Stop();
            };

            timer.Start();
        }

        private void TrySetMediaPlayerTrack(string methodName, string propertyName, int trackId)
        {
            if (_mediaPlayer == null) return;

            try
            {
                var type = _mediaPlayer.GetType();
                var method = type.GetMethod(methodName, new[] { typeof(int) });

                if (method != null)
                {
                    method.Invoke(_mediaPlayer, new object[] { trackId });
                    return;
                }

                var property = type.GetProperty(propertyName);
                if (property != null && property.CanWrite)
                    property.SetValue(_mediaPlayer, trackId);
            }
            catch
            {
            }
        }

        private void SubtitleLocationBottom_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting("bottom", _subtitleSize, _subtitleColor);
        private void SubtitleLocationMiddle_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting("middle", _subtitleSize, _subtitleColor);
        private void SubtitleLocationTop_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting("top", _subtitleSize, _subtitleColor);
        private void SubtitleSizeSmall_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting(_subtitleLocation, "small", _subtitleColor);
        private void SubtitleSizeMedium_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting(_subtitleLocation, "medium", _subtitleColor);
        private void SubtitleSizeLarge_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting(_subtitleLocation, "large", _subtitleColor);
        private void SubtitleColorWhite_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting(_subtitleLocation, _subtitleSize, "white");
        private void SubtitleColorYellow_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting(_subtitleLocation, _subtitleSize, "yellow");
        private void SubtitleColorBlue_Click(object sender, RoutedEventArgs e) => ApplySubtitleSetting(_subtitleLocation, _subtitleSize, "blue");

        private void ApplySubtitleSetting(string location, string size, string color)
        {
            _subtitleLocation = location;
            _subtitleSize = size;
            _subtitleColor = color;

            if (_currentIndex < 0 || _currentIndex >= _playlist.Count)
                return;

            long oldTime = _mediaPlayer?.Time ?? 0;
            bool wasPlaying = _mediaPlayer?.IsPlaying ?? true;

            RecreatePlaybackEngineAndReplay(oldTime, wasPlaying);
        }

        private void DisableSubtitles_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_selectedSubtitlePath))
                _lastSubtitlePath = _selectedSubtitlePath;

            _selectedSubtitlePath = null;
            ForceDisableSubtitles();
        }

        private void EnableSubtitles_Click(object sender, RoutedEventArgs e)
        {
            if (!HasLoadedVideo())
                return;

            _selectedSubtitlePath = null;

            int embeddedTrackId = GetFirstEmbeddedSubtitleTrackId();
            if (embeddedTrackId >= 0)
            {
                TrySetMediaPlayerTrack("SetSpu", "Spu", embeddedTrackId);
                return;
            }

            MessageBox.Show("No embedded subtitle track was found in this video.", "Subtitles", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private int GetFirstEmbeddedSubtitleTrackId()
        {
            if (_mediaPlayer?.SpuDescription == null)
                return -1;

            foreach (var track in _mediaPlayer.SpuDescription)
            {
                if (track.Id >= 0)
                    return track.Id;
            }

            return -1;
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

        private void TogglePlayPause()
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.Media == null && _playlist.Count > 0)
            {
                PlayFromPlaylist(0, false);
                return;
            }

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                PlayPauseButton.Content = "▶";
            }
            else
            {
                if (_hasReachedEnd)
                {
                    RestartCurrentMediaAtRatio(0.0, true);
                }
                else
                {
                    _mediaPlayer.Play();
                }

                PlayPauseButton.Content = "⏸";
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                VideoView.Visibility = Visibility.Visible;
            }

            _isUiVisible = true;
            UpdateLayoutState();
            Focus();
        }

        private void RestartCurrentMediaAtRatio(double ratio, bool play)
        {
            if (_mediaPlayer == null || _currentIndex < 0 || _currentIndex >= _playlist.Count) return;

            long targetTime = _mediaPlayer.Length > 0
                ? (long)(_mediaPlayer.Length * Math.Max(0.0, Math.Min(1.0, ratio)))
                : 0;

            RestartCurrentMediaAtTime(targetTime, play);
        }

        private void RestartCurrentMediaAtTime(long targetTime, bool play)
        {
            if (_mediaPlayer == null || _currentIndex < 0 || _currentIndex >= _playlist.Count) return;

            SaveCurrentPlaybackPosition();

            string filePath = _playlist[_currentIndex];
            _currentMedia?.Dispose();
            _currentMedia = CreateMedia(filePath);
            _mediaPlayer.Media = _currentMedia;
            _hasReachedEnd = false;

            if (play)
            {
                _mediaPlayer.Play();
                PlayPauseButton.Content = "⏸";
            }

            var seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            seekTimer.Tick += (s, e) =>
            {
                seekTimer.Stop();

                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    _mediaPlayer.Time = Math.Max(0, Math.Min(_mediaPlayer.Length - 1, targetTime));
                    PositionSlider.Value = _mediaPlayer.Length > 0
                        ? (_mediaPlayer.Time / (double)_mediaPlayer.Length) * PositionSlider.Maximum
                        : 0;

                    CurrentTimeText.Text = FormatTime(_mediaPlayer.Time);
                }

                TryApplySubtitleToCurrentPlayer(showError: false);
                BeginAudioTrackMenuRefresh();
            };

            seekTimer.Start();
        }

        private void BeginAudioTrackMenuRefresh()
        {
            _audioTrackSelectionWindow?.RefreshTracks();
            _toolsWindow?.RefreshAudioTracks();

            if (!HasLoadedVideo())
                return;

            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                attempts++;
                _audioTrackSelectionWindow?.RefreshTracks();
                _toolsWindow?.RefreshAudioTracks();

                if (HasAudioTracks() || attempts >= 8)
                    timer.Stop();
            };
            timer.Start();
        }

        private bool HasAudioTracks()
        {
            try
            {
                if (_mediaPlayer?.AudioTrackDescription == null)
                    return false;

                foreach (var track in _mediaPlayer.AudioTrackDescription)
                {
                    if (track.Id >= 0)
                        return true;
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

        private void Screenshot_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null || _imageProcessingService == null || !HasLoadedVideo())
            {
                MessageBox.Show("Open a video before taking a screenshot.", "Screenshot", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string currentVideoPath = _playlist[_currentIndex];
                string filePath = _imageProcessingService.SaveCurrentFrameSnapshot(currentVideoPath, MainVideoArea);
                MessageBox.Show("Screenshot saved:\n" + filePath, "Screenshot", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save screenshot.\n\n" + ex.Message, "Screenshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Tools_Click(object sender, RoutedEventArgs e)
        {
            if (_toolsWindow != null && _toolsWindow.IsVisible)
            {
                _toolsWindow.Activate();
                return;
            }

            _toolsWindow = new ToolsWindow(
                () => Screenshot_Click(this, new RoutedEventArgs()),
                () => AudioEqualizerEnableCheckBox.IsChecked == true,
                enabled =>
                {
                    AudioEqualizerEnableCheckBox.IsChecked = enabled;
                    _audioEqualizerService?.SetEnabled(enabled);
                },
                () => AudioPreampSlider.Value,
                bandIndex => GetAudioBandSlider(bandIndex)?.Value ?? 0,
                value =>
                {
                    AudioPreampSlider.Value = value;
                    _audioEqualizerService?.SetPreamp((float)value);
                },
                (bandIndex, value) =>
                {
                    Slider? slider = GetAudioBandSlider(bandIndex);
                    if (slider != null)
                        slider.Value = value;

                    _audioEqualizerService?.SetBand(bandIndex, (float)value);
                },
                () =>
                {
                    AudioPreampSlider.Value = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        Slider? slider = GetAudioBandSlider(i);
                        if (slider != null)
                            slider.Value = 0;
                    }

                    _audioEqualizerService?.Reset();
                },
                () => VideoEqualizerEnableCheckBox.IsChecked == true,
                enabled =>
                {
                    VideoEqualizerEnableCheckBox.IsChecked = enabled;
                    _videoAdjustmentService?.SetEnabled(enabled);
                },
                GetVideoEqualizerValue,
                SetVideoEqualizerValue,
                () =>
                {
                    BrightnessSlider.Value = 1.0;
                    ContrastSlider.Value = 1.0;
                    SaturationSlider.Value = 1.0;
                    GammaSlider.Value = 1.0;
                    HueSlider.Value = 0.0;
                    SharpnessSlider.Value = 0.0;
                    _videoAdjustmentService?.Reset();
                },
                () => _audioTrackManager?.GetAudioTracks() ?? Array.Empty<AudioTrackOption>(),
                () => _audioTrackManager?.IsAudioDisabled ?? false,
                trackId =>
                {
                    if (_mediaPlayer == null || _audioTrackManager == null || !HasLoadedVideo())
                    {
                        MessageBox.Show("Open a video before selecting an audio track.", "Audio Track", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    _audioTrackManager.SelectTrack(trackId, Math.Max(1, (int)VolumeSlider.Value), () =>
                    {
                        BeginAudioTrackMenuRefresh();
                        _toolsWindow?.RefreshAudioTracks();
                    });
                },
                () => _playbackHistory.ResumeEnabled,
                enabled => _playbackHistory.SetResumeEnabled(enabled),
                () =>
                {
                    _playbackHistory.ClearHistory();
                    MessageBox.Show("Watched history cleared.", "Resume", MessageBoxButton.OK, MessageBoxImage.Information);
                },
                () => _aspectRatio,
                SetAspectRatio,
                () => Topmost,
                enabled => Topmost = enabled,
                () => _audioDelayMs,
                SetAudioDelayMs)
            {
                Owner = this
            };

            _toolsWindow.Closed += (_, _) => _toolsWindow = null;
            _toolsWindow.Show();
        }

        private void AudioEqualizerPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_audioEqualizerWindow != null && _audioEqualizerWindow.IsVisible)
            {
                _audioEqualizerWindow.Activate();
                return;
            }

            double[] bands =
            {
                AudioBand0Slider.Value,
                AudioBand1Slider.Value,
                AudioBand2Slider.Value,
                AudioBand3Slider.Value,
                AudioBand4Slider.Value,
                AudioBand5Slider.Value,
                AudioBand6Slider.Value,
                AudioBand7Slider.Value,
                AudioBand8Slider.Value,
                AudioBand9Slider.Value
            };

            _audioEqualizerWindow = new AudioEqualizerWindow(
                AudioEqualizerEnableCheckBox.IsChecked == true,
                AudioPreampSlider.Value,
                bands,
                enabled =>
                {
                    AudioEqualizerEnableCheckBox.IsChecked = enabled;
                    _audioEqualizerService?.SetEnabled(enabled);
                },
                value =>
                {
                    AudioPreampSlider.Value = value;
                    _audioEqualizerService?.SetPreamp((float)value);
                },
                (bandIndex, value) =>
                {
                    Slider? slider = GetAudioBandSlider(bandIndex);
                    if (slider != null)
                        slider.Value = value;

                    _audioEqualizerService?.SetBand(bandIndex, (float)value);
                },
                () =>
                {
                    AudioPreampSlider.Value = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        Slider? slider = GetAudioBandSlider(i);
                        if (slider != null)
                            slider.Value = 0;
                    }

                    _audioEqualizerService?.Reset();
                })
            {
                Owner = this
            };

            _audioEqualizerWindow.Closed += (_, _) => _audioEqualizerWindow = null;
            _audioEqualizerWindow.Show();
        }

        private void VideoEqualizerPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_videoEqualizerWindow != null && _videoEqualizerWindow.IsVisible)
            {
                _videoEqualizerWindow.Activate();
                return;
            }

            _videoEqualizerWindow = new VideoEqualizerWindow(
                VideoEqualizerEnableCheckBox.IsChecked == true,
                BrightnessSlider.Value,
                ContrastSlider.Value,
                SaturationSlider.Value,
                GammaSlider.Value,
                HueSlider.Value,
                enabled =>
                {
                    VideoEqualizerEnableCheckBox.IsChecked = enabled;
                    _videoAdjustmentService?.SetEnabled(enabled);
                },
                (setting, value) =>
                {
                    SetVideoEqualizerValue(setting, value);
                },
                () =>
                {
                    BrightnessSlider.Value = 1.0;
                    ContrastSlider.Value = 1.0;
                    SaturationSlider.Value = 1.0;
                    GammaSlider.Value = 1.0;
                    HueSlider.Value = 0.0;
                    SharpnessSlider.Value = 0.0;
                    _videoAdjustmentService?.Reset();
                })
            {
                Owner = this
            };

            _videoEqualizerWindow.Closed += (_, _) => _videoEqualizerWindow = null;
            _videoEqualizerWindow.Show();
        }

        private void AudioTrackSelection_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null || _audioTrackManager == null || !HasLoadedVideo())
            {
                MessageBox.Show("Open a video before selecting an audio track.", "Audio Track", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BeginAudioTrackMenuRefresh();

            if (_audioTrackSelectionWindow != null && _audioTrackSelectionWindow.IsVisible)
            {
                _audioTrackSelectionWindow.RefreshTracks();
                _audioTrackSelectionWindow.Activate();
                return;
            }

            _audioTrackSelectionWindow = new AudioTrackSelectionWindow(
                () => _audioTrackManager?.GetAudioTracks() ?? Array.Empty<AudioTrackOption>(),
                () => _audioTrackManager?.IsAudioDisabled ?? false,
                trackId =>
                {
                    _audioTrackManager?.SelectTrack(trackId, Math.Max(1, (int)VolumeSlider.Value), () =>
                    {
                        BeginAudioTrackMenuRefresh();
                        _audioTrackSelectionWindow?.RefreshTracks();
                    });
                })
            {
                Owner = this
            };

            _audioTrackSelectionWindow.Closed += (_, _) => _audioTrackSelectionWindow = null;
            _audioTrackSelectionWindow.Show();
        }

        private Slider? GetAudioBandSlider(int bandIndex)
        {
            return bandIndex switch
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
                _ => null
            };
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
            }
        }

        private double GetVideoEqualizerValue(string setting)
        {
            return setting switch
            {
                "brightness" => BrightnessSlider.Value,
                "contrast" => ContrastSlider.Value,
                "saturation" => SaturationSlider.Value,
                "gamma" => GammaSlider.Value,
                "hue" => HueSlider.Value,
                _ => 0.0
            };
        }

        private void CloseAudioEqualizerPanel_Click(object sender, RoutedEventArgs e)
        {
            AudioEqualizerPanel.Visibility = Visibility.Collapsed;
        }

        private void CloseVideoEqualizerPanel_Click(object sender, RoutedEventArgs e)
        {
            VideoEqualizerPanel.Visibility = Visibility.Collapsed;
        }

        private void AudioEqualizerEnable_Checked(object sender, RoutedEventArgs e)
        {
            _audioEqualizerService?.SetEnabled(AudioEqualizerEnableCheckBox.IsChecked == true);
        }

        private void AudioEqualizerReset_Click(object sender, RoutedEventArgs e)
        {
            AudioPreampSlider.Value = 0;
            AudioBand0Slider.Value = 0;
            AudioBand1Slider.Value = 0;
            AudioBand2Slider.Value = 0;
            AudioBand3Slider.Value = 0;
            AudioBand4Slider.Value = 0;
            AudioBand5Slider.Value = 0;
            AudioBand6Slider.Value = 0;
            AudioBand7Slider.Value = 0;
            AudioBand8Slider.Value = 0;
            AudioBand9Slider.Value = 0;
            _audioEqualizerService?.Reset();
        }

        private void AudioEqualizerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioEqualizerService == null || sender is not Slider slider || slider.Tag == null)
                return;

            string tag = slider.Tag.ToString() ?? string.Empty;
            float value = (float)slider.Value;

            if (tag == "preamp")
            {
                _audioEqualizerService.SetPreamp(value);
                return;
            }

            if (tag.StartsWith("band", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(tag.Substring(4), out int bandIndex))
            {
                _audioEqualizerService.SetBand(bandIndex, value);
            }
        }

        private void VideoEqualizerEnable_Checked(object sender, RoutedEventArgs e)
        {
            _videoAdjustmentService?.SetEnabled(VideoEqualizerEnableCheckBox.IsChecked == true);
        }

        private void VideoEqualizerReset_Click(object sender, RoutedEventArgs e)
        {
            BrightnessSlider.Value = 1.0;
            ContrastSlider.Value = 1.0;
            SaturationSlider.Value = 1.0;
            GammaSlider.Value = 1.0;
            HueSlider.Value = 0.0;
            SharpnessSlider.Value = 0.0;
            _videoAdjustmentService?.Reset();
        }

        private void VideoEqualizerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_videoAdjustmentService == null || sender is not Slider slider || slider.Tag == null)
                return;

            float value = (float)slider.Value;

            switch (slider.Tag.ToString())
            {
                case "brightness":
                    _videoAdjustmentService.SetBrightness(value);
                    break;
                case "contrast":
                    _videoAdjustmentService.SetContrast(value);
                    break;
                case "saturation":
                    _videoAdjustmentService.SetSaturation(value);
                    break;
                case "gamma":
                    _videoAdjustmentService.SetGamma(value);
                    break;
                case "hue":
                    _videoAdjustmentService.SetHue(value);
                    break;
                case "sharpness":
                    _videoAdjustmentService.SetSharpness(value);
                    break;
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        private void StopPlayback()
        {
            SaveCurrentPlaybackPosition();
            _hasReachedEnd = false;
            _mediaPlayer?.Stop();

            PositionSlider.Value = 0;
            CurrentTimeText.Text = "00:00";
            TotalTimeText.Text = "00:00";
            PlayPauseButton.Content = "▶";

            _isUiVisible = true;
            UpdateLayoutState();
        }

        private void SaveCurrentPlaybackPosition()
        {
            if (_mediaPlayer == null || _currentIndex < 0 || _currentIndex >= _playlist.Count)
                return;

            if (_mediaPlayer.Length <= 0)
                return;

            long time = Math.Max(0, _mediaPlayer.Time);
            long remaining = _mediaPlayer.Length - time;
            string filePath = _playlist[_currentIndex];

            if (time < 5000 || remaining < 5000)
            {
                _playbackHistory.SetPosition(filePath, 0);
                return;
            }

            _playbackHistory.SetPosition(filePath, time);
        }

        private void SaveCurrentPlaybackPositionThrottled()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPlaybackHistorySaveUtc).TotalSeconds < 2)
                return;

            _lastPlaybackHistorySaveUtc = now;
            SaveCurrentPlaybackPosition();
        }

        private void ShowResumePromptIfNeeded(string filePath)
        {
            if (!_playbackHistory.ResumeEnabled || _mediaPlayer == null)
                return;

            long resumeTime = _playbackHistory.GetPosition(filePath);
            if (resumeTime < 5000)
                return;

            _resumePromptFilePath = filePath;
            _resumePromptTime = resumeTime;
            ResumePromptText.Text = "Resume from " + FormatTime(resumeTime) + "?";
            ResumePromptPanel.BeginAnimation(UIElement.OpacityProperty, null);
            ResumePromptPanel.Visibility = Visibility.Visible;
            ResumePromptPanel.Opacity = 1.0;

            _resumePromptTimer?.Stop();
            _resumePromptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _resumePromptTimer.Tick += (_, _) =>
            {
                _resumePromptTimer?.Stop();
                FadeResumePrompt();
            };
            _resumePromptTimer.Start();
        }

        private void ResumePromptYes_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_resumePromptFilePath))
                SeekToResumeTime(_resumePromptFilePath, _resumePromptTime);

            HideResumePrompt();
        }

        private void ResumePromptNo_Click(object sender, RoutedEventArgs e)
        {
            HideResumePrompt();
        }

        private void FadeResumePrompt()
        {
            var animation = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(350));
            animation.Completed += (_, _) => HideResumePrompt();
            ResumePromptPanel.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private void HideResumePrompt()
        {
            _resumePromptTimer?.Stop();
            ResumePromptPanel.BeginAnimation(UIElement.OpacityProperty, null);
            ResumePromptPanel.Opacity = 0.0;
            ResumePromptPanel.Visibility = Visibility.Collapsed;
            _resumePromptFilePath = null;
            _resumePromptTime = 0;
        }

        private void SeekToResumeTime(string filePath, long resumeTime)
        {
            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };

            timer.Tick += (_, _) =>
            {
                attempts++;

                if (_mediaPlayer == null || _currentIndex < 0 || _currentIndex >= _playlist.Count ||
                    !string.Equals(_playlist[_currentIndex], filePath, StringComparison.OrdinalIgnoreCase))
                {
                    timer.Stop();
                    return;
                }

                if (_mediaPlayer.Length > 0)
                {
                    _mediaPlayer.Time = Math.Max(0, Math.Min(_mediaPlayer.Length - 1, resumeTime));
                    timer.Stop();
                    return;
                }

                if (attempts >= 12)
                    timer.Stop();
            };

            timer.Start();
        }

        private void SeekRelative(long milliseconds)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0)
                return;

            long targetTime = Math.Max(0, Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + milliseconds));

            if (_hasReachedEnd)
            {
                RestartCurrentMediaAtTime(targetTime, true);
                return;
            }

            _mediaPlayer.Time = targetTime;
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;

            int newIndex = _currentIndex - 1;
            if (newIndex < 0)
                newIndex = _playlist.Count - 1;

            PlayFromPlaylist(newIndex, false);
        }

        private void Next_Click(object sender, RoutedEventArgs e) => PlayNext();

        private void PlayNext()
        {
            if (_playlist.Count == 0) return;

            int? nextIndex = GetNextPlaylistIndex(manualAdvance: true);
            if (nextIndex.HasValue)
                PlayFromPlaylist(nextIndex.Value, false);
        }

        private int? GetNextPlaylistIndex(bool manualAdvance)
        {
            if (_playlist.Count == 0)
                return null;

            if (_playOrderMode == PlaybackOrderMode.RepeatOne && !manualAdvance)
                return Math.Max(0, _currentIndex);

            if (_playOrderMode == PlaybackOrderMode.Shuffle && _playlist.Count > 1)
            {
                int next;
                do
                {
                    next = _shuffleRandom.Next(_playlist.Count);
                } while (next == _currentIndex);

                return next;
            }

            int newIndex = _currentIndex + 1;
            if (newIndex < _playlist.Count)
                return newIndex;

            return (_playOrderMode == PlaybackOrderMode.RepeatPlaylist || manualAdvance) ? 0 : null;
        }

        private void PlayOrderButton_Click(object sender, RoutedEventArgs e)
        {
            _playOrderMode = _playOrderMode switch
            {
                PlaybackOrderMode.Single => PlaybackOrderMode.Shuffle,
                PlaybackOrderMode.Shuffle => PlaybackOrderMode.RepeatPlaylist,
                PlaybackOrderMode.RepeatPlaylist => PlaybackOrderMode.RepeatOne,
                _ => PlaybackOrderMode.Single
            };

            UpdatePlayOrderButton();
            Focus();
        }

        private void UpdatePlayOrderButton()
        {
            if (PlayOrderButton == null)
                return;

            PlayOrderButton.Content = _playOrderMode switch
            {
                PlaybackOrderMode.Shuffle => "⇄",
                PlaybackOrderMode.RepeatPlaylist => "↻",
                PlaybackOrderMode.RepeatOne => "①",
                _ => "→"
            };

            PlayOrderButton.ToolTip = _playOrderMode switch
            {
                PlaybackOrderMode.Shuffle => "Play order: shuffle",
                PlaybackOrderMode.RepeatPlaylist => "Play order: repeat playlist",
                PlaybackOrderMode.RepeatOne => "Play order: repeat one video",
                _ => "Play order: single"
            };
        }

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreenDebounced()
        {
            DateTime now = DateTime.UtcNow;

            if ((now - _lastFullscreenToggleUtc).TotalMilliseconds < 280)
                return;

            _lastFullscreenToggleUtc = now;
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                _playlistWasVisibleBeforeFullscreen = _isPlaylistVisible;
                ApplyManualWindowChromeHidden(false);
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                Background = System.Windows.Media.Brushes.Black;

                _isFullscreen = true;
                _manualUiHidden = false;
                _isUiVisible = true;
                _isPlaylistVisible = false;
                ApplyPlaylistVisibility();
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;

                _isFullscreen = false;
                _isPlaylistVisible = _playlistWasVisibleBeforeFullscreen;
                ApplyPlaylistVisibility();
                if (_manualUiHidden)
                    ApplyManualWindowChromeHidden(true);
            }

            _isUiVisible = true;
            UpdateLayoutState();
            Focus();
        }

        private void ShowUiForFullscreenNearCursor(Point windowPoint)
        {
            if (!_isFullscreen) return;

            if (_manualUiHidden)
                _manualUiHidden = false;

            if (!_isUiVisible)
            {
                _isUiVisible = true;
                UpdateLayoutState();
            }
            else if (IsVideoActivelyPlaying())
            {
                _fullscreenHideTimer.Stop();
                _fullscreenHideTimer.Start();
            }
        }

        private void FullscreenHideTimer_Tick(object? sender, EventArgs e)
        {
            _fullscreenHideTimer.Stop();

            if (_isFullscreen && !_manualUiHidden && IsVideoActivelyPlaying())
            {
                _isUiVisible = false;
                UpdateLayoutState();
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isFullscreen)
            {
                Point p = e.GetPosition(this);

                if (Distance(p, _lastMousePosition) > 2)
                {
                    _lastMousePosition = p;
                    ShowUiForFullscreenNearCursor(p);
                }
            }
        }

        private void SingleClickTimer_Tick(object? sender, EventArgs e)
        {
            _singleClickTimer.Stop();
            if (AreVideoClickShortcutsSuspended())
                return;

            TogglePlayPause();
        }

        private void ComponentDispatcher_ThreadFilterMessage(ref MSG msg, ref bool handled)
        {
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_LBUTTONDBLCLK = 0x0203;

            if (msg.message == WM_MOUSEMOVE)
            {
                if (_isFullscreen && IsCursorInsideThisWindow())
                {
                    Point p = GetCursorPointInThisWindow();

                    if (Distance(p, _lastMousePosition) > 2)
                    {
                        _lastMousePosition = p;
                        ShowUiForFullscreenNearCursor(p);
                    }
                }

                return;
            }

            if (msg.message != WM_LBUTTONDOWN && msg.message != WM_LBUTTONDBLCLK)
                return;

            if (AreVideoClickShortcutsSuspended())
                return;

            Point screenPoint = GetCursorScreenPoint();
            if (!IsScreenPointInsideMainViewingSurface(screenPoint))
                return;

            Point windowPoint = PointFromScreen(screenPoint);

            Focus();

            if (msg.message == WM_LBUTTONDBLCLK)
            {
                _singleClickTimer.Stop();
                _lastVideoClickTime = DateTime.MinValue;
                ToggleFullscreenDebounced();
                handled = true;
                return;
            }

            DateTime now = DateTime.UtcNow;
            double elapsedMs = (now - _lastVideoClickTime).TotalMilliseconds;
            double distance = Distance(windowPoint, _lastVideoClickPoint);

            if (elapsedMs <= 500 && distance <= 20)
            {
                _singleClickTimer.Stop();
                _lastVideoClickTime = DateTime.MinValue;
                ToggleFullscreenDebounced();
                handled = true;
                return;
            }

            _lastVideoClickTime = now;
            _lastVideoClickPoint = windowPoint;
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        }

        private void InstallMouseHook()
        {
            if (_mouseHookHandle != IntPtr.Zero)
                return;

            _mouseHookProc = LowLevelMouseHookCallback;
            _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, IntPtr.Zero, 0);
        }

        private IntPtr LowLevelMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == new IntPtr(WM_LBUTTONDOWN_LL))
            {
                var hookInfo = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Point screenPoint = new(hookInfo.pt.X, hookInfo.pt.Y);

                if (AreVideoClickShortcutsSuspended())
                    return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

                if (!IsMainWindowClientClickAtHookTime(screenPoint))
                    return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);

                Dispatcher.BeginInvoke(new Action(() => HandleGlobalMouseLeftButtonDown(screenPoint)), DispatcherPriority.Input);
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private void HandleGlobalMouseLeftButtonDown(Point screenPoint)
        {
            if (AreVideoClickShortcutsSuspended())
                return;

            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return;

            var mainHandle = GetMainWindowHandle();
            if (!IsMainWindowOrChildWindow(mainHandle, foregroundWindow))
                return;

            if (!IsScreenPointInMainWindowClientArea(screenPoint))
                return;

            if (!IsScreenPointInsideMainViewingSurface(screenPoint))
                return;

            Point windowPoint = PointFromScreen(screenPoint);

            Focus();

            DateTime now = DateTime.UtcNow;
            double elapsedMs = (now - _lastVideoClickTime).TotalMilliseconds;
            double distance = Distance(windowPoint, _lastVideoClickPoint);

            if (elapsedMs <= 500 && distance <= 20)
            {
                _singleClickTimer.Stop();
                _lastVideoClickTime = DateTime.MinValue;
                ToggleFullscreenDebounced();
                return;
            }

            _lastVideoClickTime = now;
            _lastVideoClickPoint = windowPoint;
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        }

        private bool AreVideoClickShortcutsSuspended()
        {
            return _subtitleMenuOpen ||
                   (_toolsWindow != null && _toolsWindow.IsVisible) ||
                   (_audioEqualizerWindow != null && _audioEqualizerWindow.IsVisible) ||
                   (_videoEqualizerWindow != null && _videoEqualizerWindow.IsVisible) ||
                   (_audioTrackSelectionWindow != null && _audioTrackSelectionWindow.IsVisible);
        }

        private bool IsCursorInsideThisWindow()
        {
            Point point = GetCursorPointInThisWindow();
            return point.X >= 0 && point.X <= ActualWidth && point.Y >= 0 && point.Y <= ActualHeight;
        }

        private Point GetCursorPointInThisWindow()
        {
            return PointFromScreen(GetCursorScreenPoint());
        }

        private Point GetCursorScreenPoint()
        {
            GetCursorPos(out POINT screenPoint);
            return new Point(screenPoint.X, screenPoint.Y);
        }

        private bool IsWindowPointInsideMainViewingSurface(Point windowPoint)
        {
            return IsScreenPointInsideMainViewingSurface(PointToScreen(windowPoint));
        }

        private bool IsScreenPointInsideMainViewingSurface(Point screenPoint)
        {
            Point windowPoint = PointFromScreen(screenPoint);
            if (!IsWindowPointInsideRootGrid(windowPoint))
                return false;

            if (MainVideoArea.Visibility != Visibility.Visible || MainVideoArea.ActualWidth <= 0 || MainVideoArea.ActualHeight <= 0)
                return false;

            if (!IsScreenPointInsideVisibleElement(screenPoint, MainVideoArea))
                return false;

            return !IsScreenPointInsideVisibleElement(screenPoint, TopBar) &&
                   !IsScreenPointInsideVisibleElement(screenPoint, ControlPanel) &&
                   !IsScreenPointInsideVisibleElement(screenPoint, PlaylistPanel) &&
                   !(IsShowUiButtonVisible() && IsScreenPointInsideVisibleElement(screenPoint, ShowUiButton));
        }

        private bool IsWindowPointInsideRootGrid(Point windowPoint)
        {
            return RootGrid.ActualWidth > 0 &&
                   RootGrid.ActualHeight > 0 &&
                   windowPoint.X >= 0 &&
                   windowPoint.X <= RootGrid.ActualWidth &&
                   windowPoint.Y >= 0 &&
                   windowPoint.Y <= RootGrid.ActualHeight;
        }

        private bool IsScreenPointInMainWindowClientArea(Point screenPoint)
        {
            IntPtr mainHandle = GetMainWindowHandle();
            if (mainHandle == IntPtr.Zero)
                return false;

            IntPtr hitTest = SendMessage(mainHandle, WM_NCHITTEST, IntPtr.Zero, MakeLParam(screenPoint));
            return hitTest == new IntPtr(HTCLIENT);
        }

        private bool IsMainWindowClientClickAtHookTime(Point screenPoint)
        {
            IntPtr mainHandle = GetMainWindowHandle();
            if (mainHandle == IntPtr.Zero)
                return false;

            IntPtr foregroundWindow = GetForegroundWindow();
            if (!IsMainWindowOrChildWindow(mainHandle, foregroundWindow))
                return false;

            IntPtr hitTest = SendMessage(mainHandle, WM_NCHITTEST, IntPtr.Zero, MakeLParam(screenPoint));
            return hitTest == new IntPtr(HTCLIENT);
        }

        private IntPtr GetMainWindowHandle()
        {
            if (_mainWindowHandle == IntPtr.Zero)
                _mainWindowHandle = new WindowInteropHelper(this).Handle;

            return _mainWindowHandle;
        }

        private static bool IsMainWindowOrChildWindow(IntPtr mainHandle, IntPtr foregroundWindow)
        {
            return foregroundWindow == mainHandle || IsChild(mainHandle, foregroundWindow);
        }

        private bool IsWindowPointInsideVisibleElement(Point windowPoint, FrameworkElement element)
        {
            return IsScreenPointInsideVisibleElement(PointToScreen(windowPoint), element);
        }

        private bool IsScreenPointInsideVisibleElement(Point screenPoint, FrameworkElement element)
        {
            if (_isFullscreen && !_isUiVisible && (ReferenceEquals(element, TopBar) || ReferenceEquals(element, ControlPanel)))
                return false;

            if (element.Visibility != Visibility.Visible || element.Opacity <= 0.05 || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            Point topLeft = element.PointToScreen(new Point(0, 0));
            Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

            double left = Math.Min(topLeft.X, bottomRight.X);
            double right = Math.Max(topLeft.X, bottomRight.X);
            double top = Math.Min(topLeft.Y, bottomRight.Y);
            double bottom = Math.Max(topLeft.Y, bottomRight.Y);

            return screenPoint.X >= left && screenPoint.X <= right &&
                   screenPoint.Y >= top && screenPoint.Y <= bottom;
        }

        private Point TranslatePoint(Point windowPoint, FrameworkElement target)
        {
            Point windowScreenPoint = PointToScreen(windowPoint);
            Point targetScreenOrigin = target.PointToScreen(new Point(0, 0));
            return new Point(windowScreenPoint.X - targetScreenOrigin.X, windowScreenPoint.Y - targetScreenOrigin.Y);
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN_LL = 0x0201;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static IntPtr MakeLParam(Point point)
        {
            int x = (short)Math.Round(point.X);
            int y = (short)Math.Round(point.Y);
            int value = (y << 16) | (x & 0xFFFF);
            return new IntPtr(value);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider slider) return;
            if (IsOriginalSourceInsideThumb(e.OriginalSource as DependencyObject)) return;
            if (slider.ActualWidth <= 0) return;

            Point point = e.GetPosition(slider);
            double ratio = Math.Max(0.0, Math.Min(1.0, point.X / slider.ActualWidth));
            int volume = (int)Math.Round(slider.Minimum + ratio * (slider.Maximum - slider.Minimum));

            slider.Value = volume;

            if (_mediaPlayer != null)
            {
                if (_audioTrackManager?.IsAudioDisabled ?? false)
                {
                    _mediaPlayer.Volume = 0;
                    _mediaPlayer.Mute = true;
                }
                else
                {
                    _mediaPlayer.Mute = volume <= 0;
                    _mediaPlayer.Volume = volume;
                }
            }

            if (VolumePercentText != null)
                VolumePercentText.Text = volume.ToString(CultureInfo.InvariantCulture) + "%";

            e.Handled = true;
        }

        private static bool IsOriginalSourceInsideThumb(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Thumb)
                    return true;

                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int volume = (int)VolumeSlider.Value;
            if (volume > 0)
                _volumeBeforeMute = volume;

            if (_mediaPlayer != null)
            {
                if (_audioTrackManager?.IsAudioDisabled ?? false)
                {
                    _mediaPlayer.Volume = 0;
                    _mediaPlayer.Mute = true;
                }
                else
                {
                    _mediaPlayer.Volume = volume;
                    _mediaPlayer.Mute = volume <= 0;
                }
            }

            if (VolumePercentText != null)
                VolumePercentText.Text = volume.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void VolumeLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            int currentVolume = (int)VolumeSlider.Value;

            if (currentVolume > 0)
            {
                _volumeBeforeMute = currentVolume;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = Math.Max(1, Math.Min(100, _volumeBeforeMute));
            }

            e.Handled = true;
        }

        private void ChangeVolumeBy(int delta)
        {
            int currentVolume = (int)VolumeSlider.Value;
            int newVolume = Math.Max(0, Math.Min(100, currentVolume + delta));

            VolumeSlider.Value = newVolume;

            if (_mediaPlayer != null)
            {
                if (_audioTrackManager?.IsAudioDisabled ?? false)
                {
                    _mediaPlayer.Volume = 0;
                    _mediaPlayer.Mute = true;
                }
                else
                {
                    _mediaPlayer.Volume = newVolume;
                    _mediaPlayer.Mute = newVolume <= 0;
                }
            }

            VolumePercentText.Text = newVolume.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (SpeedComboBox.SelectedItem is ComboBoxItem item)
            {
                string text = item.Content.ToString() ?? "1.0x";
                text = text.Replace("x", "");

                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed))
                    _mediaPlayer.SetRate(speed);
            }

            Focus();
        }

        private void SetAspectRatio(string aspectRatio)
        {
            _aspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? "Default" : aspectRatio;
            ApplyPlaybackOutputSettings();
        }

        private void SetAudioDelayMs(long delayMs)
        {
            _audioDelayMs = Math.Max(-2000, Math.Min(2000, delayMs));
            ApplyPlaybackOutputSettings();
        }

        private void ApplyPlaybackOutputSettings()
        {
            if (_mediaPlayer == null)
                return;

            _mediaPlayer.AspectRatio = _aspectRatio == "Default" ? null : _aspectRatio;
            _mediaPlayer.SetAudioDelay(_audioDelayMs * 1000);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.Length <= 0)
            {
                UpdateLayoutState();
                return;
            }

            if (!_isDraggingSlider)
                PositionSlider.Value = _mediaPlayer.Position * 1000.0;

            CurrentTimeText.Text = FormatTime(_mediaPlayer.Time);
            TotalTimeText.Text = FormatTime(_mediaPlayer.Length);

            if (_mediaPlayer.IsPlaying)
                SaveCurrentPlaybackPositionThrottled();

            if (!_mediaPlayer.IsPlaying && _isFullscreen && !_manualUiHidden && !_isUiVisible)
            {
                _isUiVisible = true;
                UpdateLayoutState();
            }

            if (!_mediaPlayer.IsPlaying && _mediaPlayer.Position >= 0.99f)
            {
                int? nextIndex = GetNextPlaylistIndex(manualAdvance: false);
                if (nextIndex.HasValue)
                    PlayFromPlaylist(nextIndex.Value, false);
            }
        }

        private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0 || PositionSlider.ActualWidth <= 0)
                return;

            _isDraggingSlider = true;
            PositionSlider.CaptureMouse();
            PreviewSeekToMousePositionOnSlider(e.GetPosition(PositionSlider));
            e.Handled = true;
        }

        private void PositionSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingSlider || e.LeftButton != MouseButtonState.Pressed)
                return;

            PreviewSeekToMousePositionOnSlider(e.GetPosition(PositionSlider));
        }

        private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingSlider)
                return;

            CommitSeekToMousePositionOnSlider(e.GetPosition(PositionSlider));
            _isDraggingSlider = false;
            PositionSlider.ReleaseMouseCapture();
            e.Handled = true;
        }

        private double GetSliderRatio(Point point)
        {
            if (PositionSlider.ActualWidth <= 0)
                return 0.0;

            return Math.Max(0.0, Math.Min(1.0, point.X / PositionSlider.ActualWidth));
        }

        private void PreviewSeekToMousePositionOnSlider(Point point)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0)
                return;

            double ratio = GetSliderRatio(point);
            long targetTime = (long)(_mediaPlayer.Length * ratio);

            PositionSlider.Value = ratio * PositionSlider.Maximum;
            CurrentTimeText.Text = FormatTime(targetTime);

            if (!_hasReachedEnd)
                _mediaPlayer.Time = targetTime;
        }

        private void CommitSeekToMousePositionOnSlider(Point point)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0)
                return;

            double ratio = GetSliderRatio(point);
            long targetTime = (long)(_mediaPlayer.Length * ratio);

            PositionSlider.Value = ratio * PositionSlider.Maximum;
            CurrentTimeText.Text = FormatTime(targetTime);

            if (_hasReachedEnd)
                RestartCurrentMediaAtTime(targetTime, true);
            else
                _mediaPlayer.Time = targetTime;
        }

        private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

            int firstAddedIndex = AddFilesToPlaylist(files);

            if (firstAddedIndex >= 0)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(new Action(() => PlayFromPlaylist(firstAddedIndex, false)), DispatcherPriority.Background);
                return;
            }

            foreach (string file in files)
            {
                if (File.Exists(file) && IsSupportedSubtitleFile(file) && HasLoadedVideo())
                {
                    _selectedSubtitlePath = file;
                    TryApplySubtitleToCurrentPlayer(showError: true);
                    break;
                }
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isFullscreen)
            {
                _manualUiHidden = false;
                _isUiVisible = true;
                UpdateLayoutState();
            }

            if (e.Key == Key.Space || e.Key == Key.K)
            {
                TogglePlayPause();
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                SeekRelative(-5000);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                SeekRelative(5000);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                ChangeVolumeBy(5);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                ChangeVolumeBy(-5);
                e.Handled = true;
            }
            else if (e.Key == Key.J)
            {
                SeekRelative(-10000);
                e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                SeekRelative(10000);
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        private static string FormatTime(long milliseconds)
        {
            if (milliseconds < 0)
                milliseconds = 0;

            var time = TimeSpan.FromMilliseconds(milliseconds);
            return time.TotalHours >= 1
                ? time.ToString(@"hh\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveCurrentPlaybackPosition();
            ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;

            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            _timer.Stop();
            _fullscreenHideTimer.Stop();
            _singleClickTimer.Stop();

            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _currentMedia?.Dispose();
            _libVLC?.Dispose();

            base.OnClosed(e);
        }
    }
}
