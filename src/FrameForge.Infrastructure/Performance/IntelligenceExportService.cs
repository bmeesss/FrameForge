using System.Text.Json;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Local intelligence/snapshot export-import. Validates strictly; never executes content.
/// Imported fingerprints stay associated with their original ids.
/// </summary>
public sealed class IntelligenceExportService : IIntelligenceExportService
{
    private readonly IPerformanceIntelligenceService _intel;
    private readonly ISettingChangeSnapshotStore _snapshots;
    private readonly ISystemFingerprintService _fingerprint;
    private readonly ICs2SettingCatalog _catalog;
    private readonly IPathService _paths;
    private readonly IAppLog _log;

    public IntelligenceExportService(
        IPerformanceIntelligenceService intel,
        ISettingChangeSnapshotStore snapshots,
        ISystemFingerprintService fingerprint,
        ICs2SettingCatalog catalog,
        IPathService paths,
        IAppLog log)
    {
        _intel = intel;
        _snapshots = snapshots;
        _fingerprint = fingerprint;
        _catalog = catalog;
        _paths = paths;
        _log = log;
    }

    public async Task<IntelligenceExportPackage> BuildExportAsync(CancellationToken cancellationToken = default)
    {
        var index = await _intel.GetIndexAsync(forceRebuild: false, cancellationToken).ConfigureAwait(false);
        var package = new IntelligenceExportPackage
        {
            SchemaVersion = IntelligenceExportSchema.CurrentVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Fingerprints = SanitizeFingerprints(index.KnownFingerprints.Count > 0
                ? index.KnownFingerprints
                : index.CurrentFingerprint is null
                    ? Array.Empty<SystemFingerprint>()
                    : new[] { index.CurrentFingerprint }),
            Records = index.Records.Select(SanitizeRecord).ToList(),
            Evidence = index.AllEvidence.Select(SanitizeEvidence).ToList()
        };
        return package;
    }

    public async Task ExportIntelligenceAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var package = await BuildExportAsync(cancellationToken).ConfigureAwait(false);
        await FrameForgeJson.SerializeFileAsync(destinationPath, package, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Exported intelligence to {destinationPath}");
    }

    public async Task ExportSnapshotsAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var list = await _snapshots.ListAsync(cancellationToken).ConfigureAwait(false);
        var package = new SnapshotExportPackage
        {
            SchemaVersion = IntelligenceExportSchema.CurrentVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Snapshots = list.ToList()
        };
        await FrameForgeJson.SerializeFileAsync(destinationPath, package, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Exported {package.Snapshots.Count} snapshot(s) to {destinationPath}");
    }

