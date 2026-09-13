namespace FrameForge.Core.Models;

/// <summary>
/// Aggregate view model data for the Home dashboard.
/// </summary>
public sealed class DashboardSnapshot
{
    public Cs2InstallInfo Cs2 { get; init; } = Cs2InstallInfo.NotFound("Not scanned yet");
    public HardwareInfo Hardware { get; init; } = new();
    public string OptimizationStatus { get; init; } = "Not analyzed";
    public int AvailableRecommendationCount { get; init; }
    public int ApplicableOptimizationCount { get; init; }
    public int AppliedOptimizationCount { get; init; }
    public int BackupCount { get; init; }
    public string? ActiveProfileName { get; init; }
    public OptimizationScore? Score { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
