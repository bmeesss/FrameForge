using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.CS2.Settings;

/// <summary>
/// Reads/writes supported CS2 settings via the existing cfg + backup infrastructure.
/// Managed keys are written to frameforge_settings.cfg so unrelated user cfg is preserved.
/// </summary>
public sealed class Cs2SettingsService : ICs2SettingsService
{
    public const string ManagedFileName = "frameforge_settings.cfg";
    public const string BackupTag = "cs2.settings.apply";

    private readonly ICs2SettingCatalog _catalog;
    private readonly ICs2ConfigService _config;
    private readonly ICs2DetectionService _detection;
    private readonly IBackupService _backups;
    private readonly IAppSettingsService _appSettings;
    private readonly IAppLog _log;

    public Cs2SettingsService(
        ICs2SettingCatalog catalog,
        ICs2ConfigService config,
        ICs2DetectionService detection,
        IBackupService backups,
        IAppSettingsService appSettings,
        IAppLog log)
    {
        _catalog = catalog;
        _config = config;
        _detection = detection;
        _backups = backups;
        _appSettings = appSettings;
        _log = log;
    }

    public string ManagedConfigFileName => ManagedFileName;

    public async Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var install = await _detection.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
        {
            return new Cs2SettingsSnapshot
            {
                Cs2Available = false,
                Message = install.DetectionMessage,
                Settings = _catalog.GetAll().Select(d => new Cs2SettingValue
                {
                    Definition = d,
                    CurrentValue = d.DefaultValue,
                    RecommendedValue = d.RecommendedValue,
                    Source = Cs2SettingSource.Unavailable
                }).ToList()
            };
        }

        var cfgDir = install.CfgDirectory;
        var managedPath = Path.Combine(cfgDir, ManagedFileName);
        var merged = new Dictionary<string, (string Value, Cs2SettingSource Source)>(StringComparer.OrdinalIgnoreCase);

        // Priority (low → high): other cfg < autoexec < frameforge presets < managed settings
        await MergeCfgFolderAsync(cfgDir, merged, cancellationToken).ConfigureAwait(false);
        await MergeFileAsync(Path.Combine(cfgDir, "autoexec.cfg"), merged, Cs2SettingSource.Autoexec, cancellationToken)
            .ConfigureAwait(false);
        await MergeFileAsync(managedPath, merged, Cs2SettingSource.FrameForgeManaged, cancellationToken)
            .ConfigureAwait(false);

        var settings = new List<Cs2SettingValue>();
        foreach (var def in _catalog.GetAll())
        {
            string? current = def.DefaultValue;
            var source = Cs2SettingSource.Default;
            if (merged.TryGetValue(def.ConfigKey, out var hit))
            {
                current = hit.Value;
                source = hit.Source;
            }

            settings.Add(new Cs2SettingValue
            {
                Definition = def,
                CurrentValue = current,
                RecommendedValue = def.RecommendedValue,
                Source = source
            });
        }

