using System.Text;

namespace FrameForge.Core.IO;

/// <summary>
/// Cross-platform helpers for safe file writes and copies.
/// Prefer replace-into-place; fall back when the OS cannot replace atomically.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        Encoding? encoding = null,
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
            await File.WriteAllTextAsync(tempPath, contents, encoding, cancellationToken)
                .ConfigureAwait(false);
            Replace(tempPath, path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task WriteAllBytesAsync(
        string path,
        byte[] bytes,
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
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            Replace(tempPath, path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void Copy(string source, string destination, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source file was not found.", source);
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
            if (File.Exists(destination) && !overwrite)
            {
                TryDelete(tempPath);
                throw new IOException($"Destination already exists: {destination}");
            }

            Replace(tempPath, destination);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Atomically replace destination with source when possible.
    /// Falls back to delete+move on platforms/filesystems that reject File.Replace.
    /// </summary>
    public static void Replace(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Temporary source was not found.", sourcePath);
        }

        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // fall through
            }
            catch (IOException)
            {
                // Different volumes / some Linux FS — fall through
            }
        }

        try
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(sourcePath, destinationPath);
        }
        catch
        {
            // Last resort: copy then delete temp
            File.Copy(sourcePath, destinationPath, overwrite: true);
            TryDelete(sourcePath);
        }
    }

    private static string GetTempSibling(string path) =>
        path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void TryDelete(string path)
    {
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
}
