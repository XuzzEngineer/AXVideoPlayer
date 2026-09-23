using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AXVideoPlayer
{
    internal sealed class GpuSuperResolutionProcessor : IDisposable
    {
        private const int BytesPerPixel = 4;
        private const int ThreadGroupSize = 16;
        private const uint D3DCompileOptimizationLevel3 = 4;

        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11ComputeShader _upscaleShader;
        private readonly ID3D11ComputeShader _enhanceShader;
        private readonly object _gpuLock = new object();

        private ID3D11Buffer? _sourceBuffer;
        private ID3D11Buffer? _intermediateBuffer;
        private ID3D11Buffer? _targetBuffer;
        private ID3D11Buffer? _stagingBuffer;
        private ID3D11Buffer? _constantBuffer;
        private ID3D11ShaderResourceView? _sourceView;
        private ID3D11ShaderResourceView? _intermediateView;
        private ID3D11UnorderedAccessView? _intermediateUnorderedView;
        private ID3D11UnorderedAccessView? _targetUnorderedView;
        private int _sourceBufferBytes;
        private int _targetBufferBytes;
        private int _sourceElementCount;
        private int _targetElementCount;
        private bool _disposed;

        public GpuSuperResolutionProcessor()
        {
            FeatureLevel[] featureLevels =
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels);
            _context = _device.ImmediateContext;
            _upscaleShader = _device.CreateComputeShader(CompileShader(GpuShaders, "UpscaleMain"), null);
            _enhanceShader = _device.CreateComputeShader(CompileShader(GpuShaders, "EnhanceMain"), null);
            _constantBuffer = _device.CreateBuffer((uint)Marshal.SizeOf<ShaderConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        }

        public string EngineName => "D3D11 compute SR";

        public double LastGpuMilliseconds { get; private set; }

        public void ProcessFrame(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int sourcePitch,
            int sourceFrameBytes,
            byte[] target,
            int targetWidth,
            int targetHeight,
            double sharpness)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GpuSuperResolutionProcessor));
            if (sourceWidth <= 0 || sourceHeight <= 0 || sourcePitch <= 0 || sourceFrameBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source frame dimensions must be positive.");
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetWidth), "Target frame dimensions must be positive.");

            int targetBytes = checked(targetWidth * targetHeight * BytesPerPixel);
            if (target.Length < targetBytes)
                throw new ArgumentException("Target buffer is smaller than the requested frame.", nameof(target));

            lock (_gpuLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(GpuSuperResolutionProcessor));
                EnsureResources(sourceFrameBytes, targetBytes);

                var constants = new ShaderConstants
                {
                    SourceWidth = (uint)sourceWidth,
                    SourceHeight = (uint)sourceHeight,
                    SourcePitchBytes = (uint)sourcePitch,
                    TargetWidth = (uint)targetWidth,
                    TargetHeight = (uint)targetHeight,
                    ScaleX = sourceWidth / (float)targetWidth,
                    ScaleY = sourceHeight / (float)targetHeight,
                    Sharpness = (float)Math.Max(0.0, Math.Min(2.0, sharpness))
                };

                Stopwatch stopwatch = Stopwatch.StartNew();
                _context.UpdateSubresource(source.AsSpan(0, sourceFrameBytes), _sourceBuffer!, 0, 0, 0, null);
                _context.UpdateSubresource(in constants, _constantBuffer!, 0, 0, 0, null);

                _context.CSSetConstantBuffers(0, 1, new ID3D11Buffer[] { _constantBuffer! });
                _context.CSSetShader(_upscaleShader);
                _context.CSSetShaderResources(0, 1, new ID3D11ShaderResourceView[] { _sourceView! });
                _context.CSSetUnorderedAccessViews(0, 1, new ID3D11UnorderedAccessView[] { _intermediateUnorderedView! });
                Dispatch(targetWidth, targetHeight);

                UnbindComputeResources();

                _context.CSSetConstantBuffers(0, 1, new ID3D11Buffer[] { _constantBuffer! });
                _context.CSSetShader(_enhanceShader);
                _context.CSSetShaderResources(0, 1, new ID3D11ShaderResourceView[] { _intermediateView! });
                _context.CSSetUnorderedAccessViews(0, 1, new ID3D11UnorderedAccessView[] { _targetUnorderedView! });
                Dispatch(targetWidth, targetHeight);

                UnbindComputeResources();

                _context.CopyResource(_stagingBuffer!, _targetBuffer!);
                MappedSubresource mapped = _context.Map(_stagingBuffer!, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    Marshal.Copy(mapped.DataPointer, target, 0, targetBytes);
                }
                finally
                {
                    _context.Unmap(_stagingBuffer!, 0);
                }

                stopwatch.Stop();
                LastGpuMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        public void Dispose()
        {
            lock (_gpuLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                ReleaseFrameResources();
                _constantBuffer?.Dispose();
                _enhanceShader.Dispose();
                _upscaleShader.Dispose();
                _context.Dispose();
                _device.Dispose();
            }
        }

        private void EnsureResources(int sourceBytes, int targetBytes)
        {
            int sourceElements = Math.Max(1, sourceBytes / BytesPerPixel);
            int targetElements = Math.Max(1, targetBytes / BytesPerPixel);
            if (_sourceBuffer != null &&
                _targetBuffer != null &&
                _sourceBufferBytes == sourceBytes &&
                _targetBufferBytes == targetBytes &&
                _sourceElementCount == sourceElements &&
                _targetElementCount == targetElements)
            {
                return;
            }

            ReleaseFrameResources();

            _sourceBufferBytes = sourceBytes;
            _targetBufferBytes = targetBytes;
            _sourceElementCount = sourceElements;
            _targetElementCount = targetElements;

            _sourceBuffer = _device.CreateBuffer((uint)sourceBytes, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0);
            _intermediateBuffer = _device.CreateBuffer((uint)targetBytes, BindFlags.ShaderResource | BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0);
            _targetBuffer = _device.CreateBuffer((uint)targetBytes, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0);
            _stagingBuffer = _device.CreateBuffer((uint)targetBytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0);

            _sourceView = _device.CreateShaderResourceView(
                _sourceBuffer,
                new ShaderResourceViewDescription(_sourceBuffer, Format.R32_Typeless, 0, (uint)sourceElements, BufferExtendedShaderResourceViewFlags.Raw));
            _intermediateView = _device.CreateShaderResourceView(
                _intermediateBuffer,
                new ShaderResourceViewDescription(_intermediateBuffer, Format.R32_Typeless, 0, (uint)targetElements, BufferExtendedShaderResourceViewFlags.Raw));
            _intermediateUnorderedView = _device.CreateUnorderedAccessView(
                _intermediateBuffer,
                new UnorderedAccessViewDescription(_intermediateBuffer, Format.R32_Typeless, 0, (uint)targetElements, BufferUnorderedAccessViewFlags.Raw));
            _targetUnorderedView = _device.CreateUnorderedAccessView(
                _targetBuffer,
                new UnorderedAccessViewDescription(_targetBuffer, Format.R32_Typeless, 0, (uint)targetElements, BufferUnorderedAccessViewFlags.Raw));
        }

        private void ReleaseFrameResources()
        {
            _targetUnorderedView?.Dispose();
            _intermediateUnorderedView?.Dispose();
            _intermediateView?.Dispose();
            _sourceView?.Dispose();
            _stagingBuffer?.Dispose();
            _targetBuffer?.Dispose();
            _intermediateBuffer?.Dispose();
            _sourceBuffer?.Dispose();
            _targetUnorderedView = null;
            _intermediateUnorderedView = null;
            _intermediateView = null;
            _sourceView = null;
            _stagingBuffer = null;
            _targetBuffer = null;
            _intermediateBuffer = null;
            _sourceBuffer = null;
        }

        private void Dispatch(int width, int height)
        {
            uint groupsX = (uint)((width + ThreadGroupSize - 1) / ThreadGroupSize);
            uint groupsY = (uint)((height + ThreadGroupSize - 1) / ThreadGroupSize);
            _context.Dispatch(groupsX, groupsY, 1);
        }

        private void UnbindComputeResources()
        {
            _context.CSSetShaderResources(0, 1, new ID3D11ShaderResourceView[] { null! });
            _context.CSSetUnorderedAccessViews(0, 1, new ID3D11UnorderedAccessView[] { null! });
            _context.CSSetShader(null!);
        }

        private static byte[] CompileShader(string source, string entryPoint)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            int hr = D3DCompile(bytes, (nuint)bytes.Length, "AXVideoPlayerGpuSR", IntPtr.Zero, IntPtr.Zero, entryPoint, "cs_5_0", D3DCompileOptimizationLevel3, 0, out ID3DBlob? code, out ID3DBlob? errors);
            try
            {
                if (hr < 0)
                {
                    string message = "D3DCompile failed 0x" + hr.ToString("x8");
                    if (errors != null)
                        message += Environment.NewLine + ReadBlobText(errors);
                    throw new InvalidOperationException(message);
                }

                if (code == null)
                    throw new InvalidOperationException("D3DCompile returned no shader bytecode.");

                int byteCount = checked((int)code.GetBufferSize());
                byte[] bytecode = new byte[byteCount];
                Marshal.Copy(code.GetBufferPointer(), bytecode, 0, byteCount);
                return bytecode;
            }
            finally
            {
                ReleaseBlob(code);
                ReleaseBlob(errors);
            }
        }

        private static string ReadBlobText(ID3DBlob blob)
        {
            int length = checked((int)blob.GetBufferSize());
            byte[] bytes = new byte[length];
            Marshal.Copy(blob.GetBufferPointer(), bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void ReleaseBlob(ID3DBlob? blob)
        {
            if (blob != null && Marshal.IsComObject(blob))
                Marshal.ReleaseComObject(blob);
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern int D3DCompile(
            byte[] srcData,
            nuint srcDataSize,
            string? sourceName,
            IntPtr defines,
            IntPtr include,
            string entryPoint,
            string target,
            uint flags1,
            uint flags2,
            [MarshalAs(UnmanagedType.Interface)] out ID3DBlob? code,
            [MarshalAs(UnmanagedType.Interface)] out ID3DBlob? errorMsgs);

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderConstants
        {
            public uint SourceWidth;
            public uint SourceHeight;
            public uint SourcePitchBytes;
            public uint TargetWidth;
            public uint TargetHeight;
            public float ScaleX;
            public float ScaleY;
            public float Sharpness;
        }

        [ComImport]
        [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig]
            IntPtr GetBufferPointer();

            [PreserveSig]
            nuint GetBufferSize();
        }

        private const string GpuShaders = @"
ByteAddressBuffer Source : register(t0);
ByteAddressBuffer Intermediate : register(t0);
RWByteAddressBuffer IntermediateOut : register(u0);
RWByteAddressBuffer TargetOut : register(u0);

cbuffer Params : register(b0)
{
    uint SourceWidth;
    uint SourceHeight;
    uint SourcePitchBytes;
    uint TargetWidth;
    uint TargetHeight;
    float ScaleX;
    float ScaleY;
    float Sharpness;
}

uint ClampUInt(int value, uint maxValue)
{
    return (uint)max(0, min(value, (int)maxValue));
}

uint SourcePixel(uint x, uint y)
{
    x = min(x, SourceWidth - 1);
    y = min(y, SourceHeight - 1);
    return Source.Load(y * SourcePitchBytes + x * 4);
}

uint IntermediatePixel(uint x, uint y)
{
    x = min(x, TargetWidth - 1);
    y = min(y, TargetHeight - 1);
    return Intermediate.Load((y * TargetWidth + x) * 4);
}

float Channel(uint packed, uint channel)
{
    return (float)((packed >> (channel * 8)) & 255);
}

float Luma(uint packed)
{
    return Channel(packed, 2) * 0.2126 + Channel(packed, 1) * 0.7152 + Channel(packed, 0) * 0.0722;
}

float SourceChannel(int x, int y, uint channel)
{
    uint cx = ClampUInt(x, SourceWidth - 1);
    uint cy = ClampUInt(y, SourceHeight - 1);
    return Channel(SourcePixel(cx, cy), channel);
}

float EstimateSourceEdge(int x, int y)
{
    uint cx0 = ClampUInt(x - 1, SourceWidth - 1);
    uint cx1 = ClampUInt(x + 1, SourceWidth - 1);
    uint cy0 = ClampUInt(y - 1, SourceHeight - 1);
    uint cy1 = ClampUInt(y + 1, SourceHeight - 1);
    uint cx = ClampUInt(x, SourceWidth - 1);
    uint cy = ClampUInt(y, SourceHeight - 1);
    float left = Luma(SourcePixel(cx0, cy));
    float right = Luma(SourcePixel(cx1, cy));
    float up = Luma(SourcePixel(cx, cy0));
    float down = Luma(SourcePixel(cx, cy1));
    return min(1.0, (abs(right - left) + abs(down - up)) / 110.0);
}

uint PackPixel(float b, float g, float r, float a)
{
    uint ib = (uint)round(clamp(b, 0.0, 255.0));
    uint ig = (uint)round(clamp(g, 0.0, 255.0));
    uint ir = (uint)round(clamp(r, 0.0, 255.0));
    uint ia = (uint)round(clamp(a, 0.0, 255.0));
    return ib | (ig << 8) | (ir << 16) | (ia << 24);
}

[numthreads(16, 16, 1)]
void UpscaleMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= TargetWidth || id.y >= TargetHeight)
        return;

    float sourceXf = ((float)id.x + 0.5) * ScaleX - 0.5;
    float sourceYf = ((float)id.y + 0.5) * ScaleY - 0.5;
    int x0 = (int)floor(sourceXf);
    int y0 = (int)floor(sourceYf);
    int x1 = x0 + 1;
    int y1 = y0 + 1;
    float fx = clamp(sourceXf - (float)x0, 0.0, 1.0);
    float fy = clamp(sourceYf - (float)y0, 0.0, 1.0);
    int cx = (int)round(sourceXf);
    int cy = (int)round(sourceYf);
    float edge = EstimateSourceEdge(cx, cy);
    float adaptiveAmount = Sharpness * 1.15 * (0.75 + edge * 1.35);

    float values[4];
    [unroll]
    for (uint c = 0; c < 4; c++)
    {
        float top = lerp(SourceChannel(x0, y0, c), SourceChannel(x1, y0, c), fx);
        float bottom = lerp(SourceChannel(x0, y1, c), SourceChannel(x1, y1, c), fx);
        float value = lerp(top, bottom, fy);
        if (c < 3)
        {
            float center = SourceChannel(cx, cy, c);
            float blur = (SourceChannel(cx - 1, cy, c) + SourceChannel(cx + 1, cy, c) + SourceChannel(cx, cy - 1, c) + SourceChannel(cx, cy + 1, c)) * 0.25;
            float detail = center - blur;
            if (abs(detail) > 0.45)
                value += detail * adaptiveAmount;
        }
        values[c] = value;
    }

    IntermediateOut.Store((id.y * TargetWidth + id.x) * 4, PackPixel(values[0], values[1], values[2], values[3]));
}

