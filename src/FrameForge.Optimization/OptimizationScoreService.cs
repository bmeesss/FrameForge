using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Optimization;

/// <summary>
/// Computes an Optimization Score from detected conditions only.
/// Never invents FPS numbers or unverified performance claims.
/// </summary>
public sealed class OptimizationScoreService : IOptimizationScoreService
{
    private readonly ICs2DetectionService _cs2;
    private readonly IHardwareInfoService _hardware;
    private readonly IOptimizationCatalog _catalog;
    private readonly IBackupService _backups;
    private readonly IAppSettingsService _settings;
    private readonly IAppLog? _log;

    public OptimizationScoreService(
        ICs2DetectionService cs2,
        IHardwareInfoService hardware,
        IOptimizationCatalog catalog,
        IBackupService backups,
        IAppSettingsService settings,
        IAppLog? log = null)
    {
        _cs2 = cs2;
        _hardware = hardware;
        _catalog = catalog;
        _backups = backups;
        _settings = settings;
        _log = log;
    }

    public async Task<OptimizationScore> CalculateAsync(CancellationToken cancellationToken = default)
    {
        _log?.LogInformation("Calculating optimization score.");

        var cs2Task = _cs2.DetectAsync(cancellationToken);
        var hardwareTask = _hardware.GetHardwareInfoAsync(cancellationToken);
        var settingsTask = _settings.LoadAsync(cancellationToken);
        var backupsTask = _backups.ListBackupsAsync(cancellationToken);

        await Task.WhenAll(cs2Task, hardwareTask, settingsTask, backupsTask).ConfigureAwait(false);

        var cs2 = await cs2Task.ConfigureAwait(false);
        var hardware = await hardwareTask.ConfigureAwait(false);
        var settings = await settingsTask.ConfigureAwait(false);
        var backups = await backupsTask.ConfigureAwait(false);

        var factors = new List<ScoreFactor>();

        // CS2 detection (25)
        factors.Add(Factor(
            "cs2.detected",
            "Counter-Strike 2 installation detected",
            25,
            cs2.IsInstalled,
            cs2.IsInstalled
                ? $"Found at {cs2.InstallPath}"
                : cs2.DetectionMessage));

        // Steam found even if CS2 missing (5) — only if CS2 not already scored full path
        if (!cs2.IsInstalled)
        {
            factors.Add(Factor(
                "steam.found",
                "Steam installation discovered",
                5,
                cs2.SteamFound,
                cs2.SteamFound
                    ? $"Steam roots: {cs2.SearchedSteamRoots.Count}"
                    : "Steam was not found"));
        }

        // Configuration directory healthy (15)
        var cfgHealthy = cs2.IsInstalled &&
                         !string.IsNullOrWhiteSpace(cs2.CfgDirectory) &&
                         Directory.Exists(cs2.CfgDirectory);
        factors.Add(Factor(
            "cs2.cfg-healthy",
            "CS2 configuration directory is present and accessible",
            15,
            cfgHealthy,
            cfgHealthy
                ? cs2.CfgDirectory!
                : cs2.IsInstalled
                    ? "CFG directory missing or inaccessible"
                    : "Requires CS2 install"));

        // FrameForge cfg files present (10)
        var hasFrameForgeCfg = false;
        if (cfgHealthy)
        {
            try
            {
                hasFrameForgeCfg = Directory.EnumerateFiles(cs2.CfgDirectory!, "frameforge_*.cfg").Any();
            }
            catch
            {
                hasFrameForgeCfg = false;
            }
        }

        factors.Add(Factor(
            "cs2.frameforge-cfg",
            "FrameForge configuration presets applied on disk",
            10,
            hasFrameForgeCfg,
            hasFrameForgeCfg
                ? "One or more frameforge_*.cfg files found"
                : "No FrameForge cfg presets written yet"));

        // Backup available (15)
        var hasBackup = backups.Count > 0;
        factors.Add(Factor(
            "backup.available",
            "At least one restore point exists",
            15,
            hasBackup,
            hasBackup
                ? $"{backups.Count} backup(s) available"
                : "No backups created yet"));

        // Automatic backup enabled (10)
        factors.Add(Factor(
            "settings.auto-backup",
            "Automatic backup before apply is enabled",
            10,
            settings.AutomaticBackup,
            settings.AutomaticBackup
                ? "Automatic backup is on"
                : "Automatic backup is off — enable it in Settings"));

        // Recommended / applicable optimizations known (10)
        var applicable = 0;
        foreach (var opt in _catalog.GetAll())
        {
            try
            {
                if (await opt.CanApplyAsync(cancellationToken).ConfigureAwait(false))
                {
                    applicable++;
                }
            }
            catch
            {
                // ignore
            }
        }

        var hasRecommendations = _catalog.GetAll().Count > 0;
        factors.Add(Factor(
            "opt.catalog",
            "Optimization catalog loaded with recommendations",
            10,
            hasRecommendations,
            hasRecommendations
                ? $"{_catalog.GetAll().Count} recommendation(s), {applicable} currently applicable"
                : "Catalog empty"));

        // Hardware inventory complete (10)
        var hardwareOk =
            !string.IsNullOrWhiteSpace(hardware.CpuName) &&
            hardware.CpuThreadCount > 0 &&
            !string.IsNullOrWhiteSpace(hardware.Architecture) &&
            !string.IsNullOrWhiteSpace(hardware.OsDescription);
        factors.Add(Factor(
            "hw.inventory",
            "Hardware inventory collected (CPU / RAM / OS / arch)",
            10,
            hardwareOk,
            hardwareOk
                ? $"{hardware.CpuName}, {hardware.FormattedRam}, {hardware.Architecture}"
                : "Incomplete hardware inventory"));

        // Active profile selected (5)
        var profileSelected = !string.IsNullOrWhiteSpace(settings.ActiveProfileId);
        factors.Add(Factor(
            "profile.active",
            "An active performance profile is selected",
            5,
            profileSelected,
            profileSelected
                ? $"Active profile id: {settings.ActiveProfileId}"
                : "No active profile selected"));

        // Windows Game Mode — honest: status unknown without a Windows probe implementation
        var gameModeKnown = false;
        factors.Add(Factor(
            "windows.game-mode",
            "Windows Game Mode status known",
            5,
            gameModeKnown,
            "Game Mode status probe not implemented yet (no points awarded)"));

        var score = factors.Where(f => f.Awarded).Sum(f => f.Points);
        var max = factors.Sum(f => f.Points);

        var summary = score switch
        {
            >= 80 => "Strong readiness — core detection and safety nets are in place.",
            >= 50 => "Moderate readiness — some recommendations or backups still missing.",
            >= 25 => "Early setup — detect CS2 and enable backups to improve the score.",
            _ => "Limited readiness — Steam/CS2 not detected or inventory incomplete."
        };

        return new OptimizationScore
        {
            Score = score,
            MaxScore = max,
            Factors = factors,
            Summary = summary
        };
    }

    private static ScoreFactor Factor(string id, string description, int points, bool awarded, string reason) =>
        new()
        {
            Id = id,
            Description = description,
            Points = points,
            Awarded = awarded,
            Reason = reason
        };
}
