using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Profiles;
using FrameForge.Core.Models;
using Xunit;

namespace FrameForge.Tests;

public sealed class ProfileTests
{
    [Fact]
    public async Task GetProfiles_IncludesBuiltInCompetitiveBalancedQualityCustom()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var service = new ProfileService(paths, log);

            var profiles = await service.GetProfilesAsync();
            Assert.True(profiles.Count >= 4);
            Assert.Contains(profiles, static p => p.Id == "competitive");
            Assert.Contains(profiles, static p => p.Id == "balanced");
            Assert.Contains(profiles, static p => p.Id == "quality");
            Assert.Contains(profiles, static p => p.Id == "custom");
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task SaveCustomProfile_CanBeReloaded()
    {
        var root = NewTempRoot();
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var service = new ProfileService(paths, log);

            var custom = new PerformanceProfile
            {
                Id = "my-custom",
                Name = "My Custom",
                Description = "Test profile",
                IsCustom = true,
                Cs2Settings = { ["fps_max"] = "300" },
                RecommendedOptimizationIds = { "cs2.cfg.balanced" }
            };

            await service.SaveCustomProfileAsync(custom);
            var loaded = await service.GetProfileAsync("my-custom");
            Assert.NotNull(loaded);
            Assert.Equal("My Custom", loaded!.Name);
            Assert.Equal("300", loaded.Cs2Settings["fps_max"]);
            Assert.True(loaded.IsCustom);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void EmbeddedDefaults_AreDataDriven()
    {
        var defaults = ProfileService.GetEmbeddedDefaults();
        Assert.NotEmpty(defaults);
        foreach (var profile in defaults)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
        }
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
