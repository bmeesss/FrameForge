using System.Diagnostics;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Benchmark;

/// <summary>
/// Lightweight system sample — intentionally not a full game benchmark.
/// </summary>
public sealed class BasicSystemBenchmark
{
    private readonly IHardwareInfoService _hardwareInfoService;
    private readonly IAppLog? _log;

    public BasicSystemBenchmark(IHardwareInfoService hardwareInfoService, IAppLog? log = null)
    {
        _hardwareInfoService = hardwareInfoService;
        _log = log;
    }

    public async Task<BenchmarkReport> RunAsync(CancellationToken cancellationToken = default)
    {
        _log?.LogInformation("Running basic system benchmark sample.");
        var hardware = await _hardwareInfoService.GetHardwareInfoAsync(cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var checksum = 0L;
        // Short CPU spin to produce a relative score without heavy load
        for (var i = 0; i < 2_000_000 && !cancellationToken.IsCancellationRequested; i++)
        {
            checksum = unchecked(checksum + i * 17);
        }

        sw.Stop();

        return new BenchmarkReport
        {
            Hardware = hardware,
            Duration = sw.Elapsed,
            RelativeCpuScore = Math.Max(1, (int)(2_000_000.0 / Math.Max(1, sw.Elapsed.TotalMilliseconds))),
            Notes = "Lightweight CPU spin sample only. Use IBenchmarkEngine for external OS counter sessions (no CS2 injection / no fake FPS)."
        };
    }
}

public sealed class BenchmarkReport
{
    public HardwareInfo Hardware { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public int RelativeCpuScore { get; init; }
    public string Notes { get; init; } = string.Empty;
}
