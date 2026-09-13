using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Backup;

/// <summary>
/// File-based backup store under Backups/ with metadata.json.
/// Each entry stores file snapshots + previous values sufficient for restore.
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
            var created = new List<string>();
            var copyFailures = new List<string>();

            foreach (var file in affectedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                if (!File.Exists(file))
                {
                    // File will be created by the upcoming apply — record so restore can delete it.
                    created.Add(file);
                    affected.Add(file);
                    continue;
                }

                try
                {
                    var safeName = MakeSafeFileName(file);
                    var dest = Path.Combine(entryDir, safeName);
                    AtomicFile.Copy(file, dest, overwrite: true);
                    snapshots[file] = safeName;
                    affected.Add(file);
                }
                catch (Exception ex)
                {
                    copyFailures.Add(file);
                    _log.LogWarning($"Failed to snapshot '{file}': {ex.Message}");
                }
            }

            if (copyFailures.Count > 0 && snapshots.Count == 0 && created.Count == 0 && previousValues.Count == 0)
            {
                TryDeleteDirectory(entryDir);
                throw new IOException(
                    $"Backup failed: could not snapshot any of the requested files ({copyFailures.Count} failure(s)).");
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
                FileSnapshots = snapshots,
                CreatedFiles = created
            };

            var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);

            // Prevent duplicate IDs (extremely unlikely, but guard anyway)
            store.Backups.RemoveAll(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            store.Backups.Insert(0, entry);

            try
            {
                await SaveStoreAsync(store, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(entryDir);
                throw;
            }

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
        if (string.IsNullOrWhiteSpace(backupId))
        {
            return null;
        }

        var store = await LoadStoreAsync(cancellationToken).ConfigureAwait(false);
        return store.Backups.FirstOrDefault(b => b.Id.Equals(backupId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<BackupRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(backupId))
            {
                return BackupRestoreResult.Fail(backupId ?? string.Empty, "Backup id is required.");
            }

            var entry = await GetBackupAsync(backupId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return BackupRestoreResult.Fail(backupId, $"Backup '{backupId}' was not found.");
            }

            var entryDir = Path.Combine(_paths.BackupsDirectory, entry.Id);
            if (!Directory.Exists(entryDir) && entry.FileSnapshots.Count > 0)
            {
                return BackupRestoreResult.Fail(backupId, $"Backup directory missing for '{backupId}'.");
            }

            var restored = new List<string>();
            var failed = new List<string>();

            foreach (var (originalPath, snapshotName) in entry.FileSnapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(entryDir, snapshotName);
                if (!File.Exists(source))
                {
                    _log.LogWarning($"Snapshot missing for restore: {snapshotName}");
                    failed.Add(originalPath);
                    continue;
                }

                try
                {
                    var directory = Path.GetDirectoryName(originalPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    AtomicFile.Copy(source, originalPath, overwrite: true);
                    restored.Add(originalPath);
                }
                catch (Exception ex)
                {
                    _log.LogError($"Failed to restore '{originalPath}'.", ex);
                    failed.Add(originalPath);
                }
            }

            // Delete files that did not exist at backup time (newly created by apply).
            foreach (var createdPath in entry.CreatedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FileSnapshots.ContainsKey(createdPath))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(createdPath))
                    {
                        File.Delete(createdPath);
                        restored.Add(createdPath);
                        _log.LogInformation($"Removed file created after backup: {createdPath}");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError($"Failed to remove created file '{createdPath}' on restore.", ex);
                    failed.Add(createdPath);
                }
            }

            if (failed.Count > 0 && restored.Count == 0)
            {
                return BackupRestoreResult.Fail(backupId, "Restore failed for all files.", restored, failed);
            }

            if (failed.Count > 0)
            {
                _log.LogWarning($"Partial restore for {backupId}: {restored.Count} ok, {failed.Count} failed.");
                return BackupRestoreResult.Fail(
                    backupId,
                    $"Partial restore: {restored.Count} restored, {failed.Count} failed.",
                    restored,
                    failed);
            }

            _log.LogInformation($"Restored backup {backupId}.");
            return BackupRestoreResult.Ok(backupId, restored);
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
            TryDeleteDirectory(dir);

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
            // Corrupted metadata: quarantine and start fresh rather than crashing.
            _log.LogError("Failed to read backup metadata; quarantining corrupt file.", ex);
            try
            {
                var quarantine = _paths.BackupMetadataPath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(_paths.BackupMetadataPath, quarantine);
            }
            catch
            {
                // ignore quarantine failure
            }

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

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path)))[..8];
        return $"{hash}_{name}";
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