        return new Cs2SettingsSnapshot
        {
            Cs2Available = true,
            CfgDirectory = cfgDir,
            ManagedConfigPath = managedPath,
            Message = $"Loaded {settings.Count} supported setting(s) from {cfgDir}",
            Settings = settings,
            LoadedAt = DateTimeOffset.UtcNow
        };
    }

    public SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var issues = new List<SettingsValidationIssue>();

        foreach (var (key, value) in values)
        {
            var def = _catalog.GetByConfigKey(key) ?? _catalog.GetById(key);
            if (def is null)
            {
                issues.Add(new SettingsValidationIssue
                {
                    SettingId = key,
                    ConfigKey = key,
                    Message = $"Unsupported or unknown setting '{key}'.",
                    IsError = true
                });
                continue;
            }

            if (!def.IsSupported)
            {
                issues.Add(new SettingsValidationIssue
                {
                    SettingId = def.Id,
                    ConfigKey = def.ConfigKey,
                    Message = $"Setting '{def.DisplayName}' is not supported for writes.",
                    IsError = true
                });
                continue;
            }

            var trimmed = value.Trim();
            if (!IsValidValue(def, trimmed, out var reason))
            {
                issues.Add(new SettingsValidationIssue
                {
                    SettingId = def.Id,
                    ConfigKey = def.ConfigKey,
                    Message = reason ?? "Invalid value.",
                    IsError = true
                });
            }
        }

        return new SettingsValidationResult { Issues = issues };
    }

    public SettingsDiff CreateDiff(
        Cs2SettingsSnapshot current,
        IReadOnlyDictionary<string, string> desired,
        string reason,
        string? profileId = null,
        string? profileName = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var currentMap = current.Settings.ToDictionary(
            s => s.Definition.ConfigKey,
            s => s,
            StringComparer.OrdinalIgnoreCase);

        var entries = new List<SettingsDiffEntry>();
        foreach (var (rawKey, rawValue) in desired)
        {
            var def = _catalog.GetByConfigKey(rawKey) ?? _catalog.GetById(rawKey);
            if (def is null || !def.IsSupported)
            {
                continue;
            }

            currentMap.TryGetValue(def.ConfigKey, out var existing);
            var currentValue = existing?.CurrentValue;
            var newValue = rawValue.Trim();

            if (string.Equals(
                    Normalize(def, currentValue),
                    Normalize(def, newValue),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new SettingsDiffEntry
            {
                SettingId = def.Id,
                ConfigKey = def.ConfigKey,
                DisplayName = def.DisplayName,
                Category = def.Category,
                CurrentValue = currentValue,
                NewValue = newValue,
                Reason = reason,
                Risk = def.RiskNote,
                RequiresRestart = def.RequiresRestart
            });
        }

        return new SettingsDiff
        {
            Title = profileName is null ? "Settings change" : $"Apply profile: {profileName}",
            ProfileId = profileId,
            ProfileName = profileName,
            Entries = entries
        };
    }

    public Task<SettingsApplyResult> ApplyDiffAsync(
        SettingsDiff diff,
        bool createBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var desired = diff.Entries
            .Where(e => e.IsChange && e.NewValue is not null)
            .ToDictionary(e => e.ConfigKey, e => e.NewValue!, StringComparer.OrdinalIgnoreCase);

        var reason = diff.ProfileName is null ? diff.Title : $"Profile:{diff.ProfileName}";
        return ApplySettingsAsync(desired, reason, diff.ProfileId, createBackup, cancellationToken);
    }

    public async Task<SettingsApplyResult> ApplySettingsAsync(
        IReadOnlyDictionary<string, string> desired,
        string reason,
        string? profileId = null,
        bool createBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desired);

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in desired)
        {
            var def = _catalog.GetByConfigKey(k) ?? _catalog.GetById(k);
            if (def is null || !def.IsSupported)
            {
                continue;
            }

            normalized[def.ConfigKey] = v.Trim();
        }

        var validation = ValidateSettings(normalized);
        if (!validation.IsValid)
        {
            var msg = string.Join("; ", validation.Issues.Where(i => i.IsError).Select(i => i.Message));
            return SettingsApplyResult.Fail($"Validation failed: {msg}");
        }

        var snapshot = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Cs2Available || string.IsNullOrWhiteSpace(snapshot.CfgDirectory))
        {
            return SettingsApplyResult.Fail(
                string.IsNullOrWhiteSpace(snapshot.Message)
                    ? "CS2 configuration directory is not available."
                    : snapshot.Message);
        }

        var diff = CreateDiff(snapshot, normalized, reason, profileId);
        if (!diff.HasChanges)
        {
            return SettingsApplyResult.Ok("No changes to apply.", null, diff, Array.Empty<string>());
        }

        Directory.CreateDirectory(snapshot.CfgDirectory);
        var managedPath = snapshot.ManagedConfigPath ?? Path.Combine(snapshot.CfgDirectory, ManagedFileName);

        string? backupId = null;
        var appSettings = await _appSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var shouldBackup = createBackup && appSettings.AutomaticBackup;

        if (shouldBackup)
        {
            var previous = diff.Entries.ToDictionary(
                e => e.ConfigKey,
                e => e.CurrentValue,
                StringComparer.OrdinalIgnoreCase);

            var files = new List<string>();
            if (File.Exists(managedPath))
            {
                files.Add(managedPath);
            }

            try
            {
                var entry = await _backups.CreateBackupAsync(
                    description: $"CS2 settings apply: {reason}",
                    optimizationIds: new[] { BackupTag },
                    affectedFiles: files,
                    previousValues: previous,
                    profileId: profileId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                backupId = entry.Id;

                appSettings.LastSettingsBackupId = backupId;
                await _appSettings.SaveAsync(appSettings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError("Settings backup failed; aborting apply.", ex);
                return SettingsApplyResult.Fail($"Backup failed; apply aborted: {ex.Message}", diff: diff);
            }
        }

        try
        {
            var existingManaged = await _config.ReadAsync(managedPath, cancellationToken).ConfigureAwait(false);
            var writeMap = existingManaged.ToDictionary();
            foreach (var (key, value) in normalized)
            {
                writeMap[key] = value;
            }

            var supportedOnly = writeMap
                .Where(kv => _catalog.GetByConfigKey(kv.Key) is { IsSupported: true })
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            await _config.WriteValuesAsync(managedPath, supportedOnly, cancellationToken).ConfigureAwait(false);

            var verifyDoc = await _config.ReadAsync(managedPath, cancellationToken).ConfigureAwait(false);
            var verifyMap = verifyDoc.ToDictionary();
            foreach (var entry in diff.Entries.Where(e => e.IsChange))
            {
                var def = _catalog.GetByConfigKey(entry.ConfigKey)!;
                if (!verifyMap.TryGetValue(entry.ConfigKey, out var actual) ||
                    !string.Equals(Normalize(def, actual), Normalize(def, entry.NewValue), StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError($"Verify failed for {entry.ConfigKey}.");
                    if (backupId is not null)
                    {
                        var restore = await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                        return SettingsApplyResult.Fail(
                            $"Verification failed for '{entry.DisplayName}'. Rolled back.",
                            backupId,
                            rolledBack: restore.Success,
                            diff: diff);
                    }

                    return SettingsApplyResult.Fail(
                        $"Verification failed for '{entry.DisplayName}'.",
                        diff: diff);
                }
            }

            _log.LogInformation($"Applied {diff.ChangeCount} CS2 setting change(s). Backup={backupId ?? "none"}");
            return SettingsApplyResult.Ok(
                $"Applied {diff.ChangeCount} change(s) to {ManagedFileName}.",
                backupId,
                diff,
                new[] { managedPath });
        }
        catch (Exception ex)
        {
            _log.LogError("Settings apply failed.", ex);
            var rolledBack = false;
            if (backupId is not null)
            {
                try
                {
                    var restore = await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                    rolledBack = restore.Success;
                }
                catch (Exception rex)
                {
                    _log.LogError("Rollback after settings failure also failed.", rex);
                }
            }

            return SettingsApplyResult.Fail(ex.Message, backupId, rolledBack, diff);
        }
    }

    public async Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default)
    {
        var app = await _appSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var lastId = app.LastSettingsBackupId;

        if (string.IsNullOrWhiteSpace(lastId))
        {
            var all = await _backups.ListBackupsAsync(cancellationToken).ConfigureAwait(false);
            var match = all.FirstOrDefault(b =>
                b.OptimizationIds.Any(id => id.Equals(BackupTag, StringComparison.OrdinalIgnoreCase)));
            if (match is null)
            {
                return SettingsApplyResult.Fail("No FrameForge settings backup was found to restore.");
            }

            lastId = match.Id;
        }

        var result = await _backups.RestoreAsync(lastId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return SettingsApplyResult.Fail(result.Message, lastId);
        }

        _log.LogInformation($"Restored FrameForge settings backup {lastId}.");
        return SettingsApplyResult.Ok(
            result.Message,
            lastId,
            new SettingsDiff { Title = "Restore FrameForge changes" },
            result.RestoredFiles);
    }

    private async Task MergeCfgFolderAsync(
        string cfgDir,
        Dictionary<string, (string Value, Cs2SettingSource Source)> merged,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cfgDir))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(cfgDir, "*.cfg");
        }
        catch
        {
            return;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(ManagedFileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("autoexec.cfg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = name.StartsWith("frameforge_", StringComparison.OrdinalIgnoreCase)
                ? Cs2SettingSource.FrameForgeManaged
                : Cs2SettingSource.UserConfig;

            await MergeFileAsync(file, merged, source, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MergeFileAsync(
        string path,
        Dictionary<string, (string Value, Cs2SettingSource Source)> merged,
        Cs2SettingSource source,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var doc = await _config.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (var (key, value) in doc.ToDictionary())
            {
                if (_catalog.GetByConfigKey(key) is null)
                {
                    continue;
                }

                merged[key] = (value, source);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not read '{path}': {ex.Message}");
        }
    }

    public static bool IsValidValue(Cs2SettingDefinition def, string value, out string? reason)
    {
        reason = null;
        if (def.AllowedValues is { Count: > 0 })
        {
            if (def.AllowedValues.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            reason = $"Value '{value}' is not in allowed set [{string.Join(", ", def.AllowedValues)}].";
            return false;
        }

        switch (def.ValueKind)
        {
            case Cs2SettingValueKind.Boolean:
                if (value is "0" or "1" or "true" or "false" or "True" or "False")
                {
                    return true;
                }

                reason = "Boolean value must be 0/1 or true/false.";
                return false;

            case Cs2SettingValueKind.Integer:
                if (!long.TryParse(value, out var i))
                {
                    reason = "Expected an integer.";
                    return false;
                }

                if (def.MinValue is not null && i < def.MinValue)
                {
                    reason = $"Minimum is {def.MinValue}.";
                    return false;
                }

                if (def.MaxValue is not null && i > def.MaxValue)
                {
                    reason = $"Maximum is {def.MaxValue}.";
                    return false;
                }

                return true;

            case Cs2SettingValueKind.Decimal:
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    reason = "Expected a decimal number.";
                    return false;
                }

                if (def.MinValue is not null && d < def.MinValue)
                {
                    reason = $"Minimum is {def.MinValue}.";
                    return false;
                }

                if (def.MaxValue is not null && d > def.MaxValue)
                {
                    reason = $"Maximum is {def.MaxValue}.";
                    return false;
                }

                return true;

            default:
                if (value.Length > 256)
                {
                    reason = "Value is too long.";
                    return false;
                }

                return true;
        }
    }

    private static string? Normalize(Cs2SettingDefinition def, string? value)
    {
        if (value is null)
        {
            return null;
        }

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
}
