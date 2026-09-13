namespace FrameForge.Core.Models;

/// <summary>Guided optimization session lifecycle.</summary>
public enum GuidedOptimizationStatus
{
    Idle,
    Preparing,
    BenchmarkingBefore,
    PreparingOptimization,
    AwaitingConfirmation,
    Applying,
    BenchmarkingAfter,
    /// <summary>Severe condition mismatch — user must Continue Comparison or Stop/Restore.</summary>
    AwaitingConditionDecision,
    Comparing,
    AwaitingDecision,
    Completed,
    Restoring,
    Restored,
    Cancelled,
    Failed
}

/// <summary>User decision after A/B comparison.</summary>
public enum GuidedUserDecision
{
    None,
    Keep,
    Restore,
    Cancelled,
    /// <summary>User chose to continue comparison after severe condition mismatch.</summary>
    ContinueComparison,
    /// <summary>User chose stop/restore after severe condition mismatch (default).</summary>
    StopRestore
}

/// <summary>
/// Classification based only on measured external metrics.
/// Never uses invented FPS.
/// </summary>
public enum GuidedResultClassification
{
    /// <summary>Not enough overlapping measured metrics.</summary>
    Inconclusive,

    /// <summary>All scored metrics moved in the preferred direction (or flat within noise).</summary>
    Improved,

    /// <summary>No meaningful change beyond noise on scored metrics.</summary>
    Neutral,

    /// <summary>One or more scored metrics moved against the preferred direction beyond noise.</summary>
    Regressed,

    /// <summary>Some improved and some regressed beyond noise.</summary>
    Mixed
}

/// <summary>What the guided run will apply (explicit selection only).</summary>
public enum GuidedTargetKind
{
    Profile,
    SettingsMap
}

public sealed class GuidedOptimizationRequest
{
    public GuidedTargetKind TargetKind { get; init; } = GuidedTargetKind.Profile;

    /// <summary>Profile id when TargetKind is Profile.</summary>
    public string? ProfileId { get; init; }

    public string? ProfileName { get; init; }

    /// <summary>Explicit settings map (config key → value). Used for Profile resolution and SettingsMap.</summary>
    public Dictionary<string, string> DesiredSettings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public BenchmarkConfiguration BenchmarkConfiguration { get; init; } = BenchmarkConfiguration.CreateDefault();

    /// <summary>When true, only detection/validation/diff — no benchmark, backup, or apply.</summary>
    public bool PreviewOnly { get; init; }

    /// <summary>
    /// When true, baseline/post benchmarks require CS2 process present.
    /// Default true for meaningful process metrics.
    /// </summary>
    public bool RequireCs2ProcessForBenchmark { get; init; } = true;

    public string? Label { get; init; }

    /// <summary>Optional link to a saved custom optimization set.</summary>
    public string? CustomSetId { get; init; }

    public string? CustomSetName { get; init; }
}

public sealed class GuidedOptimizationProgress
{
    public GuidedOptimizationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public double? ProgressPercent { get; init; }
    public string? Detail { get; init; }
}

public sealed class GuidedConditionWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsBlocking { get; init; }
}

public sealed class GuidedComparisonRow
{
    public string Metric { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public double? Before { get; init; }
    public double? After { get; init; }
    public double? AbsoluteDifference { get; init; }
    public double? PercentDifference { get; init; }
    public bool IsAvailable { get; init; }
    public string Interpretation { get; init; } = "N/A";
    public string PreferredDirection { get; init; } = "n/a";
}

public sealed class GuidedOptimizationRun
{
    public int SchemaVersion { get; set; } = GuidedOptimizationSchema.CurrentVersion;
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public GuidedOptimizationStatus Status { get; set; } = GuidedOptimizationStatus.Idle;
    public GuidedUserDecision UserDecision { get; set; } = GuidedUserDecision.None;
    public GuidedResultClassification Classification { get; set; } = GuidedResultClassification.Inconclusive;

