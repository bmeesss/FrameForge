using FrameForge.Core.Models;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Profiles;
using Xunit;

namespace FrameForge.Tests;

public sealed class ProfileTests
{
    [Fact]
    public async Task GetProfiles_IncludesBuiltInCompetitiveBalancedQuality()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var service = new ProfileService(paths, log, new Cs2SettingCatalog());

            var profiles = await service.GetProfilesAsync();
            Assert.True(profiles.Count >= 3);
            Assert.Contains(profiles, static p => p.Id == "competitive");
            Assert.Contains(profiles, static p => p.Id == "balanced");
            Assert.Contains(profiles, static p => p.Id == "quality");

            var competitive = profiles.First(p => p.Id == "competitive");
            Assert.True(competitive.IsBuiltIn);
            Assert.False(competitive.IsCustom);
            Assert.Equal(1, competitive.SchemaVersion);
            Assert.True(competitive.Settings.Count > 0);
            Assert.True(competitive.SettingCount == competitive.Settings.Count);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task SaveCustomProfile_CanBeReloaded_AndHasTimestamps()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var service = new ProfileService(paths, log, new Cs2SettingCatalog());

            var before = DateTimeOffset.UtcNow.AddMinutes(-1);
            var custom = new PerformanceProfile
            {
                Id = "my-custom",
                Name = "My Custom",
                Description = "Test profile",
                IsCustom = true,
                Settings = { ["fps_max"] = "300" },
                RecommendedOptimizationIds = { "cs2.cfg.balanced" }
            };

            await service.SaveCustomProfileAsync(custom);
            var loaded = await service.GetProfileAsync("my-custom");
            Assert.NotNull(loaded);
            Assert.Equal("My Custom", loaded!.Name);
            Assert.Equal("300", loaded.Settings["fps_max"]);
            Assert.True(loaded.IsCustom);
            Assert.False(loaded.IsBuiltIn);
            Assert.Equal(ProfileSchema.CurrentVersion, loaded.SchemaVersion);
            Assert.True(loaded.CreatedAt >= before);
            Assert.True(loaded.UpdatedAt >= loaded.CreatedAt);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task BuiltInProfiles_CannotBeDeletedOrOverwritten()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteCustomProfileAsync("competitive"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SaveCustomProfileAsync(new PerformanceProfile
                {
                    Id = "balanced",
                    Name = "Hacked",
                    Settings = { ["fps_max"] = "1" }
                }));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RenameCustomProfileAsync("quality", "Nope"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task Duplicate_CreatesCustomCopy()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var copy = await service.DuplicateProfileAsync("competitive", "My Comp Copy");
            Assert.False(copy.IsBuiltIn);
            Assert.True(copy.IsCustom);
            Assert.Equal("My Comp Copy", copy.Name);
            Assert.True(copy.Id.StartsWith("custom-", StringComparison.Ordinal));
            Assert.Equal(
                (await service.GetProfileAsync("competitive"))!.Settings["fps_max"],
                copy.Settings["fps_max"]);

            var reloaded = await service.GetProfileAsync(copy.Id);
            Assert.NotNull(reloaded);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task ExportImport_RoundTrips_AndRejectsFutureSchema()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var exportPath = Path.Combine(root, "out" + ProfileSchema.FileExtension);
            await service.ExportProfileAsync("balanced", exportPath);
            Assert.True(File.Exists(exportPath));

            var json = await File.ReadAllTextAsync(exportPath);
            Assert.Contains("schemaVersion", json);
            Assert.Contains("\"schemaVersion\":1", json.Replace(" ", ""));

            var imported = await service.ImportProfileAsync(exportPath);
            Assert.True(imported.Success, imported.Message);
            Assert.NotNull(imported.Profile);
            Assert.False(imported.Profile!.IsBuiltIn);
            Assert.True(imported.Profile.IsCustom);
            // Built-in id must not be reused
            Assert.NotEqual("balanced", imported.Profile.Id);

            // Future schema rejected
            var futurePath = Path.Combine(root, "future.json");
            await File.WriteAllTextAsync(futurePath, """
                {
                  "schemaVersion": 99,
                  "id": "future-profile",
                  "name": "Future",
                  "description": "too new",
                  "settings": { "fps_max": "0" }
                }
                """);
            var reject = await service.ImportProfileAsync(futurePath);
            Assert.False(reject.Success);
            Assert.Contains("schema", reject.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task Import_RejectsInvalidValues_NeverExecutesContent()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            var badPath = Path.Combine(root, "bad.frameforge-profile.json");
            await File.WriteAllTextAsync(badPath, """
                {
                  "schemaVersion": 1,
                  "id": "evil",
                  "name": "Evil",
                  "description": "bad values",
                  "settings": {
                    "fps_max": "not-a-number",
                    "volume": "999"
                  }
                }
                """);

            var result = await service.ImportProfileAsync(badPath);
            Assert.False(result.Success);
            Assert.Contains("Invalid", result.Message, StringComparison.OrdinalIgnoreCase);

            // Unknown keys rejected when catalog is present
            var unknownPath = Path.Combine(root, "unknown.json");
            await File.WriteAllTextAsync(unknownPath, """
                {
                  "schemaVersion": 1,
                  "id": "unk",
                  "name": "Unk",
                  "settings": { "sv_cheats": "1" }
                }
                """);
            var unk = await service.ImportProfileAsync(unknownPath);
            Assert.False(unk.Success);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task DeleteCustom_RemovesFile_RenameWorks()
    {
        var root = NewTempRoot();
        try
        {
            var service = CreateService(root);
            await service.SaveCustomProfileAsync(new PerformanceProfile
            {
                Id = "to-delete",
                Name = "Temp",
                Settings = { ["fps_max"] = "120" }
            });

            await service.RenameCustomProfileAsync("to-delete", "Renamed Temp");
            var renamed = await service.GetProfileAsync("to-delete");
            Assert.NotNull(renamed);
            Assert.Equal("Renamed Temp", renamed!.Name);

            await service.DeleteCustomProfileAsync("to-delete");
            Assert.Null(await service.GetProfileAsync("to-delete"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void ValidateProfile_RequiresNameAndValidSettings()
    {
        var service = CreateService(NewTempRoot());
        var empty = service.ValidateProfile(new PerformanceProfile());
        Assert.False(empty.IsValid);

        var ok = service.ValidateProfile(new PerformanceProfile
        {
            Name = "OK",
            Settings = { ["fps_max"] = "0", ["volume"] = "0.5" }
        });
        Assert.True(ok.IsValid, string.Join("; ", ok.Issues));
    }

    [Fact]
    public void EmbeddedDefaults_AreDataDriven_NoFpsClaims()
    {
        var defaults = ProfileService.GetEmbeddedDefaults();
        Assert.Equal(3, defaults.Count);
        foreach (var profile in defaults)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
            Assert.True(profile.IsBuiltIn);
            Assert.Equal(1, profile.SchemaVersion);
            Assert.DoesNotContain("FPS boost", profile.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("+FPS", profile.Description, StringComparison.OrdinalIgnoreCase);
            Assert.True(profile.Settings.Count >= 5);
        }
    }

    private static ProfileService CreateService(string root)
    {
        var paths = new PathService(root);
        var log = new FileAppLog(paths, LogLevelSetting.Warning);
        return new ProfileService(paths, log, new Cs2SettingCatalog());
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "ff_prof_" + Guid.NewGuid().ToString("N"));

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
}
