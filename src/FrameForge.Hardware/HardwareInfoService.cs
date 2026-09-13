using System.Runtime.InteropServices;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Hardware;

/// <summary>
/// Cross-platform hardware detection using legitimate system APIs only.
/// No kernel drivers, no memory scanning, no third-party packages.
/// Unknown hardware is reported gracefully rather than throwing.
/// </summary>
public sealed class HardwareInfoService : IHardwareInfoService
{
    public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var threadCount = Math.Max(1, Environment.ProcessorCount);
            var physicalCores = TryGetPhysicalCoreCount() ?? EstimatePhysicalCores(threadCount);

            var info = new HardwareInfo
            {
                CpuName = DetectCpuName(),
                CpuCoreCount = physicalCores,
                CpuThreadCount = threadCount,
                GpuName = DetectGpuName(),
                TotalRamBytes = DetectTotalRamBytes(),
                WindowsVersion = DetectOsVersionLabel(),
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                OsDescription = SafeOsDescription()
            };

            return Task.FromResult(info);
        }
        catch (Exception)
        {
            // Absolute last resort — never throw for inventory failures.
            return Task.FromResult(new HardwareInfo
            {
                CpuName = "Unknown CPU",
                CpuCoreCount = Math.Max(1, Environment.ProcessorCount),
                CpuThreadCount = Math.Max(1, Environment.ProcessorCount),
                GpuName = "Unknown GPU",
                TotalRamBytes = 0,
                WindowsVersion = "Unknown",
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                OsDescription = "Unknown OS"
            });
        }
    }

    private static int EstimatePhysicalCores(int threads) =>
        // Without topology data, prefer threads (honest) over inventing a /2 split.
        // Callers see CpuCoreCount == CpuThreadCount when topology is unknown.
        Math.Max(1, threads);

    private static string DetectCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            // PROCESSOR_IDENTIFIER is always present on modern Windows; brand string may be coarser.
            var brand = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(brand))
            {
                return brand.Trim();
            }

            var arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (!string.IsNullOrWhiteSpace(arch))
            {
                return $"{arch.Trim()} CPU";
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
                        if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("cpu model", StringComparison.OrdinalIgnoreCase))
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

        if (OperatingSystem.IsMacOS())
        {
            // sysctl is available but may be restricted; keep graceful.
            try
            {
                var brand = Environment.GetEnvironmentVariable("CPU_BRAND");
                if (!string.IsNullOrWhiteSpace(brand))
                {
                    return brand.Trim();
                }
            }
            catch
            {
                // fall through
            }
        }

        return $"{RuntimeInformation.ProcessArchitecture} CPU";
    }

    private static string DetectGpuName()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Prefer DRM device names / uevent DRIVER=
                if (Directory.Exists("/sys/class/drm"))
                {
                    foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card?")
                                 .OrderBy(c => c, StringComparer.Ordinal))
                    {
                        var namePath = Path.Combine(card, "device", "label");
                        if (File.Exists(namePath))
                        {
                            var label = File.ReadAllText(namePath).Trim();
                            if (!string.IsNullOrWhiteSpace(label))
                            {
                                return label;
                            }
                        }

                        var uevent = Path.Combine(card, "device", "uevent");
                        if (File.Exists(uevent))
                        {
                            string? driver = null;
                            string? pci = null;
                            foreach (var line in File.ReadLines(uevent))
                            {
                                if (line.StartsWith("DRIVER=", StringComparison.OrdinalIgnoreCase))
                                {
                                    driver = line[7..].Trim();
                                }
                                else if (line.StartsWith("PCI_ID=", StringComparison.OrdinalIgnoreCase))
                                {
                                    pci = line[7..].Trim();
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(driver) || !string.IsNullOrWhiteSpace(pci))
                            {
                                return string.IsNullOrWhiteSpace(pci)
                                    ? $"GPU ({driver})"
                                    : $"GPU {pci} ({driver})";
                            }
                        }

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

        // Windows: WMI / DXGI would need platform packs. Report honestly.
        // TODO(windows): enumerate adapters via DXGI or Win32_VideoController without third-party packages.
        if (OperatingSystem.IsWindows())
        {
            return "GPU (name unavailable — Windows adapter enumeration pending)";
        }

        return "Unknown GPU";
    }

    private static long DetectTotalRamBytes()
    {
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
                            if (long.TryParse(number, out var kb) && kb > 0)
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

        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            if (gcInfo.TotalAvailableMemoryBytes > 0)
            {
                // This is an approximation of available addressable memory, not always physical RAM.
                return gcInfo.TotalAvailableMemoryBytes;
            }
        }
        catch
        {
            // fall through
        }

        return 0;
    }

    private static string DetectOsVersionLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            try
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
            catch
            {
                return "Windows (version unknown)";
            }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists("/etc/os-release"))
                {
                    string? pretty = null;
                    string? name = null;
                    string? version = null;
                    foreach (var line in File.ReadLines("/etc/os-release"))
                    {
                        if (line.StartsWith("PRETTY_NAME=", StringComparison.OrdinalIgnoreCase))
                        {
                            pretty = UnquoteEnv(line["PRETTY_NAME=".Length..]);
                        }
                        else if (line.StartsWith("NAME=", StringComparison.OrdinalIgnoreCase))
                        {
                            name = UnquoteEnv(line["NAME=".Length..]);
                        }
                        else if (line.StartsWith("VERSION=", StringComparison.OrdinalIgnoreCase))
                        {
                            version = UnquoteEnv(line["VERSION=".Length..]);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(pretty))
                    {
                        return pretty;
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        return SafeOsDescription();
    }

    private static string UnquoteEnv(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string SafeOsDescription()
    {
        try
        {
            return RuntimeInformation.OSDescription;
        }
        catch
        {
            return "Unknown OS";
        }
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

                // Some ARM systems only expose "processor" lines
                var processors = File.ReadLines("/proc/cpuinfo")
                    .Count(l => l.StartsWith("processor", StringComparison.OrdinalIgnoreCase));
                if (processors > 0)
                {
                    return processors;
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