    public string? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public string? OptimizationLabel { get; set; }

    /// <summary>Optional custom optimization set id (Phase 6).</summary>
    public string? CustomSetId { get; set; }

    public string? CustomSetName { get; set; }

    /// <summary>Config keys explicitly selected for this run (settings map / single / multi).</summary>
    public List<string> SelectedSettingKeys { get; set; } = new();

    /// <summary>Snapshot of desired key→value at start of run.</summary>
    public Dictionary<string, string> SelectedSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Non-PII system fingerprint id at run time (Phase 8). Prefer benchmark snapshots when present.</summary>
    public string? SystemFingerprintId { get; set; }

    /// <summary>Fingerprint from baseline benchmark snapshot (source of truth).</summary>
    public string? BaselineFingerprintId { get; set; }

    /// <summary>Fingerprint from post-optimization benchmark snapshot (source of truth).</summary>
    public string? PostFingerprintId { get; set; }

    /// <summary>Condition match report for baseline vs post (Phase 11).</summary>
    public BenchmarkConditionReport? ConditionReport { get; set; }

    /// <summary>User forced comparison despite severe mismatch (legacy alias of ConditionOverride).</summary>
    public bool ForcedCompareDespiteMismatch { get; set; }

    /// <summary>Decision at the integrity gate (ContinueComparison / StopRestore / None).</summary>
    public GuidedUserDecision ConditionDecision { get; set; } = GuidedUserDecision.None;

    /// <summary>True when user continued comparison after severe mismatch.</summary>
    public bool ConditionOverride { get; set; }

    /// <summary>Why override was allowed / denied (human-readable, no PII).</summary>
    public string? ConditionOverrideReason { get; set; }

    /// <summary>Overall condition status when the gate decision was made.</summary>
    public ConditionMatchStatus? ConditionStatusAtDecision { get; set; }

    /// <summary>Reliability at decision time (condition reliability, not statistical confidence).</summary>
    public ComparisonReliability? ConditionReliabilityAtDecision { get; set; }

    public string? InitialBenchmarkId { get; set; }
    public string? PostBenchmarkId { get; set; }
    public string? BackupId { get; set; }

    public BenchmarkConfiguration BenchmarkConfiguration { get; set; } = BenchmarkConfiguration.CreateDefault();
    public SettingsDiff? PreviewDiff { get; set; }
    public IReadOnlyList<string> AffectedFiles { get; set; } = Array.Empty<string>();
    public string? ApplyMessage { get; set; }
    public bool ApplySucceeded { get; set; }
    public bool RestoredSuccessfully { get; set; }

    public BenchmarkComparison? Comparison { get; set; }
    public List<GuidedComparisonRow> ComparisonRows { get; set; } = new();
    public List<GuidedConditionWarning> ConditionWarnings { get; set; } = new();
    public string ClassificationReason { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public bool PreviewOnly { get; set; }
    public List<string> ProgressLog { get; set; } = new();

    public string DisplayTitle =>
        $"{StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {ProfileName ?? ProfileId ?? OptimizationLabel ?? "run"} · {Status}";
}

public static class GuidedOptimizationSchema
{
    /// <summary>v1 = Phase 5; v2 = SelectedSettings/CustomSet; v3 = condition gate fields (backward compatible).</summary>
    public const int CurrentVersion = 3;
    public const int MinReadableVersion = 1;
    public const string FileExtension = ".json";
}

/// <summary>
/// Classification rules (documented):
/// Score only metrics that are available on BOTH runs among:
///   - System CPU average (lower is better)
///   - CS2 process CPU average (lower is better)
///   - System memory % average (lower is better)
///   - CS2 working set MB average (lower is better)
///   - Average frame-time ms (lower is better) — only if measured
/// Noise band: |percent change| &lt; 3% OR |absolute| below metric-specific epsilon → Neutral for that metric.
/// Improved: ≥1 improved, 0 regressed.
/// Regressed: ≥1 regressed, 0 improved.
/// Neutral: all neutral (or no scored metrics beyond noise).
/// Mixed: ≥1 improved and ≥1 regressed.
/// Inconclusive: fewer than 1 scored available metric pair.
/// Never invents FPS or classifies from unavailable frame-time.
/// </summary>
public static class GuidedClassificationRules
{
    public const double PercentNoiseBand = 3.0;
    public const double CpuAbsEpsilon = 1.0;          // percentage points
    public const double MemoryPctAbsEpsilon = 1.0;
    public const double WorkingSetMbAbsEpsilon = 16.0;
    public const double FrameTimeMsAbsEpsilon = 0.25;

