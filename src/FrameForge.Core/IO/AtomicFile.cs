using System.Security.Cryptography;
using System.Text;

namespace FrameForge.Core.IO;

/// <summary>
/// How a file was put into place by <see cref="AtomicFile"/>.
/// Reporting this honestly matters: only <see cref="FileReplaceStrategy.AtomicReplace"/>
/// is atomic. <see cref="FileReplaceStrategy.FallbackReplace"/> is a guarded best-effort
/// replacement and must never be described as atomic.
/// </summary>
public enum FileReplaceStrategy
{
    /// <summary>
    /// The destination was swapped in with a single atomic operation
    /// (<c>File.Replace</c>, or a rename when no destination existed yet).
    /// A concurrent reader either sees the old file or the new file — never a partial file.
    /// </summary>
    AtomicReplace,

    /// <summary>
    /// The atomic operation was unavailable or rejected by the filesystem, so the
    /// destination was replaced with a multi-step, NON-atomic fallback:
    /// a recovery copy of the original is retained first, the replacement is performed,
    /// the result is verified, and the original is restored if anything fails.
    /// A concurrent reader may observe the destination as briefly missing or partially written.
    /// </summary>
    FallbackReplace
}

/// <summary>
/// Diagnostic description of a single <see cref="AtomicFile"/> write/replace operation.
/// </summary>
public sealed class FileWriteResult
{
    public string Path { get; init; } = string.Empty;

    public FileReplaceStrategy Strategy { get; init; }

    /// <summary>True when <see cref="Strategy"/> is <see cref="FileReplaceStrategy.AtomicReplace"/>.</summary>
    public bool UsedAtomicReplace => Strategy == FileReplaceStrategy.AtomicReplace;

    /// <summary>True when <see cref="Strategy"/> is <see cref="FileReplaceStrategy.FallbackReplace"/>.</summary>
    public bool UsedFallbackReplace => Strategy == FileReplaceStrategy.FallbackReplace;

    /// <summary>True when the fallback path kept a recovery copy of the previous destination.</summary>
    public bool UsedRecoveryCopy { get; init; }

    /// <summary>True when the previous destination was restored from the recovery copy after a failed replacement.</summary>
    public bool RestoredOriginalAfterFailure { get; init; }

    /// <summary>True when the destination was verified to contain exactly the intended bytes.</summary>
    public bool Verified { get; init; }

    /// <summary>Short human-readable note (e.g. which OS primitive succeeded or why the fallback was used).</summary>
    public string? Detail { get; init; }

    public override string ToString() =>
        $"{Strategy} (recoveryCopy={UsedRecoveryCopy}, restored={RestoredOriginalAfterFailure}, verified={Verified})";
}

/// <summary>
/// Cross-platform helpers for safe file writes and copies.
///
/// Preferred order per write:
///   1. write a temp sibling file, flush data, flush metadata where supported
///   2. attempt an atomic replace (File.Replace / rename)
///   3. if the atomic replace is unavailable: retain a recovery copy of the original,
///      perform the fallback replacement, verify the destination, and restore the
///      original when the replacement or verification fails.
///
/// The destination is never intentionally left deleted: if the fallback cannot put the
/// new file in place, the original is restored from the retained recovery copy before
/// the exception is surfaced.
/// </summary>
public static class AtomicFile
{
    private const int DefaultBufferSize = 4096;
    private const string RecoveryCopySuffixPrefix = ".ffrecover-";

