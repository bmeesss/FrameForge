using System.Security.Cryptography;

namespace FrameForge.Core.IO;

/// <summary>
/// One file tracked by a <see cref="RecoverySnapshot"/>.
/// </summary>
public sealed class RecoverySnapshotEntry
{
    /// <summary>Original path the file is restored to.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>False when the file did not exist at snapshot time (restore deletes it).</summary>
    public bool ExistedBeforeSnapshot { get; init; }

    /// <summary>Copy of the original bytes inside the snapshot directory. Null when absent before.</summary>
    public string? StoredFile { get; init; }

    /// <summary>Byte length of the original file (verification).</summary>
    public long Length { get; init; }

    /// <summary>SHA-256 (hex, upper-case) of the original file (verification).</summary>
    public string? Sha256 { get; init; }

    /// <summary>False when the file existed but could not be copied into the snapshot.</summary>
    public bool Captured { get; init; }

    /// <summary>Why capture failed (null when capture succeeded).</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Outcome of restoring a <see cref="RecoverySnapshot"/>.
/// </summary>
public sealed class RecoveryRestoreReport
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> RestoredFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FailedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Per-file detail lines (failures only).</summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public static RecoveryRestoreReport Ok(IReadOnlyList<string> restored, string snapshotId) => new()
    {
        Success = true,
        RestoredFiles = restored,
        Message = $"Recovery snapshot {snapshotId} restored {restored.Count} file(s)."
    };

    public static RecoveryRestoreReport Fail(
        string snapshotId,
        IReadOnlyList<string> restored,
        IReadOnlyList<string> failed,
        IReadOnlyList<string> details) => new()
    {
        Success = false,
        RestoredFiles = restored,
        FailedFiles = failed,
        Details = details,
        Message =
            $"RECOVERY FAILED: snapshot {snapshotId} could not restore {failed.Count} of " +
            $"{restored.Count + failed.Count} file(s) ({string.Join(", ", failed.Select(f => Path.GetFileName(f) ?? f))})."
    };
}

/// <summary>
/// Internal, temporary transaction recovery for configuration writes.
///
/// This is NOT the user-visible backup: it exists for every apply operation, even when
/// <c>AutomaticBackup</c> is disabled, and it is removed again after a verified success.
/// It stores exact bytes (SHA-256 verified) for every file an operation may mutate, plus
/// the knowledge that a file did not exist yet, so restore can delete it again.
/// </summary>
public sealed class RecoverySnapshot : IDisposable
{
    private const string SnapshotRootName = "frameforge-recovery";

    private RecoverySnapshot(string id, string snapshotDirectory, IReadOnlyList<RecoverySnapshotEntry> entries)
    {
        Id = id;
        SnapshotDirectory = snapshotDirectory;
        Entries = entries;
    }

    public string Id { get; }

    public string SnapshotDirectory { get; }

    public IReadOnlyList<RecoverySnapshotEntry> Entries { get; }

    /// <summary>
    /// True when every tracked file was captured (or recorded as absent). When false the
    /// caller must abort before the first mutation — a partial snapshot cannot guarantee restore.
    /// </summary>
    public bool IsComplete => Entries.All(e => e.Captured);

    public bool IsCleanedUp { get; private set; }

    /// <summary>
    /// Captures the current bytes of every path. Paths that do not exist are recorded as absent.
    /// </summary>
    public static RecoverySnapshot Create(IEnumerable<string> paths, string? snapshotRoot = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var id = "rec_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N");
        var root = string.IsNullOrWhiteSpace(snapshotRoot)
            ? Path.Combine(Path.GetTempPath(), SnapshotRootName)
            : snapshotRoot;

        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);

        var entries = new List<RecoverySnapshotEntry>();
        var index = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var tracked = path;
            if (entries.Any(e => string.Equals(e.Path, tracked, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            index++;

            if (!File.Exists(tracked))
            {
                // Absent before → restore must delete it again.
                entries.Add(new RecoverySnapshotEntry
                {
                    Path = tracked,
                    ExistedBeforeSnapshot = false,
                    Captured = true
                });
                continue;
            }

            var storedName = "f" + index.ToString("D3") + ".ffrecovery";
            var storedPath = Path.Combine(directory, storedName);
            try
            {
                File.Copy(tracked, storedPath, overwrite: true);
                var bytes = File.ReadAllBytes(tracked);
                entries.Add(new RecoverySnapshotEntry
                {
                    Path = tracked,
                    ExistedBeforeSnapshot = true,
                    Captured = true,
                    StoredFile = storedPath,
                    Length = bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                });
            }
            catch (Exception ex)
            {
                entries.Add(new RecoverySnapshotEntry
                {
                    Path = tracked,
                    ExistedBeforeSnapshot = true,
                    Captured = false,
                    Detail = ex.Message
                });
            }
        }

        return new RecoverySnapshot(id, directory, entries);
    }

    /// <summary>
    /// Restores every tracked file to its exact pre-snapshot state and verifies the result.
    /// Never throws: inspect <see cref="RecoveryRestoreReport.Success"/> instead.
    /// </summary>
    public RecoveryRestoreReport Restore()
    {
        var restored = new List<string>();
        var failed = new List<string>();
        var details = new List<string>();

        foreach (var entry in Entries)
        {
            try
            {
                if (!entry.ExistedBeforeSnapshot)
                {
                    if (File.Exists(entry.Path))
                    {
                        File.Delete(entry.Path);
                    }

                    restored.Add(entry.Path);
                    continue;
                }

                if (!entry.Captured || entry.StoredFile is null || !File.Exists(entry.StoredFile))
                {
                    failed.Add(entry.Path);
                    details.Add(entry.Path + ": no recovery data captured for this file.");
                    continue;
                }

                var directory = Path.GetDirectoryName(entry.Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                AtomicFile.Copy(entry.StoredFile, entry.Path, overwrite: true);

                var bytes = File.ReadAllBytes(entry.Path);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (bytes.Length != entry.Length || !string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
                {
                    failed.Add(entry.Path);
                    details.Add(entry.Path + ": content after restore does not match the snapshot.");
                    continue;
                }

                restored.Add(entry.Path);
            }
            catch (Exception ex)
            {
                failed.Add(entry.Path);
                details.Add(entry.Path + ": " + ex.Message);
            }
        }

        return failed.Count == 0
            ? RecoveryRestoreReport.Ok(restored, Id)
            : RecoveryRestoreReport.Fail(Id, restored, failed, details);
    }

    /// <summary>Deletes the temporary snapshot directory. Returns false (with reason) on failure.</summary>
    public bool TryCleanup(out string? error)
    {
        error = null;
        try
        {
            if (Directory.Exists(SnapshotDirectory))
            {
                Directory.Delete(SnapshotDirectory, recursive: true);
            }

            IsCleanedUp = !Directory.Exists(SnapshotDirectory);
            return IsCleanedUp;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose() => TryCleanup(out _);
}

/// <summary>
/// Creates recovery snapshots. Injectable so diagnostics/tests can observe or corrupt a snapshot.
/// </summary>
public interface IRecoverySnapshotFactory
{
    RecoverySnapshot Create(IReadOnlyList<string> paths);
}

/// <summary>Default factory: snapshots under the per-user temp directory.</summary>
public sealed class RecoverySnapshotFactory : IRecoverySnapshotFactory
{
    public RecoverySnapshot Create(IReadOnlyList<string> paths) => RecoverySnapshot.Create(paths);
}
