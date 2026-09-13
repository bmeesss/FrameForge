namespace FrameForge.Core.Models;

/// <summary>
/// Heuristic confidence labels — not statistical certainty.
/// Rules (documented): see <see cref="PerformanceConfidenceRules"/>.
/// </summary>
public enum PerformanceConfidence
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// How a guided run relates to a setting for evidence purposes.
/// </summary>
public enum PerformanceEvidenceType
{
    /// <summary>Exactly one setting was changed — may be used as strong/direct evidence.</summary>
    SingleSetting,

    /// <summary>
    /// Multiple settings changed together — associated only; must NOT be treated as
    /// proven causal effect for any one setting.
    /// </summary>
    MultiSetting
}

/// <summary>
/// Result of evaluating whether a single FrameForge-managed key can be surgically restored.
/// Surgical restore is NOT exposed in the UI in Phase 7.
/// </summary>
public enum TargetedRestoreSafety
{
    /// <summary>Enough metadata + managed-file ownership to attempt a future targeted restore.</summary>
    SafeToTargetRestore,

    /// <summary>Missing snapshot metadata or incomplete chain.</summary>
    InsufficientMetadata,

    /// <summary>User/external edits outside FrameForge markers make surgical restore unsafe.</summary>
    UnsafeToTargetRestore,

    /// <summary>Setting not present in any FrameForge-managed change record.</summary>
    NotTracked
}

/// <summary>
/// Stable local hardware/software fingerprint. No PII, no network identifiers.
/// </summary>
public sealed class SystemFingerprint
{
    public string CpuModel { get; init; } = "unknown";
    public string GpuModel { get; init; } = "unknown";
    public long? TotalRamBytes { get; init; }
    public string OsVersion { get; init; } = "unknown";
    public string Architecture { get; init; } = "unknown";

    /// <summary>Compact stable hash of the fields above (hex). Empty components become "unknown".</summary>
    public string FingerprintId { get; init; } = "unknown";

    public string DisplaySummary =>
        $"{CpuModel} · {GpuModel} · {(TotalRamBytes is null ? "RAM unknown" : $"{TotalRamBytes.Value / (1024.0 * 1024 * 1024):0.0} GB")} · {OsVersion} · {Architecture}";
}

/// <summary>Average deltas for metrics that were actually available (null = never observed).</summary>
public sealed class AverageMetricChanges
{
    public double? SystemCpuDelta { get; set; }
    public double? ProcessCpuDelta { get; set; }
    public double? SystemMemoryDelta { get; set; }
    public double? ProcessWorkingSetMbDelta { get; set; }
    public double? FrameTimeMsDelta { get; set; }
    public double? GpuUtilizationDelta { get; set; }

    public int SystemCpuSampleCount { get; set; }
    public int ProcessCpuSampleCount { get; set; }
    public int SystemMemorySampleCount { get; set; }
    public int ProcessWorkingSetSampleCount { get; set; }
    public int FrameTimeSampleCount { get; set; }
    public int GpuUtilizationSampleCount { get; set; }
}

/// <summary>
/// One guided-run observation linked to a setting (direct or associated).
/// </summary>
public sealed class SettingTestEvidence
{
    public string GuidedRunId { get; init; } = string.Empty;
    public string SettingId { get; init; } = string.Empty;
    public string SettingKey { get; init; } = string.Empty;
    public string SettingName { get; init; } = string.Empty;
    public PerformanceEvidenceType EvidenceType { get; init; }
    public GuidedResultClassification Classification { get; init; }
    public DateTimeOffset TestedAt { get; init; }
    public string? InitialBenchmarkId { get; init; }
    public string? PostBenchmarkId { get; init; }
    public string? BackupId { get; init; }
    public string? SystemFingerprintId { get; init; }
    public string? AppliedValue { get; init; }
    public IReadOnlyList<string> AllSettingKeysInRun { get; init; } = Array.Empty<string>();
    public List<GuidedComparisonRow> ComparisonRows { get; init; } = new();
    public bool BenchmarkComplete { get; init; }
    public string? ClassificationReason { get; init; }
}

