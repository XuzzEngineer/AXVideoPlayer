using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AXVideoPlayer
{
    internal sealed class SuperResolutionFrameResult
    {
        public string SourcePath { get; init; } = string.Empty;
        public string EnhancedPath { get; init; } = string.Empty;
        public string ComparisonPath { get; init; } = string.Empty;
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        public int TargetWidth { get; init; }
        public int TargetHeight { get; init; }
        public double BaselineSharpnessScore { get; init; }
        public double EnhancedSharpnessScore { get; init; }

        public string Summary =>
            SourceWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            SourceHeight.ToString(CultureInfo.InvariantCulture) + " -> " +
            TargetWidth.ToString(CultureInfo.InvariantCulture) + "x" +
            TargetHeight.ToString(CultureInfo.InvariantCulture) +
            "\nDetail score: " +
            BaselineSharpnessScore.ToString("0.0", CultureInfo.InvariantCulture) +
            " -> " +
            EnhancedSharpnessScore.ToString("0.0", CultureInfo.InvariantCulture);
    }

    internal static class SuperResolutionFrameProcessor
    {
        private const int LanczosRadius = 3;

        public static SuperResolutionFrameResult ProcessSnapshot(
            string sourcePath,
            string outputDirectory,
            string outputBaseName,
            string targetMode,
            double customScale,
            double sharpness)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("Source frame was not found.", sourcePath);

            Directory.CreateDirectory(outputDirectory);

            BitmapFrame frame = LoadFrame(sourcePath);
            int sourceWidth = frame.PixelWidth;
            int sourceHeight = frame.PixelHeight;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new InvalidOperationException("Source frame has invalid dimensions.");

            int targetHeight = ResolveTargetHeight(sourceHeight, targetMode, customScale);
            int targetWidth = MakeEven((int)Math.Round(sourceWidth * (targetHeight / (double)sourceHeight)));

            byte[] sourcePixels = CopyBgra32(frame, out int sourceStride);
            byte[] baseline = ResizeLanczosBgra32(sourcePixels, sourceWidth, sourceHeight, sourceStride, targetWidth, targetHeight);
            byte[] enhanced = (byte[])baseline.Clone();
            ApplyUnsharpMask(enhanced, targetWidth, targetHeight, Math.Max(0.0, Math.Min(2.0, sharpness)));

            string safeBaseName = MakeSafeFileName(outputBaseName);
            string suffix = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string enhancedPath = Path.Combine(outputDirectory, safeBaseName + "_SR_" + targetHeight.ToString(CultureInfo.InvariantCulture) + "p_" + suffix + ".png");
            string comparisonPath = Path.Combine(outputDirectory, safeBaseName + "_SR_compare_" + suffix + ".png");

            SaveBgra32Png(enhancedPath, enhanced, targetWidth, targetHeight);
            SaveBgra32Png(comparisonPath, CreateComparisonCrop(baseline, enhanced, targetWidth, targetHeight, out int comparisonWidth, out int comparisonHeight), comparisonWidth, comparisonHeight);

            return new SuperResolutionFrameResult
            {
                SourcePath = sourcePath,
                EnhancedPath = enhancedPath,
                ComparisonPath = comparisonPath,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
                BaselineSharpnessScore = ComputeSharpnessScore(baseline, targetWidth, targetHeight),
                EnhancedSharpnessScore = ComputeSharpnessScore(enhanced, targetWidth, targetHeight)
            };
        }

        private static BitmapFrame LoadFrame(string path)
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        private static byte[] CopyBgra32(BitmapSource source, out int stride)
        {
            BitmapSource bgra = source.Format == System.Windows.Media.PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            stride = bgra.PixelWidth * 4;
            byte[] pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            return pixels;
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
                _ => sourceHeight < 1080 ? 1080 : sourceHeight
            };

            if (targetHeight < sourceHeight)
                targetHeight = sourceHeight;

            return MakeEven(targetHeight);
        }

        private static byte[] ResizeLanczosBgra32(byte[] source, int sourceWidth, int sourceHeight, int sourceStride, int targetWidth, int targetHeight)
        {
            Contribution[] xContrib = BuildContributions(sourceWidth, targetWidth);
            Contribution[] yContrib = BuildContributions(sourceHeight, targetHeight);
            float[] horizontal = new float[sourceHeight * targetWidth * 4];
            byte[] target = new byte[targetWidth * targetHeight * 4];

            Parallel.For(0, sourceHeight, y =>
            {
                int sourceRow = y * sourceStride;
                int tempRow = y * targetWidth * 4;
                for (int x = 0; x < targetWidth; x++)
                {
                    Contribution contribution = xContrib[x];
                    double b = 0;
                    double g = 0;
                    double r = 0;
                    double a = 0;
                    for (int i = 0; i < contribution.Indices.Length; i++)
                    {
                        int sourceIndex = sourceRow + contribution.Indices[i] * 4;
                        double weight = contribution.Weights[i];
                        b += source[sourceIndex] * weight;
                        g += source[sourceIndex + 1] * weight;
                        r += source[sourceIndex + 2] * weight;
                        a += source[sourceIndex + 3] * weight;
                    }

                    int tempIndex = tempRow + x * 4;
                    horizontal[tempIndex] = (float)b;
                    horizontal[tempIndex + 1] = (float)g;
                    horizontal[tempIndex + 2] = (float)r;
                    horizontal[tempIndex + 3] = (float)a;
                }
            });

            Parallel.For(0, targetHeight, y =>
            {
                Contribution contributionY = yContrib[y];
                int targetRow = y * targetWidth * 4;
                for (int x = 0; x < targetWidth; x++)
                {
                    double b = 0;
                    double g = 0;
                    double r = 0;
                    double a = 0;
                    for (int i = 0; i < contributionY.Indices.Length; i++)
                    {
                        int tempIndex = (contributionY.Indices[i] * targetWidth + x) * 4;
                        double weight = contributionY.Weights[i];
                        b += horizontal[tempIndex] * weight;
                        g += horizontal[tempIndex + 1] * weight;
                        r += horizontal[tempIndex + 2] * weight;
                        a += horizontal[tempIndex + 3] * weight;
                    }

                    int targetIndex = targetRow + x * 4;
                    target[targetIndex] = ClampToByte(b);
                    target[targetIndex + 1] = ClampToByte(g);
                    target[targetIndex + 2] = ClampToByte(r);
                    target[targetIndex + 3] = ClampToByte(a);
                }
            });

            return target;
        }

        private static Contribution[] BuildContributions(int sourceLength, int targetLength)
        {
            Contribution[] contributions = new Contribution[targetLength];
            double scale = targetLength / (double)sourceLength;
            double support = scale < 1.0 ? LanczosRadius / scale : LanczosRadius;

            for (int target = 0; target < targetLength; target++)
            {
                double center = (target + 0.5) / scale - 0.5;
                int left = (int)Math.Ceiling(center - support);
                int right = (int)Math.Floor(center + support);
                int count = right - left + 1;
                int[] indices = new int[count];
                double[] weights = new double[count];
                double totalWeight = 0;

                for (int i = 0; i < count; i++)
                {
                    int sourceIndex = Math.Max(0, Math.Min(sourceLength - 1, left + i));
                    double distance = center - (left + i);
                    double weight = scale < 1.0 ? Lanczos(distance * scale) : Lanczos(distance);
                    indices[i] = sourceIndex;
                    weights[i] = weight;
                    totalWeight += weight;
                }

                if (Math.Abs(totalWeight) > 0.000001)
                {
                    for (int i = 0; i < weights.Length; i++)
                        weights[i] /= totalWeight;
                }

                contributions[target] = new Contribution(indices, weights);
            }

            return contributions;
        }

        private static double Lanczos(double x)
        {
            x = Math.Abs(x);
            if (x < 0.000001)
                return 1.0;
            if (x >= LanczosRadius)
                return 0.0;
            return Sinc(x) * Sinc(x / LanczosRadius);
        }

        private static double Sinc(double x)
        {
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        private static void ApplyUnsharpMask(byte[] pixels, int width, int height, double sharpness)
        {
            if (sharpness <= 0.0001 || width < 3 || height < 3)
                return;

            byte[] source = (byte[])pixels.Clone();
            double amount = Math.Min(1.85, 0.25 + sharpness * 0.85);
            int stride = width * 4;

            Parallel.For(1, height - 1, y =>
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int index = y * stride + x * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int center = source[index + c];
                        int blur =
                            source[index - stride - 4 + c] +
                            source[index - stride + c] * 2 +
                            source[index - stride + 4 + c] +
                            source[index - 4 + c] * 2 +
                            center * 4 +
                            source[index + 4 + c] * 2 +
                            source[index + stride - 4 + c] +
                            source[index + stride + c] * 2 +
                            source[index + stride + 4 + c];
                        double blurred = blur / 16.0;
                        double detail = center - blurred;
                        if (Math.Abs(detail) < 0.35)
                            continue;
                        pixels[index + c] = ClampToByte(center + detail * amount);
                    }
                }
            });
        }

        private static byte[] CreateComparisonCrop(byte[] baseline, byte[] enhanced, int width, int height, out int comparisonWidth, out int comparisonHeight)
        {
            int cropWidth = Math.Min(960, width);
            int cropHeight = Math.Min(540, height);
            int startX = Math.Max(0, (width - cropWidth) / 2);
            int startY = Math.Max(0, (height - cropHeight) / 2);
            comparisonWidth = cropWidth * 2;
            comparisonHeight = cropHeight;
            byte[] comparison = new byte[comparisonWidth * comparisonHeight * 4];

            for (int y = 0; y < cropHeight; y++)
            {
                int sourceRow = ((startY + y) * width + startX) * 4;
                int comparisonRow = y * comparisonWidth * 4;
                Buffer.BlockCopy(baseline, sourceRow, comparison, comparisonRow, cropWidth * 4);
                Buffer.BlockCopy(enhanced, sourceRow, comparison, comparisonRow + cropWidth * 4, cropWidth * 4);
            }

            return comparison;
        }

        private static double ComputeSharpnessScore(byte[] pixels, int width, int height)
        {
            if (width < 3 || height < 3)
                return 0;

            double sum = 0;
            double sumSquared = 0;
            long count = 0;
            int stride = width * 4;
            int step = Math.Max(1, Math.Min(width, height) / 720);

            for (int y = step; y < height - step; y += step)
            {
                for (int x = step; x < width - step; x += step)
                {
                    int index = y * stride + x * 4;
                    double center = Luma(pixels, index);
                    double laplacian =
                        Luma(pixels, index - stride) +
                        Luma(pixels, index + stride) +
                        Luma(pixels, index - 4) +
                        Luma(pixels, index + 4) -
                        center * 4;

                    sum += laplacian;
                    sumSquared += laplacian * laplacian;
                    count++;
                }
            }

            if (count <= 1)
                return 0;

            double mean = sum / count;
            return (sumSquared / count) - mean * mean;
        }

        private static double Luma(byte[] pixels, int index)
        {
            return pixels[index + 2] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index] * 0.0722;
        }

        private static void SaveBgra32Png(string path, byte[] pixels, int width, int height)
        {
            BitmapSource source = BitmapSource.Create(
                width,
                height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        private static int MakeEven(int value)
        {
            if (value < 2)
                return 2;

            return value % 2 == 0 ? value : value + 1;
        }

        private static byte ClampToByte(double value)
        {
            if (value <= 0)
                return 0;
            if (value >= 255)
                return 255;
            return (byte)Math.Round(value);
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "AXVideoPlayer";

            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');

            return value.Trim();
        }

        private readonly struct Contribution
        {
            public Contribution(int[] indices, double[] weights)
            {
                Indices = indices;
                Weights = weights;
            }

            public int[] Indices { get; }
            public double[] Weights { get; }
        }
    }
}
