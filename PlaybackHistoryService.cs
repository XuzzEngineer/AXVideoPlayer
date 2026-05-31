using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AXVideoPlayer
{
    internal sealed class PlaybackHistoryService
    {
        private readonly string _filePath;
        private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);

        public bool ResumeEnabled { get; private set; } = true;

        public PlaybackHistoryService()
        {
            _filePath = Path.Combine(AppContext.BaseDirectory, "AXVideoPlayer.playback.json");
            Load();
        }

        public long GetPosition(string videoPath)
        {
            return _positions.TryGetValue(NormalizePath(videoPath), out long position)
                ? position
                : 0;
        }

        public void SetPosition(string videoPath, long milliseconds)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return;

            if (milliseconds <= 0)
                _positions.Remove(NormalizePath(videoPath));
            else
                _positions[NormalizePath(videoPath)] = milliseconds;

            Save();
        }

        public void SetResumeEnabled(bool enabled)
        {
            ResumeEnabled = enabled;
            Save();
        }

        public void ClearHistory()
        {
            _positions.Clear();
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return;

                string json = File.ReadAllText(_filePath);
                PlaybackHistoryData? data = JsonSerializer.Deserialize<PlaybackHistoryData>(json);
                if (data == null)
                    return;

                ResumeEnabled = data.ResumeEnabled;
                _positions.Clear();

                if (data.Positions == null)
                    return;

                foreach (var pair in data.Positions)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                        _positions[NormalizePath(pair.Key)] = pair.Value;
                }
            }
            catch
            {
                ResumeEnabled = true;
                _positions.Clear();
            }
        }

        private void Save()
        {
            try
            {
                var data = new PlaybackHistoryData
                {
                    ResumeEnabled = ResumeEnabled,
                    Positions = new Dictionary<string, long>(_positions, StringComparer.OrdinalIgnoreCase)
                };

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Playback history is a convenience feature; never interrupt playback for persistence errors.
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private sealed class PlaybackHistoryData
        {
            public bool ResumeEnabled { get; set; } = true;
            public Dictionary<string, long>? Positions { get; set; }
        }
    }
}