/// <summary>
/// Aggregated local performance record for one setting on one system fingerprint.
/// Only aggregates metrics that actually exist — never invents FPS.
/// </summary>
public sealed class SettingPerformanceRecord
{
    public string SettingId { get; set; } = string.Empty;
    public string SettingKey { get; set; } = string.Empty;
    public string SettingName { get; set; } = string.Empty;
    public string SystemFingerprintId { get; set; } = string.Empty;
    public SystemFingerprint? SystemFingerprint { get; set; }

    public int TestCount { get; set; }
    public int DirectTestCount { get; set; }
    public int AssociatedMultiSettingTestCount { get; set; }

    public int ImprovedCount { get; set; }
    public int RegressedCount { get; set; }
    public int NeutralCount { get; set; }
    public int MixedCount { get; set; }
    public int InconclusiveCount { get; set; }

    /// <summary>Counts from SingleSetting evidence only (strong evidence pool).</summary>
    public int DirectImprovedCount { get; set; }
    public int DirectRegressedCount { get; set; }
    public int DirectNeutralCount { get; set; }
    public int DirectMixedCount { get; set; }
    public int DirectInconclusiveCount { get; set; }

    public AverageMetricChanges AverageMetricChanges { get; set; } = new();
    public DateTimeOffset? LastTestedAt { get; set; }
    public PerformanceConfidence Confidence { get; set; } = PerformanceConfidence.Unknown;
    public GuidedResultClassification? LatestClassification { get; set; }
    public GuidedResultClassification? AggregateDirectClassification { get; set; }
    public string ConfidenceReason { get; set; } = string.Empty;
    public string RecommendationSummary { get; set; } = "No local benchmark evidence yet.";

    public List<SettingTestEvidence> DirectEvidence { get; set; } = new();
    public List<SettingTestEvidence> AssociatedEvidence { get; set; } = new();

    public bool HasEvidence => TestCount > 0;
    public bool HasDirectEvidence => DirectTestCount > 0;

    public string DisplayLatest =>
        LatestClassification?.ToString() ?? "—";

    public string DisplayConfidence => Confidence.ToString();
}

/// <summary>
/// Snapshot of the full local intelligence index (cache + optional disk export).
/// </summary>
public sealed class PerformanceIntelligenceIndex
{
    public int SchemaVersion { get; set; } = PerformanceIntelligenceSchema.CurrentVersion;
    public DateTimeOffset BuiltAt { get; set; } = DateTimeOffset.UtcNow;
    public SystemFingerprint? CurrentFingerprint { get; set; }
    public List<SettingPerformanceRecord> Records { get; set; } = new();
    public List<SettingTestEvidence> AllEvidence { get; set; } = new();
    public int GuidedRunsAnalyzed { get; set; }
    public string Notes { get; set; } =
        "Local only. No telemetry. Confidence is heuristic, not statistical proof. No FPS guarantees.";
}

public static class PerformanceIntelligenceSchema
{
    public const int CurrentVersion = 1;
    public const string IndexFileName = "index.json";
}

/// <summary>
/// Per-key change metadata recorded alongside a full backup (does not replace full backup).
/// </summary>
public sealed class SettingChangeSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string SettingId { get; set; } = string.Empty;
    public string ConfigKey { get; set; } = string.Empty;
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? File { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? BackupId { get; set; }
    public string? ProfileId { get; set; }
    public string? Reason { get; set; }
}

public sealed class SettingChangeSnapshotStoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<SettingChangeSnapshot> Entries { get; set; } = new();
}

/// <summary>Assessment for a future targeted restore (not executed in Phase 7 UI).</summary>
public sealed class TargetedRestoreAssessment
{
    public string ConfigKey { get; init; } = string.Empty;
    public TargetedRestoreSafety Safety { get; init; } = TargetedRestoreSafety.NotTracked;
    public string Message { get; init; } = string.Empty;
    public SettingChangeSnapshot? LatestSnapshot { get; init; }
    public bool PreferFullBackupRestore { get; init; } = true;
}

