using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Local JSON log of per-key setting changes (alongside full backups).
/// </summary>
public sealed class SettingChangeSnapshotStore : ISettingChangeSnapshotStore
{
    public const string FileName = "setting-change-snapshots.json";

    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingChangeSnapshotStore(IPathService paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.PerformanceHistoryDirectory);
    }

    private string StorePath => Path.Combine(_paths.PerformanceHistoryDirectory, FileName);

    public async Task AppendAsync(
        IEnumerable<SettingChangeSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var list = snapshots.Where(s => s is not null).ToList();
        if (list.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var s in list)
            {
                if (string.IsNullOrWhiteSpace(s.Id))
                {
                    s.Id = "scs_" + Guid.NewGuid().ToString("N")[..12];
                }

                if (s.Timestamp == default)
                {
                    s.Timestamp = DateTimeOffset.UtcNow;
                }

                doc.Entries.Insert(0, s);
            }

            // Cap growth
            if (doc.Entries.Count > 5000)
            {
                doc.Entries = doc.Entries.Take(5000).ToList();
            }

            await FrameForgeJson.SerializeFileAsync(StorePath, doc, cancellationToken).ConfigureAwait(false);
            _log.LogInformation($"Appended {list.Count} setting change snapshot(s).");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SettingChangeSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var doc = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Entries.OrderByDescending(e => e.Timestamp).ToList();
    }

    public async Task<IReadOnlyList<SettingChangeSnapshot>> ListForKeyAsync(
        string configKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configKey))
        {
            return Array.Empty<SettingChangeSnapshot>();
        }

        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(e => e.ConfigKey.Equals(configKey, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<SettingChangeSnapshot?> GetLatestForKeyAsync(
        string configKey,
        CancellationToken cancellationToken = default)
    {
        var list = await ListForKeyAsync(configKey, cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    private async Task<SettingChangeSnapshotStoreDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StorePath))
        {
            return new SettingChangeSnapshotStoreDocument();
        }

        try
        {
            return await FrameForgeJson.DeserializeFileAsync<SettingChangeSnapshotStoreDocument>(StorePath, cancellationToken)
                .ConfigureAwait(false) ?? new SettingChangeSnapshotStoreDocument();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Corrupt setting-change snapshot store: {ex.Message}");
            return new SettingChangeSnapshotStoreDocument();
        }
    }
}
