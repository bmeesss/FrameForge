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

    /// <summary>Primary display resolution when reliably detected (e.g. "1920x1080").</summary>
    public string? DisplayResolution { get; init; }

    /// <summary>Primary display refresh rate in Hz when reliably detected.</summary>
    public int? DisplayRefreshRateHz { get; init; }

    /// <summary>Windows Game Mode enabled flag when readable from registry; null = unknown.</summary>
    public bool? GameModeEnabled { get; init; }

    /// <summary>Active power plan friendly name (read-only). Null if unavailable.</summary>
    public string? PowerPlanName { get; init; }

    /// <summary>Honest status when power plan cannot be read.</summary>
    public string? PowerPlanStatus { get; init; }

    /// <summary>GPU adapter enumeration notes (never invents utilization).</summary>
    public string? GpuDetectionNotes { get; init; }

    public double TotalRamGigabytes => TotalRamBytes / (1024.0 * 1024.0 * 1024.0);

    public string FormattedRam => $"{TotalRamGigabytes:0.0} GB";
}