/// <summary>
/// Documented confidence heuristics (not scientific certainty).
/// Only SingleSetting evidence with a decisive classification (not Inconclusive)
/// and complete before/after benchmarks counts as a "valid direct test".
/// </summary>
public static class PerformanceConfidenceRules
{
    public const int MediumMinValidDirect = 2;
    public const int HighMinValidDirect = 4;

    /// <summary>
    /// Compute confidence from direct (single-setting) valid tests only.
    /// Multi-setting evidence never raises confidence above Low by itself.
    /// </summary>
    public static PerformanceConfidence Compute(
        int validDirectTests,
        int consistentDirectMajority,
        int conflictingDirect,
        int associatedOnlyTests,
        out string reason)
    {
        if (validDirectTests <= 0)
        {
            if (associatedOnlyTests > 0)
            {
                reason =
                    "Unknown/Low ceiling: only multi-setting associated evidence exists — not treated as causal proof.";
                return PerformanceConfidence.Unknown;
            }

            reason = "Unknown: 0 valid single-setting tests.";
            return PerformanceConfidence.Unknown;
        }

        if (validDirectTests == 1)
        {
            reason = "Low: 1 valid single-setting test — evidence is limited.";
            return PerformanceConfidence.Low;
        }

        if (conflictingDirect > 0 && conflictingDirect >= consistentDirectMajority)
        {
            reason =
                $"Low: {validDirectTests} valid direct tests but results conflict (mixed improved/regressed).";
            return PerformanceConfidence.Low;
        }

        if (validDirectTests >= HighMinValidDirect && conflictingDirect == 0)
        {
            reason =
                $"High: {validDirectTests} consistent valid single-setting tests (heuristic, not statistical proof).";
            return PerformanceConfidence.High;
        }

        if (validDirectTests >= MediumMinValidDirect && conflictingDirect == 0)
        {
            reason =
                $"Medium: {validDirectTests} consistent valid single-setting tests.";
            return PerformanceConfidence.Medium;
        }

        reason =
            $"Low: {validDirectTests} valid direct test(s) with limited consistency.";
        return PerformanceConfidence.Low;
    }

    public static string BuildRecommendationSummary(SettingPerformanceRecord record)
    {
        if (!record.HasEvidence)
        {
            return "No local benchmark evidence yet.";
        }

        if (record.DirectTestCount == 0 && record.AssociatedMultiSettingTestCount > 0)
        {
            return
                $"Seen in {record.AssociatedMultiSettingTestCount} multi-setting test(s) only — not direct causal evidence.";
        }

        if (record.DirectTestCount == 1)
        {
            return
                $"Tested once as a single setting ({record.LatestClassification}) — evidence is limited.";
        }

        if (record.DirectImprovedCount > 0 &&
            record.DirectRegressedCount == 0 &&
            record.DirectMixedCount == 0)
        {
            return
                $"Multiple single-setting tests generally show improvement ({record.DirectImprovedCount}/{record.DirectTestCount}). Not a guarantee.";
        }

        if (record.DirectRegressedCount > 0 &&
            record.DirectImprovedCount == 0 &&
            record.DirectMixedCount == 0)
        {
            return
                $"Multiple single-setting tests generally show regression ({record.DirectRegressedCount}/{record.DirectTestCount}). Not definitive.";
        }

        if (record.DirectMixedCount > 0 ||
            (record.DirectImprovedCount > 0 && record.DirectRegressedCount > 0))
        {
            return "Results are mixed across single-setting tests.";
        }

        if (record.DirectNeutralCount > 0 &&
            record.DirectImprovedCount == 0 &&
            record.DirectRegressedCount == 0)
        {
            return "Single-setting tests are mostly neutral (within noise).";
        }

        return $"Local evidence available ({record.DirectTestCount} direct test(s)). Confidence: {record.Confidence}.";
    }
}
