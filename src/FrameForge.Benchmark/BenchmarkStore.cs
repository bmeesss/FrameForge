using System.Globalization;
using System.Text;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Benchmark;

/// <summary>
/// Local JSON persistence under Benchmarks/. No network, no telemetry upload.
/// </summary>
public sealed class BenchmarkStore : IBenchmarkStore
{
    private readonly IPathService _paths;
    private readonly IAppLog _log;

    public BenchmarkStore(IPathService paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.BenchmarksDirectory);
    }

    public async Task<string> SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.Id))
        {
            run.Id = CreateId(run.StartedAt);
        }

        run.SchemaVersion = BenchmarkSchema.CurrentVersion;
        var fileName = SanitizeFileName(run.Id) + BenchmarkSchema.FileExtension;
        var path = Path.Combine(_paths.BenchmarksDirectory, fileName);
        run.FilePath = path;
        await FrameForgeJson.SerializeFileAsync(path, run, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Saved benchmark run {run.Id} → {path}");
        return path;
    }

    public async Task<IReadOnlyList<BenchmarkRun>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<BenchmarkRun>();
        if (!Directory.Exists(_paths.BenchmarksDirectory))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.BenchmarksDirectory, "*.json")
                     .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var run = await FrameForgeJson.DeserializeFileAsync<BenchmarkRun>(file, cancellationToken)
                    .ConfigureAwait(false);
                if (run is null || string.IsNullOrWhiteSpace(run.Id))
                {
                    continue;
                }

                run.FilePath = file;
                list.Add(run);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Skipping unreadable benchmark file '{file}': {ex.Message}");
            }
        }

        return list
            .OrderByDescending(r => r.StartedAt)
            .ToList();
    }

    public async Task<BenchmarkRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(r => r.Id.Equals(runId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run?.FilePath is not null && File.Exists(run.FilePath))
        {
            File.Delete(run.FilePath);
            _log.LogInformation($"Deleted benchmark {runId}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task ExportJsonAsync(string runId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var run = await GetAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Benchmark '{runId}' was not found.");
        await FrameForgeJson.SerializeFileAsync(destinationPath, run, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportCsvAsync(string runId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var run = await GetAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Benchmark '{runId}' was not found.");

        var sb = new StringBuilder();
        sb.AppendLine(
            "timestamp_utc,elapsed_ms,is_warmup,system_cpu_pct,process_cpu_pct,process_ws_bytes,system_mem_pct,gpu_util_pct,frame_time_ms,cs2_pid,cs2_present");
        foreach (var s in run.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.Append(s.Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                .Append(F(s.ElapsedMs)).Append(',')
                .Append(s.IsWarmup ? "1" : "0").Append(',')
                .Append(F(s.SystemCpuPercent)).Append(',')
                .Append(F(s.ProcessCpuPercent)).Append(',')
                .Append(s.ProcessWorkingSetBytes?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(F(s.SystemMemoryPercent)).Append(',')
                .Append(F(s.GpuUtilizationPercent)).Append(',')
                .Append(F(s.FrameTimeMs)).Append(',')
                .Append(s.Cs2ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(s.Cs2ProcessPresent ? "1" : "0")
                .AppendLine();
        }

        await File.WriteAllTextAsync(destinationPath, sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    public static string CreateId(DateTimeOffset startedAt) =>
        startedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) +
        "_" + Guid.NewGuid().ToString("N")[..6];

    private static string F(double? v) =>
        v is null ? "" : v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string F(double v) =>
        v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SanitizeFileName(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
