using FrameForge.Hardware;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Core.Models;
using FrameForge.Core.IO;
using Xunit;

namespace FrameForge.Tests;

public sealed class HardwareAndLoggingTests
{
    [Fact]
    public async Task HardwareInfoService_ReturnsBasicFields()
    {
        var service = new HardwareInfoService();
        var info = await service.GetHardwareInfoAsync();
        Assert.False(string.IsNullOrWhiteSpace(info.CpuName));
        Assert.True(info.CpuThreadCount >= 1);
        Assert.True(info.CpuCoreCount >= 1);
        Assert.False(string.IsNullOrWhiteSpace(info.Architecture));
        Assert.False(string.IsNullOrWhiteSpace(info.OsDescription));
        Assert.False(string.IsNullOrWhiteSpace(info.GpuName));
        Assert.False(string.IsNullOrWhiteSpace(info.WindowsVersion));
    }

    [Fact]
    public void FileAppLog_Sanitize_RedactsSecrets()
    {
        var sanitized = FileAppLog.Sanitize("login password=supersecret token=abcd1234 ok");
        Assert.DoesNotContain("supersecret", sanitized);
        Assert.DoesNotContain("abcd1234", sanitized);
        Assert.Contains("***", sanitized);
    }

    [Fact]
    public void FileAppLog_WritesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            using var log = new FileAppLog(paths, LogLevelSetting.Trace);
            log.LogInformation("application startup");
            log.LogInformation("CS2 detection");
            var files = Directory.GetFiles(paths.LogsDirectory, "frameforge-*.log");
            Assert.NotEmpty(files);
            var text = File.ReadAllText(files[0]);
            Assert.Contains("application startup", text);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task AtomicFile_Write_ReplacesContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "data.txt");
        try
        {
            await AtomicFile.WriteAllTextAsync(path, "one");
            Assert.Equal("one", await File.ReadAllTextAsync(path));
            await AtomicFile.WriteAllTextAsync(path, "two");
            Assert.Equal("two", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
