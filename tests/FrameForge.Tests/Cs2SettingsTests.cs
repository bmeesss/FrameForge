using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Settings;
using Xunit;

namespace FrameForge.Tests;

public sealed class Cs2SettingsTests
{
    [Fact]
    public void Catalog_OnlyIncludesSupportedDocumentedKeys()
    {
        var catalog = new Cs2SettingCatalog();
        var all = catalog.GetAll();
        Assert.NotEmpty(all);
        Assert.All(all, d => Assert.True(d.IsSupported));
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.ConfigKey)));
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.DisplayName)));
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Description)));

        // Categories used by UI
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Video);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Hud);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Game);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.KeyboardMouse);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Audio);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Communication);

        // Lookup
        Assert.NotNull(catalog.GetByConfigKey("fps_max"));
        Assert.NotNull(catalog.GetById("video.fps_max"));
        Assert.Null(catalog.GetByConfigKey("sv_cheats"));
    }

    [Fact]
    public void ValidateSettings_RejectsUnknownAndOutOfRange()
    {
        var svc = CreateService(out _, out _, out _);
        var bad = svc.ValidateSettings(new Dictionary<string, string>
        {
            ["fps_max"] = "not-int",
            ["volume"] = "2.5",
            ["sv_cheats"] = "1",
            ["m_rawinput"] = "9"
        });

        Assert.False(bad.IsValid);
        Assert.True(bad.Issues.Count >= 3);

        var good = svc.ValidateSettings(new Dictionary<string, string>
        {
            ["fps_max"] = "0",
            ["volume"] = "0.5",
            ["m_rawinput"] = "1",
            ["engine_low_latency_sleep_after_client_tick"] = "true"
        });
        Assert.True(good.IsValid, string.Join("; ", good.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void IsValidValue_CoversKinds()
    {
        var catalog = new Cs2SettingCatalog();
        var fps = catalog.GetByConfigKey("fps_max")!;
        Assert.True(Cs2SettingsService.IsValidValue(fps, "0", out _));
        Assert.False(Cs2SettingsService.IsValidValue(fps, "abc", out var r1));
        Assert.NotNull(r1);

        var vol = catalog.GetByConfigKey("volume")!;
        Assert.True(Cs2SettingsService.IsValidValue(vol, "0.75", out _));
        Assert.False(Cs2SettingsService.IsValidValue(vol, "1.5", out _));

        var raw = catalog.GetByConfigKey("m_rawinput")!;
        Assert.True(Cs2SettingsService.IsValidValue(raw, "1", out _));
        Assert.False(Cs2SettingsService.IsValidValue(raw, "2", out _));
    }

    [Fact]
    public async Task ReadSettings_WhenCs2Missing_ReturnsUnavailableSnapshot()
    {
        var root = NewTempRoot();
        try
        {
            var svc = CreateService(root, out _, out _, out _, cfgDir: null);
            var snap = await svc.ReadSettingsAsync();
            Assert.False(snap.Cs2Available);
            Assert.NotEmpty(snap.Settings);
            Assert.All(snap.Settings, s => Assert.Equal(Cs2SettingSource.Unavailable, s.Source));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task CreateDiff_OnlyIncludesActualChanges()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(Path.Combine(cfgDir, "autoexec.cfg"), "fps_max 400\nvolume 0.8\n");

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "test"
            };

            var snap = await svc.ReadSettingsAsync();
            Assert.True(snap.Cs2Available);

            var diff = svc.CreateDiff(snap, new Dictionary<string, string>
            {
                ["fps_max"] = "400", // same
                ["volume"] = "0.5",  // change
                ["m_rawinput"] = "1"
            }, "unit-test");

            Assert.True(diff.HasChanges);
            Assert.DoesNotContain(diff.Entries, e => e.ConfigKey.Equals("fps_max", StringComparison.OrdinalIgnoreCase) && e.IsChange);
            Assert.Contains(diff.Entries, e => e.ConfigKey.Equals("volume", StringComparison.OrdinalIgnoreCase) && e.IsChange);
            Assert.All(diff.Entries.Where(e => e.IsChange), e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(e.Reason));
            });
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplySettings_CreatesBackup_WritesManagedFile_AndVerifies()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(Path.Combine(cfgDir, "autoexec.cfg"), "fps_max 300\n// user note\n");

            var svc = CreateService(root, out var detection, out var backups, out var appSettings, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "test-install"
            };

            var result = await svc.ApplySettingsAsync(
                new Dictionary<string, string>
                {
                    ["fps_max"] = "0",
                    ["cl_hud_telemetry_ping_show"] = "1",
                    ["volume"] = "0.6"
                },
                reason: "unit apply");

            Assert.True(result.Success, result.Message);
            Assert.False(string.IsNullOrWhiteSpace(result.BackupId));
            Assert.True(result.Diff is { HasChanges: true });

            var managed = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            Assert.True(File.Exists(managed));
            var text = await File.ReadAllTextAsync(managed);
            Assert.Contains("fps_max", text);
            Assert.Contains("0", text);

            // autoexec untouched
            var autoexec = await File.ReadAllTextAsync(Path.Combine(cfgDir, "autoexec.cfg"));
            Assert.Contains("user note", autoexec);
            Assert.Contains("fps_max 300", autoexec);

            var settings = await appSettings.LoadAsync();
            Assert.Equal(result.BackupId, settings.LastSettingsBackupId);

            var listed = await backups.ListBackupsAsync();
            Assert.Contains(listed, b => b.Id == result.BackupId);

            // Re-read reflects managed values
            var snap = await svc.ReadSettingsAsync();
            var fps = snap.Settings.First(s => s.Definition.ConfigKey == "fps_max");
            Assert.Equal("0", fps.CurrentValue);
            Assert.Equal(Cs2SettingSource.FrameForgeManaged, fps.Source);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplySettings_ValidationFailure_DoesNotWrite()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var result = await svc.ApplySettingsAsync(
                new Dictionary<string, string> { ["fps_max"] = "nope" },
                "bad");
            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName)));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task RestoreLastFrameForgeChanges_RestoresManagedFile()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var managed = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            await File.WriteAllTextAsync(managed, "fps_max 111\n");

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var apply = await svc.ApplySettingsAsync(
                new Dictionary<string, string> { ["fps_max"] = "0" },
                "change");
            Assert.True(apply.Success, apply.Message);

            // Corrupt / change after apply
            await File.WriteAllTextAsync(managed, "fps_max 999\n");

            var restore = await svc.RestoreLastFrameForgeChangesAsync();
            Assert.True(restore.Success, restore.Message);

            var after = await File.ReadAllTextAsync(managed);
            // Restored to pre-apply content (fps_max 111) from backup snapshot
            Assert.Contains("111", after);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplyDiff_NoChanges_IsSuccessNoop()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(
                Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName),
                "fps_max 0\n");

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var snap = await svc.ReadSettingsAsync();
            var diff = svc.CreateDiff(snap, new Dictionary<string, string> { ["fps_max"] = "0" }, "noop");
            var result = await svc.ApplyDiffAsync(diff);
            Assert.True(result.Success);
            Assert.Contains("No changes", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static Cs2SettingsService CreateService(out FakeDetection detection, out BackupService backups, out AppSettingsService appSettings)
        => CreateService(NewTempRoot(), out detection, out backups, out appSettings, cfgDir: null);

    private static Cs2SettingsService CreateService(
        string root,
        out FakeDetection detection,
        out BackupService backups,
        out AppSettingsService appSettings,
        string? cfgDir)
    {
        var paths = new PathService(root);
        var log = new FileAppLog(paths, LogLevelSetting.Warning);
        appSettings = new AppSettingsService(paths, log);
        backups = new BackupService(paths, log);
        detection = new FakeDetection(cfgDir);
        var catalog = new Cs2SettingCatalog();
        var config = new Cs2ConfigService();
        return new Cs2SettingsService(catalog, config, detection, backups, appSettings, log);
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "ff_set_" + Guid.NewGuid().ToString("N"));

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch { }
    }

    private sealed class FakeDetection : ICs2DetectionService
    {
        public FakeDetection(string? cfgDir)
        {
            if (cfgDir is not null)
            {
                InstallOverride = new Cs2InstallInfo
                {
                    IsInstalled = true,
                    InstallPath = Path.GetDirectoryName(cfgDir) ?? cfgDir,
                    CfgDirectory = cfgDir,
                    DetectionMessage = "override"
                };
            }
        }

        public Cs2InstallInfo? InstallOverride { get; set; }

        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default)
        {
            if (InstallOverride is not null)
            {
                return Task.FromResult(InstallOverride);
            }

            return Task.FromResult(new Cs2InstallInfo
            {
                IsInstalled = false,
                DetectionMessage = "CS2 not found (test)"
            });
        }
    }
}
