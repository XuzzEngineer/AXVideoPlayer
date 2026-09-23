using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AXVideoPlayer
{
    internal sealed class VideoEqualizerSettingsService
    {
        private readonly string _filePath;
        private readonly CoalescedTextFileWriter _writer;
        private EqualizerSettingsData? _data;

        public VideoEqualizerSettingsService()
        {
            _filePath = AppStoragePaths.GetUserDataFilePath("AXVideoPlayer.equalizers.json");
            _writer = new CoalescedTextFileWriter(_filePath);
        }

        public VideoEqualizerSettings LoadVideo(string? videoPath)
        {
            string? key = NormalizePathOrNull(videoPath);
            if (key == null)
                return VideoEqualizerSettings.Default;

            EqualizerSettingsData data = LoadData();
            return data.Video.TryGetValue(key, out VideoEqualizerSettings? settings)
                ? settings.Clamp()
                : VideoEqualizerSettings.Default;
        }

        public void SaveVideo(string? videoPath, VideoEqualizerSettings settings)
        {
            string? key = NormalizePathOrNull(videoPath);
            if (key == null)
                return;

            EqualizerSettingsData data = LoadData();
            data.Video[key] = settings.Clamp();
            SaveData(data);
        }

        public void ClearVideo(string? videoPath)
        {
            string? key = NormalizePathOrNull(videoPath);
            EqualizerSettingsData data = LoadData();

            if (key == null)
                data.Video.Clear();
            else
                data.Video.Remove(key);

            SaveData(data);
        }

        public AudioEqualizerSettings LoadAudio(string? videoPath)
        {
            string? key = NormalizePathOrNull(videoPath);
            if (key == null)
                return AudioEqualizerSettings.Default;

            EqualizerSettingsData data = LoadData();
            return data.Audio.TryGetValue(key, out AudioEqualizerSettings? settings)
                ? settings.Clamp()
                : AudioEqualizerSettings.Default;
        }

        public void SaveAudio(string? videoPath, AudioEqualizerSettings settings)
        {
            string? key = NormalizePathOrNull(videoPath);
            if (key == null)
                return;

            EqualizerSettingsData data = LoadData();
            data.Audio[key] = settings.Clamp();
            SaveData(data);
        }

        public void ClearAudio(string? videoPath)
        {
            string? key = NormalizePathOrNull(videoPath);
            EqualizerSettingsData data = LoadData();

            if (key == null)
                data.Audio.Clear();
            else
                data.Audio.Remove(key);

            SaveData(data);
        }

        private EqualizerSettingsData LoadData()
        {
            if (_data != null)
                return _data;

            try
            {
                if (!File.Exists(_filePath))
                    return _data = new EqualizerSettingsData();

                string json = File.ReadAllText(_filePath);
                _data = JsonSerializer.Deserialize<EqualizerSettingsData>(json) ?? new EqualizerSettingsData();
                _data.Video ??= new Dictionary<string, VideoEqualizerSettings>(StringComparer.OrdinalIgnoreCase);
                _data.Audio ??= new Dictionary<string, AudioEqualizerSettings>(StringComparer.OrdinalIgnoreCase);
                return _data;
            }
            catch
            {
                return _data = new EqualizerSettingsData();
            }
        }

        private void SaveData(EqualizerSettingsData data)
        {
            try
            {
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                _writer.QueueWrite(json);
                _data = data;
            }
            catch
            {
                // Equalizer persistence is a convenience feature; never interrupt playback for it.
            }
        }

        public void Flush()
        {
            _writer.Flush();
        }

        private static string? NormalizePathOrNull(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private sealed class EqualizerSettingsData
        {
            public Dictionary<string, VideoEqualizerSettings> Video { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, AudioEqualizerSettings> Audio { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal sealed class VideoEqualizerSettings
    {
        public bool Enabled { get; set; }
        public double Brightness { get; set; } = 1.0;
        public double Contrast { get; set; } = 1.0;
        public double Saturation { get; set; } = 1.0;
        public double Gamma { get; set; } = 1.0;
        public double Hue { get; set; }
        public double Sharpness { get; set; }

        public static VideoEqualizerSettings Default => new();

        public VideoEqualizerSettings Clamp()
        {
            Brightness = Clamp(Brightness, 0, 2);
            Contrast = Clamp(Contrast, 0, 2);
            Saturation = Clamp(Saturation, 0, 3);
            Gamma = Clamp(Gamma, 0.1, 3);
            Hue = Clamp(Hue, -180, 180);
            Sharpness = Clamp(Sharpness, 0, 2);
            return this;
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
    }

    internal sealed class AudioEqualizerSettings
    {
        public bool Enabled { get; set; }
        public double Preamp { get; set; }
        public double[] Bands { get; set; } = new double[10];

        public static AudioEqualizerSettings Default => new();

        public AudioEqualizerSettings Clamp()
        {
            Preamp = Math.Max(-20, Math.Min(20, Preamp));

            if (Bands == null || Bands.Length != 10)
                Bands = new double[10];

            for (int i = 0; i < Bands.Length; i++)
                Bands[i] = Math.Max(-20, Math.Min(20, Bands[i]));

            return this;
        }
    }
}
