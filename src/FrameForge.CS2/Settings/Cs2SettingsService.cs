using System.Text;
using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Models;

namespace FrameForge.CS2.Settings;

/// <summary>
/// Reads/writes supported CS2 settings into the real CS2 cfg directory.
/// Values live in frameforge_settings.cfg; autoexec.cfg gets a marked
/// FRAMEFORGE section that execs that file so CS2 actually loads them.
/// User autoexec content outside the markers is never destroyed.
/// </summary>
public sealed class Cs2SettingsService : ICs2SettingsService
{
    public const string ManagedFileName = Cs2AutoexecIntegration.ManagedFileName;
    public const string BackupTag = "cs2.settings.apply";

    private readonly ICs2SettingCatalog _catalog;
    private readonly ICs2ConfigService _config;
    private readonly ICs2DetectionService _detection;
    private readonly IBackupService _backups;
    private readonly IAppSettingsService _appSettings;
    private readonly IAppLog _log;
    private readonly ISettingChangeSnapshotStore? _changeSnapshots;
    private readonly IManagedConfigBaselineSink? _baselineSink;
    private readonly IRecoverySnapshotFactory _recoveryFactory;

    public Cs2SettingsService(
        ICs2SettingCatalog catalog,
        ICs2ConfigService config,
        ICs2DetectionService detection,
        IBackupService backups,
        IAppSettingsService appSettings,
        IAppLog log,
        ISettingChangeSnapshotStore? changeSnapshots = null,
        IManagedConfigBaselineSink? baselineSink = null,
        IRecoverySnapshotFactory? recoveryFactory = null)
    {
        _catalog = catalog;
        _config = config;
        _detection = detection;
        _backups = backups;
        _appSettings = appSettings;
        _log = log;
        _changeSnapshots = changeSnapshots;
        _baselineSink = baselineSink;
        _recoveryFactory = recoveryFactory ?? new RecoverySnapshotFactory();
    }

    /// <summary>
    /// Acquires the shared gate for the managed configuration scope (one CS2 cfg directory).
    ///
    /// Every read/apply/restore of that scope serializes on the same gate, so writes never
    /// interleave and reads never observe a half-written managed cfg or autoexec.
    /// Waiting is cancellation aware and the gate is released by <c>await using</c>.
    /// </summary>
    private async Task<ConfigScope> AcquireConfigScopeAsync(CancellationToken cancellationToken)
    {
        var install = await _detection.DetectAsync(cancellationToken).ConfigureAwait(false);
        var cfgDirectory = install?.CfgDirectory;

        if (install is null || !install.IsInstalled || string.IsNullOrWhiteSpace(cfgDirectory))
        {
            // Nothing managed to protect: operations will report CS2 as unavailable.
            return new ConfigScope(install, null, null);
        }

        var lease = await ManagedConfigGate.AcquireAsync(cfgDirectory, cancellationToken).ConfigureAwait(false);
        return new ConfigScope(install, cfgDirectory, lease);
    }

    public string ManagedConfigFileName => ManagedFileName;
    public string AutoexecFileName => Cs2AutoexecIntegration.AutoexecFileName;

