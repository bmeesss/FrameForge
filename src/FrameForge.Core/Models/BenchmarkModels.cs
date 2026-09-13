namespace FrameForge.Core.Models;

/// <summary>Benchmark session lifecycle.</summary>
public enum BenchmarkStatus
{
    Idle,
    Preparing,
    Running,
    Stopping,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Validated configuration for a benchmark session.</summary>
public sealed class BenchmarkConfiguration
{
    public const int DefaultDurationSeconds = 60;
    public const int DefaultWarmupSeconds = 10;
    public const int DefaultSampleIntervalMs = 100;
    public const int MinSampleIntervalMs = 50;
    public const int MaxSampleIntervalMs = 1000;
    public const int MinDurationSeconds = 30;
    public const int MaxDurationSeconds = 120;
    public const int MinWarmupSeconds = 0;
    public const int MaxWarmupSeconds = 60;

    public static readonly int[] AllowedDurationsSeconds = [30, 60, 120];

    public int DurationSeconds { get; init; } = DefaultDurationSeconds;
    public int WarmupSeconds { get; init; } = DefaultWarmupSeconds;
    public int SampleIntervalMs { get; init; } = DefaultSampleIntervalMs;
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public string? Label { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
    public TimeSpan Warmup => TimeSpan.FromSeconds(WarmupSeconds);
    public TimeSpan SampleInterval => TimeSpan.FromMilliseconds(SampleIntervalMs);

    public static BenchmarkConfiguration CreateDefault() => new();

    public static BenchmarkConfigurationValidationResult Validate(BenchmarkConfiguration config)
    {
        var issues = new List<string>();
        if (config is null)
        {
            return new BenchmarkConfigurationValidationResult { Issues = ["Configuration is null."] };
        }

        if (!AllowedDurationsSeconds.Contains(config.DurationSeconds))
        {
            issues.Add(
                $"Duration must be one of: {string.Join(", ", AllowedDurationsSeconds)} seconds (got {config.DurationSeconds}).");
        }

        if (config.SampleIntervalMs < MinSampleIntervalMs || config.SampleIntervalMs > MaxSampleIntervalMs)
        {
            issues.Add(
                $"Sample interval must be between {MinSampleIntervalMs} and {MaxSampleIntervalMs} ms (got {config.SampleIntervalMs}).");
        }

        if (config.WarmupSeconds < MinWarmupSeconds || config.WarmupSeconds > MaxWarmupSeconds)
        {
            issues.Add(
                $"Warm-up must be between {MinWarmupSeconds} and {MaxWarmupSeconds} seconds (got {config.WarmupSeconds}).");
        }

        if (config.WarmupSeconds >= config.DurationSeconds && config.DurationSeconds > 0)
        {
            issues.Add("Warm-up must be shorter than total duration.");
        }

        return new BenchmarkConfigurationValidationResult { Issues = issues };
    }
}

public sealed class BenchmarkConfigurationValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}

/// <summary>One external performance sample (never from CS2 memory).</summary>
public sealed class BenchmarkSample
{
    public DateTimeOffset Timestamp { get; init; }
    public double ElapsedMs { get; init; }

    /// <summary>True during warm-up; excluded from final aggregates.</summary>
    public bool IsWarmup { get; init; }

    /// <summary>System-wide CPU utilization 0–100, or null if unavailable.</summary>
    public double? SystemCpuPercent { get; init; }

    /// <summary>CS2 process CPU percent (normalized by logical cores), or null.</summary>
    public double? ProcessCpuPercent { get; init; }

    /// <summary>Working set of CS2 process in bytes, or null.</summary>
    public long? ProcessWorkingSetBytes { get; init; }

    /// <summary>System memory in use (bytes), or null.</summary>
    public long? SystemMemoryUsedBytes { get; init; }

    /// <summary>System memory used percent 0–100, or null.</summary>
    public double? SystemMemoryPercent { get; init; }

    /// <summary>GPU utilization 0–100 when a safe OS source exists; otherwise null.</summary>
    public double? GpuUtilizationPercent { get; init; }

    /// <summary>CPU package temperature °C when reliably available; otherwise null.</summary>
    public double? CpuTemperatureC { get; init; }

    /// <summary>GPU temperature °C when reliably available; otherwise null.</summary>
    public double? GpuTemperatureC { get; init; }