    public static GuidedResultClassification Classify(
        IReadOnlyList<GuidedComparisonRow> rows,
        out string reason)
    {
        var scored = rows.Where(r => r.IsAvailable && r.AbsoluteDifference is not null).ToList();
        if (scored.Count == 0)
        {
            reason = "Inconclusive: no overlapping measured metrics to score.";
            return GuidedResultClassification.Inconclusive;
        }

        var improved = 0;
        var regressed = 0;
        var neutral = 0;

        foreach (var row in scored)
        {
            var direction = ClassifyRow(row);
            switch (direction)
            {
                case +1: improved++; break;
                case -1: regressed++; break;
                default: neutral++; break;
            }
        }

        if (improved > 0 && regressed == 0)
        {
            reason = $"Improved: {improved} metric(s) better, {neutral} neutral, 0 regressed (noise band {PercentNoiseBand}%).";
            return GuidedResultClassification.Improved;
        }

        if (regressed > 0 && improved == 0)
        {
            reason = $"Regressed: {regressed} metric(s) worse, {neutral} neutral, 0 improved.";
            return GuidedResultClassification.Regressed;
        }

        if (improved > 0 && regressed > 0)
        {
            reason = $"Mixed: {improved} improved, {regressed} regressed, {neutral} neutral.";
            return GuidedResultClassification.Mixed;
        }

        reason = $"Neutral: all {scored.Count} scored metric(s) within noise band.";
        return GuidedResultClassification.Neutral;
    }

    /// <returns>+1 improved, -1 regressed, 0 neutral</returns>
    public static int ClassifyRow(GuidedComparisonRow row)
    {
        if (!row.IsAvailable || row.Before is null || row.After is null || row.AbsoluteDifference is null)
        {
            return 0;
        }

        var abs = row.AbsoluteDifference.Value;
        var pct = row.PercentDifference;
        var eps = EpsilonFor(row.Metric, row.Unit);

        if (Math.Abs(abs) < eps || (pct is not null && Math.Abs(pct.Value) < PercentNoiseBand))
        {
            return 0;
        }

        // Preferred direction: lower is better for all scored metrics in this phase
        if (abs < 0)
        {
            return +1;
        }

        if (abs > 0)
        {
            return -1;
        }

        return 0;
    }

    public static double EpsilonFor(string metric, string unit)
    {
        if (unit.Equals("ms", StringComparison.OrdinalIgnoreCase) ||
            metric.Contains("frame-time", StringComparison.OrdinalIgnoreCase))
        {
            return FrameTimeMsAbsEpsilon;
        }

        if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase) ||
            metric.Contains("working set", StringComparison.OrdinalIgnoreCase))
        {
            return WorkingSetMbAbsEpsilon;
        }

        if (metric.Contains("memory", StringComparison.OrdinalIgnoreCase))
        {
            return MemoryPctAbsEpsilon;
        }

        return CpuAbsEpsilon;
    }

    public static string InterpretRow(GuidedComparisonRow row)
    {
        if (!row.IsAvailable)
        {
            return "N/A";
        }

        var code = ClassifyRow(row);
        return code switch
        {
            +1 => "Lower after (preferred direction)",
            -1 => "Higher after (against preferred direction)",
            _ => "Within noise / no meaningful change"
        };
    }
}
