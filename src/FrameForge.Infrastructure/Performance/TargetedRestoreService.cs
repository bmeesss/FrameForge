using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2.Settings;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Safe surgical restore of individual FrameForge-managed settings.
/// Never silently falls back to full backup restore. Refuses when unsafe.
/// </summary>
public sealed class TargetedRestoreService : ITargetedRestoreService
{
    public const string BackupTag = "cs2.settings.targeted-restore";

    private readonly ISettingChangeSnapshotStore _snapshots;
    private readonly IBackupService _backups;
    private readonly ICs2SettingsService _settings;
    private readonly ICs2ConfigService _config;
    private readonly ICs2SettingCatalog _catalog;
    private readonly IAppLog _log;
    private readonly IManagedConfigWatcher? _watcher;

    public TargetedRestoreService(
        ISettingChangeSnapshotStore snapshots,
        IBackupService backups,
        ICs2SettingsService settings,
        ICs2ConfigService config,
        ICs2SettingCatalog catalog,
        IAppLog log,
        IManagedConfigWatcher? watcher = null)
    {
        _snapshots = snapshots;
        _backups = backups;
        _settings = settings;
        _config = config;
        _catalog = catalog;
        _log = log;
        _watcher = watcher;
    }

    public Task<TargetedRestoreAssessment> AssessAsync(
        string settingIdOrKey,
        CancellationToken cancellationToken = default) =>
        AssessCoreAsync(settingIdOrKey, cancellationToken);

    public async Task<TargetedRestoreResult> RestoreAsync(
        string settingIdOrKey,
        CancellationToken cancellationToken = default)
    {
        var assessment = await AssessCoreAsync(settingIdOrKey, cancellationToken).ConfigureAwait(false);
        if (assessment.Safety != TargetedRestoreSafety.SafeToTargetRestore)
        {
            return TargetedRestoreResult.Refuse(assessment);
        }

        return await ExecuteRestoreAsync(assessment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TargetedRestoreBatchResult> RestoreSetAsync(
        IEnumerable<string> settingIdsOrKeys,
        CancellationToken cancellationToken = default)
    {
        var batch = new TargetedRestoreBatchResult();
        var keys = settingIdsOrKeys
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Results.Add(await RestoreAsync(key, cancellationToken).ConfigureAwait(false));
        }

        return batch;
    }

    private async Task<TargetedRestoreAssessment> AssessCoreAsync(
        string settingIdOrKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settingIdOrKey))
        {
            return Base(string.Empty, TargetedRestoreSafety.NotTracked, "Setting id is required.");
        }

        var def = _catalog.GetByConfigKey(settingIdOrKey) ?? _catalog.GetById(settingIdOrKey);
        if (def is null || !def.IsSupported)
        {
            return Base(settingIdOrKey, TargetedRestoreSafety.NotTracked,
                $"Setting '{settingIdOrKey}' is unknown or unsupported.");
        }

        var key = def.ConfigKey;

        // Most recent valid snapshot (chronological)
        var all = (await _snapshots.ListForKeyAsync(key, cancellationToken).ConfigureAwait(false))
            .Where(IsSnapshotInternallyConsistent)
            .OrderByDescending(s => s.Timestamp)
            .ToList();

        if (all.Count == 0)
        {
            // Try raw latest even if incomplete → InsufficientEvidence
            var raw = await _snapshots.GetLatestForKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return Base(key, TargetedRestoreSafety.NotTracked,
                    "No per-key FrameForge change snapshot exists for this setting.",
                    settingId: def.Id, name: def.DisplayName);
            }

