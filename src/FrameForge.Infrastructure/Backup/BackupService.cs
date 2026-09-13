using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Backup;

/// <summary>
/// File-based backup store under Backups/ with metadata.json.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(IPathService paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.BackupsDirectory);
    }

    public async Task<BackupEntry> CreateBackupAsync(
        string description,
        IEnumerable<string> optimizationIds,
        IEnumerable<string> affectedFiles,
        IReadOnlyDictionary<string, string?> previousValues,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = $"bak_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];
            var entryDir = Path.Combine(_paths.BackupsDirectory, id);
            Directory.CreateDirectory(entryDir);

            var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var affected = new List<string>();

            foreach (var file in affectedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    continue;
                }

                var safeName = MakeSafeFileName(file);
                var dest = Path.Combine(entryDir, safeName);
                File.Copy(file, dest, overwrite: true);
                snapshots[file] = safeName;
                affected.Add(file);
            }

            var entry = new BackupEntry
            {
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Description = description,
                OptimizationIds = optimizationIds.ToList(),
                AffectedFiles = affected,
                PreviousValues = previousValues.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                ProfileId = profileId,
                FileSnapshots = snapshots
            };

            var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
            store.Backups.Insert(0, entry);
            await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);

            _log.LogInformation($"Created backup {id} with {affected.Count} file(s).");
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
        return store.Backups
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    public async Task<BackupEntry?> GetBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
        return store.Backups.FirstOrDefault(b => b.Id.Equals(backupId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RestoreAsync(string backupId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await GetBackupAsync(backupId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Backup '{backupId}' was not found.");

            var entryDir = Path.Combine(_paths.BackupsDirectory, entry.Id);
            foreach (var (originalPath, snapshotName) in entry.FileSnapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(entryDir, snapshotName);
                if (!File.Exists(source))
                {
                    _log.LogWarning($"Snapshot missing for restore: {snapshotName}");
                    continue;
                }

                var directory = Path.GetDirectoryName(originalPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = originalPath + ".restore.tmp";
                File.Copy(source, temp, overwrite: true);
                if (File.Exists(originalPath))
                {
                    File.Replace(temp, originalPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temp, originalPath);
                }
            }

            _log.LogInformation($"Restored backup {backupId}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string backupId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
            var removed = store.Backups.RemoveAll(b => b.Id.Equals(backupId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return;
            }

            await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);

            var dir = Path.Combine(_paths.BackupsDirectory, backupId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            _log.LogInformation($"Deleted backup {backupId}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackupMetadataStore> LoadStoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.BackupMetadataPath))
        {
            return new BackupMetadataStore();
        }

        try
        {
            return await FrameForgeJson.DeserializeFileAsync<BackupMetadataStore>(_paths.BackupMetadataPath, cancellationToken)
                .ConfigureAwait(false) ?? new BackupMetadataStore();
        }
        catch (Exception ex)
        {
            _log.LogError("Failed to read backup metadata; starting empty store.", ex);
            return new BackupMetadataStore();
        }
    }

    private Task SaveStoreAsync(BackupMetadataStore store, CancellationToken cancellationToken) =>
        FrameForgeJson.SerializeFileAsync(_paths.BackupMetadataPath, store, cancellationToken);

    public static string MakeSafeFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "file";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        // Avoid collisions for same file names from different folders
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path)))[..8];
        return $"{hash}_{name}";
    }
}
