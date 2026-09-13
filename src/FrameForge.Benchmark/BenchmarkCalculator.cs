using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Benchmark;

/// <summary>
/// Deterministic aggregates and before/after comparison.
/// Does not invent metrics — unavailable sources stay unavailable.
/// </summary>
public sealed class BenchmarkCalculator : IBenchmarkCalculator
{
    public const int MinSamplesForPercentiles = 20;

    public BenchmarkResult Calculate(IReadOnlyList<BenchmarkSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var measured = samples.Where(s => !s.IsWarmup).ToList();
        var warmup = samples.Count(s => s.IsWarmup);

        var systemCpu = MetricSummary.FromValues(
            measured.Where(s => s.SystemCpuPercent is not null).Select(s => s.SystemCpuPercent!.Value).ToList(),
            "%",
            MinSamplesForPercentiles);

        var processCpu = MetricSummary.FromValues(
            measured.Where(s => s.ProcessCpuPercent is not null).Select(s => s.ProcessCpuPercent!.Value).ToList(),
            "%",
            MinSamplesForPercentiles);

        var systemMem = MetricSummary.FromValues(
            measured.Where(s => s.SystemMemoryPercent is not null).Select(s => s.SystemMemoryPercent!.Value).ToList(),
            "%",
            MinSamplesForPercentiles);

        var processMemMb = MetricSummary.FromValues(
            measured.Where(s => s.ProcessWorkingSetBytes is not null)
                .Select(s => s.ProcessWorkingSetBytes!.Value / (1024.0 * 1024.0)).ToList(),
            "MB",
            MinSamplesForPercentiles);

        var gpu = measured.Where(s => s.GpuUtilizationPercent is not null)
            .Select(s => s.GpuUtilizationPercent!.Value).ToList();
        var gpuSummary = gpu.Count > 0
            ? MetricSummary.FromValues(gpu, "%", MinSamplesForPercentiles)
            : MetricSummary.Unavailable(
                "GPU utilization is unavailable without a safe OS counter (no DXGI/vendor hook in this build).",
                "%");

        var cpuTemp = measured.Where(s => s.CpuTemperatureC is not null)
            .Select(s => s.CpuTemperatureC!.Value).ToList();
        var cpuTempSummary = cpuTemp.Count > 0
            ? MetricSummary.FromValues(cpuTemp, "°C", MinSamplesForPercentiles)
            : MetricSummary.Unavailable("CPU temperature is not exposed via a portable OS API in this build.", "°C");

        var gpuTemp = measured.Where(s => s.GpuTemperatureC is not null)
            .Select(s => s.GpuTemperatureC!.Value).ToList();
        var gpuTempSummary = gpuTemp.Count > 0
            ? MetricSummary.FromValues(gpuTemp, "°C", MinSamplesForPercentiles)
            : MetricSummary.Unavailable("GPU temperature is not exposed via a portable OS API in this build.", "°C");

        var frameTimes = measured.Where(s => s.FrameTimeMs is not null && s.FrameTimeMs > 0)
            .Select(s => s.FrameTimeMs!.Value).ToList();
        MetricSummary frameSummary;
        MetricSummary? derivedFps = null;
        if (frameTimes.Count > 0)
        {
            frameSummary = MetricSummary.FromValues(frameTimes, "ms", MinSamplesForPercentiles);
            var fpsValues = frameTimes.Select(ms => 1000.0 / ms).ToList();
            derivedFps = MetricSummary.FromValues(fpsValues, "FPS", MinSamplesForPercentiles);
            // Note: P1 on derived FPS is the 99th percentile of instantaneous FPS samples —
            // it is NOT the reciprocal of P1 frame-time. UI should prefer frame-time percentiles.
        }
        else
        {
            frameSummary = MetricSummary.Unavailable(
                "CS2 render frame-time is not collected: FrameForge does not inject into CS2 or hook graphics APIs. " +
                "Compare CPU/memory (and GPU when available) across runs instead.",
                "ms");
        }

        var present = measured.Count == 0
            ? 0
            : 100.0 * measured.Count(s => s.Cs2ProcessPresent) / measured.Count;

        var notes = frameSummary.IsAvailable
            ? "Frame-time samples were provided by an external source."
            : "External OS counters only. No CS2 frame-time. FrameForge does not guarantee FPS improvements.";

        return new BenchmarkResult
        {
            SystemCpu = systemCpu,
            ProcessCpu = processCpu,
            SystemMemory = systemMem,
            ProcessMemoryMb = processMemMb,
            GpuUtilization = gpuSummary,
            CpuTemperature = cpuTempSummary,
            GpuTemperature = gpuTempSummary,
            FrameTimeMs = frameSummary,
            DerivedFps = derivedFps,
            MeasuredSampleCount = measured.Count,
            WarmupSampleCount = warmup,
            TotalSampleCount = samples.Count,
            Cs2PresentSamplePercent = present,
            Notes = notes
        };
    }