    /// <summary>
    /// Frame time in milliseconds. Only set when a valid external frame-time source exists.
    /// FrameForge does not inject into CS2, so this is typically null.
    /// </summary>
    public double? FrameTimeMs { get; init; }

    public int? Cs2ProcessId { get; init; }
    public bool Cs2ProcessPresent { get; init; }
}

/// <summary>Aggregated metric with availability flag — never invent values.</summary>
public sealed class MetricSummary
{
    public bool IsAvailable { get; init; }
    public string? UnavailableReason { get; init; }
    public double? Average { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? P1 { get; init; }
    public double? P01 { get; init; }
    public int SampleCount { get; init; }
    public string Unit { get; init; } = string.Empty;

    public static MetricSummary Unavailable(string reason, string unit = "") => new()
    {
        IsAvailable = false,
        UnavailableReason = reason,
        Unit = unit
    };

    public static MetricSummary FromValues(
        IReadOnlyList<double> values,
        string unit,
        int minSamplesForPercentiles = 20)
    {
        if (values.Count == 0)
        {
            return Unavailable("No samples.", unit);
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var avg = sorted.Average();
        double? p1 = null;
        double? p01 = null;
        if (sorted.Length >= minSamplesForPercentiles)
        {
            // P1 frame-time style: 99th percentile (worse tail)
            p1 = PercentileSorted(sorted, 0.99);
            // P0.1: 99.9th percentile
            p01 = PercentileSorted(sorted, 0.999);
        }

        return new MetricSummary
        {
            IsAvailable = true,
            Average = avg,
            Minimum = sorted[0],
            Maximum = sorted[^1],
            P1 = p1,
            P01 = p01,
            SampleCount = sorted.Length,
            Unit = unit
        };
    }

    /// <summary>Nearest-rank percentile on a pre-sorted ascending array. p in [0,1].</summary>
    public static double PercentileSorted(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("Empty sample set.", nameof(sortedAscending));
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        p = Math.Clamp(p, 0.0, 1.0);
        var rank = p * (sortedAscending.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi)
        {
            return sortedAscending[lo];
        }

        var w = rank - lo;
        return sortedAscending[lo] * (1 - w) + sortedAscending[hi] * w;
    }
}

/// <summary>Computed results for one completed (or failed) run.</summary>
public sealed class BenchmarkResult
{
    public MetricSummary SystemCpu { get; init; } = MetricSummary.Unavailable("Not calculated.");
    public MetricSummary ProcessCpu { get; init; } = MetricSummary.Unavailable("Not calculated.");
    public MetricSummary SystemMemory { get; init; } = MetricSummary.Unavailable("Not calculated.");
    public MetricSummary ProcessMemoryMb { get; init; } = MetricSummary.Unavailable("Not calculated.");
    public MetricSummary GpuUtilization { get; init; } = MetricSummary.Unavailable("Not calculated.");
    public MetricSummary CpuTemperature { get; init; } = MetricSummary.Unavailable("Not calculated.");
    public MetricSummary GpuTemperature { get; init; } = MetricSummary.Unavailable("Not calculated.");

    /// <summary>
    /// Frame-time aggregates. Unavailable unless an external frame-time source provided samples.
    /// P1 / P0.1 are frame-time percentiles (99th / 99.9th), not FPS percentiles.
    /// </summary>
    public MetricSummary FrameTimeMs { get; init; } = MetricSummary.Unavailable(
        "CS2 render frame-time is not collected: FrameForge does not inject into CS2 or hook graphics APIs.");

    /// <summary>FPS derived only from valid frame-time samples (1000/ms). Null when frame-time unavailable.</summary>
    public MetricSummary? DerivedFps { get; init; }

    public int MeasuredSampleCount { get; init; }
    public int WarmupSampleCount { get; init; }
    public int TotalSampleCount { get; init; }
    public double Cs2PresentSamplePercent { get; init; }
    public string Notes { get; init; } = string.Empty;
}

/// <summary>Hardware / profile context captured at run start for repeatability.</summary>
public sealed class BenchmarkSystemSnapshot
{
    public string CpuName { get; init; } = "Unknown";
    public int CpuCoreCount { get; init; }
    public int CpuThreadCount { get; init; }
    public string GpuName { get; init; } = "Unknown";
    public long TotalRamBytes { get; init; }
    public string OsVersion { get; init; } = "Unknown";
    public string Architecture { get; init; } = "Unknown";
    public string? PowerPlan { get; init; }
    public string? PowerPlanStatus { get; init; }
    public int? DisplayRefreshRateHz { get; init; }
    public string? DisplayResolution { get; init; }
    public bool? GameModeEnabled { get; init; }
    public string? SystemFingerprintId { get; init; }
    public string? GpuDetectionNotes { get; init; }
    public string? Cs2InstallPath { get; init; }
    public string? Cs2VersionHint { get; init; }
    public bool Cs2ProcessRunningAtStart { get; init; }
    public int? Cs2ProcessIdAtStart { get; init; }
    public DateTimeOffset? Cs2ProcessStartTime { get; init; }
    public string? ActiveProfileId { get; init; }
    public string? ActiveProfileName { get; init; }
    public IReadOnlyDictionary<string, string> ActiveFrameForgeSettings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Full benchmark run record (persisted as JSON).</summary>
public sealed class BenchmarkRun
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double DurationSecondsActual { get; set; }
    public BenchmarkStatus Status { get; set; } = BenchmarkStatus.Idle;
    public string? Error { get; set; }
    public BenchmarkConfiguration Configuration { get; set; } = BenchmarkConfiguration.CreateDefault();
    public BenchmarkSystemSnapshot SystemInformation { get; set; } = new();
    public BenchmarkResult Result { get; set; } = new();
    public List<BenchmarkSample> Samples { get; set; } = new();
    public string? FilePath { get; set; }

    /// <summary>Convenience mirror of SystemInformation.SystemFingerprintId (may be null).</summary>
    public string? SystemFingerprintId => SystemInformation?.SystemFingerprintId;

    public string DisplayTitle =>
        $"{StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {Configuration.ProfileName ?? Configuration.ProfileId ?? "no profile"} · {Configuration.DurationSeconds}s";
}

public sealed class BenchmarkComparisonMetric
{
    public string Metric { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public double? Before { get; init; }
    public double? After { get; init; }
    public double? Difference { get; init; }
    public double? PercentDifference { get; init; }
    public bool IsAvailable { get; init; }
    public string? Notes { get; init; }
    /// <summary>Human label: Lower / Higher / Unavailable — never invents values.</summary>
    public string Interpretation { get; init; } = "Unavailable";
    public string BeforeDisplay => IsAvailable && Before is not null ? Format(Before.Value) : "Unavailable";
    public string AfterDisplay => IsAvailable && After is not null ? Format(After.Value) : "Unavailable";
    public string DeltaDisplay => IsAvailable && Difference is not null
        ? (Difference.Value > 0 ? "+" : "") + Difference.Value.ToString("0.##") + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit)
        : "Unavailable";

    private string Format(double v) =>
        Unit == "%" ? $"{v:0.#}%" :
        Unit == "MB" ? (Math.Abs(v) >= 1024 ? $"{v/1024.0:0.00} GB" : $"{v:0.0} MB") :
        $"{v:0.##}{(string.IsNullOrEmpty(Unit) ? "" : " " + Unit)}";
}

/// <summary>Side-by-side comparison of two runs (A = before, B = after).</summary>
public sealed class BenchmarkComparison
{
    public BenchmarkRun RunA { get; init; } = new();
    public BenchmarkRun RunB { get; init; } = new();
    public IReadOnlyList<BenchmarkComparisonMetric> Metrics { get; init; } = Array.Empty<BenchmarkComparisonMetric>();
    public IReadOnlyList<BenchmarkConditionWarning> ConditionWarnings { get; init; } = Array.Empty<BenchmarkConditionWarning>();
    public BenchmarkConditionReport? ConditionReport { get; init; }
    public bool ForcedDespiteMismatch { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public static class BenchmarkSchema
{
    public const int CurrentVersion = 1;
    public const string FileExtension = ".json";
}

/// <summary>External process metadata only — never used for memory reads.</summary>
public sealed class Cs2ProcessInfo
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public DateTimeOffset? StartTime { get; init; }
    public long WorkingSetBytes { get; init; }
    public TimeSpan TotalProcessorTime { get; init; }
}
