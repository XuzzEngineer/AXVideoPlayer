using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AXVideoPlayer
{
    internal sealed class MediaPlaylistItem
    {
        public string Path { get; init; } = string.Empty;
        public bool IsAudio { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string Album { get; init; } = string.Empty;
        public string Duration { get; init; } = string.Empty;
        public ImageSource? Artwork { get; init; }

        public string PlaylistLabel => IsAudio && !string.IsNullOrWhiteSpace(Artist)
            ? Title + " — " + Artist
            : Title;

        public static MediaPlaylistItem Create(string path, bool isAudio)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!isAudio)
            {
                return new MediaPlaylistItem { Path = path, Title = fileName, IsAudio = false };
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                string title = string.IsNullOrWhiteSpace(file.Tag.Title) ? fileName : file.Tag.Title;
                string artist = file.Tag.Performers?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                string album = file.Tag.Album ?? string.Empty;
                string duration = file.Properties.Duration > TimeSpan.Zero
                    ? file.Properties.Duration.ToString(file.Properties.Duration.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss")
                    : string.Empty;

                return new MediaPlaylistItem
                {
                    Path = path,
                    IsAudio = true,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    Duration = duration,
                    Artwork = LoadArtwork(file)
                };
            }
            catch
            {
                return new MediaPlaylistItem { Path = path, IsAudio = true, Title = fileName };
            }
        }

        private static ImageSource? LoadArtwork(TagLib.File file)
        {
            try
            {
                byte[]? bytes = file.Tag.Pictures?.FirstOrDefault()?.Data?.Data;
                if (bytes == null || bytes.Length == 0)
                    return null;

                var image = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