            return Base(key, TargetedRestoreSafety.InsufficientEvidence,
                "Snapshot metadata is incomplete or corrupted. Not enough information to safely restore.",
                snapshot: raw, settingId: def.Id, name: def.DisplayName,
                restore: raw.PreviousValue, applied: raw.NewValue, file: raw.File);
        }

        var latest = all[0];

        if (string.IsNullOrWhiteSpace(latest.PreviousValue))
        {
            return Base(key, TargetedRestoreSafety.InsufficientEvidence,
                "Latest snapshot has no previous value to restore.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                applied: latest.NewValue, file: latest.File);
        }

        if (string.IsNullOrWhiteSpace(latest.NewValue))
        {
            return Base(key, TargetedRestoreSafety.InsufficientEvidence,
                "Latest snapshot has no recorded applied (new) value — cannot verify ownership.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, file: latest.File);
        }

        if (string.IsNullOrWhiteSpace(latest.File) || !File.Exists(latest.File))
        {
            return Base(key, TargetedRestoreSafety.InsufficientEvidence,
                "Affected managed file is missing or was not recorded on the snapshot.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, applied: latest.NewValue, file: latest.File);
        }

        var liveSnap = await _settings.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!liveSnap.Cs2Available || string.IsNullOrWhiteSpace(liveSnap.ManagedConfigPath))
        {
            return Base(key, TargetedRestoreSafety.UnsafeToTargetRestore,
                "CS2 cfg directory unavailable — cannot verify current managed state.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, applied: latest.NewValue, file: latest.File);
        }

        if (!string.Equals(latest.File, liveSnap.ManagedConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            return Base(key, TargetedRestoreSafety.UnsafeToTargetRestore,
                "Managed cfg path changed since the snapshot. Prefer full backup restore.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, applied: latest.NewValue, file: latest.File,
                current: null, affected: liveSnap.ManagedConfigPath);
        }

        // Read value from managed file only (not autoexec / other cfg)
        string? managedFileValue = null;
        try
        {
            var doc = await _config.ReadAsync(latest.File, cancellationToken).ConfigureAwait(false);
            var map = doc.ToDictionary();
            map.TryGetValue(key, out managedFileValue);
        }
        catch (Exception ex)
        {
            return Base(key, TargetedRestoreSafety.InsufficientEvidence,
                $"Could not read managed file for verification: {ex.Message}",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, applied: latest.NewValue, file: latest.File);
        }

        var live = liveSnap.Settings.FirstOrDefault(s =>
            s.Definition.ConfigKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        var currentValue = managedFileValue ?? live?.CurrentValue;

        // Mandatory: current must match last FrameForge-applied value
        if (!ValuesEqual(def, currentValue, latest.NewValue))
        {
            // Already at previous? still refuse if not equal to applied — user may have changed away
            return Base(key, TargetedRestoreSafety.UnsafeToTargetRestore,
                "Current value differs from the last FrameForge-managed value. " +
                "Targeted restore will not overwrite a user or external change.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, applied: latest.NewValue, file: latest.File,
                current: currentValue, affected: latest.File);
        }

        // Source should be FrameForge managed when key is present in managed file
        if (live is not null &&
            live.Source is not Cs2SettingSource.FrameForgeManaged and not Cs2SettingSource.Default &&
            managedFileValue is null)
        {
            return Base(key, TargetedRestoreSafety.UnsafeToTargetRestore,
                $"Current value source is '{live.Source}' (not FrameForge-managed). Prefer full backup restore.",
                snapshot: latest, settingId: def.Id, name: def.DisplayName,
                restore: latest.PreviousValue, applied: latest.NewValue, file: latest.File,
                current: currentValue, affected: latest.File);
        }

        // Linked backup is recommended but not strictly required if managed file + values check out
        if (!string.IsNullOrWhiteSpace(latest.BackupId))
        {
            var backup = await _backups.GetBackupAsync(latest.BackupId, cancellationToken).ConfigureAwait(false);
            if (backup is null)
            {
                // Still allow if file+value checks pass — note in message
                _log.LogWarning($"Targeted restore assessment: linked backup '{latest.BackupId}' missing for {key}.");
            }
        }

        return new TargetedRestoreAssessment
        {
            ConfigKey = key,
            SettingId = def.Id,
            SettingName = def.DisplayName,
            Safety = TargetedRestoreSafety.SafeToTargetRestore,
            Message =
                $"Safe: restore '{def.DisplayName}' from '{latest.NewValue}' back to previous FrameForge-managed value '{latest.PreviousValue}'.",
            LatestSnapshot = latest,
            CurrentValue = currentValue,
            RestoreValue = latest.PreviousValue,
            AppliedValue = latest.NewValue,
            AffectedFile = latest.File,
            PreferFullBackupRestore = false
        };
    }

    private async Task<TargetedRestoreResult> ExecuteRestoreAsync(
        TargetedRestoreAssessment assessment,
        CancellationToken cancellationToken)
    {
        var key = assessment.ConfigKey;
        var def = _catalog.GetByConfigKey(key) ?? _catalog.GetById(assessment.SettingId ?? key);
        if (def is null || assessment.LatestSnapshot is null || assessment.RestoreValue is null)
        {
            return TargetedRestoreResult.Fail(key, "Internal assessment incomplete.", assessment: assessment);
        }

        var file = assessment.AffectedFile ?? assessment.LatestSnapshot.File;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            return TargetedRestoreResult.Fail(key, "Managed file disappeared before restore.", assessment: assessment);
        }

        // Re-verify current state immediately before write
        var reassess = await AssessCoreAsync(key, cancellationToken).ConfigureAwait(false);
        if (reassess.Safety != TargetedRestoreSafety.SafeToTargetRestore)
        {
            return TargetedRestoreResult.Refuse(reassess);
        }

        // Refuse if managed files changed externally since last FrameForge baseline
        if (_watcher is not null)
        {
            try
            {
                if (await _watcher.HasExternalChangesAsync(cancellationToken).ConfigureAwait(false))
                {
                    return TargetedRestoreResult.Refuse(new TargetedRestoreAssessment
                    {
                        ConfigKey = key,
                        SettingId = assessment.SettingId,
                        SettingName = assessment.SettingName,
                        Safety = TargetedRestoreSafety.UnsafeToTargetRestore,
                        Message =
                            "Managed cfg changed externally since the last FrameForge baseline. " +
                            "Re-assess after reviewing the file; targeted restore refused.",
                        LatestSnapshot = assessment.LatestSnapshot,
                        CurrentValue = assessment.CurrentValue,
                        RestoreValue = assessment.RestoreValue,
                        AppliedValue = assessment.AppliedValue,
                        AffectedFile = assessment.AffectedFile,
                        PreferFullBackupRestore = true
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"External change check failed (continuing with re-assess only): {ex.Message}");
            }
        }

        // Backup current managed file state first — fail stops
        string? backupId = null;
        try
        {
            var entry = await _backups.CreateBackupAsync(
                description: $"Targeted restore: {key} → {assessment.RestoreValue}",
                optimizationIds: new[] { BackupTag },
                affectedFiles: new[] { file },
                previousValues: new Dictionary<string, string?>
                {
                    [key] = assessment.CurrentValue
                },
                profileId: null,
                settingChangeSnapshots: new[]
                {
                    new SettingChangeSnapshot
                    {
                        SettingId = def.Id,
                        ConfigKey = key,
                        PreviousValue = assessment.CurrentValue,
                        NewValue = assessment.RestoreValue,
                        File = file,
                        Timestamp = DateTimeOffset.UtcNow,
                        Reason = "targeted-restore"
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            backupId = entry.Id;

            await _snapshots.AppendAsync(entry.SettingChangeSnapshots, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError("Targeted restore aborted: backup failed.", ex);
            return TargetedRestoreResult.Fail(
                key,
                $"Backup failed; targeted restore aborted: {ex.Message}",
                assessment: assessment);
        }

        try
        {
            // Write only the tracked key into managed cfg — merge existing
            var existing = await _config.ReadAsync(file, cancellationToken).ConfigureAwait(false);
            var map = existing.ToDictionary();
            var beforeOther = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);

            map[key] = assessment.RestoreValue;
            var supportedOnly = map
                .Where(kv => _catalog.GetByConfigKey(kv.Key) is { IsSupported: true })
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            await _config.WriteValuesAsync(file, supportedOnly, cancellationToken).ConfigureAwait(false);

            // Verify restored key
            var verifyDoc = await _config.ReadAsync(file, cancellationToken).ConfigureAwait(false);
            var verifyMap = verifyDoc.ToDictionary();
            if (!verifyMap.TryGetValue(key, out var actual) ||
                !ValuesEqual(def, actual, assessment.RestoreValue))
            {
                _log.LogError($"Targeted restore verify failed for {key}.");
                var rolled = false;
                if (backupId is not null)
                {
                    var restore = await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                    rolled = restore.Success;
                }

                return TargetedRestoreResult.Fail(
                    key,
                    $"Verification failed after targeted restore for '{key}'.",
                    backupId,
                    rolledBack: rolled,
                    assessment: assessment);
            }

            // Unrelated keys in managed file must still match (preservation)
            foreach (var (ok, ov) in beforeOther)
            {
                if (ok.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!verifyMap.TryGetValue(ok, out var nv) ||
                    !string.Equals(ov, nv, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError($"Targeted restore altered unrelated key '{ok}'.");
                    var rolled = false;
                    if (backupId is not null)
                    {
                        var restore = await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                        rolled = restore.Success;
                    }

                    return TargetedRestoreResult.Fail(
                        key,
                        $"Safety check failed: unrelated setting '{ok}' changed. Rolled back={rolled}.",
                        backupId,
                        rolledBack: rolled,
                        assessment: assessment);
                }
            }

            _log.LogInformation(
                $"Targeted restore OK: {key} '{assessment.CurrentValue}' → '{assessment.RestoreValue}' (backup {backupId}).");

            if (_watcher is not null)
            {
                try { await _watcher.CaptureBaselineAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug($"Baseline capture after restore: {ex.Message}"); }
            }

            return TargetedRestoreResult.Ok(
                key,
                $"Restored '{def.DisplayName}' to previous FrameForge-managed value '{assessment.RestoreValue}'.",
                backupId,
                assessment.RestoreValue,
                file,
                assessment);
        }
        catch (Exception ex)
        {
            _log.LogError("Targeted restore failed.", ex);
            var rolled = false;
            if (backupId is not null)
            {
                try
                {
                    var restore = await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                    rolled = restore.Success;
                }
                catch (Exception rex)
                {
                    _log.LogError("Recovery backup restore also failed.", rex);
                }
            }

            return TargetedRestoreResult.Fail(key, ex.Message, backupId, rolled, assessment);
        }
    }

    private static bool IsSnapshotInternallyConsistent(SettingChangeSnapshot s)
    {
        if (s is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(s.ConfigKey) && string.IsNullOrWhiteSpace(s.SettingId))
        {
            return false;
        }

        // Need at least previous + new to prove chain
        if (string.IsNullOrWhiteSpace(s.PreviousValue) || string.IsNullOrWhiteSpace(s.NewValue))
        {
            return false;
        }

        if (s.Timestamp == default)
        {
            return false;
        }

        return true;
    }

    private static bool ValuesEqual(Cs2SettingDefinition def, string? a, string? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        var na = Normalize(def, a);
        var nb = Normalize(def, b);
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(Cs2SettingDefinition def, string value)
    {
        var v = value.Trim();
        if (def.ValueKind == Cs2SettingValueKind.Boolean)
        {
            if (v is "1" or "true" or "True") return "true";
            if (v is "0" or "false" or "False") return "false";
        }

        if (def.ValueKind is Cs2SettingValueKind.Decimal or Cs2SettingValueKind.Integer &&
            double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return v;
    }

    private static TargetedRestoreAssessment Base(
        string key,
        TargetedRestoreSafety safety,
        string message,
        SettingChangeSnapshot? snapshot = null,
        string? settingId = null,
        string? name = null,
        string? restore = null,
        string? applied = null,
        string? file = null,
        string? current = null,
        string? affected = null) => new()
    {
        ConfigKey = key,
        SettingId = settingId,
        SettingName = name,
        Safety = safety,
        Message = message,
        LatestSnapshot = snapshot,
        RestoreValue = restore ?? snapshot?.PreviousValue,
        AppliedValue = applied ?? snapshot?.NewValue,
        CurrentValue = current,
        AffectedFile = affected ?? file ?? snapshot?.File,
        PreferFullBackupRestore = safety != TargetedRestoreSafety.SafeToTargetRestore
    };
}
