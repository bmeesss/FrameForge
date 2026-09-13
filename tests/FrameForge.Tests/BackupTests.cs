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
            Assert.Contains(listed, b => b.Id == entry.Id);
            Assert.True(listed.Any(b => b.Id == entry.Id));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task RestoreBackup_RestoresFileContents()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var service = new BackupService(paths, log);

            var file = Path.Combine(root, "game.cfg");
            await File.WriteAllTextAsync(file, "original-value");

            var entry = await service.CreateBackupAsync(
                "restore-test",
                new[] { "opt1" },
                new[] { file },
                new Dictionary<string, string?>());

            await File.WriteAllTextAsync(file, "modified-value");
            Assert.Equal("modified-value", await File.ReadAllTextAsync(file));

            await service.RestoreAsync(entry.Id);
            Assert.Equal("original-value", await File.ReadAllTextAsync(file));
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
            Assert.DoesNotContain(listed, b => b.Id == entry.Id);
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
