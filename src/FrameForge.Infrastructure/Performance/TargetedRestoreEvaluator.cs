using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Determines whether a specific setting could safely be surgically restored.
/// Does NOT perform restore — full backup remains the safe UI path.
/// </summary>
public sealed class TargetedRestoreEvaluator : ITargetedRestoreEvaluator
{
    private readonly ISettingChangeSnapshotStore _snapshots;
    private readonly IBackupService _backups;
    private readonly ICs2SettingsService _settings;
    private readonly ICs2ConfigService _config;
    private readonly ICs2SettingCatalog _catalog;

    public TargetedRestoreEvaluator(
        ISettingChangeSnapshotStore snapshots,
        IBackupService backups,
        ICs2SettingsService settings,
        ICs2ConfigService config,
        ICs2SettingCatalog catalog)
    {
        _snapshots = snapshots;
        _backups = backups;
        _settings = settings;
        _config = config;
        _catalog = catalog;
    }

    public async Task<TargetedRestoreAssessment> EvaluateAsync(
        string configKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configKey))
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = configKey ?? string.Empty,
                Safety = TargetedRestoreSafety.NotTracked,
                Message = "Config key is required.",
                PreferFullBackupRestore = true
            };
        }

        var def = _catalog.GetByConfigKey(configKey) ?? _catalog.GetById(configKey);
        var key = def?.ConfigKey ?? configKey;

        var latest = await _snapshots.GetLatestForKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.NotTracked,
                Message =
                    "No per-key FrameForge change snapshot exists for this setting. Use full backup restore.",
                PreferFullBackupRestore = true
            };
        }

        if (string.IsNullOrWhiteSpace(latest.BackupId))
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.InsufficientMetadata,
                Message = "Snapshot lacks a linked full backup id. Use full backup restore.",
                LatestSnapshot = latest,
                PreferFullBackupRestore = true
            };
        }

        var backup = await _backups.GetBackupAsync(latest.BackupId, cancellationToken).ConfigureAwait(false);
        if (backup is null)
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.InsufficientMetadata,
                Message = $"Linked backup '{latest.BackupId}' is missing. Use full backup restore if available.",
                LatestSnapshot = latest,
                PreferFullBackupRestore = true
            };
        }

        // Read live managed file value
        var snap = await _settings.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!snap.Cs2Available || string.IsNullOrWhiteSpace(snap.ManagedConfigPath))
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.UnsafeToTargetRestore,
                Message = "CS2 cfg directory unavailable — cannot verify ownership for surgical restore.",
                LatestSnapshot = latest,
                PreferFullBackupRestore = true
            };
        }

        var live = snap.Settings.FirstOrDefault(s =>
            s.Definition.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase));

        // If current value is not from FrameForge managed source, user/external edit may have occurred.
        if (live is not null &&
            live.Source is not Cs2SettingSource.FrameForgeManaged and not Cs2SettingSource.Default)
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.UnsafeToTargetRestore,
                Message =
                    $"Current value source is '{live.Source}' (not FrameForge-managed). " +
                    "Surgical restore could fight user/autoexec edits. Prefer full backup restore.",
                LatestSnapshot = latest,
                PreferFullBackupRestore = true
            };
        }

        // If live value already differs from both previous and new without matching chain, treat unsafe
        if (live?.CurrentValue is not null &&
            latest.NewValue is not null &&
            latest.PreviousValue is not null &&
            !string.Equals(live.CurrentValue.Trim(), latest.NewValue.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(live.CurrentValue.Trim(), latest.PreviousValue.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.UnsafeToTargetRestore,
                Message =
                    "Live value matches neither the recorded previous nor new value — external change detected. " +
                    "UnsafeToTargetRestore; use full backup restore.",
                LatestSnapshot = latest,
                PreferFullBackupRestore = true
            };
        }

        // Verify managed file path matches snapshot file when present
        if (!string.IsNullOrWhiteSpace(latest.File) &&
            snap.ManagedConfigPath is not null &&
            !string.Equals(latest.File, snap.ManagedConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            return new TargetedRestoreAssessment
            {
                ConfigKey = key,
                Safety = TargetedRestoreSafety.UnsafeToTargetRestore,
                Message = "Managed cfg path changed since snapshot. Prefer full backup restore.",
                LatestSnapshot = latest,
                PreferFullBackupRestore = true
            };
        }

        return new TargetedRestoreAssessment
        {
            ConfigKey = key,
            Safety = TargetedRestoreSafety.SafeToTargetRestore,
            Message =
                "Metadata and managed ownership look consistent for a future targeted restore. " +
                "Phase 7 does not execute surgical restore — full backup remains the supported path.",
            LatestSnapshot = latest,
            PreferFullBackupRestore = true
        };
    }
}
