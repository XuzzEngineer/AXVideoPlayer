using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AXVideoPlayer
{
    internal static class ResourceProfileNames
    {
        public const string BalancedAuto = "BalancedAuto";
        public const string MaxSpeed = "MaxSpeed";
        public const string QuietStable = "QuietStable";

        public static string Normalize(string? value)
        {
            if (string.Equals(value, MaxSpeed, StringComparison.OrdinalIgnoreCase))
                return MaxSpeed;
            if (string.Equals(value, QuietStable, StringComparison.OrdinalIgnoreCase))
                return QuietStable;

            return BalancedAuto;
        }
    }

    internal sealed class ResourcePlan
    {
        public string ProfileName { get; set; } = ResourceProfileNames.BalancedAuto;
        public int CpuLogicalProcessors { get; set; }
        public long TotalMemoryMb { get; set; }
        public bool PreferGpuForLive { get; set; }
        public bool GpuComputeAvailable { get; set; }
        public int LiveMaxInFlightFrames { get; set; } = 1;
        public int LiveCpuWorkerThreads { get; set; } = 2;
        public int LivePresentationQueueFrames { get; set; } = 6;
        public int OfflineLoadThreads { get; set; } = 2;
        public int OfflineProcessThreads { get; set; } = 4;
        public int OfflineSaveThreads { get; set; } = 2;
        public string GpuName { get; set; } = "Unknown GPU";
        public string GpuDriverVersion { get; set; } = string.Empty;
        public string GpuStatus { get; set; } = "Not scanned";
        public DateTime LastScanUtc { get; set; } = DateTime.MinValue;

        public string NcnnThreadPlan =>
            OfflineLoadThreads.ToString(CultureInfo.InvariantCulture) + ":" +
            OfflineProcessThreads.ToString(CultureInfo.InvariantCulture) + ":" +
            OfflineSaveThreads.ToString(CultureInfo.InvariantCulture);

        public static ResourcePlan CreateFallback(string? profileName = null)
        {
            return CreateBalanced(
                ResourceProfileNames.Normalize(profileName),
                Math.Max(2, Environment.ProcessorCount),
                0,
                Array.Empty<HardwareGpuInfo>(),
                gpuComputeAvailable: false,
                "GPU compute not scanned",
                DateTime.UtcNow);
        }

        public static ResourcePlan CreateBalanced(
            string? profileName,
            int logicalProcessors,
            long totalMemoryMb,
            IReadOnlyList<HardwareGpuInfo> gpus,
            bool gpuComputeAvailable,
            string gpuStatus,
            DateTime scannedUtc)
        {
            int cpuCount = Math.Max(2, logicalProcessors);
            bool lowMemory = totalMemoryMb > 0 && totalMemoryMb < 8192;
            HardwareGpuInfo? primaryGpu = ChoosePrimaryGpu(gpus);
            int liveWorkers = Math.Max(1, Math.Min(4, cpuCount / 4));
            int loadThreads = lowMemory ? 2 : Math.Max(1, Math.Min(4, cpuCount / 5));
            int processThreads = lowMemory ? 4 : Math.Max(2, Math.Min(8, cpuCount / 2));
            int saveThreads = lowMemory ? 2 : Math.Max(1, Math.Min(4, cpuCount / 5));

            return new ResourcePlan
            {
                ProfileName = ResourceProfileNames.Normalize(profileName),
                CpuLogicalProcessors = cpuCount,
                TotalMemoryMb = Math.Max(0, totalMemoryMb),
                PreferGpuForLive = gpuComputeAvailable,
                GpuComputeAvailable = gpuComputeAvailable,
                LiveMaxInFlightFrames = gpuComputeAvailable ? 2 : 1,
                LiveCpuWorkerThreads = liveWorkers,
                LivePresentationQueueFrames = gpuComputeAvailable ? 8 : 6,
                OfflineLoadThreads = loadThreads,
                OfflineProcessThreads = processThreads,
                OfflineSaveThreads = saveThreads,
                GpuName = string.IsNullOrWhiteSpace(primaryGpu?.Name) ? "Unknown GPU" : primaryGpu.Name,
                GpuDriverVersion = primaryGpu?.DriverVersion ?? string.Empty,
                GpuStatus = string.IsNullOrWhiteSpace(gpuStatus) ? "GPU compute not scanned" : gpuStatus,
                LastScanUtc = scannedUtc
            };
        }

        public string FormatLiveStatus(string? fallbackReason = null, bool lastFrameUsedGpu = false)
        {
            string gpuText = GpuComputeAvailable
                ? lastFrameUsedGpu ? "GPU active" : "GPU ready"
                : "CPU fallback";
            if (!string.IsNullOrWhiteSpace(fallbackReason))
                gpuText = "CPU fallback: " + fallbackReason;

            return "plan " + ProfileName +
                ", CPU " + LiveCpuWorkerThreads.ToString(CultureInfo.InvariantCulture) + " workers" +
                ", in-flight " + LiveMaxInFlightFrames.ToString(CultureInfo.InvariantCulture) +
                ", queue cap " + LivePresentationQueueFrames.ToString(CultureInfo.InvariantCulture) +
                ", " + gpuText;
        }

        public string FormatOfflineStatus()
        {
            return "plan " + ProfileName +
                ", CPU " + CpuLogicalProcessors.ToString(CultureInfo.InvariantCulture) + " logical" +
                ", ncnn -j " + NcnnThreadPlan +
                ", " + (GpuComputeAvailable ? "GPU compute available" : "CPU/GPU fallback");
        }

        public string FormatDetailedStatus()
        {
            string memoryText = TotalMemoryMb > 0
                ? TotalMemoryMb.ToString("N0", CultureInfo.InvariantCulture) + " MB RAM"
                : "RAM unknown";
            string driverText = string.IsNullOrWhiteSpace(GpuDriverVersion) ? string.Empty : ", driver " + GpuDriverVersion;
            string scannedText = LastScanUtc == DateTime.MinValue
                ? "not scanned"
                : LastScanUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

            return "Balanced Auto: " + CpuLogicalProcessors.ToString(CultureInfo.InvariantCulture) +
                " logical CPU, " + memoryText +
                "\nGPU: " + GpuName + driverText +
                "\nLive: " + LiveMaxInFlightFrames.ToString(CultureInfo.InvariantCulture) +
                " in-flight, " + LiveCpuWorkerThreads.ToString(CultureInfo.InvariantCulture) +
                " CPU workers, queue cap " + LivePresentationQueueFrames.ToString(CultureInfo.InvariantCulture) +
                "\nExport: ncnn -j " + NcnnThreadPlan + ", FFmpeg -threads 0" +
                "\nProbe: " + GpuStatus +
                "\nLast scan: " + scannedText;
        }

        private static HardwareGpuInfo? ChoosePrimaryGpu(IReadOnlyList<HardwareGpuInfo> gpus)
        {
            if (gpus.Count == 0)
                return null;

            HardwareGpuInfo? best = null;
            long bestMemory = -1;
            foreach (HardwareGpuInfo gpu in gpus)
            {
                long memory = Math.Max(gpu.DedicatedVideoMemoryMb, gpu.AdapterRamMb);
                if (best == null || memory > bestMemory)
                {
                    best = gpu;
                    bestMemory = memory;
                }
            }

            return best;
        }
    }

    internal sealed class HardwareProfile
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime ScannedUtc { get; set; } = DateTime.MinValue;
        public string CpuName { get; set; } = string.Empty;
        public int LogicalProcessors { get; set; }
        public long TotalMemoryMb { get; set; }
        public List<HardwareGpuInfo> Gpus { get; set; } = new();
        public bool D3D11ComputeAvailable { get; set; }
        public double D3D11ProbeMilliseconds { get; set; }
        public string D3D11ProbeStatus { get; set; } = "Not scanned";
        public string HardwareSignature { get; set; } = string.Empty;
        public ResourcePlan RecommendedPlan { get; set; } = ResourcePlan.CreateFallback();

        public static HardwareProfile CreateFallback(string? profileName = null)
        {
            return new HardwareProfile
            {
                ScannedUtc = DateTime.UtcNow,
                CpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU",
                LogicalProcessors = Math.Max(2, Environment.ProcessorCount),
                D3D11ProbeStatus = "Hardware profile not scanned",
                HardwareSignature = string.Empty,
                RecommendedPlan = ResourcePlan.CreateFallback(profileName)
            };
        }
    }

    internal sealed class HardwareGpuInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DriverVersion { get; set; } = string.Empty;
        public long AdapterRamMb { get; set; }
        public long DedicatedVideoMemoryMb { get; set; }
    }

    internal sealed class HardwareCapabilityService
    {
        private const int SchemaVersion = 1;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
        private readonly string _filePath;

        public HardwareCapabilityService()
        {
            _filePath = AppStoragePaths.GetUserDataFilePath("AXVideoPlayer.hardware-profile.json");
        }

        public string FilePath => _filePath;

        public HardwareProfile LoadOrScan(string? resourceProfile)
        {
            HardwareProfile? cached = TryLoadCachedProfile();
            string currentSignature = BuildHardwareSignature(GetCpuName(), Environment.ProcessorCount, ScanGpus(fast: true));
            if (cached != null &&
                cached.SchemaVersion == SchemaVersion &&
                DateTime.UtcNow - cached.ScannedUtc <= CacheTtl &&
                string.Equals(cached.HardwareSignature, currentSignature, StringComparison.Ordinal))
            {
                cached.RecommendedPlan = BuildPlan(cached, resourceProfile);
                return cached;
            }

            return Rescan(resourceProfile);
        }

        public HardwareProfile Rescan(string? resourceProfile)
        {
            HardwareProfile profile = Scan(resourceProfile);
            TrySaveProfile(profile);
            return profile;
        }

        private HardwareProfile Scan(string? resourceProfile)
        {
            DateTime scannedUtc = DateTime.UtcNow;
            string cpuName = GetCpuName();
            int logicalProcessors = Math.Max(2, Environment.ProcessorCount);
            long totalMemoryMb = GetTotalMemoryMb();
            List<HardwareGpuInfo> gpus = ScanGpus(fast: false);
            ProbeResult probe = RunD3D11Probe();

            var profile = new HardwareProfile
            {
                SchemaVersion = SchemaVersion,
                ScannedUtc = scannedUtc,
                CpuName = cpuName,
                LogicalProcessors = logicalProcessors,
                TotalMemoryMb = totalMemoryMb,
                Gpus = gpus,
                D3D11ComputeAvailable = probe.Available,
                D3D11ProbeMilliseconds = probe.ElapsedMilliseconds,
                D3D11ProbeStatus = probe.Status,
                HardwareSignature = BuildHardwareSignature(cpuName, logicalProcessors, gpus)
            };
            profile.RecommendedPlan = BuildPlan(profile, resourceProfile);
            return profile;
        }

        private ResourcePlan BuildPlan(HardwareProfile profile, string? resourceProfile)
        {
            return ResourcePlan.CreateBalanced(
                resourceProfile,
                profile.LogicalProcessors,
                profile.TotalMemoryMb,
                profile.Gpus,
                profile.D3D11ComputeAvailable,
                profile.D3D11ProbeStatus,
                profile.ScannedUtc);
        }

        private HardwareProfile? TryLoadCachedProfile()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return null;

                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<HardwareProfile>(json);
            }
            catch
            {
                return null;
            }
        }

        private void TrySaveProfile(HardwareProfile profile)
        {
            try
            {
                string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Hardware profile caching is an optimization; do not interrupt playback.
            }
        }

        private static string GetCpuName()
        {
            string? processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(processor))
                return processor;

            string? architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            return string.IsNullOrWhiteSpace(architecture) ? "Unknown CPU" : architecture;
        }

        private static string BuildHardwareSignature(string cpuName, int logicalProcessors, IReadOnlyList<HardwareGpuInfo> gpus)
        {
            var parts = new List<string>
            {
                cpuName.Trim(),
                logicalProcessors.ToString(CultureInfo.InvariantCulture)
            };
            foreach (HardwareGpuInfo gpu in gpus.OrderBy(gpu => gpu.Name, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add((gpu.Name ?? string.Empty).Trim());
                parts.Add((gpu.DriverVersion ?? string.Empty).Trim());
                parts.Add(gpu.AdapterRamMb.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join("|", parts);
        }

        private static List<HardwareGpuInfo> ScanGpus(bool fast)
        {
            var gpus = new List<HardwareGpuInfo>();
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.ArgumentList.Add("-NoProfile");
                process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
                process.StartInfo.ArgumentList.Add("Bypass");
                process.StartInfo.ArgumentList.Add("-Command");
                process.StartInfo.ArgumentList.Add("Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,DriverVersion,VideoProcessor | ConvertTo-Json -Compress");

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(fast ? 1000 : 3000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    return gpus;
                }

                ParseGpuJson(output, gpus);
            }
            catch
            {
            }

            return gpus;
        }

        private static void ParseGpuJson(string json, List<HardwareGpuInfo> gpus)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in document.RootElement.EnumerateArray())
                    AddGpuFromJson(element, gpus);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddGpuFromJson(document.RootElement, gpus);
            }
        }

        private static void AddGpuFromJson(JsonElement element, List<HardwareGpuInfo> gpus)
        {
            string name = GetJsonString(element, "Name");
            if (string.IsNullOrWhiteSpace(name))
                return;

            long adapterRamMb = BytesToMb(GetJsonInt64(element, "AdapterRAM"));
            gpus.Add(new HardwareGpuInfo
            {
                Name = name,
                DriverVersion = GetJsonString(element, "DriverVersion"),
                AdapterRamMb = adapterRamMb,
                DedicatedVideoMemoryMb = adapterRamMb
            });
        }

        private static string GetJsonString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static long GetJsonInt64(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return 0;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                return number;
            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }

            return 0;
        }

        private static long BytesToMb(long bytes)
        {
            return bytes > 0 ? bytes / (1024 * 1024) : 0;
        }

        private static long GetTotalMemoryMb()
        {
            var status = new MemoryStatusEx();
            status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
            return GlobalMemoryStatusEx(ref status) ? (long)(status.ullTotalPhys / (1024UL * 1024UL)) : 0;
        }

        private static ProbeResult RunD3D11Probe()
        {
            byte[] source = ArrayPool<byte>.Shared.Rent(16 * 16 * 4);
            byte[] target = ArrayPool<byte>.Shared.Rent(32 * 32 * 4);
            try
            {
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int index = (y * 16 + x) * 4;
                        source[index] = (byte)(x * 16);
                        source[index + 1] = (byte)(y * 16);
                        source[index + 2] = (byte)((x + y) * 8);
                        source[index + 3] = 255;
                    }
                }

                using var processor = new GpuSuperResolutionProcessor();
                processor.ProcessFrame(source, 16, 16, 16 * 4, 16 * 16 * 4, target, 32, 32, 0.25);
                return new ProbeResult(true, processor.LastGpuMilliseconds, "D3D11 compute probe OK in " + processor.LastGpuMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
            }
            catch (Exception ex)
            {
                return new ProbeResult(false, 0.0, "D3D11 compute probe failed: " + ex.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(source);
                ArrayPool<byte>.Shared.Return(target);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        private readonly struct ProbeResult
        {
            public ProbeResult(bool available, double elapsedMilliseconds, string status)
            {
                Available = available;
                ElapsedMilliseconds = elapsedMilliseconds;
                Status = status;
            }

            public bool Available { get; }

            public double ElapsedMilliseconds { get; }

            public string Status { get; }
        }
    }
}
