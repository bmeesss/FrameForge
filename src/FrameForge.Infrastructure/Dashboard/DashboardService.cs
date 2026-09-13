using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Dashboard;

public sealed class DashboardService : IDashboardService
{
    private readonly IHardwareInfoService _hardware;
    private readonly ICs2DetectionService _cs2;
    private readonly IOptimizationCatalog _catalog;
    private readonly IProfileService _profiles;
    private readonly IAppSettingsService _settings;
    private readonly IAppLog _log;

    public DashboardService(
        IHardwareInfoService hardware,
        ICs2DetectionService cs2,
        IOptimizationCatalog catalog,
        IProfileService profiles,
        IAppSettingsService settings,
        IAppLog log)
    {
        _hardware = hardware;
        _cs2 = cs2;
        _catalog = catalog;
        _profiles = profiles;
        _settings = settings;
        _log = log;
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        _log.LogInformation("Building dashboard snapshot.");

        var hardwareTask = _hardware.GetHardwareInfoAsync(cancellationToken);
        var cs2Task = _cs2.DetectAsync(cancellationToken);
        var settingsTask = _settings.LoadAsync(cancellationToken);

        await Task.WhenAll(hardwareTask, cs2Task, settingsTask).ConfigureAwait(false);

        var settings = settingsTask.Result;
        var profile = settings.ActiveProfileId is null
            ? null
            : await _profiles.GetProfileAsync(settings.ActiveProfileId, cancellationToken).ConfigureAwait(false);

        var available = 0;
        foreach (var opt in _catalog.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await opt.CanApplyAsync(cancellationToken).ConfigureAwait(false))
                {
                    available++;
                }
            }
            catch
            {
                // ignore individual probe failures for dashboard
            }
        }

        // Also count advisory recommendations so the dashboard is useful when CS2 is missing
        var recommendationCount = Math.Max(available, _catalog.GetAll().Count);

        return new DashboardSnapshot
        {
            Hardware = hardwareTask.Result,
            Cs2 = cs2Task.Result,
            OptimizationStatus = available > 0 ? "Recommendations available" : "Analyzed",
            AvailableRecommendationCount = recommendationCount,
            AppliedOptimizationCount = 0,
            ActiveProfileName = profile?.Name,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}
