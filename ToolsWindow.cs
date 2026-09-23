using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AXVideoPlayer
{
    internal sealed class ToolsWindow : Window
    {
        private readonly Action _takeScreenshot;
        private readonly Func<bool> _getAudioEqualizerEnabled;
        private readonly Action<bool> _setAudioEqualizerEnabled;
        private readonly Func<double> _getAudioPreamp;
        private readonly Func<int, double> _getAudioBand;
        private readonly Action<double> _setAudioPreamp;
        private readonly Action<int, double> _setAudioBand;
        private readonly Action _resetAudioEqualizer;
        private readonly Func<bool> _getVideoEqualizerEnabled;
        private readonly Action<bool> _setVideoEqualizerEnabled;
        private readonly Func<string, double> _getVideoValue;
        private readonly Action<string, double> _setVideoValue;
        private readonly Action _resetVideoEqualizer;
        private readonly Action _clearVideoEqualizerHistory;
        private readonly Func<bool> _getVideoUpscalingEnabled;
        private readonly Action<bool> _setVideoUpscalingEnabled;
        private readonly Func<string> _getVideoUpscalingMode;
        private readonly Action<string> _setVideoUpscalingMode;
        private readonly Func<string> _getVideoUpscalingTarget;
        private readonly Action<string> _setVideoUpscalingTarget;
        private readonly Func<double> _getVideoUpscalingScale;
        private readonly Action<double> _setVideoUpscalingScale;
        private readonly Func<double> _getVideoUpscalingSharpness;
        private readonly Action<double> _setVideoUpscalingSharpness;
        private readonly Func<string> _getVideoUpscalingStatus;
        private readonly Func<bool> _getRtxVideoEnhancementEnabled;
        private readonly Action<bool> _setRtxVideoEnhancementEnabled;
        private readonly Func<string> _getRtxVideoEnhancementStatus;
        private readonly Action _openNvidiaVideoSettings;
        private readonly Action _createAiSuperResolutionPreview;
        private readonly Action _createAiSuperResolutionFullVideo;
        private readonly Action _openOfflineSuperResolutionRenderer;
        private readonly Action _openVideoEncoder;
        private readonly Func<string> _getAiSuperResolutionStatus;
        private readonly Func<int> _getAiSuperResolutionFrameRateMultiplier;
        private readonly Action<int> _setAiSuperResolutionFrameRateMultiplier;
        private readonly Func<bool> _getLiveFrameGenerationEnabled;
        private readonly Action<bool> _setLiveFrameGenerationEnabled;
        private readonly Func<string> _getLiveFrameGenerationStatus;
        private readonly Func<IReadOnlyList<AudioTrackOption>> _loadAudioTracks;
        private readonly Func<bool> _isAudioDisabled;
        private readonly Action<int> _selectAudioTrack;
        private readonly Func<bool> _getResumeEnabled;
        private readonly Action<bool> _setResumeEnabled;
        private readonly Func<int> _getResumePromptTimeoutSeconds;
        private readonly Action<int> _setResumePromptTimeoutSeconds;
        private readonly Action _clearPlaybackHistory;
        private readonly Func<string> _getAspectRatio;
        private readonly Action<string> _setAspectRatio;
        private readonly Func<bool> _getAlwaysOnTop;
        private readonly Action<bool> _setAlwaysOnTop;
        private readonly Func<long> _getAudioDelayMs;
        private readonly Action<long> _setAudioDelayMs;
        private readonly Func<bool> _getShowProcessingStatistics;
        private readonly Action<bool> _setShowProcessingStatistics;
        private readonly Func<bool> _getShowVideoInfoOverlay;
        private readonly Action<bool> _setShowVideoInfoOverlay;
        private readonly Func<string> _getResourcePlanStatus;
        private readonly Action _rescanHardware;
        private readonly Func<bool> _getRememberUpscalingAndFrameGeneration;
        private readonly Action<bool> _setRememberUpscalingAndFrameGeneration;

        private readonly ListBox _navigation = new();
        private readonly ContentControl _content = new();
        private readonly DispatcherTimer _statusRefreshTimer = new();
        private StackPanel? _audioTrackList;
        private TextBlock? _upscalingStatusText;
        private TextBlock? _rtxVideoEnhancementStatusText;
        private TextBlock? _aiSuperResolutionStatusText;
        private TextBlock? _liveFrameGenerationStatusText;
        private TextBlock? _performanceStatusText;
        private bool _isUpdating;
        private const int GwlStyle = -16;
        private const long WsMinimizeBox = 0x00020000L;

        public ToolsWindow(
            Action takeScreenshot,
            Func<bool> getAudioEqualizerEnabled,
            Action<bool> setAudioEqualizerEnabled,
            Func<double> getAudioPreamp,
            Func<int, double> getAudioBand,
            Action<double> setAudioPreamp,
            Action<int, double> setAudioBand,
            Action resetAudioEqualizer,
            Func<bool> getVideoEqualizerEnabled,
            Action<bool> setVideoEqualizerEnabled,
            Func<string, double> getVideoValue,
            Action<string, double> setVideoValue,
            Action resetVideoEqualizer,
            Action clearVideoEqualizerHistory,
            Func<bool> getVideoUpscalingEnabled,
            Action<bool> setVideoUpscalingEnabled,
            Func<string> getVideoUpscalingMode,
            Action<string> setVideoUpscalingMode,
            Func<string> getVideoUpscalingTarget,
            Action<string> setVideoUpscalingTarget,
            Func<double> getVideoUpscalingScale,
            Action<double> setVideoUpscalingScale,
            Func<double> getVideoUpscalingSharpness,
            Action<double> setVideoUpscalingSharpness,
            Func<string> getVideoUpscalingStatus,
            Func<bool> getRtxVideoEnhancementEnabled,
            Action<bool> setRtxVideoEnhancementEnabled,
            Func<string> getRtxVideoEnhancementStatus,
            Action openNvidiaVideoSettings,
            Action createAiSuperResolutionPreview,
            Action createAiSuperResolutionFullVideo,
            Action openOfflineSuperResolutionRenderer,
            Action openVideoEncoder,
            Func<string> getAiSuperResolutionStatus,
            Func<int> getAiSuperResolutionFrameRateMultiplier,
            Action<int> setAiSuperResolutionFrameRateMultiplier,
            Func<bool> getLiveFrameGenerationEnabled,
            Action<bool> setLiveFrameGenerationEnabled,
            Func<string> getLiveFrameGenerationStatus,
            Func<IReadOnlyList<AudioTrackOption>> loadAudioTracks,
            Func<bool> isAudioDisabled,
            Action<int> selectAudioTrack,
            Func<bool> getResumeEnabled,
            Action<bool> setResumeEnabled,
            Func<int> getResumePromptTimeoutSeconds,
            Action<int> setResumePromptTimeoutSeconds,
            Action clearPlaybackHistory,
            Func<string> getAspectRatio,
            Action<string> setAspectRatio,
            Func<bool> getAlwaysOnTop,
            Action<bool> setAlwaysOnTop,
            Func<long> getAudioDelayMs,
            Action<long> setAudioDelayMs,
            Func<bool> getShowProcessingStatistics,
            Action<bool> setShowProcessingStatistics,
            Func<bool> getShowVideoInfoOverlay,
            Action<bool> setShowVideoInfoOverlay,
            Func<string> getResourcePlanStatus,
            Action rescanHardware,
            Func<bool> getRememberUpscalingAndFrameGeneration,
            Action<bool> setRememberUpscalingAndFrameGeneration)
        {
            _takeScreenshot = takeScreenshot;
            _getAudioEqualizerEnabled = getAudioEqualizerEnabled;
            _setAudioEqualizerEnabled = setAudioEqualizerEnabled;
            _getAudioPreamp = getAudioPreamp;
            _getAudioBand = getAudioBand;
            _setAudioPreamp = setAudioPreamp;
            _setAudioBand = setAudioBand;
            _resetAudioEqualizer = resetAudioEqualizer;
            _getVideoEqualizerEnabled = getVideoEqualizerEnabled;
            _setVideoEqualizerEnabled = setVideoEqualizerEnabled;
            _getVideoValue = getVideoValue;
            _setVideoValue = setVideoValue;
            _resetVideoEqualizer = resetVideoEqualizer;
            _clearVideoEqualizerHistory = clearVideoEqualizerHistory;
            _getVideoUpscalingEnabled = getVideoUpscalingEnabled;
            _setVideoUpscalingEnabled = setVideoUpscalingEnabled;
            _getVideoUpscalingMode = getVideoUpscalingMode;
            _setVideoUpscalingMode = setVideoUpscalingMode;
            _getVideoUpscalingTarget = getVideoUpscalingTarget;
            _setVideoUpscalingTarget = setVideoUpscalingTarget;
            _getVideoUpscalingScale = getVideoUpscalingScale;
            _setVideoUpscalingScale = setVideoUpscalingScale;
            _getVideoUpscalingSharpness = getVideoUpscalingSharpness;
            _setVideoUpscalingSharpness = setVideoUpscalingSharpness;
            _getVideoUpscalingStatus = getVideoUpscalingStatus;
            _getRtxVideoEnhancementEnabled = getRtxVideoEnhancementEnabled;
            _setRtxVideoEnhancementEnabled = setRtxVideoEnhancementEnabled;
            _getRtxVideoEnhancementStatus = getRtxVideoEnhancementStatus;
            _openNvidiaVideoSettings = openNvidiaVideoSettings;
            _createAiSuperResolutionPreview = createAiSuperResolutionPreview;
            _createAiSuperResolutionFullVideo = createAiSuperResolutionFullVideo;
            _openOfflineSuperResolutionRenderer = openOfflineSuperResolutionRenderer;
            _openVideoEncoder = openVideoEncoder;
            _getAiSuperResolutionStatus = getAiSuperResolutionStatus;
            _getAiSuperResolutionFrameRateMultiplier = getAiSuperResolutionFrameRateMultiplier;
            _setAiSuperResolutionFrameRateMultiplier = setAiSuperResolutionFrameRateMultiplier;
            _getLiveFrameGenerationEnabled = getLiveFrameGenerationEnabled;
            _setLiveFrameGenerationEnabled = setLiveFrameGenerationEnabled;
            _getLiveFrameGenerationStatus = getLiveFrameGenerationStatus;
            _loadAudioTracks = loadAudioTracks;
            _isAudioDisabled = isAudioDisabled;
            _selectAudioTrack = selectAudioTrack;
            _getResumeEnabled = getResumeEnabled;
            _setResumeEnabled = setResumeEnabled;
            _getResumePromptTimeoutSeconds = getResumePromptTimeoutSeconds;
            _setResumePromptTimeoutSeconds = setResumePromptTimeoutSeconds;
            _clearPlaybackHistory = clearPlaybackHistory;
            _getAspectRatio = getAspectRatio;
            _setAspectRatio = setAspectRatio;
            _getAlwaysOnTop = getAlwaysOnTop;
            _setAlwaysOnTop = setAlwaysOnTop;
            _getAudioDelayMs = getAudioDelayMs;
            _setAudioDelayMs = setAudioDelayMs;
            _getShowProcessingStatistics = getShowProcessingStatistics;
            _setShowProcessingStatistics = setShowProcessingStatistics;
            _getShowVideoInfoOverlay = getShowVideoInfoOverlay;
            _setShowVideoInfoOverlay = setShowVideoInfoOverlay;
            _getResourcePlanStatus = getResourcePlanStatus;
            _rescanHardware = rescanHardware;
            _getRememberUpscalingAndFrameGeneration = getRememberUpscalingAndFrameGeneration;
            _setRememberUpscalingAndFrameGeneration = setRememberUpscalingAndFrameGeneration;

            Title = "Tools";
            Width = 700;
            Height = 520;
            MinWidth = 620;
            MinHeight = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush(32, 32, 32);
            FontSize = 14;
            ShowInTaskbar = false;
            SourceInitialized += (_, _) => DisableMinimizeBox();
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
            };

            Content = BuildLayout();
            _statusRefreshTimer.Interval = TimeSpan.FromSeconds(1);
            _statusRefreshTimer.Tick += (_, _) => RefreshVisibleUpscalingStatus();
            _statusRefreshTimer.Start();
            Closed += (_, _) => _statusRefreshTimer.Stop();
            _navigation.SelectedIndex = 0;
        }

        private void DisableMinimizeBox()
        {
            nint handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            nint style = GetWindowLongPtr(handle, GwlStyle);
            long updatedStyle = style.ToInt64() & ~WsMinimizeBox;
            if (updatedStyle != style.ToInt64())
                SetWindowLongPtr(handle, GwlStyle, new IntPtr(updatedStyle));
        }

        public void RefreshAudioTracks()
        {
            if (_audioTrackList == null)
                return;

            PopulateAudioTrackList(_audioTrackList);
        }

        public void RefreshPerformanceStatus()
        {
            if (_performanceStatusText != null)
                _performanceStatusText.Text = _getResourcePlanStatus();
        }

        private UIElement BuildLayout()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new Border
            {
                Background = Brush(24, 24, 24),
                BorderBrush = Brush(50, 50, 50),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(10)
            };
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var leftPanel = new DockPanel();
            left.Child = leftPanel;

            _navigation.BorderThickness = new Thickness(0);
            _navigation.Background = Brushes.Transparent;
            _navigation.Foreground = Brushes.White;
            _navigation.FontSize = 14;
            _navigation.ItemContainerStyle = CreateNavigationItemStyle();
            _navigation.Items.Add("Screenshot");
            _navigation.Items.Add("Audio Equalizer");
            _navigation.Items.Add("Video Equalizer");
            _navigation.Items.Add("Upscaling");
            _navigation.Items.Add("RTX Video");
            _navigation.Items.Add("Frame Generation");
            _navigation.Items.Add("Video Encoder");
            _navigation.Items.Add("Performance");
            _navigation.Items.Add("Video Info");
            _navigation.Items.Add("Audio Track");
            _navigation.Items.Add("Aspect Ratio");
            _navigation.Items.Add("Playback");
            _navigation.Items.Add("Window");
            _navigation.Items.Add("Resume");
            _navigation.Items.Add("Updates");
            _navigation.SelectionChanged += (_, _) => ShowSelectedPage();
            DockPanel.SetDock(_navigation, Dock.Bottom);
            leftPanel.Children.Add(_navigation);

            var right = new Border
            {
                Background = Brush(32, 32, 32),
                Padding = new Thickness(18)
            };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            right.Child = _content;

            return grid;
        }

        private void ShowSelectedPage()
        {
            string selected = _navigation.SelectedItem?.ToString() ?? "Screenshot";
            _audioTrackList = null;
            _upscalingStatusText = null;
            _rtxVideoEnhancementStatusText = null;
            _aiSuperResolutionStatusText = null;
            _liveFrameGenerationStatusText = null;
            _performanceStatusText = null;

            _content.Content = selected switch
            {
                "Audio Equalizer" => BuildAudioEqualizerPage(),
                "Video Equalizer" => BuildVideoEqualizerPage(),
                "Upscaling" => BuildUpscalingPage(),
                "RTX Video" => BuildRtxVideoPage(),
                "Frame Generation" => BuildFrameGenerationPage(),
                "Video Encoder" => BuildVideoEncoderPage(),
                "Performance" => BuildPerformancePage(),
                "Video Info" => BuildVideoInfoPage(),
                "Audio Track" => BuildAudioTrackPage(),
                "Aspect Ratio" => BuildAspectRatioPage(),
                "Playback" => BuildPlaybackPage(),
                "Window" => BuildWindowPage(),
                "Resume" => BuildResumePage(),
                "Updates" => BuildUpdatesPage(),
                _ => BuildScreenshotPage()
            };
        }

        private UIElement BuildUpdatesPage()
        {
            var panel = CreatePage("Updates");
            panel.Children.Add(new TextBlock
            {
                Text = "Check GitHub releases when you want to.",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap
            });
            var status = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
            var check = CreateActionButton("Check for updates", () => { });
            Button? openButton = null;
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                status.Text = "Checking GitHub...";
                if (openButton != null) panel.Children.Remove(openButton);
                openButton = null;
                try
                {
                    var result = await UpdateCheckService.CheckAsync();
                    status.Text = result.Message;
                    if (result.ReleaseUrl is string url)
                    {
                        openButton = CreateActionButton("Open release page", () =>
                        {
                            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                            catch (Exception ex) { App.LogException(ex); status.Text = "Could not open the release page: " + ex.Message; }
                        });
                        panel.Children.Add(openButton);
                    }
                }
                catch (Exception ex)
                {
                    App.LogException(ex);
                    status.Text = "Could not check GitHub: " + ex.Message;
                }
                finally { check.IsEnabled = true; }
            };
            panel.Children.Add(check);
            panel.Children.Add(status);
            return panel;
        }

        private void RefreshVisibleUpscalingStatus()
        {
            if (_upscalingStatusText != null)
                _upscalingStatusText.Text = _getVideoUpscalingStatus();
            if (_rtxVideoEnhancementStatusText != null)
                _rtxVideoEnhancementStatusText.Text = _getRtxVideoEnhancementStatus();
            if (_liveFrameGenerationStatusText != null)
                _liveFrameGenerationStatusText.Text = _getLiveFrameGenerationStatus();
            if (_aiSuperResolutionStatusText != null)
                _aiSuperResolutionStatusText.Text = _getAiSuperResolutionStatus();
            if (_performanceStatusText != null)
                _performanceStatusText.Text = _getResourcePlanStatus();
        }

        private UIElement BuildScreenshotPage()
        {
            var panel = CreatePage("Screenshot");
            panel.Children.Add(new TextBlock
            {
                Text = "Save a snapshot beside the currently playing video file.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 14)
            });
            panel.Children.Add(CreateActionButton("Take Screenshot", _takeScreenshot));
            return panel;
        }

        private UIElement BuildAudioEqualizerPage()
        {
            var panel = CreatePage("Audio Equalizer");

            var enableBox = new CheckBox
            {
                Content = "Enable audio equalizer",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getAudioEqualizerEnabled(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setAudioEqualizerEnabled(true);
            enableBox.Unchecked += (_, _) => _setAudioEqualizerEnabled(false);
            panel.Children.Add(enableBox);

            Slider preamp = CreateSlider("Preamp", _getAudioPreamp(), -20, 20, "0.0 dB", panel, out TextBlock preampText);
            preamp.ValueChanged += (_, _) =>
            {
                preampText.Text = FormatDb(preamp.Value);
                if (!_isUpdating)
                    _setAudioPreamp(preamp.Value);
            };

            string[] labels = { "60 Hz", "170 Hz", "310 Hz", "600 Hz", "1 kHz", "3 kHz", "6 kHz", "12 kHz", "14 kHz", "16 kHz" };
            var sliders = new List<Slider> { preamp };

            for (int i = 0; i < labels.Length; i++)
            {
                int bandIndex = i;
                Slider slider = CreateSlider(labels[i], _getAudioBand(i), -20, 20, "0.0 dB", panel, out TextBlock valueText);
                slider.ValueChanged += (_, _) =>
                {
                    valueText.Text = FormatDb(slider.Value);
                    if (!_isUpdating)
                        _setAudioBand(bandIndex, slider.Value);
                };
                sliders.Add(slider);
            }

            panel.Children.Add(CreateActionButton("Reset", () =>
            {
                _isUpdating = true;
                foreach (Slider slider in sliders)
                    slider.Value = 0;
                _isUpdating = false;
                _resetAudioEqualizer();
            }));

            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        private UIElement BuildVideoEqualizerPage()
        {
            var panel = CreatePage("Video Equalizer");

            var enableBox = new CheckBox
            {
                Content = "Enable video equalizer",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getVideoEqualizerEnabled(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setVideoEqualizerEnabled(true);
            enableBox.Unchecked += (_, _) => _setVideoEqualizerEnabled(false);
            panel.Children.Add(enableBox);

            Slider brightness = AddVideoSlider(panel, "Brightness", "brightness", 0, 2);
            Slider contrast = AddVideoSlider(panel, "Contrast", "contrast", 0, 2);
            Slider saturation = AddVideoSlider(panel, "Saturation", "saturation", 0, 3);
            Slider gamma = AddVideoSlider(panel, "Gamma", "gamma", 0.1, 3);
            Slider hue = AddVideoSlider(panel, "Hue", "hue", -180, 180);
            Slider sharpness = AddVideoSlider(panel, "Sharpness", "sharpness", 0, 2);

            panel.Children.Add(CreateActionButton("Reset", () =>
            {
                _isUpdating = true;
                brightness.Value = 1.0;
                contrast.Value = 1.0;
                saturation.Value = 1.0;
                gamma.Value = 1.0;
                hue.Value = 0.0;
                sharpness.Value = 0.0;
                _isUpdating = false;
                _resetVideoEqualizer();
            }));

            panel.Children.Add(CreateActionButton("Clear History", _clearVideoEqualizerHistory));

            return panel;
        }

        private UIElement BuildUpscalingPage()
        {
            var panel = CreatePage("Upscaling");

            var statusText = new TextBlock
            {
                Text = _getVideoUpscalingStatus(),
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _upscalingStatusText = statusText;

            void RefreshStatus()
            {
                statusText.Text = _getVideoUpscalingStatus();
            }

            panel.Children.Add(CreateRememberProcessingCheckBox());

            var enableBox = new CheckBox
            {
                Content = "Enable live upscaling",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getVideoUpscalingEnabled(),
                Margin = new Thickness(0, 0, 0, 12)
            };
            enableBox.Checked += (_, _) =>
            {
                _setVideoUpscalingEnabled(true);
                RefreshStatus();
            };
            enableBox.Unchecked += (_, _) =>
            {
                _setVideoUpscalingEnabled(false);
                RefreshStatus();
            };
            panel.Children.Add(enableBox);

            var modeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };
            AddUpscalingModeRadio(modeRow, "CPU SR", "CPU", RefreshStatus);
            AddUpscalingModeRadio(modeRow, "GPU Upscale", "GPU", RefreshStatus);
            panel.Children.Add(modeRow);

            panel.Children.Add(statusText);

            Slider scale = CreateSlider("Scale", _getVideoUpscalingScale(), 1, 4, "0.00", panel, out TextBlock scaleText);
            scaleText.Text = FormatScale(scale.Value);
            scale.ValueChanged += (_, _) =>
            {
                scaleText.Text = FormatScale(scale.Value);
                if (!_isUpdating)
                {
                    _setVideoUpscalingScale(scale.Value);
                    RefreshStatus();
                }
            };

            Slider sharpness = CreateSlider("Enhance", _getVideoUpscalingSharpness(), 0, 2, "0.00", panel, out TextBlock sharpnessText);
            sharpness.ValueChanged += (_, _) =>
            {
                sharpnessText.Text = sharpness.Value.ToString("0.00", CultureInfo.InvariantCulture);
                if (!_isUpdating)
                {
                    _setVideoUpscalingSharpness(sharpness.Value);
                    RefreshStatus();
                }
            };

            panel.Children.Add(new Border
            {
                Height = 1,
                Background = Brush(62, 62, 62),
                Margin = new Thickness(0, 16, 0, 14)
            });

            var aiStatus = new TextBlock
            {
                Text = _getAiSuperResolutionStatus(),
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(aiStatus);
            _aiSuperResolutionStatusText = aiStatus;

            void RefreshAiStatus()
            {
                aiStatus.Text = _getAiSuperResolutionStatus();
            }

            var aiButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };
            aiButtons.Children.Add(CreateActionButton("AI SR Preview", () =>
            {
                _createAiSuperResolutionPreview();
                RefreshAiStatus();
            }));
            aiButtons.Children.Add(CreateActionButton("Fast SR Full Video", () =>
            {
                _createAiSuperResolutionFullVideo();
                RefreshAiStatus();
            }));
            panel.Children.Add(aiButtons);
            panel.Children.Add(CreateActionButton("Render Video File...", _openOfflineSuperResolutionRenderer));
            panel.Children.Add(CreateActionButton("Rescan Hardware", () =>
            {
                _rescanHardware();
                RefreshStatus();
                RefreshAiStatus();
            }));

            return panel;
        }

        private UIElement BuildRtxVideoPage()
        {
            var panel = CreatePage("RTX Video");

            var enableBox = new CheckBox
            {
                Content = "Enable RTX Video compatibility mode",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getRtxVideoEnhancementEnabled(),
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(enableBox);

            var statusText = new TextBlock
            {
                Text = _getRtxVideoEnhancementStatus(),
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _rtxVideoEnhancementStatusText = statusText;

            void RefreshStatus()
            {
                statusText.Text = _getRtxVideoEnhancementStatus();
            }

            enableBox.Checked += (_, _) =>
            {
                _setRtxVideoEnhancementEnabled(true);
                RefreshStatus();
            };
            enableBox.Unchecked += (_, _) =>
            {
                _setRtxVideoEnhancementEnabled(false);
                RefreshStatus();
            };

            panel.Children.Add(statusText);
            panel.Children.Add(new TextBlock
            {
                Text = "This mode uses VLC native Direct3D11 output so the NVIDIA driver can apply RTX Video Super Resolution or HDR when those features are enabled in NVIDIA App or NVIDIA Control Panel.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "The app cannot change the NVIDIA global RTX Video setting directly; use NVIDIA's video settings to enable Super Resolution or HDR.",
                Foreground = Brush(190, 190, 190),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            panel.Children.Add(CreateActionButton("Open NVIDIA Control Panel", _openNvidiaVideoSettings));
            panel.Children.Add(CreateActionButton("Rescan Hardware", () =>
            {
                _rescanHardware();
                RefreshStatus();
            }));

            return panel;
        }

        private UIElement BuildFrameGenerationPage()
        {
            var panel = CreatePage("Frame Generation");

            var liveStatus = new TextBlock
            {
                Text = _getLiveFrameGenerationStatus(),
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(liveStatus);
            _liveFrameGenerationStatusText = liveStatus;

            panel.Children.Add(CreateRememberProcessingCheckBox());

            var liveToggle = new CheckBox
            {
                Content = "Enable realtime frame generation",
                IsChecked = _getLiveFrameGenerationEnabled(),
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(0, 2, 0, 10)
            };
            liveToggle.Checked += (_, _) =>
            {
                if (_isUpdating)
                    return;

                _setLiveFrameGenerationEnabled(true);
                liveStatus.Text = _getLiveFrameGenerationStatus();
            };
            liveToggle.Unchecked += (_, _) =>
            {
                if (_isUpdating)
                    return;

                _setLiveFrameGenerationEnabled(false);
                liveStatus.Text = _getLiveFrameGenerationStatus();
            };
            panel.Children.Add(liveToggle);

            var aiStatus = new TextBlock
            {
                Text = _getAiSuperResolutionStatus(),
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(aiStatus);
            _aiSuperResolutionStatusText = aiStatus;

            void RefreshStatuses()
            {
                liveStatus.Text = _getLiveFrameGenerationStatus();
                aiStatus.Text = _getAiSuperResolutionStatus();
            }

            ComboBox fpsBox = CreateComboRow("FPS increase", panel);
            AddComboItem(fpsBox, "Same", 1);
            AddComboItem(fpsBox, "2x", 2);
            AddComboItem(fpsBox, "3x", 3);
            AddComboItem(fpsBox, "4x", 4);
            SelectComboItemByTag(fpsBox, Math.Max(1, Math.Min(4, _getAiSuperResolutionFrameRateMultiplier())));
            fpsBox.SelectionChanged += (_, _) =>
            {
                if ((fpsBox.SelectedItem as ComboBoxItem)?.Tag is int multiplier)
                {
                    _setAiSuperResolutionFrameRateMultiplier(multiplier);
                    RefreshStatuses();
                }
            };

            panel.Children.Add(CreateActionButton("Generate Full Video", () =>
            {
                _createAiSuperResolutionFullVideo();
                RefreshStatuses();
            }));
            panel.Children.Add(CreateActionButton("Rescan Hardware", () =>
            {
                _rescanHardware();
                RefreshStatuses();
            }));

            return panel;
        }

        private UIElement BuildVideoEncoderPage()
        {
            var panel = CreatePage("Video Encoder");
            panel.Children.Add(new TextBlock
            {
                Text = "Convert the current video or another video file to MP4, MKV, MOV, WebM, or AVI at a selected resolution.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });
            panel.Children.Add(CreateActionButton("Open Video Encoder...", _openVideoEncoder));
            return panel;
        }

        private CheckBox CreateRememberProcessingCheckBox()
        {
            var rememberBox = new CheckBox
            {
                Content = "Remember upscaling and frame generation",
                IsChecked = _getRememberUpscalingAndFrameGeneration(),
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            };
            rememberBox.Checked += (_, _) =>
            {
                if (!_isUpdating)
                    _setRememberUpscalingAndFrameGeneration(true);
            };
            rememberBox.Unchecked += (_, _) =>
            {
                if (!_isUpdating)
                    _setRememberUpscalingAndFrameGeneration(false);
            };

            return rememberBox;
        }

        private UIElement BuildPerformancePage()
        {
            var panel = CreatePage("Performance");

            var statusText = new TextBlock
            {
                Text = _getResourcePlanStatus(),
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            _performanceStatusText = statusText;
            panel.Children.Add(statusText);

            panel.Children.Add(CreateActionButton("Rescan Hardware", () =>
            {
                _rescanHardware();
                statusText.Text = _getResourcePlanStatus();
            }));

            return panel;
        }

        private void AddUpscalingModeRadio(Panel parent, string label, string mode, Action refreshStatus)
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = "LiveUpscalingMode",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = string.Equals(_getVideoUpscalingMode(), mode, StringComparison.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 16, 0)
            };
            radio.Checked += (_, _) =>
            {
                if (_isUpdating)
                    return;

                _setVideoUpscalingMode(mode);
                refreshStatus();
            };
            parent.Children.Add(radio);
        }

        private static ComboBox CreateComboRow(string label, Panel parent)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });

            var comboBox = new ComboBox
            {
                Height = 32,
                MinWidth = 130,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(comboBox, 1);
            grid.Children.Add(comboBox);

            parent.Children.Add(grid);
            return comboBox;
        }

        private static void AddComboItem(ComboBox comboBox, string text, int tag)
        {
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = text,
                Tag = tag
            });
        }

        private static void SelectComboItemByTag(ComboBox comboBox, int tag)
        {
            foreach (object item in comboBox.Items)
            {
                if ((item as ComboBoxItem)?.Tag is int value && value == tag)
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            if (comboBox.Items.Count > 0)
                comboBox.SelectedIndex = 0;
        }

        private UIElement BuildAudioTrackPage()
        {
            var root = CreatePage("Audio Track");
            root.Children.Add(new TextBlock
            {
                Text = "Select the audio track for the current video.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            });

            _audioTrackList = new StackPanel();
            PopulateAudioTrackList(_audioTrackList);
            root.Children.Add(_audioTrackList);
            root.Children.Add(CreateActionButton("Refresh", RefreshAudioTracks));

            return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        }

        private UIElement BuildAspectRatioPage()
        {
            var panel = CreatePage("Aspect Ratio");
            panel.Children.Add(new TextBlock
            {
                Text = "Choose how the video is shaped inside the player.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            });

            string[] options = { "Default", "16:9", "4:3", "1:1", "21:9" };
            string current = options.Contains(_getAspectRatio()) ? _getAspectRatio() : "Default";
            var group = new StackPanel { Orientation = Orientation.Horizontal };

            foreach (string option in options)
            {
                var radio = new RadioButton
                {
                    Content = option,
                    GroupName = "AspectRatio",
                    Foreground = Brushes.White,
                    FontSize = 14,
                    IsChecked = string.Equals(option, current, StringComparison.OrdinalIgnoreCase),
                    Margin = new Thickness(0, 0, 14, 0)
                };
                radio.Checked += (_, _) =>
                {
                    if (!_isUpdating)
                        _setAspectRatio(option);
                };
                group.Children.Add(radio);
            }

            panel.Children.Add(group);
            return panel;
        }

        private UIElement BuildPlaybackPage()
        {
            var panel = CreatePage("Playback");

            Slider delaySlider = CreateSlider("Audio sync", _getAudioDelayMs(), -2000, 2000, "0 ms", panel, out TextBlock delayText);
            delaySlider.TickFrequency = 100;
            delaySlider.IsSnapToTickEnabled = true;
            delaySlider.ValueChanged += (_, _) =>
            {
                long delay = (long)Math.Round(delaySlider.Value);
                delayText.Text = FormatMs(delay);
                if (!_isUpdating)
                    _setAudioDelayMs(delay);
            };

            panel.Children.Add(CreateActionButton("Reset audio sync", () =>
            {
                _isUpdating = true;
                delaySlider.Value = 0;
                delayText.Text = FormatMs(0);
                _isUpdating = false;
                _setAudioDelayMs(0);
            }));

            var statsBox = new CheckBox
            {
                Content = "Show processing statistics",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getShowProcessingStatistics(),
                Margin = new Thickness(0, 18, 0, 0)
            };
            statsBox.Checked += (_, _) => _setShowProcessingStatistics(true);
            statsBox.Unchecked += (_, _) => _setShowProcessingStatistics(false);
            panel.Children.Add(statsBox);

            return panel;
        }

        private UIElement BuildVideoInfoPage()
        {
            var panel = CreatePage("Video Info");

            var infoOverlayBox = new CheckBox
            {
                Content = "Show top-right video info",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getShowVideoInfoOverlay(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            infoOverlayBox.Checked += (_, _) => _setShowVideoInfoOverlay(true);
            infoOverlayBox.Unchecked += (_, _) => _setShowVideoInfoOverlay(false);
            panel.Children.Add(infoOverlayBox);

            return panel;
        }

        private UIElement BuildWindowPage()
        {
            var panel = CreatePage("Window");

            var topMostBox = new CheckBox
            {
                Content = "Always on top",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getAlwaysOnTop(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            topMostBox.Checked += (_, _) => _setAlwaysOnTop(true);
            topMostBox.Unchecked += (_, _) => _setAlwaysOnTop(false);
            panel.Children.Add(topMostBox);

            return panel;
        }

        private UIElement BuildResumePage()
        {
            var panel = CreatePage("Resume");

            var enableBox = new CheckBox
            {
                Content = "Remember music and videos",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = _getResumeEnabled(),
                Margin = new Thickness(0, 0, 0, 14)
            };
            enableBox.Checked += (_, _) => _setResumeEnabled(true);
            enableBox.Unchecked += (_, _) => _setResumeEnabled(false);
            panel.Children.Add(enableBox);

            panel.Children.Add(new TextBlock
            {
                Text = "When this is on, AX Video Player restores the previous music/video playlist and asks to resume each item from its last saved time. This is off by default.",
                Foreground = Brush(210, 210, 210),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            Slider timeoutSlider = CreateSlider("Show for", _getResumePromptTimeoutSeconds(), 1, 60, "0", panel, out TextBlock timeoutText);
            timeoutSlider.TickFrequency = 1;
            timeoutSlider.IsSnapToTickEnabled = true;
            timeoutText.Text = FormatSeconds(timeoutSlider.Value);
            timeoutSlider.ValueChanged += (_, _) =>
            {
                int seconds = Math.Max(1, Math.Min(60, (int)Math.Round(timeoutSlider.Value)));
                timeoutText.Text = FormatSeconds(seconds);
                if (!_isUpdating)
                    _setResumePromptTimeoutSeconds(seconds);
            };

            panel.Children.Add(CreateActionButton("Clear Remembered Playlist and History", _clearPlaybackHistory));
            return panel;
        }

        private void PopulateAudioTrackList(StackPanel target)
        {
            _isUpdating = true;
            target.Children.Clear();

            AddAudioTrackRadio(target, "Disable audio", -1, _isAudioDisabled());

            IReadOnlyList<AudioTrackOption> tracks = _loadAudioTracks();
            if (tracks.Count == 0)
            {
                target.Children.Add(new TextBlock
                {
                    Text = "No audio tracks found yet.",
                    Foreground = Brush(210, 210, 210),
                    FontSize = 14,
                    Margin = new Thickness(0, 10, 0, 10)
                });
            }
            else
            {
                foreach (AudioTrackOption track in tracks)
                    AddAudioTrackRadio(target, track.Name, track.Id, track.IsSelected);
            }

            _isUpdating = false;
        }

        private void AddAudioTrackRadio(Panel parent, string label, int trackId, bool selected)
        {
            var radio = new RadioButton
            {
                Content = label,
                Tag = trackId,
                GroupName = "AudioTracks",
                Foreground = Brushes.White,
                FontSize = 14,
                IsChecked = selected,
                Margin = new Thickness(0, 5, 0, 5)
            };
            radio.Checked += (_, _) =>
            {
                if (_isUpdating || radio.Tag is not int selectedTrackId)
                    return;

                _selectAudioTrack(selectedTrackId);
            };
            parent.Children.Add(radio);
        }

        private Slider AddVideoSlider(Panel parent, string label, string setting, double minimum, double maximum)
        {
            Slider slider = CreateSlider(label, _getVideoValue(setting), minimum, maximum, "0.00", parent, out TextBlock valueText);
            slider.ValueChanged += (_, _) =>
            {
                valueText.Text = slider.Value.ToString("0.00", CultureInfo.InvariantCulture);
                if (!_isUpdating)
                    _setVideoValue(setting, slider.Value);
            };
            return slider;
        }

        private static StackPanel CreatePage(string title)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            });
            return panel;
        }

        private static Button CreateActionButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 34,
                MinWidth = 110,
                FontSize = 14,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static Style CreateNavigationItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 9, 10, 9)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        private static Slider CreateSlider(string label, double value, double minimum, double maximum, string format, Panel parent, out TextBlock valueText)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            grid.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });

            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            valueText = new TextBlock
            {
                Text = format.EndsWith("dB", StringComparison.Ordinal)
                    ? FormatDb(value)
                    : format.EndsWith("ms", StringComparison.Ordinal)
                        ? FormatMs(value)
                        : value.ToString(format, CultureInfo.InvariantCulture),
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            parent.Children.Add(grid);
            return slider;
        }

        private static string FormatDb(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        private static string FormatMs(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        private static string FormatSeconds(double value)
        {
            int seconds = Math.Max(1, Math.Min(60, (int)Math.Round(value)));
            return seconds.ToString(CultureInfo.InvariantCulture) + " sec";
        }

        private static string FormatScale(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        }

        private static SolidColorBrush Brush(byte red, byte green, byte blue)
        {
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    }
}
