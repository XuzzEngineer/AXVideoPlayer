using System;
using System.IO;
using System.Text.Json;

namespace AXVideoPlayer
{
    internal sealed class UserSettingsService
    {
        private readonly string _filePath;
        private readonly CoalescedTextFileWriter _writer;

        public UserSettingsService()
        {
            _filePath = AppStoragePaths.GetUserDataFilePath("AXVideoPlayer.settings.json");
            _writer = new CoalescedTextFileWriter(_filePath);
        }

        public UserSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return UserSettings.Default;

                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<UserSettings>(json)?.Clamp() ?? UserSettings.Default;
            }
            catch
            {
                return UserSettings.Default;
            }
        }

        public void Save(UserSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings.Clamp(), new JsonSerializerOptions { WriteIndented = true });
                _writer.QueueWrite(json);
            }
            catch
            {
                // User settings are convenience state; never interrupt playback for persistence errors.
            }
        }

        public void Flush()
        {
            _writer.Flush();
        }
    }

    internal sealed class UserSettings
    {
        public int VolumePercent { get; set; } = 100;
        public int VolumeBeforeMute { get; set; } = 100;
        public string PlayOrderMode { get; set; } = "Single";
        public string AspectRatio { get; set; } = "Default";
        public long AudioDelayMs { get; set; }
        public float PlaybackSpeed { get; set; } = 1.0f;
        public string SubtitleLocation { get; set; } = "bottom";
        public string SubtitleSize { get; set; } = "medium";
        public string SubtitleColor { get; set; } = "white";
        public bool AlwaysOnTop { get; set; }
        public bool VideoUpscalingEnabled { get; set; }
        public string VideoUpscalingMode { get; set; } = "GPU";
        public string VideoUpscalingTarget { get; set; } = "Custom";
        public double VideoUpscalingScale { get; set; } = 1.3;
        public double VideoUpscalingSharpness { get; set; } = 1.0;
        public bool RtxVideoEnhancementEnabled { get; set; }
        public int ResumePromptTimeoutSeconds { get; set; } = 4;
        public bool FrameGenerationEnabled { get; set; }
        public int FrameGenerationMultiplier { get; set; } = 2;
        public bool RememberUpscalingAndFrameGeneration { get; set; }
        public string ResourceProfile { get; set; } = ResourceProfileNames.BalancedAuto;
        public bool ShowVideoInfoOverlay { get; set; }

        public static UserSettings Default => new();

        public UserSettings Clamp()
        {
            VolumePercent = Math.Max(0, Math.Min(200, VolumePercent));
            VolumeBeforeMute = Math.Max(1, Math.Min(200, VolumeBeforeMute));
            AudioDelayMs = Math.Max(-2000, Math.Min(2000, AudioDelayMs));
            PlaybackSpeed = PlaybackSpeed is >= 0.25f and <= 4.0f ? PlaybackSpeed : 1.0f;
            AspectRatio = NormalizeOption(AspectRatio, "Default", "16:9", "4:3", "1:1", "21:9");
            SubtitleLocation = NormalizeOption(SubtitleLocation, "bottom", "middle", "top");
            SubtitleSize = NormalizeOption(SubtitleSize, "medium", "small", "large");
            SubtitleColor = NormalizeOption(SubtitleColor, "white", "yellow", "blue");
            VideoUpscalingMode = NormalizeOption(VideoUpscalingMode, "CPU", "GPU");
            VideoUpscalingTarget = NormalizeOption(VideoUpscalingTarget, "Custom", "Auto", "1080p", "1440p", "2160p");
            VideoUpscalingScale = Clamp(VideoUpscalingScale, 1.0, 4.0);
            VideoUpscalingSharpness = Clamp(VideoUpscalingSharpness, 0.0, 2.0);
            ResumePromptTimeoutSeconds = Math.Max(1, Math.Min(60, ResumePromptTimeoutSeconds));
            FrameGenerationMultiplier = Math.Max(1, Math.Min(4, FrameGenerationMultiplier));
            ResourceProfile = ResourceProfileNames.Normalize(ResourceProfile);
            return this;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return min;

            return Math.Max(min, Math.Min(max, value));
        }

        private static string NormalizeOption(string? value, string fallback, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            if (string.Equals(value, fallback, StringComparison.OrdinalIgnoreCase))
                return fallback;

            foreach (string option in allowed)
            {
                if (string.Equals(value, option, StringComparison.OrdinalIgnoreCase))
                    return option;
            }

            return fallback;
        }
    }
}
