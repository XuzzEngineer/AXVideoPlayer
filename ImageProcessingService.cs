using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AXVideoPlayer
{
    internal sealed class ImageProcessingService
    {
        private readonly LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

        public ImageProcessingService(LibVLCSharp.Shared.MediaPlayer mediaPlayer)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
        }

        public string SaveCurrentFrameSnapshot(string? currentVideoPath, FrameworkElement videoElement)
        {
            string directory = BuildScreenshotDirectory(currentVideoPath);
            Directory.CreateDirectory(directory);

            string baseName = !string.IsNullOrWhiteSpace(currentVideoPath)
                ? Path.GetFileNameWithoutExtension(currentVideoPath)
                : "AXVideoPlayer";

            string safeBaseName = MakeSafeFileName(baseName);
            string filePath = Path.Combine(directory, $"{safeBaseName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            if (TrySaveVlcSnapshot(filePath))
                return filePath;

            if (TrySaveVisibleVideoArea(videoElement, filePath))
                return filePath;

            throw new InvalidOperationException("Could not capture a screenshot. Start playback until a frame is visible, then try again.");
        }

        public SuperResolutionFrameResult SaveSuperResolutionComparison(
            string? currentVideoPath,
            FrameworkElement videoElement,
            string targetMode,
            double customScale,
            double sharpness)
        {
            string directory = BuildScreenshotDirectory(currentVideoPath);
            Directory.CreateDirectory(directory);

            string baseName = !string.IsNullOrWhiteSpace(currentVideoPath)
                ? Path.GetFileNameWithoutExtension(currentVideoPath)
                : "AXVideoPlayer";

            string safeBaseName = MakeSafeFileName(baseName);
            string sourcePath = Path.Combine(directory, $"{safeBaseName}_SR_source_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            if (!TrySaveWpfNativeFrameSnapshot(currentVideoPath, sourcePath) && !TrySaveVlcSnapshot(sourcePath))
            {
                TryDeleteFile(sourcePath);
                if (!TrySaveVisibleVideoArea(videoElement, sourcePath) || !IsUsableSnapshot(sourcePath))
                    throw new InvalidOperationException("Could not capture the current video frame. Start playback until a frame is visible, then try again.");
            }

            if (!IsUsableSnapshot(sourcePath))
                throw new InvalidOperationException("Could not capture the current video frame. Start playback until a frame is visible, then try again.");

            return SuperResolutionFrameProcessor.ProcessSnapshot(
                sourcePath,
                directory,
                safeBaseName,
                targetMode,
                customScale,
                sharpness);
        }

        private bool TrySaveVlcSnapshot(string filePath)
        {
            try
            {
                (uint width, uint height) = TryGetCurrentVideoSize();
                if (width > 0 && height > 0 && _mediaPlayer.TakeSnapshot(0, filePath, width, height) && IsUsableSnapshot(filePath))
                    return true;
            }
            catch
            {
                // Try VLC's default snapshot size below.
            }

            try
            {
                if (_mediaPlayer.TakeSnapshot(0, filePath, 0, 0) && IsUsableSnapshot(filePath))
                    return true;
            }
            catch
            {
                // Fall back to visible window capture below.
            }

            TryDeleteEmptyFile(filePath);
            return false;
        }

        private bool TrySaveWpfNativeFrameSnapshot(string? currentVideoPath, string filePath)
        {
            if (string.IsNullOrWhiteSpace(currentVideoPath) || !File.Exists(currentVideoPath))
                return false;

            var player = new System.Windows.Media.MediaPlayer
            {
                ScrubbingEnabled = true,
                Volume = 0
            };
            bool opened = false;
            bool failed = false;
            bool saved = false;

            try
            {
                player.MediaOpened += (_, _) => opened = true;
                player.MediaFailed += (_, _) => failed = true;
                player.Open(new Uri(currentVideoPath));
                WaitForDispatcher(TimeSpan.FromSeconds(6), () => opened || failed);
                if (!opened || failed || player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
                    return false;

                long currentTimeMs = Math.Max(0, _mediaPlayer.Time);
                if (currentTimeMs > 0)
                {
                    TimeSpan requestedPosition = TimeSpan.FromMilliseconds(currentTimeMs);
                    if (!player.NaturalDuration.HasTimeSpan || requestedPosition < player.NaturalDuration.TimeSpan)
                    {
                        player.Position = requestedPosition;
                        player.Play();
                        WaitForDispatcher(TimeSpan.FromMilliseconds(350), () => false);
                        player.Pause();
                    }
                }

                int width = player.NaturalVideoWidth;
                int height = player.NaturalVideoHeight;
                var visual = new DrawingVisual();
                using (DrawingContext context = visual.RenderOpen())
                {
                    context.DrawVideo(player, new Rect(0, 0, width, height));
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(filePath))
                {
                    encoder.Save(stream);
                }

                saved = IsUsableSnapshot(filePath);
                return saved;
            }
            catch
            {
                return false;
            }
            finally
            {
                player.Close();
                if (!saved)
                    TryDeleteFile(filePath);
            }
        }

        private static void WaitForDispatcher(TimeSpan timeout, Func<bool> isComplete)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!isComplete() && DateTime.UtcNow < deadline)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(delegate { }));
                System.Threading.Thread.Sleep(15);
            }
        }

        private static bool IsUsableSnapshot(string filePath)
        {
            try
            {
                if (!File.Exists(filePath) || new FileInfo(filePath).Length <= 0)
                    return false;

                if (IsMostlyGreenFrame(filePath))
                {
                    File.Delete(filePath);
                    return false;
                }

                return true;
            }
            catch
            {
                TryDeleteFile(filePath);
                return false;
            }
        }

        private static bool IsMostlyGreenFrame(string filePath)
        {
            BitmapSource source;
            using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }

            BitmapSource bitmap = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0)
                return false;

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            int stepX = Math.Max(1, width / 64);
            int stepY = Math.Max(1, height / 64);
            int samples = 0;
            int greenSamples = 0;

            for (int y = 0; y < height; y += stepY)
            {
                int row = y * stride;
                for (int x = 0; x < width; x += stepX)
                {
                    int index = row + x * 4;
                    byte b = pixels[index];
                    byte g = pixels[index + 1];
                    byte r = pixels[index + 2];

                    samples++;
                    if (g > 80 && g > r * 1.6 && g > b * 1.6)
                        greenSamples++;
                }
            }

            if (samples == 0)
                return false;

            double greenRatio = greenSamples / (double)samples;
            if (greenRatio >= 0.55)
                return true;

            long totalR = 0;
            long totalG = 0;
            long totalB = 0;
            for (int y = height / 4; y < height * 3 / 4; y += stepY)
            {
                int row = y * stride;
                for (int x = width / 4; x < width * 3 / 4; x += stepX)
                {
                    int index = row + x * 4;
                    totalB += pixels[index];
                    totalG += pixels[index + 1];
                    totalR += pixels[index + 2];
                }
            }

            return totalG > 0 && totalG > totalR * 1.8 && totalG > totalB * 1.8;
        }

        private static bool TrySaveVisibleVideoArea(FrameworkElement videoElement, string filePath)
        {
            if (videoElement.ActualWidth <= 1 || videoElement.ActualHeight <= 1)
                return false;

            int width = Math.Max(1, (int)Math.Round(videoElement.ActualWidth));
            int height = Math.Max(1, (int)Math.Round(videoElement.ActualHeight));
            Point screenPoint = videoElement.PointToScreen(new Point(0, 0));

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
            IntPtr oldBitmap = SelectObject(memoryDc, bitmap);

            try
            {
                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y), CopyPixelOperation.SourceCopy))
                    return false;

                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));

                using FileStream stream = File.Create(filePath);
                encoder.Save(stream);
                return File.Exists(filePath) && new FileInfo(filePath).Length > 0;
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static void TryDeleteEmptyFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath) && new FileInfo(filePath).Length == 0)
                    File.Delete(filePath);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }

        private static string BuildScreenshotDirectory(string? currentVideoPath)
        {
            if (!string.IsNullOrWhiteSpace(currentVideoPath) && File.Exists(currentVideoPath))
            {
                string? videoDirectory = Path.GetDirectoryName(currentVideoPath);
                if (!string.IsNullOrWhiteSpace(videoDirectory))
                    return videoDirectory;
            }

            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string root = string.IsNullOrWhiteSpace(pictures) ? Environment.CurrentDirectory : pictures;
            return Path.Combine(root, "AX Video Player Screenshots");
        }

        private (uint Width, uint Height) TryGetCurrentVideoSize()
        {
            (uint trackWidth, uint trackHeight) = TryGetCurrentMediaTrackVideoSize();
            if (trackWidth > 0 && trackHeight > 0)
                return (trackWidth, trackHeight);

            try
            {
                Type playerType = _mediaPlayer.GetType();
                foreach (MethodInfo method in playerType.GetMethods())
                {
                    if (method.Name != "Size") continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 3) continue;

                    object?[] args = { 0u, 0u, 0u };
                    object? result = method.Invoke(_mediaPlayer, args);
                    bool success = result is bool b && b;
                    uint width = args[1] is uint w ? w : 0u;
                    uint height = args[2] is uint h ? h : 0u;

                    if (success && width > 0 && height > 0)
                        return (width, height);
                }
            }
            catch
            {
                // Fall back below.
            }

            return (0, 0);
        }

        private (uint Width, uint Height) TryGetCurrentMediaTrackVideoSize()
        {
            try
            {
                object? media = _mediaPlayer.Media;
                object? tracks = media?.GetType().GetProperty("Tracks")?.GetValue(media);
                if (tracks is not System.Collections.IEnumerable enumerable)
                    return (0, 0);

                foreach (object track in enumerable)
                {
                    object? trackType = track.GetType().GetProperty("TrackType")?.GetValue(track);
                    if (!string.Equals(trackType?.ToString(), "Video", StringComparison.OrdinalIgnoreCase))
                        continue;

                    object? data = track.GetType().GetProperty("Data")?.GetValue(track);
                    object? video = data?.GetType().GetProperty("Video")?.GetValue(data);
                    uint width = ReadUIntProperty(video, "Width");
                    uint height = ReadUIntProperty(video, "Height");
                    if (width > 0 && height > 0)
                        return (width, height);
                }
            }
            catch
            {
                // Track metadata can be unavailable until playback has parsed the media.
            }

            return (0, 0);
        }

        private static uint ReadUIntProperty(object? owner, string propertyName)
        {
            try
            {
                object? value = owner?.GetType().GetProperty(propertyName)?.GetValue(owner);
                return value == null ? 0u : Convert.ToUInt32(value);
            }
            catch
            {
                return 0u;
            }
        }

        private static string MakeSafeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            return string.IsNullOrWhiteSpace(value) ? "AXVideoPlayer" : value;
        }

        private enum CopyPixelOperation : int
        {
            SourceCopy = 0x00CC0020
        }

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, CopyPixelOperation dwRop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    }
}
