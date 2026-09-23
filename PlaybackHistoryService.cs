using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AXVideoPlayer
{
    internal sealed class PlaybackHistoryService
    {
        private readonly string _filePath;
        private readonly CoalescedTextFileWriter _writer;
        private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _playlist = new();

        public bool RememberEnabled { get; private set; }

        public PlaybackHistoryService()
        {
            _filePath = AppStoragePaths.GetUserDataFilePath("AXVideoPlayer.playback.json");
            _writer = new CoalescedTextFileWriter(_filePath);
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
            if (!RememberEnabled || string.IsNullOrWhiteSpace(videoPath))
                return;

            if (milliseconds <= 0)
                _positions.Remove(NormalizePath(videoPath));
            else
                _positions[NormalizePath(videoPath)] = milliseconds;

            Save();
        }

        public void SetRememberEnabled(bool enabled)
        {
            RememberEnabled = enabled;
            if (!enabled)
            {
                _positions.Clear();
                _playlist.Clear();
            }
            Save();
        }

        public void SetPlaylist(IEnumerable<string> paths)
        {
            if (!RememberEnabled)
                return;

            _playlist.Clear();
            _playlist.AddRange(paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            Save();
        }

        public IReadOnlyList<string> GetPlaylist() => RememberEnabled ? _playlist.ToArray() : Array.Empty<string>();

        public void ClearHistory()
        {
            _positions.Clear();
            _playlist.Clear();
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

                // Older builds used ResumeEnabled. Do not migrate that opt-in automatically:
                // the combined music/video memory feature is intentionally off by default.
                RememberEnabled = data.RememberEnabled == true;
                _positions.Clear();
                _playlist.Clear();

                if (!RememberEnabled)
                    return;

                if (data.Positions != null)
                {
                    foreach (var pair in data.Positions)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
                            _positions[NormalizePath(pair.Key)] = pair.Value;
                    }
                }

                if (data.Playlist != null)
                    _playlist.AddRange(data.Playlist.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                RememberEnabled = false;
                _positions.Clear();
                _playlist.Clear();
            }
        }

        private void Save()
        {
            try
            {
                var data = new PlaybackHistoryData
                {
                    RememberEnabled = RememberEnabled,
                    Playlist = new List<string>(_playlist),
                    Positions = new Dictionary<string, long>(_positions, StringComparer.OrdinalIgnoreCase)
                };

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                _writer.QueueWrite(json);
            }
            catch
            {
                // Playback history is a convenience feature; never interrupt playback for persistence errors.
            }
        }

        public void Flush()
        {
            _writer.Flush();
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
            public bool? RememberEnabled { get; set; }
            public Dictionary<string, long>? Positions { get; set; }
            public List<string>? Playlist { get; set; }
        }
    }
}
