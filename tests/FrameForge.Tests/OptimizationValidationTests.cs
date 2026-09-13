using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Settings;
using FrameForge.Optimization;
using FrameForge.Optimization.Optimizations;
using Xunit;

namespace FrameForge.Tests;

public sealed class OptimizationValidationTests
{
    [Fact]
    public void Catalog_ContainsUniqueIdsAndRequiredMetadata()
    {
        var config = new Cs2ConfigService();
        var detection = new Cs2DetectionService();
        var catalog = new OptimizationCatalog(config, detection);

        var all = catalog.GetAll();
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(o => o.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var opt in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(opt.Id));
            Assert.False(string.IsNullOrWhiteSpace(opt.Name));
            Assert.False(string.IsNullOrWhiteSpace(opt.Description));
            Assert.NotNull(opt.BackupRequirements);
        }
    }

    [Fact]
    public async Task AdvisoryOptimizations_CannotApply()
    {
        var power = new RecommendHighPerformancePowerPlan();
        var launch = new SafeCs2LaunchOptionsHint();

        Assert.False(await power.CanApplyAsync());
        Assert.False(await launch.CanApplyAsync());

        var powerPreview = await power.ExplainAsync();
        Assert.False(powerPreview.CanApply);
        Assert.NotNull(powerPreview.BlockReason);
    }

    [Fact]
    public async Task Cs2ConfigOptimization_ApplyAndRevert_OnSyntheticInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_opt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var install = CreateInstall(root);
            var settingsPathRoot = Path.Combine(root, "appdata");
            var paths = new PathService(settingsPathRoot);
            var log = new FileAppLog(paths);
            var settings = new AppSettingsService(paths, log);
            await settings.SaveAsync(new AppSettings { CustomCs2Path = install, AutomaticBackup = true });

            var detection = new Cs2DetectionService(settings, log);
            var config = new Cs2ConfigService(log);
            var opt = new Cs2ConfigValueOptimization(
                id: "test.cfg",
                name: "Test",
                description: "Test cfg write",
                values: new Dictionary<string, string> { ["fps_max"] = "123" },
                configService: config,
                detectionService: detection,
                relativeCfgPath: "frameforge_test.cfg");

            Assert.True(await opt.CanApplyAsync());
            var apply = await opt.ApplyAsync();
            Assert.True(apply.Success, apply.Message);

            var cfgPath = Path.Combine(install, "game", "csgo", "cfg", "frameforge_test.cfg");
            Assert.True(File.Exists(cfgPath));
            var content = await File.ReadAllTextAsync(cfgPath);
            Assert.Contains("fps_max", content);
            Assert.Contains("123", content);

            var revert = await opt.RevertAsync();
            Assert.True(revert.Success, revert.Message);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Pipeline_SkipsNonApplicable_WithoutFailing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_pipe_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var settings = new AppSettingsService(paths, log);
            var detection = new Cs2DetectionService(settings, log);
            var config = new Cs2ConfigService(log);
            var catalog = new OptimizationCatalog(config, detection);
            var backups = new BackupService(paths, log);
            var pipeline = new OptimizationPipeline(catalog, backups, detection, settings, log);

            var result = await pipeline.ExecuteAsync(new[]
            {
                RecommendHighPerformancePowerPlan.OptimizationId,
                SafeCs2LaunchOptionsHint.OptimizationId
            });

            Assert.True(result.Success);
            Assert.Contains(result.Steps, static s => s.StepName == "Preview");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Catalog_GetById_IsCaseInsensitive()
    {
        var catalog = new OptimizationCatalog(new Cs2ConfigService(), new Cs2DetectionService());
        var a = catalog.GetById("cs2.cfg.balanced");
        var b = catalog.GetById("CS2.CFG.BALANCED");
        Assert.NotNull(a);
        Assert.Same(a, b);
    }

    private static string CreateInstall(string root)
    {
        var install = Path.Combine(root, "CS2");
        var cfg = Path.Combine(install, "game", "csgo", "cfg");
        Directory.CreateDirectory(cfg);
        return install;
    }
}
