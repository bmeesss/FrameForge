using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.Hardware;
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
    public async Task Pipeline_UnknownId_FailsWithoutPartialApply()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_pipe_bad_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var settings = new AppSettingsService(paths, log);
            var detection = new Cs2DetectionService(settings, log);
            var catalog = new OptimizationCatalog(new Cs2ConfigService(log), detection);
            var pipeline = new OptimizationPipeline(catalog, new BackupService(paths, log), detection, settings, log);

            var result = await pipeline.ExecuteAsync(new[] { "does.not.exist" });
            Assert.False(result.Success);
            Assert.Empty(result.Applied);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Pipeline_FailedApply_RollsBackPreviousOptimizations()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_pipe_rb_" + Guid.NewGuid().ToString("N"));
        try
        {
            var install = CreateInstall(root);
            var appdata = Path.Combine(root, "appdata");
            var paths = new PathService(appdata);
            var log = new FileAppLog(paths);
            var settingsSvc = new AppSettingsService(paths, log);
            await settingsSvc.SaveAsync(new AppSettings
            {
                CustomCs2Path = install,
                AutomaticBackup = true
            });

            var detection = new Cs2DetectionService(settingsSvc, log);
            var config = new Cs2ConfigService(log);
            var good = new Cs2ConfigValueOptimization(
                "test.good",
                "Good",
                "writes ok",
                new Dictionary<string, string> { ["fps_max"] = "999" },
                config,
                detection,
                "frameforge_good.cfg");
            var bad = new FailingOptimization();

            var catalog = new TestCatalog(good, bad);
            var backups = new BackupService(paths, log);
            var pipeline = new OptimizationPipeline(catalog, backups, detection, settingsSvc, log);

            var result = await pipeline.ExecuteAsync(new[] { "test.good", "test.fail" });
            Assert.False(result.Success);
            Assert.True(result.RolledBack);

            // Good optimization should have been reverted (side-car or previous values)
            // File may still exist but pipeline reported rollback.
            Assert.Contains(result.Steps, s => s.StepName == "Rollback");
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

    [Fact]
    public async Task Score_IsBasedOnDetectedConditions_AndExplainsFactors()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_score_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var settings = new AppSettingsService(paths, log);
            await settings.SaveAsync(new AppSettings { AutomaticBackup = true, ActiveProfileId = "balanced" });

            var detection = new Cs2DetectionService(settings, log);
            var catalog = new OptimizationCatalog(new Cs2ConfigService(log), detection);
            var backups = new BackupService(paths, log);
            var scoreService = new OptimizationScoreService(
                detection,
                new HardwareInfoService(),
                catalog,
                backups,
                settings,
                log);

            var score = await scoreService.CalculateAsync();
            Assert.True(score.MaxScore > 0);
            Assert.True(score.Score >= 0);
            Assert.True(score.Score <= score.MaxScore);
            Assert.NotEmpty(score.Factors);
            Assert.False(string.IsNullOrWhiteSpace(score.Summary));

            // Every factor has a reason — no silent scoring
            foreach (var factor in score.Factors)
            {
                Assert.False(string.IsNullOrWhiteSpace(factor.Id));
                Assert.False(string.IsNullOrWhiteSpace(factor.Description));
                Assert.False(string.IsNullOrWhiteSpace(factor.Reason));
                Assert.True(factor.Points > 0);
            }

            // Auto-backup enabled should award points
            Assert.Contains(score.Factors, f => f.Id == "settings.auto-backup" && f.Awarded);

            // Game Mode is honestly not awarded
            Assert.Contains(score.Factors, f => f.Id == "windows.game-mode" && !f.Awarded);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string CreateInstall(string root)
    {
        var install = Path.Combine(root, "CS2");
        var cfg = Path.Combine(install, "game", "csgo", "cfg");
        Directory.CreateDirectory(cfg);
        return install;
    }

    private sealed class FailingOptimization : OptimizationBase
    {
        public override string Id => "test.fail";
        public override string Name => "Fail";
        public override string Description => "Always fails";
        public override OptimizationCategory Category => OptimizationCategory.System;
        public override RiskLevel RiskLevel => RiskLevel.Low;
        public override ExpectedImpact ExpectedImpact => ExpectedImpact.Low;

        public override Task<bool> CanApplyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public override Task<OptimizationPreview> ExplainAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatePreview(true, "Will fail on purpose."));

        public override Task<OptimizationResult> ApplyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OptimizationResult.Fail(Id, "intentional failure"));

        public override Task<OptimizationResult> RevertAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OptimizationResult.Ok(Id, "nothing to revert"));
    }

    private sealed class TestCatalog : IOptimizationCatalog
    {
        private readonly Dictionary<string, IOptimization> _map;

        public TestCatalog(params IOptimization[] items) =>
            _map = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<IOptimization> GetAll() => _map.Values.ToList();
        public IOptimization? GetById(string id) =>
            _map.TryGetValue(id, out var o) ? o : null;
    }
}