    public async Task<IntelligenceImportPreview> PreviewImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var preview = await LoadAndValidateAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!preview.IsValid)
        {
            return preview;
        }

        var index = await _intel.GetIndexAsync(forceRebuild: false, cancellationToken).ConfigureAwait(false);
        var currentFp = index.CurrentFingerprint?.FingerprintId;
        var existingKeys = new HashSet<string>(
            index.Records.Select(r => RecordKey(r.SettingKey, r.SystemFingerprintId)),
            StringComparer.OrdinalIgnoreCase);
        var existingEvidence = new HashSet<string>(
            index.AllEvidence.Select(e => e.GuidedRunId + "|" + e.SettingKey),
            StringComparer.OrdinalIgnoreCase);

        var toAdd = 0;
        var toUpdate = 0;
        var toSkip = 0;
        var conflicts = 0;
        var conflictDetails = new List<string>();
        var differentFp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (preview.Package is not null)
        {
            foreach (var rec in preview.Package.Records)
            {
                if (!string.IsNullOrWhiteSpace(rec.SystemFingerprintId) &&
                    currentFp is not null &&
                    !rec.SystemFingerprintId.Equals(currentFp, StringComparison.OrdinalIgnoreCase))
                {
                    differentFp.Add(rec.SystemFingerprintId);
                }

                var key = RecordKey(rec.SettingKey, rec.SystemFingerprintId);
                if (existingKeys.Contains(key))
                {
                    // same key+fp with different latest classification = conflict candidate
                    var existing = index.Records.FirstOrDefault(r =>
                        r.SettingKey.Equals(rec.SettingKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.SystemFingerprintId, rec.SystemFingerprintId, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null &&
                        existing.LatestClassification != rec.LatestClassification &&
                        rec.LatestClassification is not null)
                    {
                        conflicts++;
                        conflictDetails.Add(
                            $"{rec.SettingKey}@{rec.SystemFingerprintId}: existing {existing.LatestClassification} vs import {rec.LatestClassification}");
                        toUpdate++;
                    }
                    else
                    {
                        toSkip++;
                    }
                }
                else
                {
                    toAdd++;
                }
            }

            var evidenceAdd = preview.Package.Evidence.Count(e =>
                !existingEvidence.Contains(e.GuidedRunId + "|" + e.SettingKey));

            return new IntelligenceImportPreview
            {
                IsValid = true,
                Message = "Import preview ready. Confirm to apply.",
                RecordsToAdd = toAdd,
                RecordsToUpdate = toUpdate,
                RecordsToSkip = toSkip,
                EvidenceToAdd = evidenceAdd,
                SnapshotsToAdd = preview.SnapshotPackage?.Snapshots.Count ?? 0,
                Conflicts = conflicts,
                ConflictDetails = conflictDetails,
                DifferentFingerprints = differentFp.ToList(),
                Package = preview.Package,
                SnapshotPackage = preview.SnapshotPackage
            };
        }

        if (preview.SnapshotPackage is not null)
        {
            return new IntelligenceImportPreview
            {
                IsValid = true,
                Message = "Snapshot import preview ready.",
                SnapshotsToAdd = preview.SnapshotPackage.Snapshots.Count,
                Package = null,
                SnapshotPackage = preview.SnapshotPackage
            };
        }

        return preview;
    }

    public async Task<IntelligenceImportResult> ImportAsync(
        string sourcePath,
        IntelligenceImportMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == IntelligenceImportMode.Preview)
        {
            var p = await PreviewImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            return p.IsValid
                ? IntelligenceImportResult.Ok("Preview only — no changes written.", p)
                : IntelligenceImportResult.Fail(p.Message, p);
        }

        var preview = await PreviewImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!preview.IsValid || (preview.Package is null && preview.SnapshotPackage is null))
        {
            return IntelligenceImportResult.Fail(preview.Message, preview);
        }

        try
        {
            if (preview.Package is not null)
            {
                await MergeIntelligenceFileAsync(preview.Package, mode, cancellationToken).ConfigureAwait(false);
            }

            if (preview.SnapshotPackage is not null)
            {
                await _snapshots.AppendAsync(preview.SnapshotPackage.Snapshots, cancellationToken).ConfigureAwait(false);
            }

            _intel.InvalidateCache();
            await _intel.RebuildAsync(cancellationToken).ConfigureAwait(false);

            return IntelligenceImportResult.Ok(
                mode == IntelligenceImportMode.Merge
                    ? $"Merged import: {preview.RecordsToAdd} add, {preview.RecordsToUpdate} update, {preview.EvidenceToAdd} evidence."
                    : $"Imported as new history: {preview.RecordsToAdd} records, {preview.EvidenceToAdd} evidence.",
                preview);
        }
        catch (Exception ex)
        {
            _log.LogError("Intelligence import failed.", ex);
            return IntelligenceImportResult.Fail(ex.Message, preview);
        }
    }

    private async Task MergeIntelligenceFileAsync(
        IntelligenceExportPackage package,
        IntelligenceImportMode mode,
        CancellationToken cancellationToken)
    {
        // Persist as a side archive under PerformanceHistory/imports — rebuild still uses guided runs primarily.
        // For evidence that isn't in guided store, write a supplemental evidence file the intel service can load.
        var importDir = Path.Combine(_paths.PerformanceHistoryDirectory, "imports");
        Directory.CreateDirectory(importDir);
        var name = $"import_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.json";
        if (mode == IntelligenceImportMode.ImportAsNew)
        {
            // Ensure evidence run ids don't collide silently — prefix
            foreach (var e in package.Evidence)
            {
                if (!e.GuidedRunId.StartsWith("imp_", StringComparison.OrdinalIgnoreCase))
                {
                    // SettingTestEvidence has init-only props — rewrite via new list
                }
            }

            package = new IntelligenceExportPackage
            {
                SchemaVersion = package.SchemaVersion,
                ExportedAt = package.ExportedAt,
                Notes = package.Notes,
                Fingerprints = package.Fingerprints,
                Records = package.Records,
                Evidence = package.Evidence.Select(e => new SettingTestEvidence
                {
                    GuidedRunId = e.GuidedRunId.StartsWith("imp_", StringComparison.OrdinalIgnoreCase)
                        ? e.GuidedRunId
                        : "imp_" + e.GuidedRunId,
                    SettingId = e.SettingId,
                    SettingKey = e.SettingKey,
                    SettingName = e.SettingName,
                    EvidenceType = e.EvidenceType,
                    Classification = e.Classification,
                    TestedAt = e.TestedAt,
                    InitialBenchmarkId = e.InitialBenchmarkId,
                    PostBenchmarkId = e.PostBenchmarkId,
                    BackupId = e.BackupId,
                    SystemFingerprintId = e.SystemFingerprintId,
                    AppliedValue = e.AppliedValue,
                    AllSettingKeysInRun = e.AllSettingKeysInRun,
                    ComparisonRows = e.ComparisonRows,
                    BenchmarkComplete = e.BenchmarkComplete,
                    ClassificationReason = e.ClassificationReason
                }).ToList()
            };
        }

        var path = Path.Combine(importDir, name);
        await FrameForgeJson.SerializeFileAsync(path, package, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Stored intelligence import archive {path}");
    }

    private async Task<IntelligenceImportPreview> LoadAndValidateAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return Invalid("Import file was not found.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Invalid("Could not read import file: " + ex.Message);
        }

        // Detect package kind by extension / content
        var ext = Path.GetExtension(sourcePath);
        if (ext.Equals(IntelligenceExportSchema.SnapshotsExtension, StringComparison.OrdinalIgnoreCase) ||
            json.Contains("\"snapshots\"", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateSnapshots(json);
        }

        return ValidateIntelligence(json);
    }

    private IntelligenceImportPreview ValidateIntelligence(string json)
    {
        IntelligenceExportPackage? package;
        try
        {
            package = FrameForgeJson.Deserialize<IntelligenceExportPackage>(json);
        }
        catch (Exception ex)
        {
            return Invalid("Invalid JSON: " + ex.Message);
        }

        if (package is null)
        {
            return Invalid("Package was empty.");
        }

        var errors = new List<string>();
        if (package.SchemaVersion <= 0)
        {
            package.SchemaVersion = 1;
        }

        if (package.SchemaVersion > IntelligenceExportSchema.CurrentVersion)
        {
            errors.Add($"Unsupported schema version {package.SchemaVersion}.");
        }

        package.Fingerprints ??= new();
        package.Records ??= new();
        package.Evidence ??= new();

        var seenRec = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fp in package.Fingerprints)
        {
            if (string.IsNullOrWhiteSpace(fp.FingerprintId) || fp.FingerprintId.Length < 8)
            {
                errors.Add("Malformed fingerprint id.");
            }

            if (LooksLikePii(fp.CpuModel) || LooksLikePii(fp.GpuModel) || LooksLikePii(fp.OsVersion))
            {
                errors.Add("Fingerprint contains disallowed PII-like content.");
            }
        }

        foreach (var rec in package.Records)
        {
            if (string.IsNullOrWhiteSpace(rec.SettingKey))
            {
                errors.Add("Record missing setting key.");
                continue;
            }

            if (_catalog.GetByConfigKey(rec.SettingKey) is null &&
                _catalog.GetById(rec.SettingId) is null)
            {
                // allow unknown with warning-as-error for safety
                errors.Add($"Unknown setting id/key '{rec.SettingKey}'.");
            }

            var rk = RecordKey(rec.SettingKey, rec.SystemFingerprintId);
            if (!seenRec.Add(rk))
            {
                errors.Add($"Duplicate record id '{rk}'.");
            }

            if (rec.LastTestedAt is { } ts && ts > DateTimeOffset.UtcNow.AddDays(1))
            {
                errors.Add($"Invalid future timestamp on {rec.SettingKey}.");
            }

            if (!Enum.IsDefined(rec.Confidence))
            {
                errors.Add($"Invalid confidence on {rec.SettingKey}.");
            }
        }

        var seenEv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in package.Evidence)
        {
            if (string.IsNullOrWhiteSpace(e.GuidedRunId) || string.IsNullOrWhiteSpace(e.SettingKey))
            {
                errors.Add("Evidence missing run id or setting key.");
                continue;
            }

            if (!Enum.IsDefined(e.EvidenceType) || !Enum.IsDefined(e.Classification))
            {
                errors.Add($"Invalid enum on evidence {e.GuidedRunId}.");
            }

            if (e.TestedAt == default || e.TestedAt > DateTimeOffset.UtcNow.AddDays(1))
            {
                errors.Add($"Invalid evidence timestamp on {e.GuidedRunId}.");
            }

            var ek = e.GuidedRunId + "|" + e.SettingKey;
            if (!seenEv.Add(ek))
            {
                errors.Add($"Duplicate evidence '{ek}'.");
            }
        }

        if (errors.Count > 0)
        {
            return new IntelligenceImportPreview
            {
                IsValid = false,
                Message = "Validation failed.",
                Errors = errors,
                Package = package
            };
        }

        return new IntelligenceImportPreview
        {
            IsValid = true,
            Message = "Valid intelligence package.",
            Package = package
        };
    }

    private IntelligenceImportPreview ValidateSnapshots(string json)
    {
        SnapshotExportPackage? package;
        try
        {
            package = FrameForgeJson.Deserialize<SnapshotExportPackage>(json);
        }
        catch (Exception ex)
        {
            return Invalid("Invalid snapshot JSON: " + ex.Message);
        }

        if (package is null)
        {
            return Invalid("Snapshot package empty.");
        }

        var errors = new List<string>();
        if (package.SchemaVersion > IntelligenceExportSchema.CurrentVersion)
        {
            errors.Add($"Unsupported snapshot schema {package.SchemaVersion}.");
        }

        package.Snapshots ??= new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in package.Snapshots)
        {
            if (string.IsNullOrWhiteSpace(s.ConfigKey))
            {
                errors.Add("Snapshot missing config key.");
                continue;
            }

            if (_catalog.GetByConfigKey(s.ConfigKey) is null)
            {
                errors.Add($"Unknown snapshot key '{s.ConfigKey}'.");
            }

            if (s.Timestamp == default)
            {
                errors.Add($"Invalid snapshot timestamp for {s.ConfigKey}.");
            }

            var id = s.Id;
            if (!string.IsNullOrWhiteSpace(id) && !seen.Add(id))
            {
                errors.Add($"Duplicate snapshot id '{id}'.");
            }
        }

        if (errors.Count > 0)
        {
            return new IntelligenceImportPreview
            {
                IsValid = false,
                Message = "Snapshot validation failed.",
                Errors = errors,
                SnapshotPackage = package
            };
        }

        return new IntelligenceImportPreview
        {
            IsValid = true,
            Message = "Valid snapshot package.",
            SnapshotPackage = package,
            SnapshotsToAdd = package.Snapshots.Count
        };
    }

    private static IntelligenceImportPreview Invalid(string message) => new()
    {
        IsValid = false,
        Message = message,
        Errors = { message }
    };

    private static string RecordKey(string settingKey, string? fp) =>
        settingKey + "@" + (fp ?? "unknown");

    private static bool LooksLikePii(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return SystemFingerprintService.LooksSensitive(value);
    }

    private static List<SystemFingerprint> SanitizeFingerprints(IEnumerable<SystemFingerprint> source) =>
        source.Select(f => new SystemFingerprint
        {
            CpuModel = SanitizeLabel(f.CpuModel),
            GpuModel = SanitizeLabel(f.GpuModel),
            TotalRamBytes = f.TotalRamBytes,
            OsVersion = SanitizeLabel(f.OsVersion),
            Architecture = SanitizeLabel(f.Architecture),
            FingerprintId = f.FingerprintId
        }).ToList();

    private static string SanitizeLabel(string? v)
    {
        if (string.IsNullOrWhiteSpace(v) || SystemFingerprintService.LooksSensitive(v))
        {
            return "unknown";
        }

        return v.Trim();
    }

    private static SettingPerformanceRecord SanitizeRecord(SettingPerformanceRecord r) => new()
    {
        SettingId = r.SettingId,
        SettingKey = r.SettingKey,
        SettingName = r.SettingName,
        SystemFingerprintId = r.SystemFingerprintId,
        SystemFingerprint = r.SystemFingerprint is null ? null : SanitizeFingerprints(new[] { r.SystemFingerprint }).First(),
        TestCount = r.TestCount,
        DirectTestCount = r.DirectTestCount,
        AssociatedMultiSettingTestCount = r.AssociatedMultiSettingTestCount,
        ImprovedCount = r.ImprovedCount,
        RegressedCount = r.RegressedCount,
        NeutralCount = r.NeutralCount,
        MixedCount = r.MixedCount,
        InconclusiveCount = r.InconclusiveCount,
        DirectImprovedCount = r.DirectImprovedCount,
        DirectRegressedCount = r.DirectRegressedCount,
        DirectNeutralCount = r.DirectNeutralCount,
        DirectMixedCount = r.DirectMixedCount,
        DirectInconclusiveCount = r.DirectInconclusiveCount,
        AverageMetricChanges = r.AverageMetricChanges,
        LastTestedAt = r.LastTestedAt,
        Confidence = r.Confidence,
        LatestClassification = r.LatestClassification,
        AggregateDirectClassification = r.AggregateDirectClassification,
        ConfidenceReason = r.ConfidenceReason,
        RecommendationSummary = r.RecommendationSummary,
        DirectEvidence = r.DirectEvidence.Select(SanitizeEvidence).ToList(),
        AssociatedEvidence = r.AssociatedEvidence.Select(SanitizeEvidence).ToList(),
        IsCurrentSystem = r.IsCurrentSystem
    };

    private static SettingTestEvidence SanitizeEvidence(SettingTestEvidence e) => new()
    {
        GuidedRunId = e.GuidedRunId,
        SettingId = e.SettingId,
        SettingKey = e.SettingKey,
        SettingName = e.SettingName,
        EvidenceType = e.EvidenceType,
        Classification = e.Classification,
        TestedAt = e.TestedAt,
        InitialBenchmarkId = e.InitialBenchmarkId,
        PostBenchmarkId = e.PostBenchmarkId,
        BackupId = e.BackupId,
        SystemFingerprintId = e.SystemFingerprintId,
        AppliedValue = e.AppliedValue,
        AllSettingKeysInRun = e.AllSettingKeysInRun,
        ComparisonRows = e.ComparisonRows,
        BenchmarkComplete = e.BenchmarkComplete,
        ClassificationReason = e.ClassificationReason
    };
}
