namespace FrameForge.Core.Models;

/// <summary>
/// Per-field or overall condition match between two benchmark runs.
/// Missing metadata on either side → <see cref="Unknown"/> (not a mismatch).
/// </summary>
public enum ConditionMatchStatus
{
    /// <summary>Both sides present and equal (or equivalent).</summary>
    Match = 0,

    /// <summary>Present on both sides and differ in a non-critical way.</summary>
    Warning = 1,

    /// <summary>
    /// Present on both sides and differ in a way that invalidates automatic
    /// Improved/Regressed classification (different machine or bench config).
    /// </summary>
    SevereMismatch = 2,

    /// <summary>One or both sides missing — not treated as mismatch.</summary>
    Unknown = 3
}

/// <summary>Human-readable reliability of a comparison (not statistical confidence).</summary>
public enum ComparisonReliability
{
    High = 0,
    Medium = 1,
    Low = 2,
    Unknown = 3
}

/// <summary>One compared environment field.</summary>
public sealed class ConditionFieldResult
{
    public string Field { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ConditionMatchStatus Status { get; init; } = ConditionMatchStatus.Unknown;
    public string? ValueA { get; init; }
    public string? ValueB { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsSevere => Status == ConditionMatchStatus.SevereMismatch;
}

/// <summary>
/// Full condition analysis for two benchmark runs.
/// Documented severe rules (all require both sides present):
/// <list type="bullet">
/// <item>Different SystemFingerprintId</item>
/// <item>Different CPU name</item>
/// <item>Different GPU name</item>
/// <item>Different DurationSeconds, SampleIntervalMs, or WarmupSeconds</item>
/// </list>
/// Warning (non-severe) when both present and differ: RAM, OS, profile, CS2 process,
/// resolution, refresh rate, power plan, Game Mode.
/// </summary>
public sealed class BenchmarkConditionReport
{
    public ConditionMatchStatus OverallStatus { get; init; } = ConditionMatchStatus.Unknown;
    public ComparisonReliability Reliability { get; init; } = ComparisonReliability.Unknown;
    public List<ConditionFieldResult> Fields { get; init; } = new();
    public List<BenchmarkConditionWarning> Warnings { get; init; } = new();
    public bool HasSevereMismatch => OverallStatus == ConditionMatchStatus.SevereMismatch;
    public bool RequiresExplicitOverride => HasSevereMismatch;
    public string Summary { get; init; } = string.Empty;
    public string ReliabilityReason { get; init; } = string.Empty;

    /// <summary>User chose Compare anyway after severe mismatch.</summary>
    public bool ForcedDespiteMismatch { get; init; }

    public string ForcedNote =>
        ForcedDespiteMismatch
            ? "Comparison performed despite condition mismatch."
            : string.Empty;
}

/// <summary>
/// Options for building a comparison when conditions may not match.
/// </summary>
public sealed class BenchmarkCompareOptions
{
    /// <summary>
    /// When true and overall status is SevereMismatch, still produce metric deltas
    /// but force classification to Inconclusive and stamp forced note.
    /// When false, severe mismatch blocks Improved/Regressed classification.
    /// </summary>
    public bool ForceCompareDespiteSevereMismatch { get; init; }
}

/// <summary>Result of validating a benchmark run before save / complete.</summary>
public sealed class BenchmarkIntegrityResult
{
    public bool IsValid { get; init; }
    public List<string> Issues { get; init; } = new();

    public static BenchmarkIntegrityResult Ok() => new() { IsValid = true };

    public static BenchmarkIntegrityResult Fail(params string[] issues) => new()
    {
        IsValid = false,
        Issues = issues.Where(i => !string.IsNullOrWhiteSpace(i)).ToList()
    };
}

/// <summary>
/// Import confirmation dialog state (validation already done). Default action = Cancel.
/// </summary>
public sealed class ImportConfirmDialogState
{
    public string Title { get; init; } = "Confirm import";
    public string SourcePath { get; init; } = string.Empty;
    public string PackageKind { get; init; } = "Intelligence";
    public string Summary { get; init; } = string.Empty;
    public List<string> ConflictLines { get; init; } = new();
    public List<string> FingerprintLines { get; init; } = new();
    public List<string> InvalidLines { get; init; } = new();
    public int RecordsToAdd { get; init; }
    public int RecordsToUpdate { get; init; }
    public int EvidenceToAdd { get; init; }
    public int SnapshotsToAdd { get; init; }
    public bool CanMerge { get; init; } = true;
    public bool CanImportAsNew { get; init; } = true;

    /// <summary>Result of dialog: null = Cancel (default).</summary>
    public IntelligenceImportMode? ChosenMode { get; set; }
}

/// <summary>
/// Pure condition matching + integrity helpers (no I/O).
/// </summary>
public static class BenchmarkConditionRules
{
    public const string SevereRuleDoc =
        "Severe mismatch (both sides present): different SystemFingerprintId; different CPU; " +
        "different GPU; different DurationSeconds / SampleIntervalMs / WarmupSeconds. " +
        "Missing metadata → Unknown (not severe). " +
        "Automatic Improved/Regressed is blocked on severe mismatch unless the user forces compare.";

    public static BenchmarkConditionReport Analyze(
        BenchmarkRun a,
        BenchmarkRun b,
        bool forcedDespiteMismatch = false)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var sa = a.SystemInformation ?? new BenchmarkSystemSnapshot();
        var sb = b.SystemInformation ?? new BenchmarkSystemSnapshot();
        var ca = a.Configuration ?? BenchmarkConfiguration.CreateDefault();
        var cb = b.Configuration ?? BenchmarkConfiguration.CreateDefault();

        var fields = new List<ConditionFieldResult>
        {
            Field("fingerprint", "System fingerprint",
                sa.SystemFingerprintId, sb.SystemFingerprintId, severeIfDiffer: true),
            Field("cpu", "CPU", NullIfUnknown(sa.CpuName), NullIfUnknown(sb.CpuName), severeIfDiffer: true),
            Field("gpu", "GPU", NullIfUnknown(sa.GpuName), NullIfUnknown(sb.GpuName), severeIfDiffer: true),
            Field("ram", "RAM",
                sa.TotalRamBytes > 0 ? sa.TotalRamBytes.ToString() : null,
                sb.TotalRamBytes > 0 ? sb.TotalRamBytes.ToString() : null,
                severeIfDiffer: false),
            Field("os", "OS", NullIfUnknown(sa.OsVersion), NullIfUnknown(sb.OsVersion), severeIfDiffer: false),
            Field("cs2-process", "CS2 process at start",
                sa.Cs2ProcessRunningAtStart.ToString(),
                sb.Cs2ProcessRunningAtStart.ToString(),
                severeIfDiffer: false),
            Field("profile", "FrameForge profile",
                sa.ActiveProfileId ?? sa.ActiveProfileName,
                sb.ActiveProfileId ?? sb.ActiveProfileName,
                severeIfDiffer: false),
            Field("duration", "Benchmark duration",
                ca.DurationSeconds.ToString(), cb.DurationSeconds.ToString(), severeIfDiffer: true),
            Field("interval", "Sampling interval",
                ca.SampleIntervalMs.ToString(), cb.SampleIntervalMs.ToString(), severeIfDiffer: true),
            Field("warmup", "Warm-up",
                ca.WarmupSeconds.ToString(), cb.WarmupSeconds.ToString(), severeIfDiffer: true),
            Field("resolution", "Display resolution",
                sa.DisplayResolution, sb.DisplayResolution, severeIfDiffer: false),
            Field("refresh", "Refresh rate",
                sa.DisplayRefreshRateHz?.ToString(), sb.DisplayRefreshRateHz?.ToString(), severeIfDiffer: false),
            Field("power", "Power plan",
                sa.PowerPlan, sb.PowerPlan, severeIfDiffer: false),
            Field("gamemode", "Game Mode",
                sa.GameModeEnabled?.ToString(), sb.GameModeEnabled?.ToString(), severeIfDiffer: false)
        };

        var overall = Aggregate(fields);
        var reliability = ToReliability(overall, fields);
        var warnings = fields
            .Where(f => f.Status is ConditionMatchStatus.Warning or ConditionMatchStatus.SevereMismatch)
            .Select(f => new BenchmarkConditionWarning
            {
                Code = f.Field,
                Message = f.Message,
                IsBlocking = f.Status == ConditionMatchStatus.SevereMismatch
            })
            .ToList();

        var summary = overall switch
        {
            ConditionMatchStatus.Match => "All compared conditions match (or are Unknown).",
            ConditionMatchStatus.Warning => "Benchmark conditions differ (warnings only).",
            ConditionMatchStatus.SevereMismatch =>
                "Severe condition mismatch — automatic Improved/Regressed classification is blocked.",
            _ => "Condition match unknown (insufficient metadata)."
        };

        if (forcedDespiteMismatch && overall == ConditionMatchStatus.SevereMismatch)
        {
            summary += " Comparison performed despite condition mismatch.";
        }

        return new BenchmarkConditionReport
        {
            OverallStatus = overall,
            Reliability = reliability,
            Fields = fields,
            Warnings = warnings,
            ForcedDespiteMismatch = forcedDespiteMismatch && overall == ConditionMatchStatus.SevereMismatch,
            Summary = summary,
            ReliabilityReason = ReliabilityReason(reliability, fields)
        };
    }

    /// <summary>
    /// Apply condition rules to a provisional classification.
    /// Severe mismatch without force → Inconclusive.
    /// Forced severe still keeps metrics but classification becomes Inconclusive with forced note.
    /// </summary>
    public static GuidedResultClassification ApplyClassificationGate(
        GuidedResultClassification provisional,
        BenchmarkConditionReport report,
        out string reasonSuffix)
    {
        if (report.OverallStatus != ConditionMatchStatus.SevereMismatch)
        {
            reasonSuffix = string.Empty;
            return provisional;
        }

        if (provisional is GuidedResultClassification.Improved or GuidedResultClassification.Regressed
            or GuidedResultClassification.Mixed or GuidedResultClassification.Neutral)
        {
            reasonSuffix = report.ForcedDespiteMismatch
                ? " Comparison performed despite condition mismatch; classification forced to Inconclusive."
                : " Severe condition mismatch blocked Improved/Regressed; classification is Inconclusive.";
            return GuidedResultClassification.Inconclusive;
        }

        reasonSuffix = report.ForcedDespiteMismatch
            ? " Comparison performed despite condition mismatch."
            : string.Empty;
        return provisional;
    }

    public static BenchmarkIntegrityResult ValidateCompletedRun(BenchmarkRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var issues = new List<string>();

        if (run.Status is not (BenchmarkStatus.Completed or BenchmarkStatus.Failed
            or BenchmarkStatus.Cancelled or BenchmarkStatus.Stopping))
        {
            // still validate structure for completed path
        }

        if (string.IsNullOrWhiteSpace(run.Id))
        {
            issues.Add("Run id is missing.");
        }

        if (run.StartedAt == default)
        {
            issues.Add("StartedAt is missing.");
        }

        if (run.EndedAt is null)
        {
            issues.Add("EndedAt is missing for a finished run.");
        }
        else if (run.EndedAt < run.StartedAt)
        {
            issues.Add("EndedAt is before StartedAt.");
        }

        var cfg = run.Configuration ?? BenchmarkConfiguration.CreateDefault();
        var cfgValidation = BenchmarkConfiguration.Validate(cfg);
        if (!cfgValidation.IsValid)
        {
            issues.AddRange(cfgValidation.Issues.Select(i => "Configuration: " + i));
        }

        if (run.Samples is null)
        {
            issues.Add("Samples collection is null.");
        }
        else if (run.Status == BenchmarkStatus.Completed && run.Samples.Count == 0)
        {
            issues.Add("Completed run has zero samples.");
        }
        else if (run.Status == BenchmarkStatus.Completed)
        {
            var measured = run.Samples.Count(s => !s.IsWarmup);
            if (measured == 0)
            {
                issues.Add("Completed run has no measured (non-warmup) samples.");
            }

            // Timestamps / elapsed monotonicity (best-effort)
            double prev = -1;
            foreach (var s in run.Samples)
            {
                if (s.ElapsedMs < 0)
                {
                    issues.Add("Sample ElapsedMs is negative.");
                    break;
                }

                if (prev >= 0 && s.ElapsedMs + 0.001 < prev)
                {
                    issues.Add("Sample ElapsedMs is not monotonic.");
                    break;
                }

                prev = s.ElapsedMs;
            }
        }

        if (run.DurationSecondsActual < 0)
        {
            issues.Add("DurationSecondsActual is negative.");
        }

        if (run.Status == BenchmarkStatus.Completed &&
            run.DurationSecondsActual > 0 &&
            cfg.DurationSeconds > 0 &&
            run.DurationSecondsActual > cfg.DurationSeconds * 3 + 30)
        {
            issues.Add("DurationSecondsActual is unreasonably larger than configured duration.");
        }

        if (run.SystemInformation is null)
        {
            issues.Add("System snapshot is missing.");
        }

        // Fingerprint optional — null is allowed
        // CS2 state is informational only

        if (run.Status == BenchmarkStatus.Completed && run.Result is null)
        {
            issues.Add("Completed run is missing Result aggregates.");
        }

        return issues.Count == 0
            ? BenchmarkIntegrityResult.Ok()
            : BenchmarkIntegrityResult.Fail(issues.ToArray());
    }

    private static ConditionFieldResult Field(
        string code,
        string display,
        string? a,
        string? b,
        bool severeIfDiffer)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return new ConditionFieldResult
            {
                Field = code,
                DisplayName = display,
                Status = ConditionMatchStatus.Unknown,
                ValueA = a,
                ValueB = b,
                Message = $"{display}: Unknown (missing on one or both runs)."
            };
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new ConditionFieldResult
            {
                Field = code,
                DisplayName = display,
                Status = ConditionMatchStatus.Match,
                ValueA = a,
                ValueB = b,
                Message = $"{display}: Match."
            };
        }

