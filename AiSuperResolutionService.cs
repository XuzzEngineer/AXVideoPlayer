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
    internal enum AiSuperResolutionDeviceMode
    {
        Cpu,
        IntegratedGpu,
        DiscreteGpu
    }

    internal enum AiFrameInterpolationMode
    {
        None,
        FfmpegMotion,
        RifeAi
    }

    internal sealed class AiSuperResolutionRequest
    {
        public string InputPath { get; init; } = string.Empty;
        public string OutputDirectory { get; init; } = string.Empty;
        public double StartSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public string TargetMode { get; init; } = "2160p";
        public double CustomScale { get; init; } = 2.0;
        public double DetailStrength { get; init; } = 1.0;
        public AiSuperResolutionDeviceMode DeviceMode { get; init; } = AiSuperResolutionDeviceMode.IntegratedGpu;
        public bool UseFrameByFrameAi { get; init; }
        public int FrameRateMultiplier { get; init; } = 1;
        public AiFrameInterpolationMode FrameInterpolationMode { get; init; } = AiFrameInterpolationMode.None;
        public ResourcePlan? ResourcePlan { get; init; }
        public bool IsPreview => DurationSeconds.HasValue;
    }

    internal sealed class AiSuperResolutionResult
    {
        public string OutputPath { get; init; } = string.Empty;
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        public int TargetWidth { get; init; }
        public int TargetHeight { get; init; }
        public double FrameRate { get; init; }
        public double SourceFrameRate { get; init; }
        public double OutputFrameRate { get; init; }
        public int FrameRateMultiplier { get; init; } = 1;
        public int FrameCount { get; init; }
        public int OutputFrameCount { get; init; }
        public string Engine { get; init; } = "Real-ESRGAN";

        public string Summary =>
            SourceWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            SourceHeight.ToString(CultureInfo.InvariantCulture) + " -> " +
            TargetWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            TargetHeight.ToString(CultureInfo.InvariantCulture) +
            FormatFrameRateSummary() +
            (FrameCount > 0
                ? ", " + FrameCount.ToString(CultureInfo.InvariantCulture) + " AI frames" + FormatOutputFrameCount()
                : ", " + Engine + " render");

        private string FormatFrameRateSummary()
        {
            double source = SourceFrameRate > 0.001 ? SourceFrameRate : FrameRate;
            double output = OutputFrameRate > 0.001 ? OutputFrameRate : FrameRate;
            int multiplier = Math.Max(1, FrameRateMultiplier);
            if (source <= 0.001 || output <= 0.001)
                return string.Empty;

            if (multiplier <= 1 || Math.Abs(source - output) < 0.01)
                return ", " + FormatFrameRate(source) + " fps";

            return ", " + FormatFrameRate(source) + " -> " + FormatFrameRate(output) + " fps (" + multiplier.ToString(CultureInfo.InvariantCulture) + "x)";
        }

        private string FormatOutputFrameCount()
        {
            return OutputFrameCount > 0 && OutputFrameCount != FrameCount
                ? ", " + OutputFrameCount.ToString(CultureInfo.InvariantCulture) + " output frames"
                : string.Empty;
        }

        private static string FormatFrameRate(double frameRate)
        {
            return frameRate.ToString(frameRate >= 100.0 ? "0" : "0.##", CultureInfo.InvariantCulture);
        }
    }

    internal static class AiSuperResolutionProgress
    {
        private const string Prefix = "[SR_PROGRESS:";

        public static string Format(double percent, string message)
        {
            double clamped = ClampPercent(percent);
            return Prefix + clamped.ToString("0.0", CultureInfo.InvariantCulture) + "] " + (string.IsNullOrWhiteSpace(message) ? "Working..." : message);
        }

        public static bool TryParse(string? status, out double percent, out string message)
        {
            percent = 0.0;
            message = status ?? string.Empty;
            if (string.IsNullOrWhiteSpace(status) || !status.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            int end = status.IndexOf(']', Prefix.Length);
            if (end <= Prefix.Length)
                return false;

            string percentText = status.Substring(Prefix.Length, end - Prefix.Length);
            if (!double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
                return false;

            percent = ClampPercent(percent);
            message = status.Length > end + 1 ? status.Substring(end + 1).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(message))
                message = "Working...";
            return true;
        }

        private static double ClampPercent(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent))
                return 0.0;

            return Math.Max(0.0, Math.Min(100.0, percent));
        }
    }

    internal sealed class AiSuperResolutionService
    {
        private const string RealEsrganUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
        private const string RifeUrl = "https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip";
        private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private const string RealEsrganModel = "realesrgan-x4plus";
        private const int RealEsrganScale = 4;
        private const int RealEsrganTileSize = 128;

        private static readonly HttpClient HttpClient = new HttpClient();

        public async Task<AiSuperResolutionResult> CreateEnhancedVideoAsync(
            AiSuperResolutionRequest request,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
                throw new FileNotFoundException("The video file was not found.", request.InputPath);

            ReportProgress(progress, 0.0, "Preparing super-resolution render...");
            ResourcePlan resourcePlan = request.ResourcePlan ?? ResourcePlan.CreateFallback();
            int frameRateMultiplier = ClampFrameRateMultiplier(request.FrameRateMultiplier);
            bool useFrameByFrameAi = request.UseFrameByFrameAi && request.DeviceMode != AiSuperResolutionDeviceMode.Cpu;
            AiFrameInterpolationMode interpolationMode = ResolveInterpolationMode(request.FrameInterpolationMode, frameRateMultiplier, useFrameByFrameAi);
            IProgress<string> setupProgress = new Progress<string>(message => ReportProgress(progress, 2.0, message));
            ToolSet tools = useFrameByFrameAi
                ? await EnsureToolsAsync(interpolationMode == AiFrameInterpolationMode.RifeAi, setupProgress, cancellationToken).ConfigureAwait(false)
                : await EnsureFfmpegToolsAsync(setupProgress, cancellationToken).ConfigureAwait(false);
            IProgress<string> probeProgress = new Progress<string>(message => ReportProgress(progress, 4.0, message));
            VideoInfo videoInfo = await ProbeVideoAsync(tools.FfprobePath, request.InputPath, probeProgress, cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, 5.0, "Using " + resourcePlan.FormatOfflineStatus() + ".");
            int targetHeight = ResolveTargetHeight(videoInfo.Height, request.TargetMode, request.CustomScale);
            int targetWidth = MakeEven((int)Math.Round(videoInfo.Width * (targetHeight / (double)Math.Max(1, videoInfo.Height))));
            int estimatedFrameCount = EstimateFrameCount(videoInfo, request);
            double outputFrameRate = videoInfo.FrameRate * frameRateMultiplier;
            double renderDurationSeconds = ResolveRenderDurationSeconds(videoInfo, request);

            string outputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? GetOutputDirectory()
                : request.OutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string previewText = request.IsPreview ? "_preview" : "_full";
            string fpsText = frameRateMultiplier > 1 ? "_" + frameRateMultiplier.ToString(CultureInfo.InvariantCulture) + "xFPS" : string.Empty;
            string outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(request.InputPath) + "_AI_SR_" + targetHeight.ToString(CultureInfo.InvariantCulture) + "p" + fpsText + previewText + "_" + stamp + ".mp4");

            string workingRoot = useFrameByFrameAi
                ? Path.Combine(GetToolsRootDirectory(), "Jobs", stamp + "_" + Guid.NewGuid().ToString("N"))
                : string.Empty;

            try
            {
                if (!useFrameByFrameAi)
                {
                    string engine = request.DeviceMode == AiSuperResolutionDeviceMode.Cpu
                        ? "Fast FFmpeg"
                        : "Fast video render";
                    int estimatedOutputFrameCount = ScaleFrameCount(estimatedFrameCount, frameRateMultiplier);
                    ReportProgress(progress, 8.0, (frameRateMultiplier > 1 ? "Rendering fast SR and motion interpolation with FFmpeg..." : "Rendering fast video upscale with FFmpeg...") + " " + resourcePlan.FormatOfflineStatus() + ".");
                    await EncodeFastUpscaledVideoAsync(
                        tools.FfmpegPath,
                        request,
                        videoInfo.FrameRate,
                        outputFrameRate,
                        renderDurationSeconds,
                        estimatedOutputFrameCount,
                        targetHeight,
                        request.DetailStrength,
                        interpolationMode,
                        outputPath,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                        throw new InvalidOperationException("The upscaled output video was not created.");

                    ReportProgress(progress, 100.0, "Fast video upscale complete.");
                    return new AiSuperResolutionResult
                    {
                        OutputPath = outputPath,
                        SourceWidth = videoInfo.Width,
                        SourceHeight = videoInfo.Height,
                        TargetWidth = targetWidth,
                        TargetHeight = targetHeight,
                        FrameRate = outputFrameRate,
                        SourceFrameRate = videoInfo.FrameRate,
                        OutputFrameRate = outputFrameRate,
                        FrameRateMultiplier = frameRateMultiplier,
                        FrameCount = 0,
                        OutputFrameCount = estimatedOutputFrameCount,
                        Engine = engine
                    };
                }

                string sourceFramesDirectory = Path.Combine(workingRoot, "source_frames");
                string enhancedFramesDirectory = Path.Combine(workingRoot, "enhanced_frames");
                string interpolatedFramesDirectory = Path.Combine(workingRoot, "interpolated_frames");
                Directory.CreateDirectory(sourceFramesDirectory);
                Directory.CreateDirectory(enhancedFramesDirectory);

                string framePattern = Path.Combine(sourceFramesDirectory, "frame_%08d.png");
                ReportProgress(progress, 8.0, request.IsPreview ? "Extracting preview frames..." : "Extracting video frames...");
                await ExtractFramesAsync(
                    tools.FfmpegPath,
                    request,
                    framePattern,
                    estimatedFrameCount,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                int frameCount = Directory.EnumerateFiles(sourceFramesDirectory, "*.png").Count();
                if (frameCount <= 0)
                    throw new InvalidOperationException("FFmpeg did not extract any video frames.");

                ReportProgress(progress, 15.0, "Running Real-ESRGAN x4plus AI super-resolution on " + frameCount.ToString(CultureInfo.InvariantCulture) + " frames with -j " + BuildThreadPlan(resourcePlan) + "...");
                await RunProcessAsync(
                    tools.RealEsrganPath,
                    "-i " + Quote(sourceFramesDirectory) +
                    " -o " + Quote(enhancedFramesDirectory) +
                    " -n " + RealEsrganModel +
                    " -s " + RealEsrganScale.ToString(CultureInfo.InvariantCulture) +
                    " -f png -t " + RealEsrganTileSize.ToString(CultureInfo.InvariantCulture) +
                    " -g " + GetGpuId(request.DeviceMode).ToString(CultureInfo.InvariantCulture) +
                    " -j " + BuildThreadPlan(resourcePlan),
                    Path.GetDirectoryName(tools.RealEsrganPath) ?? AppContext.BaseDirectory,
                    progress,
                    cancellationToken,
                    CreateRealEsrganOverallProgress(frameCount, 15.0, 88.0)).ConfigureAwait(false);

                string enhancedPattern = Path.Combine(enhancedFramesDirectory, "frame_%08d.png");
                string encodingPattern = enhancedPattern;
                int encodingStartNumber = 1;
                int outputFrameCount = frameCount;
                if (interpolationMode == AiFrameInterpolationMode.RifeAi)
                {
                    if (string.IsNullOrWhiteSpace(tools.RifePath) || !File.Exists(tools.RifePath))
                        throw new FileNotFoundException("RIFE frame interpolation was not installed correctly.", tools.RifePath);

                    Directory.CreateDirectory(interpolatedFramesDirectory);
                    int targetFrameCount = ScaleFrameCount(frameCount, frameRateMultiplier);
                    ReportProgress(progress, 88.0, "Running RIFE AI frame interpolation to " + frameRateMultiplier.ToString(CultureInfo.InvariantCulture) + "x FPS with -j " + BuildThreadPlan(resourcePlan) + "...");
                    await RunProcessAsync(
                        tools.RifePath,
                        "-i " + Quote(enhancedFramesDirectory) +
                        " -o " + Quote(interpolatedFramesDirectory) +
                        " -n " + targetFrameCount.ToString(CultureInfo.InvariantCulture) +
                        " -f %08d.png" +
                        " -g " + GetGpuId(request.DeviceMode).ToString(CultureInfo.InvariantCulture) +
                        " -j " + BuildThreadPlan(resourcePlan) +
                        (targetHeight >= 2160 ? " -u" : string.Empty),
                        Path.GetDirectoryName(tools.RifePath) ?? AppContext.BaseDirectory,
                        progress,
                        cancellationToken,
                        CreatePercentOverallProgress(88.0, 90.0, "AI interpolating frames")).ConfigureAwait(false);

                    outputFrameCount = Directory.EnumerateFiles(interpolatedFramesDirectory, "*.png").Count();
                    if (outputFrameCount <= 0)
                        throw new InvalidOperationException("RIFE did not create any interpolated frames.");

                    encodingPattern = Path.Combine(interpolatedFramesDirectory, "%08d.png");
                    encodingStartNumber = ResolveImageSequenceStartNumber(interpolatedFramesDirectory);
                }

                ReportProgress(progress, 90.0, "Encoding enhanced video...");
                await EncodeVideoAsync(
                    tools.FfmpegPath,
                    request,
                    encodingPattern,
                    encodingStartNumber,
                    outputFrameRate,
                    renderDurationSeconds,
                    outputFrameCount,
                    targetHeight,
                    request.DetailStrength,
                    outputPath,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                    throw new InvalidOperationException("The AI SR output video was not created.");

                ReportProgress(progress, 100.0, "AI super-resolution complete.");
                return new AiSuperResolutionResult
                {
                    OutputPath = outputPath,
                    SourceWidth = videoInfo.Width,
                    SourceHeight = videoInfo.Height,
                    TargetWidth = targetWidth,
                    TargetHeight = targetHeight,
                    FrameRate = outputFrameRate,
                    SourceFrameRate = videoInfo.FrameRate,
                    OutputFrameRate = outputFrameRate,
                    FrameRateMultiplier = frameRateMultiplier,
                    FrameCount = frameCount,
                    OutputFrameCount = outputFrameCount,
                    Engine = (request.DeviceMode == AiSuperResolutionDeviceMode.DiscreteGpu ? "Real-ESRGAN discrete GPU" : "Real-ESRGAN integrated GPU") +
                        (interpolationMode == AiFrameInterpolationMode.RifeAi ? " + RIFE" : string.Empty)
                };
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(workingRoot))
                    TryDeleteDirectory(workingRoot);
            }
        }

        private static async Task<ToolSet> EnsureToolsAsync(bool includeRife, IProgress<string> progress, CancellationToken cancellationToken)
        {
            string toolsRoot = GetToolsRootDirectory();
            Directory.CreateDirectory(toolsRoot);

            string realEsrganPath = await EnsureRealEsrganPathAsync(toolsRoot, progress, cancellationToken).ConfigureAwait(false);
            string rifePath = includeRife
                ? await EnsureRifePathAsync(toolsRoot, progress, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            ToolSet ffmpegTools = await EnsureFfmpegToolsAsync(progress, cancellationToken).ConfigureAwait(false);

            return new ToolSet(realEsrganPath, rifePath, ffmpegTools.FfmpegPath, ffmpegTools.FfprobePath);
        }

        private static async Task<string> EnsureRealEsrganPathAsync(string toolsRoot, IProgress<string> progress, CancellationToken cancellationToken)
        {
            string realEsrganPath = FindToolExecutable("realesrgan-ncnn-vulkan.exe", requireBinDirectory: false);
            if (!string.IsNullOrWhiteSpace(realEsrganPath) && File.Exists(realEsrganPath))
                return realEsrganPath;

            progress.Report("Downloading Real-ESRGAN Vulkan tools...");
            await DownloadAndExtractAsync(RealEsrganUrl, Path.Combine(toolsRoot, "downloads", "realesrgan.zip"), Path.Combine(toolsRoot, "RealESRGAN"), new[] { "realesrgan-ncnn-vulkan.exe" }, cancellationToken).ConfigureAwait(false);
            realEsrganPath = Directory.EnumerateFiles(Path.Combine(toolsRoot, "RealESRGAN"), "realesrgan-ncnn-vulkan.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;

            if (!File.Exists(realEsrganPath))
                throw new FileNotFoundException("Real-ESRGAN was not installed correctly.", realEsrganPath);

            return realEsrganPath;
        }

        private static async Task<string> EnsureRifePathAsync(string toolsRoot, IProgress<string> progress, CancellationToken cancellationToken)
        {
            string rifePath = FindToolExecutable("rife-ncnn-vulkan.exe", requireBinDirectory: false);
            if (!string.IsNullOrWhiteSpace(rifePath) && File.Exists(rifePath))
                return rifePath;

            progress.Report("Downloading RIFE frame interpolation tools...");
            await DownloadAndExtractAsync(RifeUrl, Path.Combine(toolsRoot, "downloads", "rife.zip"), Path.Combine(toolsRoot, "RIFE"), new[] { "rife-ncnn-vulkan.exe" }, cancellationToken).ConfigureAwait(false);
            rifePath = Directory.EnumerateFiles(Path.Combine(toolsRoot, "RIFE"), "rife-ncnn-vulkan.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;

            if (!File.Exists(rifePath))
                throw new FileNotFoundException("RIFE frame interpolation was not installed correctly.", rifePath);

            return rifePath;
        }

        private static async Task<ToolSet> EnsureFfmpegToolsAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            string toolsRoot = GetToolsRootDirectory();
            Directory.CreateDirectory(toolsRoot);

            string ffmpegPath = FindToolExecutable("ffmpeg.exe", requireBinDirectory: true);
            string ffprobePath = FindToolExecutable("ffprobe.exe", requireBinDirectory: true);
            if (string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            {
                progress.Report("Downloading FFmpeg tools...");
                await DownloadAndExtractAsync(FfmpegUrl, Path.Combine(toolsRoot, "downloads", "ffmpeg.zip"), Path.Combine(toolsRoot, "FFmpeg"), new[] { "ffmpeg.exe", "ffprobe.exe" }, cancellationToken).ConfigureAwait(false);
                ffmpegPath = Directory.EnumerateFiles(Path.Combine(toolsRoot, "FFmpeg"), "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
                ffprobePath = Directory.EnumerateFiles(Path.Combine(toolsRoot, "FFmpeg"), "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
            }

            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException("FFmpeg was not installed correctly.", ffmpegPath);
            if (!File.Exists(ffprobePath))
                throw new FileNotFoundException("FFprobe was not installed correctly.", ffprobePath);

            return new ToolSet(string.Empty, string.Empty, ffmpegPath, ffprobePath);
        }

        private static async Task DownloadAndExtractAsync(string url, string zipPath, string destinationDirectory, string[] requiredExecutables, CancellationToken cancellationToken)
        {
            await ToolArchiveDownloader.DownloadAndExtractAsync(url, zipPath, destinationDirectory, requiredExecutables, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<VideoInfo> ProbeVideoAsync(string ffprobePath, string inputPath, IProgress<string> progress, CancellationToken cancellationToken)
        {
            progress.Report("Reading source video dimensions...");
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

        private static async Task ExtractFramesAsync(
            string ffmpegPath,
            AiSuperResolutionRequest request,
            string framePattern,
            int estimatedFrameCount,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            string timeArgs = request.IsPreview
                ? "-ss " + FormatSeconds(request.StartSeconds) + " -t " + FormatSeconds(request.DurationSeconds.GetValueOrDefault())
                : string.Empty;

            await RunProcessAsync(
                ffmpegPath,
                "-hide_banner -y " + timeArgs + " -i " + Quote(request.InputPath) + " -map 0:v:0 -an -start_number 1 " + Quote(framePattern),
                AppContext.BaseDirectory,
                progress,
                cancellationToken,
                CreateFfmpegFrameOverallProgress(estimatedFrameCount, 8.0, 14.0, "Extracting frames")).ConfigureAwait(false);
        }

        private static async Task EncodeVideoAsync(
            string ffmpegPath,
            AiSuperResolutionRequest request,
            string enhancedPattern,
            int startNumber,
            double frameRate,
            double renderDurationSeconds,
            int frameCount,
            int targetHeight,
            double detailStrength,
            string outputPath,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            string audioTimeArgs = request.IsPreview
                ? "-ss " + FormatSeconds(request.StartSeconds) + " -t " + FormatSeconds(request.DurationSeconds.GetValueOrDefault())
                : string.Empty;

            string fps = frameRate.ToString("0.########", CultureInfo.InvariantCulture);
            string videoFilter = BuildEnhancementFilter(targetHeight, detailStrength);
            string outputTimeArgs = renderDurationSeconds > 0.001 ? " -t " + FormatSeconds(renderDurationSeconds) : string.Empty;
            await RunProcessAsync(
                ffmpegPath,
                "-hide_banner -y -framerate " + fps +
                " -start_number " + startNumber.ToString(CultureInfo.InvariantCulture) + " -i " + Quote(enhancedPattern) +
                " " + audioTimeArgs + " -i " + Quote(request.InputPath) +
                " -map 0:v:0 -map 1:a? -vf " + Quote(videoFilter) +
                outputTimeArgs +
                " -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -shortest " + Quote(outputPath),
                AppContext.BaseDirectory,
                progress,
                cancellationToken,
                CreateFfmpegFrameOverallProgress(frameCount, 90.0, 99.0, "Encoding enhanced video")).ConfigureAwait(false);
        }

        private static async Task EncodeFastUpscaledVideoAsync(
            string ffmpegPath,
            AiSuperResolutionRequest request,
            double sourceFrameRate,
            double outputFrameRate,
            double renderDurationSeconds,
            int estimatedFrameCount,
            int targetHeight,
            double detailStrength,
            AiFrameInterpolationMode interpolationMode,
            string outputPath,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            string timeArgs = request.IsPreview
                ? "-ss " + FormatSeconds(request.StartSeconds) + " -t " + FormatSeconds(request.DurationSeconds.GetValueOrDefault())
                : string.Empty;

            string fps = outputFrameRate.ToString("0.########", CultureInfo.InvariantCulture);
            string videoFilter = BuildEnhancementFilter(targetHeight, detailStrength, sourceFrameRate, outputFrameRate, interpolationMode);
            string outputTimeArgs = renderDurationSeconds > 0.001 ? " -t " + FormatSeconds(renderDurationSeconds) : string.Empty;
            await RunProcessAsync(
                ffmpegPath,
                "-hide_banner -y " + timeArgs +
                " -i " + Quote(request.InputPath) +
                " -map 0:v:0 -map 0:a? -vf " + Quote(videoFilter) +
                " -r " + fps +
                outputTimeArgs +
                " -c:v libx264 -threads 0 -preset veryfast -crf 19 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart -shortest " + Quote(outputPath),
                AppContext.BaseDirectory,
                progress,
                cancellationToken,
                CreateFfmpegFrameOverallProgress(estimatedFrameCount, 8.0, 99.0, "Rendering fast video upscale")).ConfigureAwait(false);
        }

        private static string BuildEnhancementFilter(int targetHeight, double detailStrength)
        {
            return BuildEnhancementFilter(targetHeight, detailStrength, 0.0, 0.0, AiFrameInterpolationMode.None);
        }

        private static string BuildEnhancementFilter(int targetHeight, double detailStrength, double sourceFrameRate, double outputFrameRate, AiFrameInterpolationMode interpolationMode)
        {
            double strength = Math.Max(0.0, Math.Min(2.0, detailStrength));
            string filter = "scale=-2:" + targetHeight.ToString(CultureInfo.InvariantCulture) + ":flags=spline+accurate_rnd+full_chroma_int";
            if (strength > 0.01)
            {
                double lumaAmount = Math.Min(1.1, 0.35 + strength * 0.35);
                double chromaAmount = Math.Min(0.35, 0.08 + strength * 0.1);
                filter += ",unsharp=5:5:" + lumaAmount.ToString("0.###", CultureInfo.InvariantCulture) +
                    ":3:3:" + chromaAmount.ToString("0.###", CultureInfo.InvariantCulture);
            }

            filter += ",setsar=1";
            if (interpolationMode == AiFrameInterpolationMode.FfmpegMotion && outputFrameRate > 0.001)
            {
                string fps = outputFrameRate.ToString("0.########", CultureInfo.InvariantCulture);
                if (sourceFrameRate > 0.001)
                    filter += ",tpad=stop_mode=clone:stop_duration=" + FormatSeconds(2.0 / sourceFrameRate);
                filter += ",minterpolate=fps=" + fps + ":mi_mode=mci:mc_mode=aobmc:vsbmc=1:scd=fdiff:scd_threshold=10";
            }

            return filter;
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
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    output.AppendLine(e.Data);
                    if (progressTransformer != null)
                    {
                        string? transformed = progressTransformer(e.Data.Trim());
                        if (!string.IsNullOrWhiteSpace(transformed))
                            progress?.Report(transformed);
                    }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    string trimmed = e.Data.Trim();
                    output.AppendLine(e.Data);
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
            };

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

        private static void ReportProgress(IProgress<string> progress, double percent, string message)
        {
            progress.Report(AiSuperResolutionProgress.Format(percent, message));
        }

        private static int ClampFrameRateMultiplier(int multiplier)
        {
            if (multiplier < 1)
                return 1;
            if (multiplier > 4)
                return 4;
            return multiplier;
        }

        private static AiFrameInterpolationMode ResolveInterpolationMode(AiFrameInterpolationMode requestedMode, int frameRateMultiplier, bool useFrameByFrameAi)
        {
            if (frameRateMultiplier <= 1)
                return AiFrameInterpolationMode.None;

            if (requestedMode == AiFrameInterpolationMode.RifeAi && useFrameByFrameAi)
                return AiFrameInterpolationMode.RifeAi;

            if (requestedMode == AiFrameInterpolationMode.FfmpegMotion || !useFrameByFrameAi)
                return AiFrameInterpolationMode.FfmpegMotion;

            return useFrameByFrameAi ? AiFrameInterpolationMode.RifeAi : AiFrameInterpolationMode.FfmpegMotion;
        }

        private static int ScaleFrameCount(int frameCount, int multiplier)
        {
            if (frameCount <= 0)
                return 0;

            long scaled = (long)frameCount * ClampFrameRateMultiplier(multiplier);
            return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
        }

        private static int ResolveImageSequenceStartNumber(string directory)
        {
            return File.Exists(Path.Combine(directory, "00000000.png")) ? 0 : 1;
        }

        private static int EstimateFrameCount(VideoInfo videoInfo, AiSuperResolutionRequest request)
        {
            double duration = request.DurationSeconds.GetValueOrDefault();
            if (duration <= 0.0)
                duration = videoInfo.DurationSeconds;

            if (duration <= 0.0 || videoInfo.FrameRate <= 0.0)
                return 0;

            return Math.Max(1, (int)Math.Ceiling(duration * videoInfo.FrameRate));
        }

        private static double ResolveRenderDurationSeconds(VideoInfo videoInfo, AiSuperResolutionRequest request)
        {
            double duration = request.DurationSeconds.GetValueOrDefault();
            return duration > 0.0 ? duration : videoInfo.DurationSeconds;
        }

        private static Func<string, string?> CreateRealEsrganOverallProgress(int frameCount, double startPercent, double endPercent)
        {
            int totalFrames = Math.Max(1, frameCount);
            int completedFrames = 0;
            double lastFramePercent = -1.0;
            double lastOverallPercent = startPercent;

            return line =>
            {
                if (!TryParsePercentLine(line, out double framePercent))
                    return null;

                if (lastFramePercent >= 60.0 && framePercent < lastFramePercent - 30.0)
                    completedFrames = Math.Min(totalFrames - 1, completedFrames + 1);

                lastFramePercent = framePercent;
                double frameProgress = Math.Min(totalFrames, completedFrames + framePercent / 100.0);
                double overall = startPercent + (endPercent - startPercent) * (frameProgress / totalFrames);
                overall = Math.Max(lastOverallPercent, Math.Min(endPercent, overall));
                lastOverallPercent = overall;

                int currentFrame = Math.Min(totalFrames, completedFrames + 1);
                return AiSuperResolutionProgress.Format(
                    overall,
                    "AI upscaling frames " + currentFrame.ToString(CultureInfo.InvariantCulture) +
                    "/" + totalFrames.ToString(CultureInfo.InvariantCulture));
            };
        }

        private static Func<string, string?> CreatePercentOverallProgress(double startPercent, double endPercent, string message)
        {
            double lastOverallPercent = startPercent;
            return line =>
            {
                if (!TryParsePercentLine(line, out double percent))
                    return null;

                double ratio = Math.Max(0.0, Math.Min(1.0, percent / 100.0));
                double overall = startPercent + (endPercent - startPercent) * ratio;
                overall = Math.Max(lastOverallPercent, Math.Min(endPercent, overall));
                lastOverallPercent = overall;
                return AiSuperResolutionProgress.Format(overall, message + " " + percent.ToString("0", CultureInfo.InvariantCulture) + "%");
            };
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

        private static bool TryParsePercentLine(string line, out double percent)
        {
            percent = 0.0;
            string text = line.Trim();
            if (!text.EndsWith("%", StringComparison.Ordinal))
                return false;

            text = text.Substring(0, text.Length - 1).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
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

        private static int ResolveTargetHeight(int sourceHeight, string targetMode, double customScale)
        {
            int targetHeight = targetMode?.Trim().ToLowerInvariant() switch
            {
                "1080p" => 1080,
                "1440p" => 1440,
                "2160p" => 2160,
                "4k" => 2160,
                "custom" => MakeEven((int)Math.Round(sourceHeight * Math.Max(1.0, Math.Min(4.0, customScale)))),
                _ => sourceHeight <= 720 ? 1080 : sourceHeight <= 1440 ? 2160 : sourceHeight
            };

            if (targetHeight < sourceHeight)
                targetHeight = sourceHeight;

            return MakeEven(targetHeight);
        }

        private static int GetGpuId(AiSuperResolutionDeviceMode deviceMode)
        {
            return deviceMode == AiSuperResolutionDeviceMode.DiscreteGpu ? 1 : 0;
        }

        private static string BuildThreadPlan(ResourcePlan resourcePlan)
        {
            if (resourcePlan != null)
                return resourcePlan.NcnnThreadPlan;

            int cpuCount = Math.Max(2, Environment.ProcessorCount);
            int loadThreads = Math.Max(1, Math.Min(4, cpuCount / 5));
            int processThreads = Math.Max(2, Math.Min(8, cpuCount / 2));
            int saveThreads = Math.Max(1, Math.Min(4, cpuCount / 5));
            return loadThreads.ToString(CultureInfo.InvariantCulture) + ":" +
                processThreads.ToString(CultureInfo.InvariantCulture) + ":" +
                saveThreads.ToString(CultureInfo.InvariantCulture);
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

        private static string FormatSeconds(double seconds)
        {
            return Math.Max(0.0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
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

        public static string GetOutputDirectory()
        {
            string markerPath = AppStoragePaths.GetUserDataFilePath("generated-super-resolution.marker");
            string root = Path.Combine(Path.GetDirectoryName(markerPath) ?? AppContext.BaseDirectory, "GeneratedSuperResolution");
            Directory.CreateDirectory(root);
            return root;
        }

        private static int MakeEven(int value)
        {
            if (value < 2)
                return 2;

            return value % 2 == 0 ? value : value + 1;
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

        private readonly record struct ToolSet(string RealEsrganPath, string RifePath, string FfmpegPath, string FfprobePath);

        private readonly record struct VideoInfo(int Width, int Height, double FrameRate, double DurationSeconds);
    }
}