    public async Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = await AcquireConfigScopeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSettingsCoreAsync(scope.Install, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = await AcquireConfigScopeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSettingsCoreAsync(scope.Install, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Body of a read operation. Callers must already hold the configuration scope gate.
    /// </summary>
    private async Task<Cs2SettingsSnapshot> ReadSettingsCoreAsync(
        Cs2InstallInfo? install,
        CancellationToken cancellationToken)
    {
        if (install is null || !install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
        {
            return new Cs2SettingsSnapshot
            {
                Cs2Available = false,
                Message = install?.DetectionMessage ?? "CS2 was not detected.",
                ExecutionStatus = Cs2ConfigExecutionStatus.Unavailable,
                ExecutionRecommendation =
                    "CS2 was not detected. Configure a custom Steam/CS2 path in Settings, or install CS2 via Steam.",
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
        var autoexecPath = Path.Combine(cfgDir, Cs2AutoexecIntegration.AutoexecFileName);
        var managedExists = File.Exists(managedPath);
        var autoexecContent = File.Exists(autoexecPath)
            ? await File.ReadAllTextAsync(autoexecPath, cancellationToken).ConfigureAwait(false)
            : null;
        var autoexecExecutes = Cs2AutoexecIntegration.SectionExecutesManagedFile(autoexecContent);

        var executionStatus = ResolveExecutionStatus(managedExists, autoexecExecutes);
        var recommendation = BuildExecutionRecommendation(executionStatus, managedPath, autoexecPath);

        var merged = new Dictionary<string, (string Value, Cs2SettingSource Source)>(StringComparer.OrdinalIgnoreCase);

        // Priority (low → high): other cfg < autoexec (user lines) < managed settings
        await MergeCfgFolderAsync(cfgDir, merged, cancellationToken).ConfigureAwait(false);
        await MergeFileAsync(autoexecPath, merged, Cs2SettingSource.Autoexec, cancellationToken)
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

        var message = new StringBuilder();
        message.Append($"Loaded {settings.Count} supported setting(s) from {cfgDir}. ");
        message.Append(executionStatus switch
        {
            Cs2ConfigExecutionStatus.Wired =>
                "FrameForge config is wired: autoexec execs frameforge_settings.cfg.",
            Cs2ConfigExecutionStatus.ManagedFilePresentButNotExecuted =>
                "frameforge_settings.cfg exists but is not executed by CS2 yet.",
            _ => "FrameForge config is not installed in this cfg directory yet."
        });

        return new Cs2SettingsSnapshot
        {
            Cs2Available = true,
            CfgDirectory = cfgDir,
            ManagedConfigPath = managedPath,
            AutoexecPath = autoexecPath,
            ManagedConfigExists = managedExists,
            AutoexecExecutesManagedConfig = autoexecExecutes,
            ExecutionStatus = executionStatus,
            ExecutionRecommendation = recommendation,
            AffectedFilesOnApply = new[] { managedPath, autoexecPath },
            Message = message.ToString(),
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

        var needsAutoexec = current.Cs2Available && !current.AutoexecExecutesManagedConfig;
        var needsManagedCreate = current.Cs2Available && !current.ManagedConfigExists && entries.Count > 0;
        var affected = new List<string>();
        if (current.ManagedConfigPath is not null && (entries.Count > 0 || needsManagedCreate || needsAutoexec))
        {
            affected.Add(current.ManagedConfigPath);
        }

        if (current.AutoexecPath is not null && (needsAutoexec || entries.Count > 0))
        {
            // Always touch autoexec on a real apply so the exec hook is present after settings change.
            if (needsAutoexec || entries.Count > 0)
            {
                if (!affected.Contains(current.AutoexecPath, StringComparer.OrdinalIgnoreCase))
                {
                    affected.Add(current.AutoexecPath);
                }
            }
        }

        // Integration-only change still counts as a change the user must confirm
        if (needsAutoexec && entries.Count == 0)
        {
            entries.Add(new SettingsDiffEntry
            {
                SettingId = "integration.autoexec",
                ConfigKey = "autoexec.cfg",
                DisplayName = "Autoexec FrameForge hook",
                Category = Cs2SettingCategory.Game,
                CurrentValue = "missing FRAMEFORGE section",
                NewValue = "add // FRAMEFORGE BEGIN … exec frameforge_settings.cfg … END",
                Reason = "Wire managed cfg so CS2 executes it on launch",
                Risk = "Low — only inserts a marked section; user lines outside markers are kept",
                RequiresRestart = true
            });
        }

        return new SettingsDiff
        {
            Title = profileName is null ? "Settings change" : $"Apply profile: {profileName}",
            ProfileId = profileId,
            ProfileName = profileName,
            Entries = entries,
            AutoexecIntegrationChange = needsAutoexec,
            AffectedFiles = affected
        };
    }

    public async Task<SettingsApplyResult> ApplyDiffAsync(
        SettingsDiff diff,
        bool createBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var desired = diff.Entries
            .Where(e => e.IsChange && e.NewValue is not null &&
                        !e.SettingId.Equals("integration.autoexec", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.ConfigKey, e => e.NewValue!, StringComparer.OrdinalIgnoreCase);

        var reason = diff.ProfileName is null ? diff.Title : $"Profile:{diff.ProfileName}";

        await using var scope = await AcquireConfigScopeAsync(cancellationToken).ConfigureAwait(false);
        return await ApplySettingsCoreAsync(scope, desired, reason, diff.ProfileId, createBackup, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SettingsApplyResult> ApplySettingsAsync(
        IReadOnlyDictionary<string, string> desired,
        string reason,
        string? profileId = null,
        bool createBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desired);

        await using var scope = await AcquireConfigScopeAsync(cancellationToken).ConfigureAwait(false);
        return await ApplySettingsCoreAsync(scope, desired, reason, profileId, createBackup, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Body of an apply operation. Callers must already hold the configuration scope gate.
    /// The gate is never reentrant: this method (and everything it calls) must use the
    /// *CoreAsync variants rather than the public entry points.
    /// </summary>
    private async Task<SettingsApplyResult> ApplySettingsCoreAsync(
        ConfigScope scope,
        IReadOnlyDictionary<string, string> desired,
        string reason,
        string? profileId,
        bool createBackup,
        CancellationToken cancellationToken)
    {
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

        var snapshot = await ReadSettingsCoreAsync(scope.Install, cancellationToken).ConfigureAwait(false);
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
            return SettingsApplyResult.Ok(
                "No changes to apply.",
                null,
                diff,
                Array.Empty<string>(),
                autoexecUpdated: false,
                executedByCs2: snapshot.AutoexecExecutesManagedConfig);
        }

        Directory.CreateDirectory(snapshot.CfgDirectory);
        var managedPath = snapshot.ManagedConfigPath ?? Path.Combine(snapshot.CfgDirectory, ManagedFileName);
        var autoexecPath = snapshot.AutoexecPath ?? Path.Combine(snapshot.CfgDirectory, AutoexecFileName);

        // --- Pre-flight: autoexec.cfg marker structure must be unambiguous -------------------
        // Ambiguous files (missing half, duplicates, nesting, reversed order) are never repaired
        // silently: the apply is refused before anything is written.
        var previousAutoexec = File.Exists(autoexecPath)
            ? await File.ReadAllTextAsync(autoexecPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var markerInspection = Cs2AutoexecIntegration.InspectMarkers(previousAutoexec);
        if (markerInspection.IsAmbiguous)
        {
            _log.LogError($"Refusing to modify autoexec.cfg: {markerInspection.Message}");
            return SettingsApplyResult.Fail(
                $"autoexec.cfg FRAMEFORGE markers are ambiguous — nothing was written. " +
                $"{markerInspection.Message} {markerInspection.UserAction}",
                diff: diff);
        }

        // Always include both real CS2 files that may change
        var filesToBackup = new List<string> { managedPath, autoexecPath };

        // --- Internal transaction recovery snapshot (always, regardless of backup settings) ---
        // This is separate from the optional user-visible backup: it is the rollback path when
        // AutomaticBackup is disabled or no backup could be created.
        RecoverySnapshot? recovery = null;
        try
        {
            recovery = _recoveryFactory.Create(filesToBackup);
            if (!recovery.IsComplete)
            {
                var uncaptured = string.Join(", ",
                    recovery.Entries.Where(e => !e.Captured).Select(e => Path.GetFileName(e.Path)));
                recovery.Dispose();
                return SettingsApplyResult.Fail(
                    $"Apply aborted before any change: the internal recovery snapshot could not capture {uncaptured}. " +
                    "No files were modified.",
                    diff: diff);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("Settings recovery snapshot failed; aborting apply.", ex);
            recovery?.Dispose();
            return SettingsApplyResult.Fail(
                $"Apply aborted before any change: the internal recovery snapshot failed ({ex.Message}). " +
                "No files were modified.",
                diff: diff);
        }

        // Anything failing between the recovery snapshot and the write block must not leak
        // the temporary snapshot: dispose it and rethrow.
        string? backupId = null;
        try
        {
            var appSettings = await _appSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
            var shouldBackup = createBackup && appSettings.AutomaticBackup;

            var previous = diff.Entries
                .Where(e => !e.SettingId.Equals("integration.autoexec", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.ConfigKey, e => e.CurrentValue, StringComparer.OrdinalIgnoreCase);

            // Always prepare per-key snapshots for every successful apply path (Phase 10).
            var keySnaps = diff.Entries
                .Where(e => e.IsChange &&
                            !e.SettingId.Equals("integration.autoexec", StringComparison.OrdinalIgnoreCase))
                .Select(e => new SettingChangeSnapshot
                {
                    SettingId = e.SettingId,
                    ConfigKey = e.ConfigKey,
                    PreviousValue = e.CurrentValue,
                    NewValue = e.NewValue,
                    File = managedPath,
                    Timestamp = DateTimeOffset.UtcNow,
                    ProfileId = profileId,
                    Reason = reason
                })
                .ToList();

            if (shouldBackup)
            {
                try
                {
                    var entry = await _backups.CreateBackupAsync(
                        description: $"CS2 settings apply: {reason}",
                        optimizationIds: new[] { BackupTag },
                        affectedFiles: filesToBackup,
                        previousValues: previous,
                        profileId: profileId,
                        settingChangeSnapshots: keySnaps,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    backupId = entry.Id;

                    foreach (var s in keySnaps)
                    {
                        s.BackupId = backupId;
                    }

                    appSettings.LastSettingsBackupId = backupId;
                    await _appSettings.SaveAsync(appSettings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError("Settings backup failed; aborting apply.", ex);
                    recovery?.Dispose();
                    return SettingsApplyResult.Fail($"Backup failed; apply aborted: {ex.Message}", diff: diff);
                }
            }
        }
        catch
        {
            recovery?.Dispose();
            throw;
        }

        var written = new List<string>();
        var autoexecUpdated = false;

        _baselineSink?.BeginSelfWrite();
        try
        {
            // 1) Write managed cfg with merged supported keys
            var existingManaged = await _config.ReadAsync(managedPath, cancellationToken).ConfigureAwait(false);
            var writeMap = existingManaged.ToDictionary();
            foreach (var (key, value) in normalized)
            {
                writeMap[key] = value;
            }

            var supportedOnly = writeMap
                .Where(kv => _catalog.GetByConfigKey(kv.Key) is { IsSupported: true })
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // Always ensure header comment via WriteValues after optional seed
            if (!File.Exists(managedPath))
            {
                var header =
                    "// FrameForge managed CS2 settings" + Environment.NewLine +
                    "// Loaded via autoexec.cfg FRAMEFORGE section: exec frameforge_settings.cfg" + Environment.NewLine +
                    "// Do not put personal binds here — use autoexec outside FRAMEFORGE markers." + Environment.NewLine;
                await AtomicFile.WriteAllTextAsync(managedPath, header, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await _config.WriteValuesAsync(managedPath, supportedOnly, cancellationToken).ConfigureAwait(false);
            written.Add(managedPath);

            // 2) Ensure autoexec FRAMEFORGE section (only the managed section may change)
            var nextAutoexec = Cs2AutoexecIntegration.EnsureFrameForgeSection(previousAutoexec);

            if (!string.Equals(previousAutoexec, nextAutoexec, StringComparison.Ordinal))
            {
                // Side-car next to autoexec for extra safety (in addition to app backup + recovery snapshot)
                if (File.Exists(autoexecPath))
                {
                    var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                    try
                    {
                        AtomicFile.Copy(autoexecPath, $"{autoexecPath}.frameforge.bak.{stamp}", overwrite: false);
                    }
                    catch (Exception copyEx)
                    {
                        _log.LogWarning($"Autoexec side-car backup failed (continuing): {copyEx.Message}");
                    }
                }

                var autoexecWrite = await AtomicFile
                    .WriteAllTextAsync(autoexecPath, nextAutoexec, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                LogWriteStrategy(autoexecWrite);
                autoexecUpdated = true;
                written.Add(autoexecPath);

                // Structural verification: prefix (before BEGIN) and suffix (after END) must be
                // byte-for-byte identical; only the managed section between the markers may change.
                var after = await File.ReadAllTextAsync(autoexecPath, cancellationToken).ConfigureAwait(false);
                var structure = Cs2AutoexecIntegration.ValidateUserContentPreserved(previousAutoexec, after);
                if (!structure.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Safety check failed: {structure.Reason} " +
                        (structure.RequiresUserIntervention
                            ? "FrameForge will not repair autoexec.cfg automatically."
                            : string.Empty));
                }

                if (!Cs2AutoexecIntegration.SectionExecutesManagedFile(after))
                {
                    throw new InvalidOperationException(
                        "Safety check failed: autoexec FRAMEFORGE section does not exec frameforge_settings.cfg.");
                }
            }
            else if (!written.Contains(autoexecPath, StringComparer.OrdinalIgnoreCase) &&
                     File.Exists(autoexecPath))
            {
                // No text change but still report path when already wired
            }

            // 3) Verify managed values
            var verifyDoc = await _config.ReadAsync(managedPath, cancellationToken).ConfigureAwait(false);
            var verifyMap = verifyDoc.ToDictionary();
            foreach (var entry in diff.Entries.Where(e =>
                         e.IsChange &&
                         !e.SettingId.Equals("integration.autoexec", StringComparison.OrdinalIgnoreCase)))
            {
                var def = _catalog.GetByConfigKey(entry.ConfigKey);
                if (def is null)
                {
                    continue;
                }

                if (!verifyMap.TryGetValue(entry.ConfigKey, out var actual) ||
                    !string.Equals(Normalize(def, actual), Normalize(def, entry.NewValue), StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogError($"Verify failed for {entry.ConfigKey}.");
                    throw new InvalidOperationException(
                        $"Verification failed for '{entry.DisplayName}' (expected '{entry.NewValue}', " +
                        $"found '{actual ?? "<missing>"}').");
                }
            }

            var post = await ReadSettingsCoreAsync(scope.Install, cancellationToken).ConfigureAwait(false);
            var filesMsg = string.Join(", ", written.Select(Path.GetFileName));
            var execNote = post.AutoexecExecutesManagedConfig
                ? "CS2 will load these via autoexec → exec frameforge_settings.cfg (restart CS2 to apply in-game)."
                : post.ExecutionRecommendation ?? "Managed file written but execution hook is incomplete.";

            _log.LogInformation(
                $"Applied {diff.ChangeCount} CS2 setting change(s) to [{filesMsg}]. Backup={backupId ?? "none"}");

            var resultDiff = new SettingsDiff
            {
                Title = diff.Title,
                ProfileId = diff.ProfileId,
                ProfileName = diff.ProfileName,
                Entries = diff.Entries,
                AutoexecIntegrationChange = autoexecUpdated || diff.AutoexecIntegrationChange,
                AffectedFiles = written
            };

            if (_changeSnapshots is not null && keySnaps.Count > 0)
            {
                try
                {
                    // Ensure BackupId stamped when backup was created
                    if (!string.IsNullOrWhiteSpace(backupId))
                    {
                        foreach (var s in keySnaps)
                        {
                            s.BackupId ??= backupId;
                        }
                    }

                    await _changeSnapshots.AppendAsync(keySnaps, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception snapEx)
                {
                    _log.LogWarning($"Per-key change snapshot append failed (non-fatal): {snapEx.Message}");
                }
            }

            if (_baselineSink is not null)
            {
                try
                {
                    await _baselineSink.EndSelfWriteAndCaptureBaselineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception bex)
                {
                    _log.LogDebug($"Baseline capture after apply: {bex.Message}");
                }
            }

            // Verification passed → the internal recovery snapshot is no longer needed.
            var cleanedUp = recovery.TryCleanup(out var cleanupError);
            if (!cleanedUp)
            {
                _log.LogWarning($"Could not remove recovery snapshot {recovery.Id}: {cleanupError}");
            }

            var recoveryInfo = new ApplyRecoveryInfo
            {
                SnapshotCreated = true,
                SnapshotId = recovery.Id,
                CleanedUp = cleanedUp,
                Message = cleanedUp ? null : cleanupError
            };

            return SettingsApplyResult.Ok(
                $"Applied changes to: {filesMsg}. {execNote}",
                backupId,
                resultDiff,
                written,
                autoexecUpdated: autoexecUpdated,
                executedByCs2: post.AutoexecExecutesManagedConfig,
                recovery: recoveryInfo);
        }
        catch (Exception ex)
        {
            _log.LogError("Settings apply failed.", ex);
            try { _baselineSink?.BeginSelfWrite(); } catch { /* ignore */ }

            // 1) Internal recovery snapshot first: it holds the exact pre-apply bytes.
            var recoveryReport = RestoreRecoverySnapshot(recovery);
            var rolledBack = recoveryReport?.Success ?? false;
            string? recoveryMessage = recoveryReport?.Message;

            // 2) Fall back to the persistent user backup when recovery could not restore everything.
            if (recoveryReport is null || !recoveryReport.Success)
            {
                if (backupId is not null)
                {
                    try
                    {
                        var restore = await _backups.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                        rolledBack = restore.Success;
                        if (restore.Success)
                        {
                            recoveryMessage = (recoveryMessage is null ? string.Empty : recoveryMessage + " ") +
                                              $"Restored from backup {backupId} instead.";
                        }
                    }
                    catch (Exception rex)
                    {
                        _log.LogError("Rollback from backup after settings failure also failed.", rex);
                    }
                }
            }

            var message = new StringBuilder();
            message.Append(ex.Message);
            if (recoveryReport is null)
            {
                message.Append(" INTERNAL RECOVERY FAILED: no recovery snapshot was available. " +
                               "The CS2 configuration files may be partially modified — restore manually from a backup.");
            }
            else if (recoveryReport.Success)
            {
                message.Append(" All affected files were restored from the internal recovery snapshot.");
            }
            else
            {
                message.Append(" INTERNAL RECOVERY FAILED: ").Append(recoveryReport.Message);
                if (recoveryReport.Details.Count > 0)
                {
                    message.Append(" Details: ").Append(string.Join("; ", recoveryReport.Details));
                }

                message.Append(" The CS2 configuration files may be partially modified — manual intervention required.");
            }

            if (backupId is not null)
            {
                message.Append($" Backup: {backupId}.");
            }

            var cleanedUpAfterFailure = recovery is not null && recovery.TryCleanup(out _);

            var failureInfo = new ApplyRecoveryInfo
            {
                SnapshotCreated = recovery is not null,
                SnapshotId = recovery?.Id,
                RestoreAttempted = true,
                RestoreSucceeded = recoveryReport?.Success ?? false,
                CleanedUp = cleanedUpAfterFailure,
                Message = recoveryMessage,
                RestoredFiles = recoveryReport?.RestoredFiles ?? Array.Empty<string>(),
                FailedFiles = recoveryReport?.FailedFiles ?? Array.Empty<string>()
            };

            return SettingsApplyResult.Fail(message.ToString(), backupId, rolledBack, diff, failureInfo);
        }
    }

    /// <summary>
    /// Restores a recovery snapshot and logs the outcome. Never throws.
    /// Returns null when no snapshot was available.
    /// </summary>
    private RecoveryRestoreReport? RestoreRecoverySnapshot(RecoverySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _log.LogError("Internal recovery failed: no recovery snapshot was available.");
            return null;
        }

        try
        {
            var report = snapshot.Restore();
            if (report.Success)
            {
                _log.LogInformation($"Internal recovery succeeded: {report.Message}");
            }
            else
            {
                _log.LogError($"Internal recovery FAILED: {report.Message} {string.Join("; ", report.Details)}");
            }

            return report;
        }
        catch (Exception ex)
        {
            _log.LogError("Internal recovery failed with an exception.", ex);
            return new RecoveryRestoreReport
            {
                Success = false,
                Message = $"Recovery failed: {ex.Message}",
                FailedFiles = snapshot.Entries.Select(e => e.Path).ToList()
            };
        }
    }

    /// <summary>
    /// Records which write strategy <see cref="AtomicFile"/> used. The fallback path is
    /// explicitly reported as non-atomic — never as "atomic".
    /// </summary>
    private void LogWriteStrategy(FileWriteResult result)
    {
        if (result.Strategy == FileReplaceStrategy.AtomicReplace)
        {
            _log.LogDebug($"Wrote '{result.Path}' with AtomicReplace.");
            return;
        }

        _log.LogWarning(
            $"Wrote '{result.Path}' with FallbackReplace (non-atomic; recovery copy retained: " +
            $"{result.UsedRecoveryCopy}). {result.Detail}");
    }

    public async Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = await AcquireConfigScopeAsync(cancellationToken).ConfigureAwait(false);
        return await RestoreLastFrameForgeChangesCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Body of a restore operation. Callers must already hold the configuration scope gate:
    /// a restore rewrites the same managed CS2 files an apply writes.
    /// </summary>
    private async Task<SettingsApplyResult> RestoreLastFrameForgeChangesCoreAsync(CancellationToken cancellationToken)
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

        _log.LogInformation($"Restored FrameForge settings backup {lastId} ({result.RestoredFiles.Count} path(s)).");
        return SettingsApplyResult.Ok(
            result.Message + " Restored: " + string.Join(", ", result.RestoredFiles.Select(Path.GetFileName)),
            lastId,
            new SettingsDiff
            {
                Title = "Restore FrameForge changes",
                AffectedFiles = result.RestoredFiles
            },
            result.RestoredFiles);
    }

    private static Cs2ConfigExecutionStatus ResolveExecutionStatus(bool managedExists, bool autoexecExecutes)
    {
        if (managedExists && autoexecExecutes)
        {
            return Cs2ConfigExecutionStatus.Wired;
        }

        if (managedExists)
        {
            return Cs2ConfigExecutionStatus.ManagedFilePresentButNotExecuted;
        }

        return Cs2ConfigExecutionStatus.NotInstalled;
    }

    private static string? BuildExecutionRecommendation(
        Cs2ConfigExecutionStatus status,
        string managedPath,
        string autoexecPath)
    {
        return status switch
        {
            Cs2ConfigExecutionStatus.Wired => null,
            Cs2ConfigExecutionStatus.ManagedFilePresentButNotExecuted =>
                "FrameForge configuration is installed but is not currently executed by CS2. " +
                "Apply settings (or re-apply a profile) so FrameForge can add a marked section to autoexec.cfg " +
                $"that runs `exec frameforge_settings.cfg`. Files: {managedPath}, {autoexecPath}. " +
                "FrameForge does not modify Steam launch options automatically. " +
                "If autoexec still does not run on your install, add `+exec autoexec.cfg` to CS2 launch options manually.",
            Cs2ConfigExecutionStatus.NotInstalled =>
                "No FrameForge managed configuration is installed yet. Preview and Apply settings or a profile " +
                "to create frameforge_settings.cfg and wire autoexec.cfg (user lines outside FRAMEFORGE markers stay intact).",
            _ => "CS2 cfg directory is unavailable."
        };
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
                name.Equals(Cs2AutoexecIntegration.AutoexecFileName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Holds the configuration-scope gate for the duration of one operation.
    /// Dispose (via <c>await using</c>) releases the gate exactly once.
    /// </summary>
    private sealed class ConfigScope : IAsyncDisposable
    {
        private readonly ManagedConfigLease? _lease;

        public ConfigScope(Cs2InstallInfo? install, string? cfgDirectory, ManagedConfigLease? lease)
        {
            Install = install;
            CfgDirectory = cfgDirectory;
            _lease = lease;
        }

        public Cs2InstallInfo? Install { get; }

        public string? CfgDirectory { get; }

        public bool IsGateHeld => _lease is { IsActive: true };

        public ValueTask DisposeAsync() => _lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
