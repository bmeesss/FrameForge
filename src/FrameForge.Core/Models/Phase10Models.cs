namespace FrameForge.Core.Models;

/// <summary>
/// UI-facing comparison row: Metric | Before | After | Delta | Interpretation.
/// Unavailable metrics never show zero as a substitute.
/// </summary>
public sealed class BenchmarkComparisonDisplayRow
{
    public string Metric { get; init; } = string.Empty;
    public string BeforeText { get; init; } = "Unavailable";
    public string AfterText { get; init; } = "Unavailable";
    public string DeltaText { get; init; } = "Unavailable";
    public string Interpretation { get; init; } = "Unavailable";
    public bool IsAvailable { get; init; }
    public string? Notes { get; init; }
    public string Unit { get; init; } = string.Empty;
}

/// <summary>Environment mismatch warning when comparing two benchmark runs.</summary>
public sealed class BenchmarkConditionWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsBlocking { get; init; }
}

/// <summary>Result of comparing two runs including condition analysis.</summary>
public sealed class BenchmarkComparisonReport
{
    public BenchmarkComparison Comparison { get; init; } = new();
    public List<BenchmarkComparisonDisplayRow> Rows { get; init; } = new();
    public List<BenchmarkConditionWarning> ConditionWarnings { get; init; } = new();
    public bool HasConditionWarnings => ConditionWarnings.Count > 0;
    public string Summary { get; init; } = string.Empty;
}

