using System.Runtime.InteropServices;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Hardware;

/// <summary>
/// Cross-platform hardware detection using legitimate system APIs only.
/// No kernel drivers, no memory scanning.
/// </summary>
public sealed class HardwareInfoService : IHardwareInfoService
{
    public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new HardwareInfo
        {
            CpuName = DetectCpuName(),
            CpuCoreCount = Environment.ProcessorCount > 0
                ? Math.Max(1, Environment.ProcessorCount / 2)
                : 1,
            CpuThreadCount = Math.Max(1, Environment.ProcessorCount),
            GpuName = DetectGpuName(),
            TotalRamBytes = DetectTotalRamBytes(),
            WindowsVersion = DetectWindowsVersion(),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            OsDescription = RuntimeInformation.OSDescription
        };

        // Prefer logical/physical distinction when we can improve core count
        var refinedCores = TryGetPhysicalCoreCount();
        if (refinedCores is > 0)
        {
            info = new HardwareInfo
            {
                CpuName = info.CpuName,
                CpuCoreCount = refinedCores.Value,
                CpuThreadCount = info.CpuThreadCount,
                GpuName = info.GpuName,
                TotalRamBytes = info.TotalRamBytes,
                WindowsVersion = info.WindowsVersion,
                Architecture = info.Architecture,
                OsDescription = info.OsDescription
            };
        }

        return Task.FromResult(info);
    }

    private static string DetectCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            var fromEnv = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    foreach (var line in File.ReadLines("/proc/cpuinfo"))
                    {
                        if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                            {
                                return parts[1].Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        return RuntimeInformation.ProcessArchitecture + " CPU";
    }

    private static string DetectGpuName()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Legitimate sysfs / dri device names only — no driver hooks
                if (Directory.Exists("/sys/class/drm"))
                {
                    foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card?") )
                    {
                        var vendorPath = Path.Combine(card, "device", "vendor");
                        var devicePath = Path.Combine(card, "device", "device");
                        if (File.Exists(vendorPath) && File.Exists(devicePath))
                        {
                            var vendor = File.ReadAllText(vendorPath).Trim();
                            var device = File.ReadAllText(devicePath).Trim();
                            return $"GPU {vendor}:{device}";
                        }
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        // Windows WMI GPU enumeration is intentionally deferred to a future
        // Windows-specific implementation to keep the foundation portable.
        // TODO: Use ManagementObjectSearcher("SELECT Name FROM Win32_VideoController") on Windows.
        return OperatingSystem.IsWindows() ? "GPU (enumeration pending)" : "Unknown GPU";
    }

    private static long DetectTotalRamBytes()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            if (gcInfo.TotalAvailableMemoryBytes > 0)
            {
                return gcInfo.TotalAvailableMemoryBytes;
            }
        }
        catch
        {
            // fall through
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var number = new string(parts[1].Where(char.IsDigit).ToArray());
                            if (long.TryParse(number, out var kb))
                            {
                                return kb * 1024;
                            }
                        }
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        return 0;
    }

    private static string DetectWindowsVersion()
    {
        if (OperatingSystem.IsWindows())
        {
            var version = Environment.OSVersion.Version;
            // Windows 11 reports as 10.0 with build >= 22000
            if (version.Major >= 10 && version.Build >= 22000)
            {
                return $"Windows 11 (Build {version.Build})";
            }

            if (version.Major >= 10)
            {
                return $"Windows 10 (Build {version.Build})";
            }

            return $"Windows {version}";
        }

        return RuntimeInformation.OSDescription;
    }

    private static int? TryGetPhysicalCoreCount()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            try
            {
                var cores = new HashSet<string>(StringComparer.Ordinal);
                string? physicalId = null;
                string? coreId = null;
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("physical id", StringComparison.OrdinalIgnoreCase))
                    {
                        physicalId = line.Split(':', 2).LastOrDefault()?.Trim();
                    }
                    else if (line.StartsWith("core id", StringComparison.OrdinalIgnoreCase))
                    {
                        coreId = line.Split(':', 2).LastOrDefault()?.Trim();
                    }
                    else if (string.IsNullOrWhiteSpace(line) && physicalId is not null && coreId is not null)
                    {
                        cores.Add($"{physicalId}-{coreId}");
                        physicalId = null;
                        coreId = null;
                    }
                }

                if (physicalId is not null && coreId is not null)
                {
                    cores.Add($"{physicalId}-{coreId}");
                }

                if (cores.Count > 0)
                {
                    return cores.Count;
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
