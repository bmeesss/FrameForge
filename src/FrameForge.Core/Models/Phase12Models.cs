namespace FrameForge.Core.Models;

/// <summary>Schema for standalone condition report export (local only, no PII).</summary>
public static class ConditionReportExportSchema
{
    public const int CurrentVersion = 1;
    public const string Extension = ".frameforge-condition-report.json";
}

/// <summary>
/// Exportable condition comparison between two benchmark runs.
/// Never includes usernames, emails, IPs, MACs, serials, Steam IDs, or secrets.
/// Missing values stay null — never invented.
/// </summary>
public sealed class ConditionReportExportPackage
{
    public int SchemaVersion { get; set; } = ConditionReportExportSchema.CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Notes { get; set; } =
        "FrameForge local condition report. No PII. Forced comparison is not a valid performance proof.";

    public ConditionReportRunRef? BenchmarkA { get; set; }
    public ConditionReportRunRef? BenchmarkB { get; set; }

    public string? FingerprintA { get; set; }
    public string? FingerprintB { get; set; }

    public ConditionMatchStatus OverallStatus { get; set; } = ConditionMatchStatus.Unknown;
    public ComparisonReliability Reliability { get; set; } = ComparisonReliability.Unknown;
    public string? ReliabilityReason { get; set; }
    public string? Summary { get; set; }

    public List<ConditionFieldResult> Fields { get; set; } = new();
    public List<string> WarningMessages { get; set; } = new();
    public List<string> SevereMismatchMessages { get; set; } = new();

    public bool UserOverrodeMismatch { get; set; }
    public string? OverrideReason { get; set; }
    public string ForcedComparisonNote { get; set; } =
        "A forced comparison does not become a valid performance proof.";

    public List<string> ValidationNotes { get; set; } = new();
}

public sealed class ConditionReportRunRef
{
    public string? RunId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Status { get; set; }
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }
    public long? TotalRamBytes { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public string? DisplayResolution { get; set; }
    public int? DisplayRefreshRateHz { get; set; }
    public string? PowerPlan { get; set; }
    public bool? GameModeEnabled { get; set; }
    public string? ActiveProfileId { get; set; }
    public string? ActiveProfileName { get; set; }
    public int? DurationSeconds { get; set; }
    public int? SampleIntervalMs { get; set; }
    public int? WarmupSeconds { get; set; }
    public string? SystemFingerprintId { get; set; }
    public bool? Cs2ProcessRunningAtStart { get; set; }
}

/// <summary>In-memory history row for Performance / Guided lists with condition metadata.</summary>
public sealed class HistoryReliabilityRow
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public string SettingOrProfile { get; init; } = "—";
    public string SystemLabel { get; init; } = "Unknown system";
    public bool IsCurrentSystem { get; init; }
    public string FingerprintId { get; init; } = string.Empty;
    public ComparisonReliability ConditionReliability { get; init; } = ComparisonReliability.Unknown;
    public ConditionMatchStatus ConditionStatus { get; init; } = ConditionMatchStatus.Unknown;
    public string Classification { get; init; } = "—";
    public string Confidence { get; init; } = "—";
    public string? ProfileName { get; init; }
    public GuidedOptimizationRun? SourceRun { get; init; }
    public SettingPerformanceRecord? SourceRecord { get; init; }

    public string DisplayDate => Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string DisplaySystem => IsCurrentSystem ? "Current system" : "Different system";
    public string DisplayReliability => ConditionReliability.ToString();
    public string DisplayCondition => ConditionStatus.ToString();
    public string ShortSummary =>
        $"{DisplayDate} · {SettingOrProfile} · {DisplaySystem} · {DisplayReliability}/{DisplayCondition} · {Classification} · conf {Confidence}";
}

/// <summary>Builds condition report packages (pure).</summary>
public static class ConditionReportExporter
{
    public static ConditionReportExportPackage Build(
        BenchmarkRun a,
        BenchmarkRun b,
        BenchmarkConditionReport? report = null,
        bool userOverrode = false,
        string? overrideReason = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        report ??= BenchmarkConditionRules.Analyze(a, b, forcedDespiteMismatch: userOverrode);

        var notes = new List<string>();
        if (a.SystemInformation is null) notes.Add("Benchmark A system snapshot missing.");
        if (b.SystemInformation is null) notes.Add("Benchmark B system snapshot missing.");

        return new ConditionReportExportPackage
        {
            SchemaVersion = ConditionReportExportSchema.CurrentVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            BenchmarkA = FromRun(a),
            BenchmarkB = FromRun(b),
            FingerprintA = SanitizeFp(a.SystemInformation?.SystemFingerprintId),
            FingerprintB = SanitizeFp(b.SystemInformation?.SystemFingerprintId),
            OverallStatus = report.OverallStatus,
            Reliability = report.Reliability,
            ReliabilityReason = report.ReliabilityReason,
            Summary = report.Summary,
            Fields = report.Fields.ToList(),
            WarningMessages = report.Fields
                .Where(f => f.Status == ConditionMatchStatus.Warning)
                .Select(f => f.Message).ToList(),
            SevereMismatchMessages = report.Fields
                .Where(f => f.Status == ConditionMatchStatus.SevereMismatch)
                .Select(f => f.Message).ToList(),
            UserOverrodeMismatch = userOverrode || report.ForcedDespiteMismatch,
            OverrideReason = overrideReason ?? (report.ForcedDespiteMismatch ? report.ForcedNote : null),
            ValidationNotes = notes
        };
    }

    public static bool LooksLikePii(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Reuse fingerprint service heuristics when available via simple patterns
        if (value.Contains('@')) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"\b\d{1,3}(\.\d{1,3}){3}\b")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}")) return true;
        return false;
    }

    private static string? SanitizeFp(string? fp) =>
        string.IsNullOrWhiteSpace(fp) || LooksLikePii(fp) ? null : fp.Trim();

    private static string? SanitizeLabel(string? v)
    {
        if (string.IsNullOrWhiteSpace(v) || LooksLikePii(v)) return null;
        return v.Trim();
    }

    private static ConditionReportRunRef FromRun(BenchmarkRun run)
    {
        var s = run.SystemInformation ?? new BenchmarkSystemSnapshot();
        var c = run.Configuration ?? BenchmarkConfiguration.CreateDefault();
        return new ConditionReportRunRef
        {
            RunId = run.Id,
            StartedAt = run.StartedAt == default ? null : run.StartedAt,
            Status = run.Status.ToString(),
            CpuName = SanitizeLabel(s.CpuName),
            GpuName = SanitizeLabel(s.GpuName),
            TotalRamBytes = s.TotalRamBytes > 0 ? s.TotalRamBytes : null,
            OsVersion = SanitizeLabel(s.OsVersion),
            Architecture = SanitizeLabel(s.Architecture),
            DisplayResolution = SanitizeLabel(s.DisplayResolution),
            DisplayRefreshRateHz = s.DisplayRefreshRateHz,
            PowerPlan = SanitizeLabel(s.PowerPlan),
            GameModeEnabled = s.GameModeEnabled,
            ActiveProfileId = SanitizeLabel(s.ActiveProfileId),
            ActiveProfileName = SanitizeLabel(s.ActiveProfileName),
            DurationSeconds = c.DurationSeconds,
            SampleIntervalMs = c.SampleIntervalMs,
            WarmupSeconds = c.WarmupSeconds,
            SystemFingerprintId = SanitizeFp(s.SystemFingerprintId),
            Cs2ProcessRunningAtStart = s.Cs2ProcessRunningAtStart
        };
    }
}
