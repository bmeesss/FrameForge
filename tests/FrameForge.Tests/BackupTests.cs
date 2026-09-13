using FrameForge.Core.Models;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using Xunit;

namespace FrameForge.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task CreateBackup_WritesMetadataAndSnapshots()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var service = new BackupService(paths, log);

            var file = Path.Combine(root, "sample.cfg");
            await File.WriteAllTextAsync(file, "fps_max 0");

            var entry = await service.CreateBackupAsync(
                description: "unit test backup",
                optimizationIds: new[] { "cs2.cfg.balanced" },
                affectedFiles: new[] { file },
                previousValues: new Dictionary<string, string?> { ["fps_max"] = "400" });

            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.True(File.Exists(paths.BackupMetadataPath));
            Assert.Single(entry.AffectedFiles);
            Assert.Equal("400", entry.PreviousValues["fps_max"]);

            var listed = await service.ListBackupsAsync();
            Assert.True(listed.Any(b => b.Id == entry.Id));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task RestoreBackup_RestoresExactContent()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var service = new BackupService(paths, log);

            var file = Path.Combine(root, "game.cfg");
            var original = "original-value\nline2\n" + new string('x', 4096);
            await File.WriteAllTextAsync(file, original);
            var originalBytes = await File.ReadAllBytesAsync(file);

            var entry = await service.CreateBackupAsync(
                "restore-test",
                new[] { "opt1" },
                new[] { file },
                new Dictionary<string, string?>());

            await File.WriteAllTextAsync(file, "modified-value");
            Assert.Equal("modified-value", await File.ReadAllTextAsync(file));

            var restore = await service.RestoreAsync(entry.Id);
            Assert.True(restore.Success, restore.Message);
            Assert.Single(restore.RestoredFiles);

            var restoredBytes = await File.ReadAllBytesAsync(file);
            Assert.Equal(originalBytes.Length, restoredBytes.Length);
            Assert.True(originalBytes.SequenceEqual(restoredBytes));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task CreateBackup_SkipsMissingSourceFiles()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var service = new BackupService(paths, new FileAppLog(paths));
            var missing = Path.Combine(root, "does-not-exist.cfg");
            var existing = Path.Combine(root, "exists.cfg");
            await File.WriteAllTextAsync(existing, "ok");

            var entry = await service.CreateBackupAsync(
                "partial",
                new[] { "id" },
                new[] { missing, existing },
                new Dictionary<string, string?> { ["k"] = "v" });

            // Existing file is snapshotted; missing path is recorded as CreatedFiles
            // so restore can delete it if the upcoming apply creates it.
            Assert.Equal(2, entry.AffectedFiles.Count);
            Assert.Contains(entry.AffectedFiles, f => f.Equals(existing, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(entry.AffectedFiles, f => f.Equals(missing, StringComparison.OrdinalIgnoreCase));
            Assert.Single(entry.FileSnapshots);
            Assert.Single(entry.CreatedFiles);
            Assert.Equal(missing, entry.CreatedFiles[0]);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task Restore_UnknownBackup_ReturnsFailure()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var service = new BackupService(paths, new FileAppLog(paths));
            var result = await service.RestoreAsync("bak_does_not_exist");
            Assert.False(result.Success);
            Assert.Contains("not found", result.Message);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task CorruptedMetadata_IsQuarantinedAndServiceContinues()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            Directory.CreateDirectory(paths.BackupsDirectory);
            await File.WriteAllTextAsync(paths.BackupMetadataPath, "{ not-json !!!");

            var service = new BackupService(paths, new FileAppLog(paths));
            var listed = await service.ListBackupsAsync();
            Assert.Empty(listed);

            // Should still allow creating a new backup
            var file = Path.Combine(root, "a.cfg");
            await File.WriteAllTextAsync(file, "x");
            var entry = await service.CreateBackupAsync("after-corrupt", new[] { "id" }, new[] { file },
                new Dictionary<string, string?>());
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task DeleteBackup_RemovesEntry()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var service = new BackupService(paths, new FileAppLog(paths));
            var file = Path.Combine(root, "a.cfg");
            await File.WriteAllTextAsync(file, "x");

            var entry = await service.CreateBackupAsync("d", new[] { "id" }, new[] { file },
                new Dictionary<string, string?>());

            await service.DeleteAsync(entry.Id);
            var listed = await service.ListBackupsAsync();
            Assert.True(listed.All(b => b.Id != entry.Id));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void MakeSafeFileName_IsStableAndSafe()
    {
        var a = BackupService.MakeSafeFileName(@"C:\Steam\cfg\autoexec.cfg");
        var b = BackupService.MakeSafeFileName(@"C:\Steam\cfg\autoexec.cfg");
        Assert.Equal(a, b);
        Assert.False(a.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "ff_bak_" + Guid.NewGuid().ToString("N"));

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }
}