    public BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var metrics = new List<BenchmarkComparisonMetric>
        {
            Diff("System CPU average", "%", before.Result.SystemCpu, after.Result.SystemCpu),
            Diff("CS2 process CPU average", "%", before.Result.ProcessCpu, after.Result.ProcessCpu),
            Diff("System memory average", "%", before.Result.SystemMemory, after.Result.SystemMemory),
            Diff("CS2 working set average", "MB", before.Result.ProcessMemoryMb, after.Result.ProcessMemoryMb),
            Diff("GPU utilization average", "%", before.Result.GpuUtilization, after.Result.GpuUtilization),
            Diff("Average frame-time", "ms", before.Result.FrameTimeMs, after.Result.FrameTimeMs,
                "Lower frame-time is better. Not an FPS claim unless frame-time was measured."),
            Diff("P1 frame-time (99th pct)", "ms",
                before.Result.FrameTimeMs.IsAvailable ? before.Result.FrameTimeMs.P1 : null,
                after.Result.FrameTimeMs.IsAvailable ? after.Result.FrameTimeMs.P1 : null,
                before.Result.FrameTimeMs.IsAvailable && after.Result.FrameTimeMs.IsAvailable,
                "ms",
                "Frame-time percentile — not an FPS percentile."),
            Diff("P0.1 frame-time (99.9th pct)", "ms",
                before.Result.FrameTimeMs.IsAvailable ? before.Result.FrameTimeMs.P01 : null,
                after.Result.FrameTimeMs.IsAvailable ? after.Result.FrameTimeMs.P01 : null,
                before.Result.FrameTimeMs.IsAvailable && after.Result.FrameTimeMs.IsAvailable,
                "ms",
                "Frame-time percentile — not an FPS percentile.")
        };

        if (before.Result.DerivedFps is { IsAvailable: true } || after.Result.DerivedFps is { IsAvailable: true })
        {
            metrics.Add(Diff(
                "Derived average FPS (from frame-time only)",
                "FPS",
                before.Result.DerivedFps ?? MetricSummary.Unavailable("n/a"),
                after.Result.DerivedFps ?? MetricSummary.Unavailable("n/a"),
                "Only valid when frame-time samples exist. Not a guaranteed improvement."));
        }

        // Stamp interpretation on each metric (Unavailable never becomes 0)
        for (var i = 0; i < metrics.Count; i++)
        {
            var row = BenchmarkComparisonPresenter.ToDisplayRow(metrics[i]);
            metrics[i] = new BenchmarkComparisonMetric
            {
                Metric = metrics[i].Metric,
                Unit = metrics[i].Unit,
                Before = metrics[i].IsAvailable ? metrics[i].Before : null,
                After = metrics[i].IsAvailable ? metrics[i].After : null,
                Difference = metrics[i].IsAvailable ? metrics[i].Difference : null,
                PercentDifference = metrics[i].IsAvailable ? metrics[i].PercentDifference : null,
                IsAvailable = metrics[i].IsAvailable,
                Notes = metrics[i].Notes,
                Interpretation = row.Interpretation
            };
            if (!metrics[i].IsAvailable)
            {
                // Explicit: never leave zeros that look like measurements
                metrics[i] = new BenchmarkComparisonMetric
                {
                    Metric = metrics[i].Metric,
                    Unit = metrics[i].Unit,
                    Before = null,
                    After = null,
                    Difference = null,
                    PercentDifference = null,
                    IsAvailable = false,
                    Notes = metrics[i].Notes ?? "Unavailable",
                    Interpretation = "Unavailable"
                };
            }
        }

        var warnings = BenchmarkComparisonPresenter.AnalyzeConditions(
            before,
            after,
            before.SystemInformation.SystemFingerprintId,
            after.SystemInformation.SystemFingerprintId).ToList();

        var available = metrics.Where(m => m.IsAvailable).ToList();
        var summary = available.Count == 0
            ? "No overlapping available metrics to compare."
            : $"Compared {available.Count} metric(s). FrameForge does not guarantee FPS improvements.";
        if (warnings.Count > 0)
        {
            summary += " Benchmark conditions differ — see warnings.";
        }

        return new BenchmarkComparison
        {
            RunA = before,
            RunB = after,
            Metrics = metrics,
            ConditionWarnings = warnings,
            Summary = summary
        };
    }

    private static BenchmarkComparisonMetric Diff(
        string name,
        string unit,
        MetricSummary before,
        MetricSummary after,
        string? notes = null)
    {
        var ok = before.IsAvailable && after.IsAvailable && before.Average is not null && after.Average is not null;
        return Diff(name, unit, before.Average, after.Average, ok, unit, notes ?? before.UnavailableReason ?? after.UnavailableReason);
    }

    private static BenchmarkComparisonMetric Diff(
        string name,
        string unit,
        double? before,
        double? after,
        bool available,
        string unit2,
        string? notes)
    {
        double? diff = null;
        double? pct = null;
        if (available && before is not null && after is not null)
        {
            diff = after.Value - before.Value;
            if (Math.Abs(before.Value) > 1e-9)
            {
                pct = 100.0 * diff.Value / before.Value;
            }
        }

        return new BenchmarkComparisonMetric
        {
            Metric = name,
            Unit = unit,
            Before = before,
            After = after,
            Difference = diff,
            PercentDifference = pct,
            IsAvailable = available,
            Notes = available ? notes : (notes ?? "Metric unavailable on one or both runs.")
        };
    }
}
