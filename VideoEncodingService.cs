using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AXVideoPlayer
{
    internal sealed class VideoEncodeRequest
    {
        public string InputPath { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public string? OutputPath { get; init; }
        public string OutputFormat { get; init; } = "MP4";
        public string ResolutionMode { get; init; } = "Same";
        public int CustomWidth { get; init; }
        public int CustomHeight { get; init; }
        public string QualityPreset { get; init; } = "Balanced";
        public bool Overwrite { get; init; }
        public ResourcePlan? ResourcePlan { get; init; }
    }

    internal sealed class VideoEncodeResult
    {
        public string OutputPath { get; init; } = string.Empty;
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        public int OutputWidth { get; init; }
        public int OutputHeight { get; init; }
        public double DurationSeconds { get; init; }
        public string Format { get; init; } = string.Empty;
        public string CodecSummary { get; init; } = string.Empty;

        public string Summary =>
            SourceWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            SourceHeight.ToString(CultureInfo.InvariantCulture) + " -> " +
            OutputWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            OutputHeight.ToString(CultureInfo.InvariantCulture) +
            ", " + Format +
            ", " + CodecSummary;
    }

    internal sealed class VideoEncodingService
    {
        private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private static readonly HttpClient HttpClient = new HttpClient();

        public async Task<VideoEncodeResult> EncodeAsync(VideoEncodeRequest request, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
                throw new FileNotFoundException("The video file was not found.", request.InputPath);

            ReportProgress(progress, 0.0, "Preparing video encoder...");
            ToolSet tools = await EnsureFfmpegToolsAsync(new Progress<string>(message => ReportProgress(progress, 2.0, message)), cancellationToken).ConfigureAwait(false);
            VideoInfo info = await ProbeVideoAsync(tools.FfprobePath, request.InputPath, new Progress<string>(message => ReportProgress(progress, 4.0, message)), cancellationToken).ConfigureAwait(false);

            ResourcePlan resourcePlan = request.ResourcePlan ?? ResourcePlan.CreateFallback();
            EncodeFormat format = ResolveFormat(request.OutputFormat);
            QualitySettings quality = ResolveQuality(request.QualityPreset, format);
            Size targetSize = ResolveTargetSize(info.Width, info.Height, request.ResolutionMode, request.CustomWidth, request.CustomHeight);
            string outputPath = ResolveOutputPath(request, format, targetSize);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);

            if (File.Exists(outputPath) && !request.Overwrite)
                throw new IOException("The output file already exists. Choose another folder/name or enable overwrite.");

            int estimatedFrames = EstimateFrameCount(info);
            string filter = BuildScaleFilter(info.Width, info.Height, targetSize.Width, targetSize.Height);
            string arguments = BuildFfmpegArguments(request.InputPath, outputPath, format, quality, filter, request.Overwrite);

            ReportProgress(progress, 5.0, "Encoding video with " + resourcePlan.FormatOfflineStatus() + "...");
            await RunProcessAsync(
                tools.FfmpegPath,
                arguments,
                AppContext.BaseDirectory,
                progress,
                cancellationToken,
                CreateFfmpegFrameOverallProgress(estimatedFrames, 5.0, 99.0, "Encoding video")).ConfigureAwait(false);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new InvalidOperationException("The encoded output video was not created.");

            ReportProgress(progress, 100.0, "Video encode complete.");
            return new VideoEncodeResult
            {
                OutputPath = outputPath,
                SourceWidth = info.Width,
                SourceHeight = info.Height,
                OutputWidth = targetSize.Width,
                OutputHeight = targetSize.Height,
                DurationSeconds = info.DurationSeconds,
                Format = format.DisplayName,
                CodecSummary = format.CodecSummary
            };
        }

        private static string BuildFfmpegArguments(string inputPath, string outputPath, EncodeFormat format, QualitySettings quality, string filter, bool overwrite)
        {
            var builder = new StringBuilder();
            builder.Append("-hide_banner ");
            builder.Append(overwrite ? "-y " : "-n ");
            builder.Append("-i ").Append(Quote(inputPath)).Append(' ');
            builder.Append("-map 0:v:0 -map 0:a? ");
            if (!string.IsNullOrWhiteSpace(filter))
                builder.Append("-vf ").Append(Quote(filter)).Append(' ');
            builder.Append(format.VideoArguments.Replace("{quality}", quality.VideoQuality, StringComparison.Ordinal)).Append(' ');
            builder.Append(format.AudioArguments).Append(' ');
            builder.Append("-threads 0 ");
            if (format.UseFastStart)
                builder.Append("-movflags +faststart ");
            builder.Append(Quote(outputPath));
            return builder.ToString();
        }

        private static string BuildScaleFilter(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            string flags = "flags=spline+accurate_rnd+full_chroma_int";
            if (targetWidth == MakeEven(sourceWidth) && targetHeight == MakeEven(sourceHeight))
                return "scale=trunc(iw/2)*2:trunc(ih/2)*2:" + flags + ",setsar=1";

            return "scale=" + targetWidth.ToString(CultureInfo.InvariantCulture) +
                ":" + targetHeight.ToString(CultureInfo.InvariantCulture) +
                ":" + flags + ",setsar=1";
        }

        private static Size ResolveTargetSize(int sourceWidth, int sourceHeight, string? resolutionMode, int customWidth, int customHeight)
        {
            string mode = (resolutionMode ?? "Same").Trim();
            if (string.Equals(mode, "Same", StringComparison.OrdinalIgnoreCase))
                return new Size(MakeEven(sourceWidth), MakeEven(sourceHeight));

            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                int maxWidth = customWidth > 0 ? customWidth : sourceWidth;
                int maxHeight = customHeight > 0 ? customHeight : sourceHeight;
                return FitInsideAspect(sourceWidth, sourceHeight, maxWidth, maxHeight);
            }

            int targetHeight = mode.TrimEnd('p', 'P') switch
            {
                "2160" => 2160,
                "1440" => 1440,
                "1080" => 1080,
                "720" => 720,
                "480" => 480,
                "360" => 360,
                _ => sourceHeight
            };

            int targetWidth = (int)Math.Round(sourceWidth * (targetHeight / (double)Math.Max(1, sourceHeight)));
            return new Size(MakeEven(targetWidth), MakeEven(targetHeight));
        }

        private static Size FitInsideAspect(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
        {
            maxWidth = MakeEven(Math.Max(2, maxWidth));
            maxHeight = MakeEven(Math.Max(2, maxHeight));
            double ratio = Math.Min(maxWidth / (double)Math.Max(1, sourceWidth), maxHeight / (double)Math.Max(1, sourceHeight));
            if (ratio <= 0.0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
                ratio = 1.0;

            int width = MakeEven((int)Math.Round(sourceWidth * ratio));
            int height = MakeEven((int)Math.Round(sourceHeight * ratio));
            return new Size(Math.Max(2, width), Math.Max(2, height));
        }

        private static string ResolveOutputPath(VideoEncodeRequest request, EncodeFormat format, Size targetSize)
        {
            if (!string.IsNullOrWhiteSpace(request.OutputPath))
                return Path.ChangeExtension(request.OutputPath, format.Extension);

            string outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? Path.GetDirectoryName(request.InputPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : request.OutputDirectory;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string baseName = Path.GetFileNameWithoutExtension(request.InputPath);
            string resolution = targetSize.Height.ToString(CultureInfo.InvariantCulture) + "p";
            return Path.Combine(outputDirectory, baseName + "_encoded_" + resolution + "_" + format.Name.ToLowerInvariant() + "_" + stamp + format.Extension);
        }

        private static EncodeFormat ResolveFormat(string? format)
        {
            string normalized = (format ?? "MP4").Trim().ToUpperInvariant();
            return normalized switch
            {
                "MKV" => new EncodeFormat("MKV", "Matroska", ".mkv", "-c:v libx264 -preset medium -crf {quality} -pix_fmt yuv420p", "-c:a aac -b:a 192k", false, "H.264 + AAC"),
                "MOV" => new EncodeFormat("MOV", "QuickTime MOV", ".mov", "-c:v libx264 -preset medium -crf {quality} -pix_fmt yuv420p", "-c:a aac -b:a 192k", true, "H.264 + AAC"),
                "WEBM" => new EncodeFormat("WebM", "WebM", ".webm", "-c:v libvpx-vp9 -b:v 0 -crf {quality} -deadline good -cpu-used 2 -pix_fmt yuv420p", "-c:a libopus -b:a 160k", false, "VP9 + Opus"),
                "AVI" => new EncodeFormat("AVI", "AVI", ".avi", "-c:v mpeg4 -q:v {quality}", "-c:a mp3 -b:a 192k", false, "MPEG-4 + MP3"),
                _ => new EncodeFormat("MP4", "MP4", ".mp4", "-c:v libx264 -preset medium -crf {quality} -pix_fmt yuv420p", "-c:a aac -b:a 192k", true, "H.264 + AAC")
            };
        }

        private static QualitySettings ResolveQuality(string? preset, EncodeFormat format)
        {
            string normalized = (preset ?? "Balanced").Trim();
            bool isWebM = string.Equals(format.Name, "WebM", StringComparison.OrdinalIgnoreCase);
            bool isAvi = string.Equals(format.Name, "AVI", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(normalized, "High", StringComparison.OrdinalIgnoreCase))
                return new QualitySettings(isAvi ? "3" : isWebM ? "28" : "18");
            if (string.Equals(normalized, "Small", StringComparison.OrdinalIgnoreCase))
                return new QualitySettings(isAvi ? "8" : isWebM ? "36" : "28");
            return new QualitySettings(isAvi ? "5" : isWebM ? "32" : "23");
        }

        private static async Task<ToolSet> EnsureFfmpegToolsAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            string toolsRoot = GetToolsRootDirectory();
            Directory.CreateDirectory(toolsRoot);
            string ffmpegPath = FindToolExecutable("ffmpeg.exe", requireBinDirectory: true);
            string ffprobePath = FindToolExecutable("ffprobe.exe", requireBinDirectory: true);
            if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            {
                progress.Report("Downloading FFmpeg...");
                await DownloadAndExtractAsync(FfmpegUrl, Path.Combine(toolsRoot, "downloads", "ffmpeg.zip"), Path.Combine(toolsRoot, "FFmpeg"), new[] { "ffmpeg.exe", "ffprobe.exe" }, cancellationToken).ConfigureAwait(false);
                ffmpegPath = Directory.EnumerateFiles(Path.Combine(toolsRoot, "FFmpeg"), "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
                ffprobePath = Directory.EnumerateFiles(Path.Combine(toolsRoot, "FFmpeg"), "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
            }

            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException("FFmpeg was not installed correctly.", ffmpegPath);
            if (!File.Exists(ffprobePath))
                throw new FileNotFoundException("FFprobe was not installed correctly.", ffprobePath);

            return new ToolSet(ffmpegPath, ffprobePath);
        }

        private static async Task DownloadAndExtractAsync(string url, string zipPath, string destinationDirectory, string[] requiredExecutables, CancellationToken cancellationToken)
        {
            await ToolArchiveDownloader.DownloadAndExtractAsync(url, zipPath, destinationDirectory, requiredExecutables, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<VideoInfo> ProbeVideoAsync(string ffprobePath, string inputPath, IProgress<string> progress, CancellationToken cancellationToken)
        {
            progress.Report("Reading source video details...");
            string output = await RunProcessCaptureAsync(
                ffprobePath,
                "-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,duration:format=duration -of default=noprint_wrappers=1 " + Quote(inputPath),
                cancellationToken).ConfigureAwait(false);

            int width = 0;
            int height = 0;
            double frameRate = 24.0;
            double durationSeconds = 0.0;

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('=', 2);
                if (parts.Length != 2)
                    continue;

                if (string.Equals(parts[0], "width", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                else if (string.Equals(parts[0], "height", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                else if (string.Equals(parts[0], "r_frame_rate", StringComparison.OrdinalIgnoreCase))
                    frameRate = ParseFrameRate(parts[1]);
                else if (string.Equals(parts[0], "duration", StringComparison.OrdinalIgnoreCase) &&
                    durationSeconds <= 0.0 &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDuration) &&
                    parsedDuration > 0.0)
                {
                    durationSeconds = parsedDuration;
                }
            }

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Could not read source video dimensions.");

            return new VideoInfo(width, height, frameRate, durationSeconds);
        }

        private static async Task RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            IProgress<string> progress,
            CancellationToken cancellationToken,
            Func<string, string?>? progressTransformer = null)
        {
            string output = await RunProcessCaptureAsync(fileName, arguments, cancellationToken, workingDirectory, progress, progressTransformer).ConfigureAwait(false);
            if (progressTransformer == null && !string.IsNullOrWhiteSpace(output))
            {
                string lastLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(lastLine))
                    progress.Report(lastLine);
            }
        }

        private static async Task<string> RunProcessCaptureAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken,
            string? workingDirectory = null,
            IProgress<string>? progress = null,
            Func<string, string?>? progressTransformer = null)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? AppContext.BaseDirectory : workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => CaptureLine(e.Data, output, progress, progressTransformer);
            process.ErrorDataReceived += (_, e) => CaptureLine(e.Data, output, progress, progressTransformer);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(Path.GetFileName(fileName) + " failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".\n\n" + output);

            return output.ToString();
        }

        private static void CaptureLine(string? line, StringBuilder output, IProgress<string>? progress, Func<string, string?>? progressTransformer)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            string trimmed = line.Trim();
            output.AppendLine(line);
            if (progressTransformer != null)
            {
                string? transformed = progressTransformer(trimmed);
                if (!string.IsNullOrWhiteSpace(transformed))
                    progress?.Report(transformed);
            }
            else if (trimmed.Contains("frame=", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("%", StringComparison.Ordinal))
            {
                progress?.Report(trimmed);
            }
        }

        private static Func<string, string?> CreateFfmpegFrameOverallProgress(int frameCount, double startPercent, double endPercent, string message)
        {
            int totalFrames = Math.Max(0, frameCount);
            double lastOverallPercent = startPercent;
            return line =>
            {
                if (!TryParseFfmpegFrameLine(line, out int currentFrame))
                    return null;

                double overall;
                string status;
                if (totalFrames > 0)
                {
                    double ratio = Math.Max(0.0, Math.Min(1.0, currentFrame / (double)totalFrames));
                    overall = startPercent + (endPercent - startPercent) * ratio;
                    status = message + " " + Math.Min(currentFrame, totalFrames).ToString(CultureInfo.InvariantCulture) +
                        "/" + totalFrames.ToString(CultureInfo.InvariantCulture) + " frames";
                }
                else
                {
                    overall = Math.Min(endPercent, lastOverallPercent + 0.1);
                    status = message + " frame " + Math.Max(0, currentFrame).ToString(CultureInfo.InvariantCulture);
                }

                overall = Math.Max(lastOverallPercent, Math.Min(endPercent, overall));
                lastOverallPercent = overall;
                return AiSuperResolutionProgress.Format(overall, status);
            };
        }

        private static bool TryParseFfmpegFrameLine(string line, out int frame)
        {
            frame = 0;
            int marker = line.IndexOf("frame=", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;

            int index = marker + "frame=".Length;
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            int start = index;
            while (index < line.Length && char.IsDigit(line[index]))
                index++;

            return index > start &&
                int.TryParse(line.Substring(start, index - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out frame);
        }

        private static int EstimateFrameCount(VideoInfo videoInfo)
        {
            if (videoInfo.DurationSeconds <= 0.0 || videoInfo.FrameRate <= 0.0)
                return 0;

            return Math.Max(1, (int)Math.Ceiling(videoInfo.DurationSeconds * videoInfo.FrameRate));
        }

        private static double ParseFrameRate(string value)
        {
            string[] parts = value.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
                denominator > 0)
            {
                return numerator / denominator;
            }

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) && rate > 0 ? rate : 24.0;
        }

        private static void ReportProgress(IProgress<string> progress, double percent, string message)
        {
            progress.Report(AiSuperResolutionProgress.Format(percent, message));
        }

        private static int MakeEven(int value)
        {
            if (value < 2)
                return 2;

            return value % 2 == 0 ? value : value + 1;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static string GetToolsRootDirectory()
        {
            string markerPath = AppStoragePaths.GetUserDataFilePath("ai-tools.marker");
            string root = Path.Combine(Path.GetDirectoryName(markerPath) ?? AppContext.BaseDirectory, "AI Tools");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string GetBundledToolsRootDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, "AI Tools");
        }

        private static string FindToolExecutable(string fileName, bool requireBinDirectory)
        {
            foreach (string root in new[] { GetToolsRootDirectory(), GetBundledToolsRootDirectory() })
            {
                if (!Directory.Exists(root))
                    continue;

                string path = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(candidate => !requireBinDirectory || candidate.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }

            return string.Empty;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }

        private readonly record struct ToolSet(string FfmpegPath, string FfprobePath);
        private readonly record struct VideoInfo(int Width, int Height, double FrameRate, double DurationSeconds);
        private readonly record struct Size(int Width, int Height);
        private readonly record struct QualitySettings(string VideoQuality);
        private readonly record struct EncodeFormat(string Name, string DisplayName, string Extension, string VideoArguments, string AudioArguments, bool UseFastStart, string CodecSummary);
    }
}