/// <summary>View-model friendly import preview (validation only — no writes).</summary>
public sealed class ImportPreviewViewState
{
    public bool IsVisible { get; set; }
    public bool IsValid { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string PackageKind { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = new();
    public int FilesDetected { get; init; } = 1;
    public int Records { get; init; }
    public int DirectEvidence { get; init; }
    public int MultiSettingEvidence { get; init; }
    public int Fingerprints { get; init; }
    public int NewRecords { get; init; }
    public int ExistingConflicts { get; init; }
    public int SkippedRecords { get; init; }
    public int InvalidRecords { get; init; }
    public int SnapshotsTotal { get; init; }
    public int SnapshotsValid { get; init; }
    public int SnapshotsInvalid { get; init; }
    public int SnapshotsConflicting { get; init; }
    public List<string> DifferentFingerprints { get; init; } = new();
    public List<string> ConflictDetails { get; init; } = new();
    public IntelligenceImportMode ProposedMode { get; set; } = IntelligenceImportMode.Merge;
    public IntelligenceImportPreview? SourcePreview { get; init; }

    public string SummaryText
    {
        get
        {
            if (PackageKind.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
            {
                return $"Snapshots: {SnapshotsTotal} total · valid {SnapshotsValid} · invalid {SnapshotsInvalid} · conflicting {SnapshotsConflicting}";
            }

            return
                $"Records {Records} (new {NewRecords}, conflicts {ExistingConflicts}, skip {SkippedRecords}, invalid {InvalidRecords}) · " +
                $"Evidence direct {DirectEvidence} / multi {MultiSettingEvidence} · Fingerprints {Fingerprints}";
        }
    }
}

/// <summary>Filter/sort options for Performance History (in-memory over cached index).</summary>
public enum PerformanceHistorySystemFilter
{
    CurrentSystem,
    OtherSystems,
    AllSystems
}

public enum PerformanceHistorySortKind
{
    Latest,
    MostTested,
    BestResult,
    WorstResult
}

/// <summary>Aggregated detail for one setting on the Performance page.</summary>
public sealed class SettingDetailViewState
{
    public string SettingKey { get; init; } = string.Empty;
    public string SettingName { get; init; } = string.Empty;
    public string? CurrentValue { get; init; }
    public string? RecommendedValue { get; init; }
    public string LastMeasuredResult { get; init; } = "—";
    public int TestCount { get; init; }
    public int DirectTestCount { get; init; }
    public int AssociatedTestCount { get; init; }
    public string Confidence { get; init; } = "Unknown";
    public string ConfidenceReason { get; init; } = string.Empty;
    public string RecommendationSummary { get; init; } = string.Empty;
    public string FingerprintLabel { get; init; } = string.Empty;
    public DateTimeOffset? LatestTestAt { get; init; }
    public string RestoreState { get; init; } = "Not assessed";
    public bool CanRestore { get; init; }
    public bool HasEvidence { get; init; }

    public string DisplayCurrent => string.IsNullOrWhiteSpace(CurrentValue) ? "—" : CurrentValue!;
    public string DisplayRecommended => string.IsNullOrWhiteSpace(RecommendedValue) ? "—" : RecommendedValue!;
}

/// <summary>
/// Builds human-readable comparison rows and condition warnings from two runs.
/// Pure helpers — no I/O.
/// </summary>
public static class BenchmarkComparisonPresenter
{
    public static BenchmarkComparisonReport Build(
        BenchmarkComparison comparison,
        string? fingerprintA = null,
        string? fingerprintB = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var rows = comparison.Metrics.Select(ToDisplayRow).ToList();
        var warnings = AnalyzeConditions(comparison.RunA, comparison.RunB, fingerprintA, fingerprintB).ToList();
        var summary = comparison.Summary;
        if (warnings.Count > 0)
        {
            summary += " Benchmark conditions differ — see warnings.";
        }

        return new BenchmarkComparisonReport
        {
            Comparison = comparison,
            Rows = rows,
            ConditionWarnings = warnings,
            Summary = summary
        };
    }

    public static BenchmarkComparisonDisplayRow ToDisplayRow(BenchmarkComparisonMetric m)
    {
        if (!m.IsAvailable || m.Before is null || m.After is null)
        {
            return new BenchmarkComparisonDisplayRow
            {
                Metric = m.Metric,
                Unit = m.Unit,
                BeforeText = "Unavailable",
                AfterText = "Unavailable",
                DeltaText = "Unavailable",
                Interpretation = "Unavailable",
                IsAvailable = false,
                Notes = m.Notes ?? "Metric unavailable on one or both runs."
            };
        }

        var unit = m.Unit ?? string.Empty;
        var beforeText = FormatValue(m.Before.Value, unit);
        var afterText = FormatValue(m.After.Value, unit);
        var delta = m.Difference ?? (m.After.Value - m.Before.Value);
        var deltaText = FormatDelta(delta, unit);
        var interpretation = Interpret(m.Metric, delta, unit);

        return new BenchmarkComparisonDisplayRow
        {
            Metric = m.Metric,
            Unit = unit,
            BeforeText = beforeText,
            AfterText = afterText,
            DeltaText = deltaText,
            Interpretation = interpretation,
            IsAvailable = true,
            Notes = m.Notes
        };
    }

    public static IEnumerable<BenchmarkConditionWarning> AnalyzeConditions(
        BenchmarkRun a,
        BenchmarkRun b,
        string? fingerprintA = null,
        string? fingerprintB = null)
    {
        var sa = a.SystemInformation;
        var sb = b.SystemInformation;
        var ca = a.Configuration;
        var cb = b.Configuration;

        if (!string.Equals(sa.CpuName, sb.CpuName, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("cpu", $"CPU differs: '{sa.CpuName}' vs '{sb.CpuName}'.");
        }

        if (!string.Equals(sa.GpuName, sb.GpuName, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("gpu", $"GPU differs: '{sa.GpuName}' vs '{sb.GpuName}'.");
        }

        if (sa.TotalRamBytes > 0 && sb.TotalRamBytes > 0 && sa.TotalRamBytes != sb.TotalRamBytes)
        {
            yield return Warn("ram", "Reported RAM total differs between runs.");
        }

        if (!string.Equals(sa.OsVersion, sb.OsVersion, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("os", $"OS differs: '{sa.OsVersion}' vs '{sb.OsVersion}'.");
        }

        if (!string.Equals(sa.ActiveProfileId ?? sa.ActiveProfileName, sb.ActiveProfileId ?? sb.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("profile",
                $"FrameForge profile differs: '{sa.ActiveProfileName ?? sa.ActiveProfileId ?? "—"}' vs '{sb.ActiveProfileName ?? sb.ActiveProfileId ?? "—"}'.");
        }

        if (ca.DurationSeconds != cb.DurationSeconds)
        {
            yield return Warn("duration", $"Benchmark durations differ ({ca.DurationSeconds}s vs {cb.DurationSeconds}s).");
        }

        if (ca.SampleIntervalMs != cb.SampleIntervalMs)
        {
            yield return Warn("interval", $"Sampling intervals differ ({ca.SampleIntervalMs}ms vs {cb.SampleIntervalMs}ms).");
        }

        if (ca.WarmupSeconds != cb.WarmupSeconds)
        {
            yield return Warn("warmup", $"Warm-up differs ({ca.WarmupSeconds}s vs {cb.WarmupSeconds}s).");
        }

        if (sa.Cs2ProcessRunningAtStart != sb.Cs2ProcessRunningAtStart)
        {
            yield return Warn("cs2-process", "CS2 process presence at start differs between runs.");
        }

        if (!string.IsNullOrWhiteSpace(fingerprintA) &&
            !string.IsNullOrWhiteSpace(fingerprintB) &&
            !fingerprintA.Equals(fingerprintB, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("fingerprint", "Runs were performed on different hardware fingerprints.");
        }

        if (sa.DisplayResolution is not null && sb.DisplayResolution is not null &&
            !string.Equals(sa.DisplayResolution, sb.DisplayResolution, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("display-res", $"Display resolution differs: {sa.DisplayResolution} vs {sb.DisplayResolution}.");
        }

        if (sa.DisplayRefreshRateHz is not null && sb.DisplayRefreshRateHz is not null &&
            sa.DisplayRefreshRateHz != sb.DisplayRefreshRateHz)
        {
            yield return Warn("display-hz", $"Display refresh rate differs: {sa.DisplayRefreshRateHz} Hz vs {sb.DisplayRefreshRateHz} Hz.");
        }

        if (sa.PowerPlan is not null && sb.PowerPlan is not null &&
            !string.Equals(sa.PowerPlan, sb.PowerPlan, StringComparison.OrdinalIgnoreCase))
        {
            yield return Warn("power", $"Power plan differs: '{sa.PowerPlan}' vs '{sb.PowerPlan}'.");
        }

        if (sa.GameModeEnabled is not null && sb.GameModeEnabled is not null &&
            sa.GameModeEnabled != sb.GameModeEnabled)
        {
            yield return Warn("gamemode", "Windows Game Mode status differs between runs.");
        }

        if (a.Status != b.Status)
        {
            yield return Warn("status", $"Run status differs: {a.Status} vs {b.Status}.");
        }
    }

    private static BenchmarkConditionWarning Warn(string code, string message) => new()
    {
        Code = code,
        Message = message,
        IsBlocking = false
    };

    private static string FormatValue(double v, string unit)
    {
        if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(v) >= 1024)
            {
                return $"{v / 1024.0:0.00} GB";
            }

            return $"{v:0.0} MB";
        }

        if (unit.Equals("%", StringComparison.OrdinalIgnoreCase))
        {
            return $"{v:0.#}%";
        }

        if (unit.Equals("ms", StringComparison.OrdinalIgnoreCase))
        {
            return $"{v:0.##} ms";
        }

        if (unit.Equals("FPS", StringComparison.OrdinalIgnoreCase))
        {
            return $"{v:0.#} FPS";
        }

        return $"{v:0.##} {unit}".Trim();
    }

    private static string FormatDelta(double delta, string unit)
    {
        var sign = delta > 0 ? "+" : string.Empty;
        if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(delta) >= 1024)
            {
                return $"{sign}{delta / 1024.0:0.00} GB";
            }

            return $"{sign}{delta:0.0} MB";
        }

        if (unit.Equals("%", StringComparison.OrdinalIgnoreCase))
        {
            return $"{sign}{delta:0.#} percentage points";
        }

        return $"{sign}{delta:0.##} {unit}".Trim();
    }

    private static string Interpret(string metric, double delta, string unit)
    {
        var abs = Math.Abs(delta);
        var lowerIsBetter =
            metric.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
            metric.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
            metric.Contains("working set", StringComparison.OrdinalIgnoreCase) ||
            metric.Contains("frame-time", StringComparison.OrdinalIgnoreCase) ||
            metric.Contains("frame time", StringComparison.OrdinalIgnoreCase);

        var higherIsBetter =
            metric.Contains("FPS", StringComparison.OrdinalIgnoreCase) &&
            !metric.Contains("frame", StringComparison.OrdinalIgnoreCase);

        string Mag()
        {
            if (unit == "%" && abs < 0.5) return "Negligible change";
            if (unit == "MB" && abs < 20) return "Slightly ";
            if (unit == "%" && abs < 3) return "Slightly ";
            if (abs < 1e-6) return "Unchanged";
            return string.Empty;
        }

        if (abs < 1e-9)
        {
            return "Unchanged";
        }

        if (higherIsBetter)
        {
            return Mag() + (delta > 0 ? "Higher" : "Lower");
        }

        if (lowerIsBetter)
        {
            return Mag() + (delta < 0 ? "Lower" : "Higher");
        }

        return Mag() + (delta < 0 ? "Lower" : "Higher");
    }
}
