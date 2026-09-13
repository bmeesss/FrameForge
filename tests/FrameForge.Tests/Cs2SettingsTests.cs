using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Profiles;
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

        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Video);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Hud);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Game);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.KeyboardMouse);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Audio);
        Assert.Contains(all, d => d.Category == Cs2SettingCategory.Communication);

        Assert.NotNull(catalog.GetByConfigKey("fps_max"));
        Assert.NotNull(catalog.GetByConfigKey("engine_low_latency_sleep_after_client_tick"));
        Assert.NotNull(catalog.GetByConfigKey("zoom_sensitivity_ratio_mouse"));
        Assert.Null(catalog.GetByConfigKey("sv_cheats"));
        Assert.Null(catalog.GetByConfigKey("snd_musicvolume_multiplier")); // disabled
        Assert.Null(catalog.GetByConfigKey("cl_auto_cursor_defend")); // disabled
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
            Assert.Equal(Cs2ConfigExecutionStatus.Unavailable, snap.ExecutionStatus);
            Assert.NotEmpty(snap.Settings);
            Assert.All(snap.Settings, s => Assert.Equal(Cs2SettingSource.Unavailable, s.Source));
            Assert.NotNull(snap.ExecutionRecommendation);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ReadSettings_DiscoversRealCfgDirectory_FromDetection()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "game", "csgo", "cfg");
            Directory.CreateDirectory(cfgDir);
            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "synthetic"
            };

            var snap = await svc.ReadSettingsAsync();
            Assert.True(snap.Cs2Available);
            Assert.Equal(cfgDir, snap.CfgDirectory);
            Assert.Equal(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName), snap.ManagedConfigPath);
            Assert.Equal(Path.Combine(cfgDir, "autoexec.cfg"), snap.AutoexecPath);
            Assert.Equal(Cs2ConfigExecutionStatus.NotInstalled, snap.ExecutionStatus);
            Assert.Contains(snap.AffectedFilesOnApply, p => p.EndsWith("frameforge_settings.cfg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(snap.AffectedFilesOnApply, p => p.EndsWith("autoexec.cfg", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void AutoexecIntegration_PreservesUserContent_WithoutMarkers()
    {
        const string user = "// user settings\nsensitivity 1.5\nbind \"f\" \"slot1\"\n";
        var next = Cs2AutoexecIntegration.EnsureFrameForgeSection(user);
        Assert.Contains("// user settings", next);
        Assert.Contains("sensitivity 1.5", next);
        Assert.Contains("bind \"f\" \"slot1\"", next);
        Assert.True(Cs2AutoexecIntegration.HasFrameForgeSection(next));
        Assert.True(Cs2AutoexecIntegration.SectionExecutesManagedFile(next));
        Assert.Contains(Cs2AutoexecIntegration.BeginMarker, next);
        Assert.Contains(Cs2AutoexecIntegration.EndMarker, next);
        Assert.Contains(Cs2AutoexecIntegration.ExecCommand, next);

        // User-owned content still contains original lines
        var owned = Cs2AutoexecIntegration.GetUserOwnedContent(next);
        Assert.Contains("sensitivity 1.5", owned);
        Assert.DoesNotContain(Cs2AutoexecIntegration.BeginMarker, owned);
    }

    [Fact]
    public void AutoexecIntegration_UpdatesOnlyFrameForgeSection_WhenMarkersExist()
    {
        var existing =
            "// keep me forever\n" +
            "volume 0.9\n" +
            Cs2AutoexecIntegration.BeginMarker + "\n" +
            "// old junk\n" +
            "exec something_else.cfg\n" +
            Cs2AutoexecIntegration.EndMarker + "\n" +
            "// after section\n";

        var next = Cs2AutoexecIntegration.EnsureFrameForgeSection(existing);
        Assert.Contains("keep me forever", next);
        Assert.Contains("volume 0.9", next);
        Assert.Contains("after section", next);
        Assert.DoesNotContain("something_else.cfg", next);
        Assert.True(Cs2AutoexecIntegration.SectionExecutesManagedFile(next));

        // Second ensure is stable for exec presence
        var again = Cs2AutoexecIntegration.EnsureFrameForgeSection(next);
        Assert.True(Cs2AutoexecIntegration.SectionExecutesManagedFile(again));
        Assert.Contains("keep me forever", again);
    }

    [Fact]
    public void AutoexecIntegration_EmptyContent_CreatesSectionOnly()
    {
        var next = Cs2AutoexecIntegration.EnsureFrameForgeSection(null);
        Assert.True(Cs2AutoexecIntegration.HasFrameForgeSection(next));
        Assert.True(Cs2AutoexecIntegration.SectionExecutesManagedFile(next));
    }

    [Fact]
    public async Task CreateDiff_OnlyIncludesActualChanges_AndReportsAffectedFiles()
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
                ["fps_max"] = "400",
                ["volume"] = "0.5",
                ["m_rawinput"] = "1"
            }, "unit-test");

            Assert.True(diff.HasChanges);
            Assert.True(diff.AutoexecIntegrationChange);
            Assert.Contains(diff.AffectedFiles, f => f.EndsWith("autoexec.cfg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(diff.AffectedFiles, f => f.EndsWith("frameforge_settings.cfg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(diff.Entries, e => e.ConfigKey.Equals("volume", StringComparison.OrdinalIgnoreCase) && e.IsChange);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplySettings_CreatesManagedFile_WiresAutoexec_PreservesUser_AndBacksUp()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var autoexecPath = Path.Combine(cfgDir, "autoexec.cfg");
            const string userBlock = "// user settings\nfps_max 300\n// user note keep\nbind \"p\" \"say hello\"\n";
            await File.WriteAllTextAsync(autoexecPath, userBlock);
            var userBytesBefore = await File.ReadAllBytesAsync(autoexecPath);

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
            Assert.True(result.AutoexecIntegrationUpdated);
            Assert.True(result.ManagedConfigExecutedByCs2);
            Assert.Contains(result.WrittenFiles, f => f.EndsWith("frameforge_settings.cfg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.WrittenFiles, f => f.EndsWith("autoexec.cfg", StringComparison.OrdinalIgnoreCase));

            var managed = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            Assert.True(File.Exists(managed));
            var managedText = await File.ReadAllTextAsync(managed);
            Assert.Contains("fps_max", managedText);
            Assert.Contains("0", managedText);

            var autoexec = await File.ReadAllTextAsync(autoexecPath);
            Assert.Contains("user note keep", autoexec);
            Assert.Contains("bind \"p\" \"say hello\"", autoexec);
            Assert.Contains("// user settings", autoexec);
            Assert.Contains(Cs2AutoexecIntegration.BeginMarker, autoexec);
            Assert.Contains(Cs2AutoexecIntegration.ExecCommand, autoexec);
            Assert.Contains(Cs2AutoexecIntegration.EndMarker, autoexec);
            // User fps_max line outside markers still present (managed file overrides at exec time)
            Assert.Contains("fps_max 300", autoexec);

            var settings = await appSettings.LoadAsync();
            Assert.Equal(result.BackupId, settings.LastSettingsBackupId);

            var listed = await backups.ListBackupsAsync();
            Assert.Contains(listed, b => b.Id == result.BackupId);
            var bak = listed.First(b => b.Id == result.BackupId);
            Assert.Contains(bak.AffectedFiles, f => f.EndsWith("autoexec.cfg", StringComparison.OrdinalIgnoreCase));

            var snap = await svc.ReadSettingsAsync();
            Assert.Equal(Cs2ConfigExecutionStatus.Wired, snap.ExecutionStatus);
            var fps = snap.Settings.First(s => s.Definition.ConfigKey == "fps_max");
            Assert.Equal("0", fps.CurrentValue);
            Assert.Equal(Cs2SettingSource.FrameForgeManaged, fps.Source);

            // Original user bytes are not identical after marker insert — but user lines remain
            var userBytesAfter = await File.ReadAllBytesAsync(autoexecPath);
            Assert.False(userBytesBefore.SequenceEqual(userBytesAfter));
            Assert.True(autoexec.Contains("user note keep", StringComparison.Ordinal));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplySettings_ExistingAutoexecWithMarkers_OnlyUpdatesSection()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var autoexecPath = Path.Combine(cfgDir, "autoexec.cfg");
            var content =
                "// top user\n" +
                Cs2AutoexecIntegration.BeginMarker + "\n" +
                "exec old.cfg\n" +
                Cs2AutoexecIntegration.EndMarker + "\n" +
                "// bottom user\n";
            await File.WriteAllTextAsync(autoexecPath, content);

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var result = await svc.ApplySettingsAsync(
                new Dictionary<string, string> { ["fps_max"] = "240" },
                "markers");
            Assert.True(result.Success, result.Message);

            var after = await File.ReadAllTextAsync(autoexecPath);
            Assert.Contains("// top user", after);
            Assert.Contains("// bottom user", after);
            Assert.DoesNotContain("exec old.cfg", after);
            Assert.Contains(Cs2AutoexecIntegration.ExecCommand, after);
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
            Assert.False(File.Exists(Path.Combine(cfgDir, "autoexec.cfg")));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplySettings_MissingCfgDirectory_Fails()
    {
        var root = NewTempRoot();
        try
        {
            var svc = CreateService(root, out var detection, out _, out _, cfgDir: null);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = false,
                DetectionMessage = "missing"
            };

            var result = await svc.ApplySettingsAsync(
                new Dictionary<string, string> { ["fps_max"] = "0" },
                "no-cfg");
            Assert.False(result.Success);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task Restore_RestoresExactAutoexecAndManaged_ByteLevel()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var autoexecPath = Path.Combine(cfgDir, "autoexec.cfg");
            var originalAutoexec = "// original user\nsensitivity 2.0\n" + new string('x', 128) + "\n";
            await File.WriteAllTextAsync(autoexecPath, originalAutoexec);
            var originalAutoexecBytes = await File.ReadAllBytesAsync(autoexecPath);

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var apply = await svc.ApplySettingsAsync(
                new Dictionary<string, string> { ["fps_max"] = "0", ["volume"] = "0.4" },
                "change");
            Assert.True(apply.Success, apply.Message);
            Assert.True(File.Exists(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName)));

            // Mutate further after apply
            await File.WriteAllTextAsync(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName), "fps_max 999\n");
            await File.AppendAllTextAsync(autoexecPath, "\n// post-apply user line should remain only if outside restore\n");

            var restore = await svc.RestoreLastFrameForgeChangesAsync();
            Assert.True(restore.Success, restore.Message);

            var restoredAutoexec = await File.ReadAllBytesAsync(autoexecPath);
            Assert.Equal(originalAutoexecBytes.Length, restoredAutoexec.Length);
            Assert.True(originalAutoexecBytes.SequenceEqual(restoredAutoexec));

            // Managed file did not exist before apply → should be deleted on restore
            Assert.False(File.Exists(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName)));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task RestoreLastFrameForgeChanges_RestoresManagedFile_WhenPreviouslyExisted()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var managed = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            var autoexec = Path.Combine(cfgDir, "autoexec.cfg");
            await File.WriteAllTextAsync(managed, "fps_max 111\n");
            await File.WriteAllTextAsync(autoexec, Cs2AutoexecIntegration.EnsureFrameForgeSection("// user\n"));
            var managedBytes = await File.ReadAllBytesAsync(managed);
            var autoBytes = await File.ReadAllBytesAsync(autoexec);

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

            await File.WriteAllTextAsync(managed, "fps_max 999\n");

            var restore = await svc.RestoreLastFrameForgeChangesAsync();
            Assert.True(restore.Success, restore.Message);

            var afterManaged = await File.ReadAllBytesAsync(managed);
            Assert.True(managedBytes.SequenceEqual(afterManaged));
            var afterAuto = await File.ReadAllBytesAsync(autoexec);
            Assert.True(autoBytes.SequenceEqual(afterAuto));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ApplyDiff_NoChanges_WhenAlreadyWiredAndMatching_IsSuccessNoop()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var managed = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            var autoexec = Path.Combine(cfgDir, "autoexec.cfg");
            await File.WriteAllTextAsync(managed, "fps_max 0\n");
            await File.WriteAllTextAsync(autoexec, Cs2AutoexecIntegration.EnsureFrameForgeSection(null));

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var snap = await svc.ReadSettingsAsync();
            Assert.Equal(Cs2ConfigExecutionStatus.Wired, snap.ExecutionStatus);
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

    [Fact]
    public async Task ProfileApplication_WritesRealCfg_AndReportsFiles()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(Path.Combine(cfgDir, "autoexec.cfg"), "// profile user\n");

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var profiles = new ProfileService(new PathService(root), new FileAppLog(new PathService(root)), new Cs2SettingCatalog());
            var competitive = await profiles.GetProfileAsync("competitive");
            Assert.NotNull(competitive);

            var snap = await svc.ReadSettingsAsync();
            var diff = svc.CreateDiff(snap, competitive!.Settings, "profile", competitive.Id, competitive.Name);
            Assert.True(diff.HasChanges);
            Assert.True(diff.AffectedFiles.Count >= 1);

            var result = await svc.ApplyDiffAsync(diff);
            Assert.True(result.Success, result.Message);
            Assert.True(result.WrittenFiles.Count >= 1);
            Assert.True(result.ManagedConfigExecutedByCs2);

            var managed = await File.ReadAllTextAsync(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName));
            Assert.Contains("fps_max", managed);
            var auto = await File.ReadAllTextAsync(Path.Combine(cfgDir, "autoexec.cfg"));
            Assert.Contains("profile user", auto);
            Assert.Contains(Cs2AutoexecIntegration.ExecCommand, auto);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task DetectExecutionStatus_ReportsNotExecuted_WhenManagedWithoutHook()
    {
        var root = NewTempRoot();
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName), "fps_max 0\n");
            await File.WriteAllTextAsync(Path.Combine(cfgDir, "autoexec.cfg"), "// no frameforge\n");

            var svc = CreateService(root, out var detection, out _, out _, cfgDir);
            detection.InstallOverride = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = root,
                CfgDirectory = cfgDir,
                DetectionMessage = "ok"
            };

            var snap = await svc.DetectExecutionStatusAsync();
            Assert.Equal(Cs2ConfigExecutionStatus.ManagedFilePresentButNotExecuted, snap.ExecutionStatus);
            Assert.Contains("not currently executed", snap.ExecutionRecommendation!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not modify Steam launch options", snap.ExecutionRecommendation!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("manually", snap.ExecutionRecommendation!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task Backup_CreatedFiles_AreDeletedOnRestore()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var backups = new BackupService(paths, log);
            var newFile = Path.Combine(root, "brand-new.cfg");

            var entry = await backups.CreateBackupAsync(
                "new-file",
                new[] { "tag" },
                new[] { newFile },
                new Dictionary<string, string?>());

            Assert.Contains(entry.CreatedFiles, f => f.Equals(newFile, StringComparison.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(newFile, "created after backup");
            Assert.True(File.Exists(newFile));

            var restore = await backups.RestoreAsync(entry.Id);
            Assert.True(restore.Success, restore.Message);
            Assert.False(File.Exists(newFile));
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
