using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace AXVideoPlayer
{
    internal readonly struct LiveSuperResolutionSettings
    {
        public LiveSuperResolutionSettings(
            string targetMode,
            double customScale,
            double sharpness,
            bool frameGenerationEnabled = false,
            int frameGenerationMultiplier = 1)
        {
            TargetMode = string.IsNullOrWhiteSpace(targetMode) ? "Auto" : targetMode;
            CustomScale = Math.Max(1.0, Math.Min(4.0, customScale));
            Sharpness = Math.Max(0.0, Math.Min(2.0, sharpness));
            FrameGenerationMultiplier = Math.Max(1, Math.Min(4, frameGenerationMultiplier));
            FrameGenerationEnabled = frameGenerationEnabled && FrameGenerationMultiplier > 1;
        }

        public string TargetMode { get; }

        public double CustomScale { get; }

        public double Sharpness { get; }

        public bool FrameGenerationEnabled { get; }

        public int FrameGenerationMultiplier { get; }
    }

    internal readonly struct LiveSuperResolutionStats
    {
        public LiveSuperResolutionStats(
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight,
            long decodedFrames,
            long processedFrames,
            long droppedFrames,
            double decodedFps,
            double processedFps,
            double presentedFps,
            long generatedFrames,
            double latencyMs)
        {
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
            DecodedFrames = decodedFrames;
            ProcessedFrames = processedFrames;
            DroppedFrames = droppedFrames;
            DecodedFps = decodedFps;
            ProcessedFps = processedFps;
            PresentedFps = presentedFps;
            GeneratedFrames = generatedFrames;
            LatencyMs = latencyMs;
        }

        public int SourceWidth { get; }

        public int SourceHeight { get; }

        public int TargetWidth { get; }

        public int TargetHeight { get; }

        public long DecodedFrames { get; }

        public long ProcessedFrames { get; }

        public long DroppedFrames { get; }

        public double DecodedFps { get; }

        public double ProcessedFps { get; }

        public double PresentedFps { get; }

        public long GeneratedFrames { get; }

        public double LatencyMs { get; }
    }

    internal sealed class LiveSuperResolutionPipeline : IDisposable
    {
        private const int BytesPerPixel = 4;
        private const int MaxReasonableOutputHeight = 4320;

        private readonly VlcMediaPlayer _mediaPlayer;
        private readonly Image _surface;
        private readonly Dispatcher _dispatcher;
        private readonly Func<LiveSuperResolutionSettings> _settingsProvider;
        private readonly Func<(int Width, int Height)> _visibleSizeProvider;
        private readonly ResourcePlan _resourcePlan;
        private readonly bool _preferGpu;
        private readonly object _bufferLock = new object();
        private readonly object _statsLock = new object();
        private readonly object _frameGenerationLock = new object();
        private readonly Queue<PresentationFrame> _presentationQueue = new Queue<PresentationFrame>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _frameGenerationTimer;
        private readonly VlcMediaPlayer.LibVLCVideoLockCb _lockCallback;
        private readonly VlcMediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
        private readonly VlcMediaPlayer.LibVLCVideoDisplayCb _displayCallback;
        private readonly VlcMediaPlayer.LibVLCVideoFormatCb _formatCallback;
        private readonly VlcMediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;

        private IntPtr _sourceBuffer = IntPtr.Zero;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _visibleSourceWidth;
        private int _visibleSourceHeight;
        private int _sourcePitch;
        private int _sourceFrameBytes;
        private int _targetWidth;
        private int _targetHeight;
        private int _inFlightFrames;
        private long _nextFrameSequence;
        private long _latestQueuedFrameSequence;
        private long _decodedFrames;
        private long _processedFrames;
        private long _presentedFrames;
        private long _generatedFrames;
        private long _droppedFrames;
        private double _lastLatencyMs;
        private double _recentDecodedFps;
        private double _recentFps;
        private double _recentPresentedFps;
        private long _lastDecodedFpsFrameCount;
        private long _lastFpsFrameCount;
        private long _lastPresentedFpsFrameCount;
        private TimeSpan _lastDecodedFpsSampleTime = TimeSpan.Zero;
        private TimeSpan _lastFpsSampleTime = TimeSpan.Zero;
        private TimeSpan _lastPresentedFpsSampleTime = TimeSpan.Zero;
        private WriteableBitmap? _bitmap;
        private GpuSuperResolutionProcessor? _gpuProcessor;
        private readonly object _gpuProcessorLock = new();
        private readonly FrameProcessingGate _frameGate = new();
        private int _processingErrorLogged;
        private string? _gpuUnavailableReason;
        private byte[]? _lastFrameGenerationFrame;
        private int _lastFrameGenerationWidth;
        private int _lastFrameGenerationHeight;
        private int _lastFrameGenerationStride;
        private int _lastFrameGenerationBytes;
        private bool _lastFrameUsedGpu;
        private bool _disposed;

        public LiveSuperResolutionPipeline(
            VlcMediaPlayer mediaPlayer,
            Image surface,
            Dispatcher dispatcher,
            Func<LiveSuperResolutionSettings> settingsProvider,
            Func<(int Width, int Height)> visibleSizeProvider,
            bool preferGpu,
            ResourcePlan resourcePlan)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _visibleSizeProvider = visibleSizeProvider ?? throw new ArgumentNullException(nameof(visibleSizeProvider));
            _resourcePlan = resourcePlan ?? ResourcePlan.CreateFallback();
            _preferGpu = preferGpu;
            _lockCallback = LockVideoFrame;
            _unlockCallback = UnlockVideoFrame;
            _displayCallback = DisplayVideoFrame;
            _formatCallback = ConfigureVideoFormat;
            _cleanupCallback = CleanupVideoFormat;
            _frameGenerationTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16.0)
            };
            _frameGenerationTimer.Tick += (_, _) => PresentNextQueuedFrame();
        }

        public bool HasFrame => _sourceWidth > 0 && _sourceHeight > 0;

        public void Attach()
        {
            _mediaPlayer.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
            _mediaPlayer.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        }

        public string GetStatusText()
        {
            LiveSuperResolutionStats stats = GetStats();
            int sourceWidth = stats.SourceWidth;
            int sourceHeight = stats.SourceHeight;
            int targetWidth = stats.TargetWidth;
            int targetHeight = stats.TargetHeight;
            long processed = stats.ProcessedFrames;
            long dropped = stats.DroppedFrames;
            double fps = stats.ProcessedFps;
            double presentedFps = stats.PresentedFps;
            LiveSuperResolutionSettings settings = GetCurrentSettingsSafely();
            if (fps <= 0.001)
            {
                double seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
                fps = processed / seconds;
            }
            if (presentedFps <= 0.001)
            {
                presentedFps = fps;
            }

            string engineText = GetEngineStatusText();
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return engineText + ": waiting for decoded frames.";

            string sourceText = sourceWidth.ToString(CultureInfo.InvariantCulture) + "x" + sourceHeight.ToString(CultureInfo.InvariantCulture);
            string targetText = targetWidth > 0 && targetHeight > 0
                ? targetWidth.ToString(CultureInfo.InvariantCulture) + "x" + targetHeight.ToString(CultureInfo.InvariantCulture)
                : "calculating";

            string frameGenerationText = settings.FrameGenerationEnabled
                ? ", presenting " + presentedFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps, FG " +
                  settings.FrameGenerationMultiplier.ToString(CultureInfo.InvariantCulture) + "x, generated " +
                  stats.GeneratedFrames.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            return engineText + ": " + sourceText + " -> " + targetText +
                   ", processed " + fps.ToString("0.0", CultureInfo.InvariantCulture) + " fps" +
                   frameGenerationText +
                   ", dropped " + dropped.ToString(CultureInfo.InvariantCulture) +
                   ", latency " + stats.LatencyMs.ToString("0", CultureInfo.InvariantCulture) + " ms" +
                   ", " + _resourcePlan.FormatLiveStatus(_gpuUnavailableReason, _lastFrameUsedGpu) + ".";
        }

        public LiveSuperResolutionStats GetStats()
        {
            long decoded = Interlocked.Read(ref _decodedFrames);
            long processed = Interlocked.Read(ref _processedFrames);
            double elapsedSeconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
            double decodedFps = _recentDecodedFps > 0.001 ? _recentDecodedFps : decoded / elapsedSeconds;
            double processedFps = _recentFps > 0.001 ? _recentFps : processed / elapsedSeconds;
            long presented = Interlocked.Read(ref _presentedFrames);
            double presentedFps = _recentPresentedFps > 0.001 ? _recentPresentedFps : presented / elapsedSeconds;
            int sourceWidth = Volatile.Read(ref _visibleSourceWidth);
            int sourceHeight = Volatile.Read(ref _visibleSourceHeight);
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                sourceWidth = Volatile.Read(ref _sourceWidth);
                sourceHeight = Volatile.Read(ref _sourceHeight);
            }

            return new LiveSuperResolutionStats(
                sourceWidth,
                sourceHeight,
                Volatile.Read(ref _targetWidth),
                Volatile.Read(ref _targetHeight),
                decoded,
                processed,
                Interlocked.Read(ref _droppedFrames),
                decodedFps,
                processedFps,
                presentedFps,
                Interlocked.Read(ref _generatedFrames),
                _lastLatencyMs);
        }

        public void Dispose()
        {
            _frameGate.Close();
            _disposed = true;
            ReleaseFrameGenerationState();
            lock (_gpuProcessorLock)
            {
                _gpuProcessor?.Dispose();
                _gpuProcessor = null;
            }
            ReleaseSourceBuffer();
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
            {
                _dispatcher.BeginInvoke((Action)delegate
                {
                    _frameGenerationTimer.Stop();
                    ReleaseQueuedPresentationFrames();
                    _bitmap = null;
                    _surface.Source = null;
                }, DispatcherPriority.Send);
            }
        }

        private uint ConfigureVideoFormat(
            ref IntPtr opaque,
            IntPtr chroma,
            ref uint width,
            ref uint height,
            ref uint pitches,
            ref uint lines)
        {
            byte[] rv32 = Encoding.ASCII.GetBytes("RV32");
            Marshal.Copy(rv32, 0, chroma, rv32.Length);

            int sourceWidth = checked((int)width);
            int sourceHeight = checked((int)height);
            int pitch = checked(sourceWidth * BytesPerPixel);
            pitches = (uint)pitch;
            lines = (uint)sourceHeight;

            AllocateSourceBuffer(sourceWidth, sourceHeight, pitch);
            return 1;
        }

        private void CleanupVideoFormat(ref IntPtr opaque)
        {
            ReleaseSourceBuffer();
        }

        private IntPtr LockVideoFrame(IntPtr opaque, IntPtr planes)
        {
            lock (_bufferLock)
            {
                Marshal.WriteIntPtr(planes, _sourceBuffer);
            }

            return IntPtr.Zero;
        }

        private void UnlockVideoFrame(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
        }

        private void DisplayVideoFrame(IntPtr opaque, IntPtr picture)
        {
            if (_disposed)
                return;

            long decodedFrameCount = Interlocked.Increment(ref _decodedFrames);
            UpdateRecentDecodedFps(decodedFrameCount);
            int maxInFlight = Math.Max(1, _resourcePlan.LiveMaxInFlightFrames);
            if (Interlocked.Increment(ref _inFlightFrames) > maxInFlight)
            {
                Interlocked.Decrement(ref _inFlightFrames);
                Interlocked.Increment(ref _droppedFrames);
                return;
            }
            if (!_frameGate.TryEnter())
            {
                Interlocked.Decrement(ref _inFlightFrames);
                return;
            }
            long sequenceId = Interlocked.Increment(ref _nextFrameSequence);

            byte[]? sourceFrame = null;
            int sourceWidth;
            int sourceHeight;
            int sourcePitch;
            int frameBytes;

            try
            {
                lock (_bufferLock)
                {
                    if (_sourceBuffer == IntPtr.Zero || _sourceFrameBytes <= 0 || _sourceWidth <= 0 || _sourceHeight <= 0)
                    {
                        Interlocked.Decrement(ref _inFlightFrames);
                        _frameGate.Leave();
                        return;
                    }

                    sourceWidth = _sourceWidth;
                    sourceHeight = _sourceHeight;
                    sourcePitch = _sourcePitch;
                    frameBytes = _sourceFrameBytes;
                    sourceFrame = ArrayPool<byte>.Shared.Rent(frameBytes);
                    Marshal.Copy(_sourceBuffer, sourceFrame, 0, frameBytes);
                }
                _ = Task.Run(() => ProcessFrame(sequenceId, sourceFrame, sourceWidth, sourceHeight, sourcePitch, frameBytes));
            }
            catch (Exception ex)
            {
                if (sourceFrame != null) ArrayPool<byte>.Shared.Return(sourceFrame);
                Interlocked.Decrement(ref _inFlightFrames);
                _frameGate.Leave();
                App.LogException(ex);
            }
        }

        private void ProcessFrame(long sequenceId, byte[] sourceFrame, int sourceWidth, int sourceHeight, int sourcePitch, int sourceFrameBytes)
        {
            byte[]? visibleSourceFrame = null;
            byte[]? outputFrame = null;

            try
            {
                if (_frameGate.IsClosed) return;
                Stopwatch latencyClock = Stopwatch.StartNew();
                LiveSuperResolutionSettings settings = _settingsProvider();
                ResolveVisibleSourceSize(sourceWidth, sourceHeight, out int visibleWidth, out int visibleHeight);

                byte[] processingSourceFrame = sourceFrame;
                int processingSourcePitch = sourcePitch;
                int processingSourceFrameBytes = sourceFrameBytes;
                if (visibleWidth != sourceWidth || visibleHeight != sourceHeight)
                {
                    processingSourcePitch = checked(visibleWidth * BytesPerPixel);
                    processingSourceFrameBytes = checked(processingSourcePitch * visibleHeight);
                    visibleSourceFrame = ArrayPool<byte>.Shared.Rent(processingSourceFrameBytes);
                    CopyCenteredVisibleFrame(
                        sourceFrame,
                        sourceWidth,
                        sourceHeight,
                        sourcePitch,
                        visibleSourceFrame,
                        visibleWidth,
                        visibleHeight,
                        processingSourcePitch);
                    processingSourceFrame = visibleSourceFrame;
                }

                ResolveTargetSize(visibleWidth, visibleHeight, settings, out int targetWidth, out int targetHeight);
                int outputStride = checked(targetWidth * BytesPerPixel);
                int outputBytes = checked(outputStride * targetHeight);
                outputFrame = ArrayPool<byte>.Shared.Rent(outputBytes);

                bool processedByGpu = false;
                if (targetWidth == visibleWidth && targetHeight == visibleHeight && settings.Sharpness <= 0.0001)
                {
                    CopyFrameRows(processingSourceFrame, visibleHeight, processingSourcePitch, outputFrame, outputStride);
                }
                else
                {
                    processedByGpu = TryProcessFrameOnGpu(
                        processingSourceFrame,
                        visibleWidth,
                        visibleHeight,
                        processingSourcePitch,
                        processingSourceFrameBytes,
                        outputFrame,
                        targetWidth,
                        targetHeight,
                        settings.Sharpness);

                    if (!processedByGpu)
                    {
                        int cpuWorkers = Math.Max(1, _resourcePlan.LiveCpuWorkerThreads);
                        EdgeAdaptiveUpscale(processingSourceFrame, visibleWidth, visibleHeight, processingSourcePitch, outputFrame, targetWidth, targetHeight, settings.Sharpness, cpuWorkers);
                        ApplyOutputDetailEnhancement(outputFrame, targetWidth, targetHeight, settings.Sharpness, cpuWorkers);
                    }
                }

                Volatile.Write(ref _visibleSourceWidth, visibleWidth);
                Volatile.Write(ref _visibleSourceHeight, visibleHeight);
                Volatile.Write(ref _targetWidth, targetWidth);
                Volatile.Write(ref _targetHeight, targetHeight);
                _lastFrameUsedGpu = processedByGpu;
                _lastLatencyMs = processedByGpu && _gpuProcessor != null ? _gpuProcessor.LastGpuMilliseconds : latencyClock.Elapsed.TotalMilliseconds;
                long processedFrameCount = Interlocked.Increment(ref _processedFrames);
                UpdateRecentFps(processedFrameCount);

                byte[] frameForUi = outputFrame;
                outputFrame = null;
                int multiplier = settings.FrameGenerationEnabled ? settings.FrameGenerationMultiplier : 1;
                List<PresentationFrame> presentationFrames = BuildPresentationFrames(frameForUi, targetWidth, targetHeight, outputStride, settings);
                if (!_frameGate.IsClosed && !_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                {
                    _dispatcher.BeginInvoke((Action)delegate
                    {
                        long latestQueued = Interlocked.Read(ref _latestQueuedFrameSequence);
                        if (_frameGate.IsClosed || sequenceId < latestQueued)
                        {
                            Interlocked.Increment(ref _droppedFrames);
                            ReleasePresentationFrames(presentationFrames);
                            return;
                        }

                        Interlocked.Exchange(ref _latestQueuedFrameSequence, sequenceId);
                        EnqueuePresentationFrames(presentationFrames, multiplier);
                    }, DispatcherPriority.Render);
                }
                else
                {
                    ReleasePresentationFrames(presentationFrames);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _droppedFrames);
                if (Interlocked.Exchange(ref _processingErrorLogged, 1) == 0)
                    App.LogException(ex);
            }
            finally
            {
                if (outputFrame != null)
                    ArrayPool<byte>.Shared.Return(outputFrame);
                if (visibleSourceFrame != null)
                    ArrayPool<byte>.Shared.Return(visibleSourceFrame);

                ArrayPool<byte>.Shared.Return(sourceFrame);
                Interlocked.Decrement(ref _inFlightFrames);
                _frameGate.Leave();
            }
        }

        private void PresentFrame(byte[] frame, int width, int height, int stride)
        {
            if (_disposed)
                return;

            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                _surface.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, stride, 0);
            if (_surface.Visibility != Visibility.Visible)
                _surface.Visibility = Visibility.Visible;

            long presentedFrameCount = Interlocked.Increment(ref _presentedFrames);
            UpdateRecentPresentedFps(presentedFrameCount);
        }

        private void AllocateSourceBuffer(int width, int height, int pitch)
        {
            if (width <= 0 || height <= 0 || pitch <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Video callback dimensions must be positive.");

            int frameBytes = checked(pitch * height);
            lock (_bufferLock)
            {
                if (_sourceBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(_sourceBuffer);

                _sourceBuffer = Marshal.AllocHGlobal(frameBytes);
                _sourceWidth = width;
                _sourceHeight = height;
                _visibleSourceWidth = 0;
                _visibleSourceHeight = 0;
                _sourcePitch = pitch;
                _sourceFrameBytes = frameBytes;
            }
        }

        private void ResolveVisibleSourceSize(int callbackWidth, int callbackHeight, out int visibleWidth, out int visibleHeight)
        {
            visibleWidth = callbackWidth;
            visibleHeight = callbackHeight;

            try
            {
                (int Width, int Height) visible = _visibleSizeProvider();
                if (visible.Width > 0 &&
                    visible.Height > 0 &&
                    visible.Width <= callbackWidth &&
                    visible.Height <= callbackHeight)
                {
                    visibleWidth = visible.Width;
                    visibleHeight = visible.Height;
                }
            }
            catch
            {
                visibleWidth = callbackWidth;
                visibleHeight = callbackHeight;
            }
        }

        private static void CopyCenteredVisibleFrame(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int sourcePitch,
            byte[] target,
            int visibleWidth,
            int visibleHeight,
            int targetPitch)
        {
            int cropLeft = Math.Max(0, (sourceWidth - visibleWidth) / 2);
            int cropTop = Math.Max(0, (sourceHeight - visibleHeight) / 2);
            int copyBytes = checked(visibleWidth * BytesPerPixel);

            for (int y = 0; y < visibleHeight; y++)
            {
                int sourceOffset = checked((cropTop + y) * sourcePitch + cropLeft * BytesPerPixel);
                int targetOffset = checked(y * targetPitch);
                Buffer.BlockCopy(source, sourceOffset, target, targetOffset, copyBytes);
            }
        }

        private static void CopyFrameRows(byte[] source, int height, int sourceStride, byte[] target, int targetStride)
        {
            int copyBytes = Math.Min(sourceStride, targetStride);
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(source, y * sourceStride, target, y * targetStride, copyBytes);
            }
        }

        private void ReleaseSourceBuffer()
        {
            lock (_bufferLock)
            {
                if (_sourceBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_sourceBuffer);
                    _sourceBuffer = IntPtr.Zero;
                }

                _sourceWidth = 0;
                _sourceHeight = 0;
                _visibleSourceWidth = 0;
                _visibleSourceHeight = 0;
                _sourcePitch = 0;
                _sourceFrameBytes = 0;
            }

            ReleaseFrameGenerationState();
        }

        private void UpdateRecentFps(long processedFrameCount)
        {
            lock (_statsLock)
            {
                TimeSpan now = _clock.Elapsed;
                double elapsedSeconds = (now - _lastFpsSampleTime).TotalSeconds;
                if (elapsedSeconds < 0.75)
                    return;

                long frameDelta = processedFrameCount - _lastFpsFrameCount;
                if (frameDelta > 0)
                    _recentFps = frameDelta / elapsedSeconds;

                _lastFpsFrameCount = processedFrameCount;
                _lastFpsSampleTime = now;
            }
        }

        private void UpdateRecentDecodedFps(long decodedFrameCount)
        {
            lock (_statsLock)
            {
                TimeSpan now = _clock.Elapsed;
                double elapsedSeconds = (now - _lastDecodedFpsSampleTime).TotalSeconds;
                if (elapsedSeconds < 0.75)
                    return;

                long frameDelta = decodedFrameCount - _lastDecodedFpsFrameCount;
                if (frameDelta > 0)
                    _recentDecodedFps = frameDelta / elapsedSeconds;

                _lastDecodedFpsFrameCount = decodedFrameCount;
                _lastDecodedFpsSampleTime = now;
            }
        }

        private void UpdateRecentPresentedFps(long presentedFrameCount)
        {
            lock (_statsLock)
            {
                TimeSpan now = _clock.Elapsed;
                double elapsedSeconds = (now - _lastPresentedFpsSampleTime).TotalSeconds;
                if (elapsedSeconds < 0.75)
                    return;

                long frameDelta = presentedFrameCount - _lastPresentedFpsFrameCount;
                if (frameDelta > 0)
                    _recentPresentedFps = frameDelta / elapsedSeconds;

                _lastPresentedFpsFrameCount = presentedFrameCount;
                _lastPresentedFpsSampleTime = now;
            }
        }

        private string GetEngineStatusText()
        {
            LiveSuperResolutionSettings settings = GetCurrentSettingsSafely();
            string frameGenerationSuffix = settings.FrameGenerationEnabled
                ? " + realtime FG " + settings.FrameGenerationMultiplier.ToString(CultureInfo.InvariantCulture) + "x"
                : string.Empty;

            if (settings.FrameGenerationEnabled && settings.CustomScale <= 1.001 && settings.Sharpness <= 0.001)
                return "Realtime Frame Generation " + settings.FrameGenerationMultiplier.ToString(CultureInfo.InvariantCulture) + "x";

            if (!_preferGpu)
                return "Live CPU SR" + frameGenerationSuffix;

            if (!string.IsNullOrWhiteSpace(_gpuUnavailableReason))
                return "Live GPU SR (CPU fallback: " + _gpuUnavailableReason + ")" + frameGenerationSuffix;

            return _lastFrameUsedGpu || _gpuProcessor != null
                ? "Live GPU SR (D3D11 compute)" + frameGenerationSuffix
                : "Live GPU SR (initializing D3D11 compute)" + frameGenerationSuffix;
        }

        private LiveSuperResolutionSettings GetCurrentSettingsSafely()
        {
            try
            {
                return _settingsProvider();
            }
            catch
            {
                return new LiveSuperResolutionSettings("Custom", 1.0, 0.0);
            }
        }

        private List<PresentationFrame> BuildPresentationFrames(
            byte[] currentFrame,
            int width,
            int height,
            int stride,
            LiveSuperResolutionSettings settings)
        {
            int multiplier = settings.FrameGenerationEnabled ? settings.FrameGenerationMultiplier : 1;
            var frames = new List<PresentationFrame>(Math.Max(1, multiplier));
            if (multiplier <= 1)
            {
                ReleaseFrameGenerationState();
                frames.Add(new PresentationFrame(currentFrame, width, height, stride));
                return frames;
            }

            int byteCount = checked(stride * height);
            byte[]? oldFrame = null;
            try
            {
                int generatedCount = 0;
                lock (_frameGenerationLock)
                {
                    bool canGenerate =
                        _lastFrameGenerationFrame != null &&
                        _lastFrameGenerationWidth == width &&
                        _lastFrameGenerationHeight == height &&
                        _lastFrameGenerationStride == stride &&
                        _lastFrameGenerationBytes == byteCount;

                    if (canGenerate)
                    {
                        for (int i = 1; i < multiplier; i++)
                        {
                            byte[] generatedFrame = ArrayPool<byte>.Shared.Rent(byteCount);
                            BlendFrames(_lastFrameGenerationFrame!, currentFrame, generatedFrame, stride, height, i / (double)multiplier);
                            frames.Add(new PresentationFrame(generatedFrame, width, height, stride));
                            generatedCount++;
                        }
                    }

                    byte[] currentCopy = ArrayPool<byte>.Shared.Rent(byteCount);
                    Buffer.BlockCopy(currentFrame, 0, currentCopy, 0, byteCount);
                    oldFrame = _lastFrameGenerationFrame;
                    _lastFrameGenerationFrame = currentCopy;
                    _lastFrameGenerationWidth = width;
                    _lastFrameGenerationHeight = height;
                    _lastFrameGenerationStride = stride;
                    _lastFrameGenerationBytes = byteCount;
                }

                if (generatedCount > 0)
                    Interlocked.Add(ref _generatedFrames, generatedCount);

                frames.Add(new PresentationFrame(currentFrame, width, height, stride));
                return frames;
            }
            catch
            {
                ReleasePresentationFrames(frames);
                return new List<PresentationFrame>(1)
                {
                    new PresentationFrame(currentFrame, width, height, stride)
                };
            }
            finally
            {
                if (oldFrame != null)
                    ArrayPool<byte>.Shared.Return(oldFrame);
            }
        }

        private void EnqueuePresentationFrames(List<PresentationFrame> frames, int multiplier)
        {
            if (_disposed)
            {
                ReleasePresentationFrames(frames);
                return;
            }

            if (multiplier <= 1 || frames.Count <= 1)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    PresentationFrame frame = frames[i];
                    try
                    {
                        PresentFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame.Pixels);
                    }
                }

                return;
            }

            for (int i = 0; i < frames.Count; i++)
                _presentationQueue.Enqueue(frames[i]);

            TrimPresentationQueue(Math.Max(multiplier + 2, _resourcePlan.LivePresentationQueueFrames));
            ConfigurePresentationTimer(multiplier);
            if (!_frameGenerationTimer.IsEnabled)
            {
                PresentNextQueuedFrame();
                if (_presentationQueue.Count > 0)
                    _frameGenerationTimer.Start();
            }
        }

        private void PresentNextQueuedFrame()
        {
            if (_disposed || _presentationQueue.Count == 0)
            {
                _frameGenerationTimer.Stop();
                return;
            }

            PresentationFrame frame = _presentationQueue.Dequeue();
            try
            {
                PresentFrame(frame.Pixels, frame.Width, frame.Height, frame.Stride);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame.Pixels);
            }

            if (_presentationQueue.Count == 0)
                _frameGenerationTimer.Stop();
        }

        private void ConfigurePresentationTimer(int multiplier)
        {
            double decodedFps = _recentDecodedFps;
            double targetFps = decodedFps > 0.001 ? decodedFps * multiplier : 60.0;
            targetFps = Math.Max(24.0, Math.Min(144.0, targetFps));
            double intervalMs = Math.Max(5.0, Math.Min(33.0, 1000.0 / targetFps));
            _frameGenerationTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        }

        private void TrimPresentationQueue(int maxFrames)
        {
            while (_presentationQueue.Count > maxFrames)
            {
                PresentationFrame frame = _presentationQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(frame.Pixels);
            }
        }

        private void ReleaseQueuedPresentationFrames()
        {
            while (_presentationQueue.Count > 0)
            {
                PresentationFrame frame = _presentationQueue.Dequeue();
                ArrayPool<byte>.Shared.Return(frame.Pixels);
            }
        }

        private static void ReleasePresentationFrames(List<PresentationFrame> frames)
        {
            for (int i = 0; i < frames.Count; i++)
                ArrayPool<byte>.Shared.Return(frames[i].Pixels);
        }

        private void ReleaseFrameGenerationState()
        {
            byte[]? previousFrame;
            lock (_frameGenerationLock)
            {
                previousFrame = _lastFrameGenerationFrame;
                _lastFrameGenerationFrame = null;
                _lastFrameGenerationWidth = 0;
                _lastFrameGenerationHeight = 0;
                _lastFrameGenerationStride = 0;
                _lastFrameGenerationBytes = 0;
            }

            if (previousFrame != null)
                ArrayPool<byte>.Shared.Return(previousFrame);
        }

        private static void BlendFrames(
            byte[] previous,
            byte[] current,
            byte[] target,
            int stride,
            int height,
            double amount)
        {
            int alpha = Clamp((int)Math.Round(amount * 256.0), 0, 256);
            int inverseAlpha = 256 - alpha;
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                int rowEnd = rowStart + stride;
                for (int i = rowStart; i < rowEnd; i += BytesPerPixel)
                {
                    int bDiff = Math.Abs(current[i] - previous[i]);
                    int gDiff = Math.Abs(current[i + 1] - previous[i + 1]);
                    int rDiff = Math.Abs(current[i + 2] - previous[i + 2]);
                    int maxDiff = Math.Max(rDiff, Math.Max(gDiff, bDiff));

                    if (maxDiff >= 32)
                    {
                        byte[] selected = alpha < 128 ? previous : current;
                        target[i] = selected[i];
                        target[i + 1] = selected[i + 1];
                        target[i + 2] = selected[i + 2];
                        target[i + 3] = selected[i + 3];
                    }
                    else
                    {
                        target[i] = (byte)((previous[i] * inverseAlpha + current[i] * alpha + 128) >> 8);
                        target[i + 1] = (byte)((previous[i + 1] * inverseAlpha + current[i + 1] * alpha + 128) >> 8);
                        target[i + 2] = (byte)((previous[i + 2] * inverseAlpha + current[i + 2] * alpha + 128) >> 8);
                        target[i + 3] = (byte)((previous[i + 3] * inverseAlpha + current[i + 3] * alpha + 128) >> 8);
                    }
                }
            }
        }

        private sealed class PresentationFrame
        {
            public PresentationFrame(byte[] pixels, int width, int height, int stride)
            {
                Pixels = pixels;
                Width = width;
                Height = height;
                Stride = stride;
            }

            public byte[] Pixels { get; }

            public int Width { get; }

            public int Height { get; }

            public int Stride { get; }
        }

        private bool TryProcessFrameOnGpu(
            byte[] sourceFrame,
            int sourceWidth,
            int sourceHeight,
            int sourcePitch,
            int sourceFrameBytes,
            byte[] outputFrame,
            int targetWidth,
            int targetHeight,
            double sharpness)
        {
            if (!_preferGpu || !_resourcePlan.PreferGpuForLive || !_resourcePlan.GpuComputeAvailable || !string.IsNullOrWhiteSpace(_gpuUnavailableReason))
                return false;

            lock (_gpuProcessorLock)
            {
                try
                {
                    if (_disposed) return false;
                    _gpuProcessor ??= new GpuSuperResolutionProcessor();
                    _gpuProcessor.ProcessFrame(sourceFrame, sourceWidth, sourceHeight, sourcePitch,
                        sourceFrameBytes, outputFrame, targetWidth, targetHeight, sharpness);
                    return true;
                }
                catch (Exception ex)
                {
                    _gpuUnavailableReason = ex.Message;
                    App.LogException(ex);
                    _gpuProcessor?.Dispose();
                    _gpuProcessor = null;
                    return false;
                }
            }
        }

        private static void ResolveTargetSize(int sourceWidth, int sourceHeight, LiveSuperResolutionSettings settings, out int targetWidth, out int targetHeight)
        {
            string targetMode = settings.TargetMode.Trim();
            if (string.Equals(targetMode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                targetHeight = MakeEven((int)Math.Round(sourceHeight * settings.CustomScale));
            }
            else if (string.Equals(targetMode, "1080p", StringComparison.OrdinalIgnoreCase))
            {
                targetHeight = 1080;
            }
            else if (string.Equals(targetMode, "1440p", StringComparison.OrdinalIgnoreCase))
            {
                targetHeight = 1440;
            }
            else if (string.Equals(targetMode, "2160p", StringComparison.OrdinalIgnoreCase) || string.Equals(targetMode, "4K", StringComparison.OrdinalIgnoreCase))
            {
                targetHeight = 2160;
            }
            else
            {
                targetHeight = sourceHeight < 1080 ? 1080 : sourceHeight;
            }

            targetHeight = Math.Max(sourceHeight, Math.Min(MaxReasonableOutputHeight, MakeEven(targetHeight)));
            targetWidth = MakeEven((int)Math.Round(sourceWidth * (targetHeight / (double)sourceHeight)));
        }

        private static void EdgeAdaptiveUpscale(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int sourceStride,
            byte[] target,
            int targetWidth,
            int targetHeight,
            double sharpness,
            int maxDegreeOfParallelism)
        {
            double scaleX = sourceWidth / (double)targetWidth;
            double scaleY = sourceHeight / (double)targetHeight;
            double detailAmount = Math.Max(0.0, Math.Min(2.0, sharpness)) * 0.95;
            int targetStride = targetWidth * BytesPerPixel;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism) };

            Parallel.For(0, targetHeight, parallelOptions, y =>
            {
                double sourceY = (y + 0.5) * scaleY - 0.5;
                int y0 = Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
                int y1 = Math.Min(sourceHeight - 1, y0 + 1);
                double fy = Math.Max(0.0, Math.Min(1.0, sourceY - y0));
                int targetRow = y * targetStride;

                for (int x = 0; x < targetWidth; x++)
                {
                    double sourceX = (x + 0.5) * scaleX - 0.5;
                    int x0 = Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                    int x1 = Math.Min(sourceWidth - 1, x0 + 1);
                    double fx = Math.Max(0.0, Math.Min(1.0, sourceX - x0));
                    int cx = Clamp((int)Math.Round(sourceX), 0, sourceWidth - 1);
                    int cy = Clamp((int)Math.Round(sourceY), 0, sourceHeight - 1);
                    double edge = EstimateEdgeStrength(source, sourceWidth, sourceHeight, sourceStride, cx, cy);
                    double adaptiveAmount = detailAmount * (0.75 + edge * 1.25);
                    int targetIndex = targetRow + x * BytesPerPixel;

                    for (int c = 0; c < 3; c++)
                    {
                        double top = Lerp(ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x0, y0, c), ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x1, y0, c), fx);
                        double bottom = Lerp(ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x0, y1, c), ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x1, y1, c), fx);
                        double value = Lerp(top, bottom, fy);
                        double center = ReadChannel(source, sourceWidth, sourceHeight, sourceStride, cx, cy, c);
                        double blur = (
                            ReadChannel(source, sourceWidth, sourceHeight, sourceStride, cx - 1, cy, c) +
                            ReadChannel(source, sourceWidth, sourceHeight, sourceStride, cx + 1, cy, c) +
                            ReadChannel(source, sourceWidth, sourceHeight, sourceStride, cx, cy - 1, c) +
                            ReadChannel(source, sourceWidth, sourceHeight, sourceStride, cx, cy + 1, c)) * 0.25;
                        double detail = center - blur;
                        if (Math.Abs(detail) > 0.6)
                            value += detail * adaptiveAmount;

                        target[targetIndex + c] = ClampToByte(value);
                    }

                    double alphaTop = Lerp(ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x0, y0, 3), ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x1, y0, 3), fx);
                    double alphaBottom = Lerp(ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x0, y1, 3), ReadChannel(source, sourceWidth, sourceHeight, sourceStride, x1, y1, 3), fx);
                    target[targetIndex + 3] = ClampToByte(Lerp(alphaTop, alphaBottom, fy));
                }
            });
        }

        private static void ApplyOutputDetailEnhancement(byte[] pixels, int width, int height, double sharpness, int maxDegreeOfParallelism)
        {
            double strength = Math.Max(0.0, Math.Min(2.0, sharpness));
            if (strength <= 0.0001 || width < 3 || height < 3)
                return;

            int stride = width * BytesPerPixel;
            int byteCount = stride * height;
            byte[] source = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Buffer.BlockCopy(pixels, 0, source, 0, byteCount);
                double amount = Math.Min(1.45, 0.35 + strength * 0.58);
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism) };

                Parallel.For(1, height - 1, parallelOptions, y =>
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        int index = y * stride + x * BytesPerPixel;
                        for (int c = 0; c < 3; c++)
                        {
                            int center = source[index + c];
                            int blur =
                                source[index - stride - BytesPerPixel + c] +
                                source[index - stride + c] * 2 +
                                source[index - stride + BytesPerPixel + c] +
                                source[index - BytesPerPixel + c] * 2 +
                                center * 4 +
                                source[index + BytesPerPixel + c] * 2 +
                                source[index + stride - BytesPerPixel + c] +
                                source[index + stride + c] * 2 +
                                source[index + stride + BytesPerPixel + c];
                            double detail = center - blur / 16.0;
                            if (Math.Abs(detail) < 0.35)
                                continue;

                            pixels[index + c] = ClampToByte(center + detail * amount);
                        }
                    }
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(source);
            }
        }

        private static double EstimateEdgeStrength(byte[] pixels, int width, int height, int stride, int x, int y)
        {
            double left = Luma(pixels, width, height, stride, x - 1, y);
            double right = Luma(pixels, width, height, stride, x + 1, y);
            double up = Luma(pixels, width, height, stride, x, y - 1);
            double down = Luma(pixels, width, height, stride, x, y + 1);
            return Math.Min(1.0, (Math.Abs(right - left) + Math.Abs(down - up)) / 120.0);
        }

        private static double Luma(byte[] pixels, int width, int height, int stride, int x, int y)
        {
            int index = GetIndex(width, height, stride, x, y);
            return pixels[index + 2] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index] * 0.0722;
        }

        private static byte ReadChannel(byte[] pixels, int width, int height, int stride, int x, int y, int channel)
        {
            return pixels[GetIndex(width, height, stride, x, y) + channel];
        }

        private static int GetIndex(int width, int height, int stride, int x, int y)
        {
            int clampedX = Clamp(x, 0, width - 1);
            int clampedY = Clamp(y, 0, height - 1);
            return clampedY * stride + clampedX * BytesPerPixel;
        }

        private static double Lerp(double a, double b, double amount)
        {
            return a + (b - a) * amount;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static byte ClampToByte(double value)
        {
            if (value <= 0)
                return 0;
            if (value >= 255)
                return 255;
            return (byte)Math.Round(value);
        }

        private static int MakeEven(int value)
        {
            if (value < 2)
                return 2;

            return value % 2 == 0 ? value : value + 1;
        }
    }
}
