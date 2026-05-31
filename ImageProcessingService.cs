using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;

namespace AXVideoPlayer
{
    internal sealed class ImageProcessingService
    {
        private readonly MediaPlayer _mediaPlayer;

        public ImageProcessingService(MediaPlayer mediaPlayer)
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

        private bool TrySaveVlcSnapshot(string filePath)
        {
            try
            {
                // Native VLC size first. This is the most reliable mode across VLC builds.
                if (_mediaPlayer.TakeSnapshot(0, filePath, 0, 0) && File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    return true;
            }
            catch
            {
                // Try decoded-size snapshot below.
            }

            try
            {
                (uint width, uint height) = TryGetCurrentVideoSize();
                if (width > 0 && height > 0 && _mediaPlayer.TakeSnapshot(0, filePath, width, height) && File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    return true;
            }
            catch
            {
                // Fall back to visible window capture below.
            }

            TryDeleteEmptyFile(filePath);
            return false;
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
