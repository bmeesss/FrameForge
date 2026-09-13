namespace FrameForge.Core.Models;

/// <summary>
/// Snapshot of the host machine hardware and OS details.
/// </summary>
public sealed class HardwareInfo
{
    public string CpuName { get; init; } = "Unknown";
    public int CpuCoreCount { get; init; }
    public int CpuThreadCount { get; init; }
    public string GpuName { get; init; } = "Unknown";
    public long TotalRamBytes { get; init; }
    public string WindowsVersion { get; init; } = "Unknown";
    public string Architecture { get; init; } = "Unknown";
    public string OsDescription { get; init; } = "Unknown";

    public double TotalRamGigabytes => TotalRamBytes / (1024.0 * 1024.0 * 1024.0);

    public string FormattedRam => $"{TotalRamGigabytes:0.0} GB";
}
