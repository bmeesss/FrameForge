using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Guided;

public sealed class GuidedOptimizationStore : IGuidedOptimizationStore
{
    private readonly IPathService _paths;
    private readonly IAppLog _log;

    public GuidedOptimizationStore(IPathService paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.GuidedRunsDirectory);
    }

    public async Task<string> SaveAsync(GuidedOptimizationRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.Id))
        {
            run.Id = CreateId(run.StartedAt);
        }

        run.SchemaVersion = GuidedOptimizationSchema.CurrentVersion;
        var path = Path.Combine(_paths.GuidedRunsDirectory, Sanitize(run.Id) + GuidedOptimizationSchema.FileExtension);
        run.FilePath = path;
        await FrameForgeJson.SerializeFileAsync(path, run, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Saved guided run {run.Id}");
        return path;
    }

    public async Task<IReadOnlyList<GuidedOptimizationRun>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<GuidedOptimizationRun>();
        if (!Directory.Exists(_paths.GuidedRunsDirectory))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.GuidedRunsDirectory, "*.json")
                     .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var run = await FrameForgeJson.DeserializeFileAsync<GuidedOptimizationRun>(file, cancellationToken)
                    .ConfigureAwait(false);
                if (run is null || string.IsNullOrWhiteSpace(run.Id))
                {
                    continue;
                }

                if (run.SchemaVersion < GuidedOptimizationSchema.MinReadableVersion)
                {
                    run.SchemaVersion = GuidedOptimizationSchema.MinReadableVersion;
                }

                // v1 → v2: ensure selection collections exist
                run.SelectedSettingKeys ??= new List<string>();
                run.SelectedSettings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (run.SchemaVersion < GuidedOptimizationSchema.CurrentVersion)
                {
                    run.SchemaVersion = GuidedOptimizationSchema.CurrentVersion;
                }

                run.FilePath = file;
                list.Add(run);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Skipping corrupt guided run '{file}'; quarantining: {ex.Message}");
                try
                {
                    File.Move(file, file + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}");
                }
                catch { /* ignore */ }
            }
        }

        return list.OrderByDescending(r => r.StartedAt).ToList();
    }

    public async Task<GuidedOptimizationRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
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
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public static string CreateId(DateTimeOffset startedAt) =>
        startedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