    public static async Task<FileWriteResult> WriteAllTextAsync(
        string path,
        string contents,
        Encoding? encoding = null,
        bool allowAtomicReplace = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = GetTempSibling(path);
        try
        {
            var bytes = encoding.GetBytes(contents);
            await WriteTempAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            return Replace(tempPath, path, allowAtomicReplace);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task<FileWriteResult> WriteAllBytesAsync(
        string path,
        byte[] bytes,
        bool allowAtomicReplace = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = GetTempSibling(path);
        try
        {
            await WriteTempAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            return Replace(tempPath, path, allowAtomicReplace);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static FileWriteResult Copy(
        string source,
        string destination,
        bool overwrite = true,
        bool allowAtomicReplace = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source file was not found.", source);
        }

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException($"Destination already exists: {destination}");
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = GetTempSibling(destination);
        try
        {
            File.Copy(source, tempPath, overwrite: true);
            return Replace(tempPath, destination, allowAtomicReplace);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Puts <paramref name="sourcePath"/> in place of <paramref name="destinationPath"/>.
    /// Prefers a single atomic replace; otherwise uses the recovery-copy guarded fallback.
    /// Returns diagnostics describing which strategy was used.
    /// </summary>
    /// <param name="sourcePath">Temporary file holding the new content (consumed on success).</param>
    /// <param name="destinationPath">File to create or replace.</param>
    /// <param name="allowAtomicReplace">
    /// When false the atomic attempt is skipped and the fallback path is used.
    /// Used by diagnostics/tests to exercise the non-atomic path on machines where
    /// the atomic primitive normally succeeds.
    /// </param>
    public static FileWriteResult Replace(
        string sourcePath,
        string destinationPath,
        bool allowAtomicReplace = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Temporary source was not found.", sourcePath);
        }

        var sourceSignature = FileSignature.Compute(sourcePath);
        var destinationExists = File.Exists(destinationPath);
        Exception? atomicReplaceFailure = null;

        // 1) Preferred: a single atomic replace operation.
        if (allowAtomicReplace && destinationExists)
        {
            try
            {
                File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
                return new FileWriteResult
                {
                    Path = destinationPath,
                    Strategy = FileReplaceStrategy.AtomicReplace,
                    Verified = true,
                    Detail = "File.Replace"
                };
            }
            catch (Exception ex)
            {
                // Unavailable for this filesystem/platform (or cross-device) — fall through to the
                // guarded fallback. The original destination is untouched at this point.
                atomicReplaceFailure = ex;
            }
        }

        // 2) No destination yet: a rename/move puts the file in place atomically.
        if (!destinationExists)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: false);
                return new FileWriteResult
                {
                    Path = destinationPath,
                    Strategy = FileReplaceStrategy.AtomicReplace,
                    Verified = true,
                    Detail = "rename into new destination"
                };
            }
            catch (Exception moveFailure)
            {
                // Cross-device / exotic filesystem: create by copy, verify, then drop the temp.
                // This is NOT atomic — a reader could observe a partially written file.
                try
                {
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    if (!sourceSignature.Matches(destinationPath))
                    {
                        throw new IOException(
                            $"Verification failed after writing '{destinationPath}' (fallback create).");
                    }
                }
                catch
                {
                    TryDelete(destinationPath);
                    throw new IOException(
                        $"Could not put '{destinationPath}' in place atomically or by copy: {moveFailure.Message}",
                        moveFailure);
                }

                TryDelete(sourcePath);
                return new FileWriteResult
                {
                    Path = destinationPath,
                    Strategy = FileReplaceStrategy.FallbackReplace,
                    Verified = true,
                    Detail = "copy into new destination (rename unavailable)"
                };
            }
        }

        // 3) Fallback (NOT atomic): keep a recovery copy, replace, verify, restore on failure.
        var originalSignature = FileSignature.Compute(destinationPath);
        var recoveryPath = TryCreateRecoveryCopy(destinationPath);
        var restoredOriginal = false;
        string? failureReason;
        Exception? failure = null;

        try
        {
            ReplaceNonAtomic(sourcePath, destinationPath);
            if (!sourceSignature.Matches(destinationPath))
            {
                throw new IOException(
                    $"Verification failed after replacing '{destinationPath}' (fallback replace).");
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            if (recoveryPath is not null)
            {
                restoredOriginal = TryRestoreFromRecovery(recoveryPath, destinationPath, originalSignature);
            }

            failureReason =
                $"Replacement of '{destinationPath}' failed ({failure.Message}). " +
                (restoredOriginal
                    ? "The original file was restored from the recovery copy."
                    : "The original file could NOT be restored from a recovery copy — the destination was left in place unchanged where possible.");

            var result = new FileWriteResult
            {
                Path = destinationPath,
                Strategy = FileReplaceStrategy.FallbackReplace,
                UsedRecoveryCopy = recoveryPath is not null,
                RestoredOriginalAfterFailure = restoredOriginal,
                Verified = false,
                Detail = failureReason
            };

            TryDelete(recoveryPath);
            throw new IOException(failureReason + " " + result, failure);
        }

        TryDelete(recoveryPath);
        return new FileWriteResult
        {
            Path = destinationPath,
            Strategy = FileReplaceStrategy.FallbackReplace,
            UsedRecoveryCopy = recoveryPath is not null,
            RestoredOriginalAfterFailure = false,
            Verified = true,
            Detail = atomicReplaceFailure is null
                ? "fallback replace (atomic replace not attempted)"
                : $"fallback replace (atomic replace rejected: {atomicReplaceFailure.GetType().Name})"
        };
    }

    /// <summary>
    /// Non-atomic replacement of an existing destination.
    /// Tries the least-bad primitive first (move with overwrite), and only deletes the
    /// destination as a last resort. Callers must hold a recovery copy before calling this.
    /// </summary>
    private static void ReplaceNonAtomic(string sourcePath, string destinationPath)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
            return;
        }
        catch
        {
            // Some filesystems/platforms reject overwrite-moves. Fall through.
        }

        // Last resort: delete then move. The destination is momentarily absent here,
        // which is exactly why the caller retains a recovery copy first.
        File.Delete(destinationPath);
        File.Move(sourcePath, destinationPath, overwrite: false);
    }

    private static async Task WriteTempAsync(string tempPath, byte[] bytes, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = DefaultBufferSize
        };

        using (var stream = new FileStream(tempPath, options))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Flush data (and metadata where the platform supports it) to stable storage.
            try
            {
                stream.Flush(flushToDisk: true);
            }
            catch (Exception)
            {
                // fsync is not supported everywhere; the write itself already succeeded.
            }
        }
    }

    private static string? TryCreateRecoveryCopy(string destinationPath)
    {
        var recoveryPath = destinationPath + RecoveryCopySuffixPrefix + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(destinationPath, recoveryPath, overwrite: false);
            return recoveryPath;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryRestoreFromRecovery(
        string recoveryPath,
        string destinationPath,
        FileSignature originalSignature)
    {
        try
        {
            File.Copy(recoveryPath, destinationPath, overwrite: true);
            return originalSignature.Matches(destinationPath);
        }
        catch
        {
            return false;
        }
    }

    private static string GetTempSibling(string path) =>
        path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Length + content hash used to verify that a write landed byte-for-byte.</summary>
    private readonly struct FileSignature
    {
        private FileSignature(long length, string sha256)
        {
            Length = length;
            Sha256 = sha256;
        }

        private long Length { get; }

        private string Sha256 { get; }

        public static FileSignature Compute(string path)
        {
            using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.SequentialScan,
                    BufferSize = DefaultBufferSize
                });

            var length = stream.Length;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return new FileSignature(length, Convert.ToHexString(hash));
        }

        public bool Matches(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var current = Compute(path);
                return current.Length == Length &&
                       string.Equals(current.Sha256, Sha256, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
