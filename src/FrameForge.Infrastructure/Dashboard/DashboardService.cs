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
    private readonly IBackupService _backups;
    private readonly IOptimizationScoreService _scoreService;
    private readonly IAppLog _log;

    public DashboardService(
        IHardwareInfoService hardware,
        ICs2DetectionService cs2,
        IOptimizationCatalog catalog,
        IProfileService profiles,
        IAppSettingsService settings,
        IBackupService backups,
        IOptimizationScoreService scoreService,
        IAppLog log)
    {
        _hardware = hardware;
        _cs2 = cs2;
        _catalog = catalog;
        _profiles = profiles;
        _settings = settings;
        _backups = backups;
        _scoreService = scoreService;
        _log = log;
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        _log.LogInformation("Building dashboard snapshot.");

        var hardwareTask = _hardware.GetHardwareInfoAsync(cancellationToken);
        var cs2Task = _cs2.DetectAsync(cancellationToken);
        var settingsTask = _settings.LoadAsync(cancellationToken);
        var backupsTask = _backups.ListBackupsAsync(cancellationToken);
        var scoreTask = _scoreService.CalculateAsync(cancellationToken);

        await Task.WhenAll(hardwareTask, cs2Task, settingsTask, backupsTask, scoreTask).ConfigureAwait(false);

        var settings = await settingsTask.ConfigureAwait(false);
        var profile = settings.ActiveProfileId is null
            ? null
            : await _profiles.GetProfileAsync(settings.ActiveProfileId, cancellationToken).ConfigureAwait(false);

        var applicable = 0;
        foreach (var opt in _catalog.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await opt.CanApplyAsync(cancellationToken).ConfigureAwait(false))
                {
                    applicable++;
                }
            }
            catch
            {
                // ignore individual probe failures for dashboard
            }
        }

        var totalRecommendations = _catalog.GetAll().Count;
        var cs2 = await cs2Task.ConfigureAwait(false);
        var backups = await backupsTask.ConfigureAwait(false);
        var score = await scoreTask.ConfigureAwait(false);

        var status = cs2.IsInstalled
            ? (applicable > 0 ? "Recommendations available" : "Analyzed — no pending applicable tweaks")
            : "CS2 not detected";

        return new DashboardSnapshot
        {
            Hardware = await hardwareTask.ConfigureAwait(false),
            Cs2 = cs2,
            OptimizationStatus = status,
            AvailableRecommendationCount = totalRecommendations,
            ApplicableOptimizationCount = applicable,
            AppliedOptimizationCount = 0,
            BackupCount = backups.Count,
            ActiveProfileName = profile?.Name,
            Score = score,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}