        var status = severeIfDiffer ? ConditionMatchStatus.SevereMismatch : ConditionMatchStatus.Warning;
        return new ConditionFieldResult
        {
            Field = code,
            DisplayName = display,
            Status = status,
            ValueA = a,
            ValueB = b,
            Message = status == ConditionMatchStatus.SevereMismatch
                ? $"{display}: Severe mismatch ('{a}' vs '{b}')."
                : $"{display}: Warning ('{a}' vs '{b}')."
        };
    }

    private static string? NullIfUnknown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Unknown CPU", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Unknown GPU", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("GPU (name unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return v;
    }

    private static ConditionMatchStatus Aggregate(IReadOnlyList<ConditionFieldResult> fields)
    {
        if (fields.Any(f => f.Status == ConditionMatchStatus.SevereMismatch))
        {
            return ConditionMatchStatus.SevereMismatch;
        }

        if (fields.Any(f => f.Status == ConditionMatchStatus.Warning))
        {
            return ConditionMatchStatus.Warning;
        }

        if (fields.Any(f => f.Status == ConditionMatchStatus.Match))
        {
            return ConditionMatchStatus.Match;
        }

        return ConditionMatchStatus.Unknown;
    }

    private static ComparisonReliability ToReliability(
        ConditionMatchStatus overall,
        IReadOnlyList<ConditionFieldResult> fields)
    {
        var known = fields.Count(f => f.Status != ConditionMatchStatus.Unknown);
        if (known == 0)
        {
            return ComparisonReliability.Unknown;
        }

        return overall switch
        {
            ConditionMatchStatus.Match when known >= 5 => ComparisonReliability.High,
            ConditionMatchStatus.Match => ComparisonReliability.Medium,
            ConditionMatchStatus.Warning => ComparisonReliability.Medium,
            ConditionMatchStatus.SevereMismatch => ComparisonReliability.Low,
            _ => ComparisonReliability.Unknown
        };
    }

    private static string ReliabilityReason(ComparisonReliability r, IReadOnlyList<ConditionFieldResult> fields)
    {
        var known = fields.Count(f => f.Status != ConditionMatchStatus.Unknown);
        var severe = fields.Count(f => f.Status == ConditionMatchStatus.SevereMismatch);
        var warn = fields.Count(f => f.Status == ConditionMatchStatus.Warning);
        return r switch
        {
            ComparisonReliability.High =>
                $"High reliability heuristic: {known} known matching fields, no warnings. Not statistical confidence.",
            ComparisonReliability.Medium =>
                $"Medium reliability heuristic: {known} known field(s), warnings={warn}. Not statistical confidence.",
            ComparisonReliability.Low =>
                $"Low reliability heuristic: severe mismatches={severe}. Not statistical confidence.",
            _ => "Unknown reliability — insufficient overlapping metadata. Not statistical confidence."
        };
    }
}