[numthreads(16, 16, 1)]
void EnhanceMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= TargetWidth || id.y >= TargetHeight)
        return;

    uint centerPixel = IntermediatePixel(id.x, id.y);
    if (id.x == 0 || id.y == 0 || id.x + 1 >= TargetWidth || id.y + 1 >= TargetHeight || Sharpness <= 0.001)
    {
        TargetOut.Store((id.y * TargetWidth + id.x) * 4, centerPixel);
        return;
    }

    float amount = min(1.85, 0.50 + Sharpness * 0.72);
    float values[4];
    [unroll]
    for (uint c = 0; c < 4; c++)
    {
        float center = Channel(centerPixel, c);
        if (c >= 3)
        {
            values[c] = center;
            continue;
        }

        float blur =
            Channel(IntermediatePixel(id.x - 1, id.y - 1), c) +
            Channel(IntermediatePixel(id.x, id.y - 1), c) * 2.0 +
            Channel(IntermediatePixel(id.x + 1, id.y - 1), c) +
            Channel(IntermediatePixel(id.x - 1, id.y), c) * 2.0 +
            center * 4.0 +
            Channel(IntermediatePixel(id.x + 1, id.y), c) * 2.0 +
            Channel(IntermediatePixel(id.x - 1, id.y + 1), c) +
            Channel(IntermediatePixel(id.x, id.y + 1), c) * 2.0 +
            Channel(IntermediatePixel(id.x + 1, id.y + 1), c);
        float detail = center - blur / 16.0;
        values[c] = abs(detail) > 0.20 ? center + detail * amount : center;
    }

    TargetOut.Store((id.y * TargetWidth + id.x) * 4, PackPixel(values[0], values[1], values[2], values[3]));
}
";
    }
}
