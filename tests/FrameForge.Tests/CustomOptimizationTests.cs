using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.CustomOptimization;
using FrameForge.Infrastructure.Guided;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Profiles;
using Xunit;

namespace FrameForge.Tests;

public sealed class CustomOptimizationTests
{
    [Fact]
    public void Catalog_BuildsFromSupportedSettings_Only()
    {
        var catalog = new IndividualOptimizationCatalog(new Cs2SettingCatalog());
        var items = catalog.BuildCatalog();
        Assert.True(items.Count > 0);
        Assert.All(items, i => Assert.True(i.Supported));
        Assert.Contains(items, i => i.ConfigKey.Equals("fps_max", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.ConfigKey.Equals("snd_headphone_eq", StringComparison.OrdinalIgnoreCase));
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.False(string.IsNullOrWhiteSpace(i.ExpectedImpactDescription));
            Assert.DoesNotContain("FPS boost", i.ExpectedImpactDescription, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("+10 FPS", i.ExpectedImpactDescription, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Catalog_MapsUiCategories()
    {
        Assert.Equal(
            IndividualOptimizationUiCategory.Performance,
            IndividualOptimizationCatalogFilter.MapUiCategory(Cs2SettingCategory.Video));
        Assert.Equal(
            IndividualOptimizationUiCategory.Mouse,
            IndividualOptimizationCatalogFilter.MapUiCategory(Cs2SettingCategory.KeyboardMouse));
        Assert.Equal(
            IndividualOptimizationUiCategory.Gameplay,
            IndividualOptimizationCatalogFilter.MapUiCategory(Cs2SettingCategory.Game));
    }

    [Fact]
    public void Filter_SearchByNameAndKey()
    {
        var catalog = new IndividualOptimizationCatalog(new Cs2SettingCatalog());
        var all = catalog.BuildCatalog();
        var byName = catalog.Filter(all, new IndividualOptimizationFilter { SearchText = "radar" });
        Assert.Contains(byName, i => i.ConfigKey.Contains("radar", StringComparison.OrdinalIgnoreCase));

        var byKey = catalog.Filter(all, new IndividualOptimizationFilter { SearchText = "fps_max" });
        Assert.Contains(byKey, i => i.ConfigKey.Equals("fps_max", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Filter_ByCategoryAndRisk()
    {
        var catalog = new IndividualOptimizationCatalog(new Cs2SettingCatalog());
        var all = catalog.BuildCatalog();
        var hud = catalog.Filter(all, new IndividualOptimizationFilter
        {
            Category = IndividualOptimizationUiCategory.Hud
        });
        Assert.NotEmpty(hud);
        Assert.All(hud, i => Assert.Equal(IndividualOptimizationUiCategory.Hud, i.UiCategory));

        var low = catalog.Filter(all, new IndividualOptimizationFilter { Risk = RiskLevel.Low });
        Assert.NotEmpty(low);
        Assert.All(low, i => Assert.Equal(RiskLevel.Low, i.Risk));
    }

    [Fact]
    public void Filter_OnlyChangedAndOnlyRecommended()
    {
        var catalog = new IndividualOptimizationCatalog(new Cs2SettingCatalog());
        var all = catalog.BuildCatalog().ToList();
        var fps = all.First(i => i.ConfigKey == "fps_max");
        fps.CurrentValue = "400";
        fps.PendingValue = "0";

        var changed = catalog.Filter(all, new IndividualOptimizationFilter { OnlyChangedFromCurrent = true });
        Assert.Contains(changed, i => i.ConfigKey == "fps_max");

        // Only differ from recommended: current != recommended
        fps.PendingValue = null;
        fps.CurrentValue = "999"; // differs from recommended "0"
        var rec = catalog.Filter(all, new IndividualOptimizationFilter { OnlyDifferFromRecommended = true });
        Assert.Contains(rec, i => i.ConfigKey == "fps_max");
    }

    [Fact]
    public void Filter_DoesNotRequireDisk()
    {
        // Pure in-memory — empty source still works
        var catalog = new IndividualOptimizationCatalog(new Cs2SettingCatalog());
        var result = catalog.Filter(Array.Empty<IndividualOptimizationItem>(), new IndividualOptimizationFilter
        {
            SearchText = "anything"
        });
        Assert.Empty(result);
    }

    [Fact]
    public void SelectSingleAndMultiple_BuildsSettingsMap()
    {
        var single = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fps_max"] = "0"
        };
        Assert.Single(single);

        var multi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fps_max"] = "0",
            ["cl_hud_radar_scale"] = "1.1",
            ["cl_teamid_overhead_always"] = "1"
        };
        Assert.Equal(3, multi.Count);
    }

    [Fact]
    public async Task EmptySelection_GuidedFails()
    {
        var env = GuidedTestEnv.Create();
        var svc = env.CreateGuidedService();
        var run = await svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { },
            BenchmarkConfiguration = new BenchmarkConfiguration { DurationSeconds = 30, WarmupSeconds = 0 }
        });
        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
        Assert.Contains("No settings", run.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomSet_CreateRenameDuplicateDelete()
    {
        var env = StoreEnv.Create();
        var set = new CustomOptimizationSet
        {
            Id = "set-test1",
            Name = "My Set",
            Description = "test",
            Settings = { ["fps_max"] = "0", ["volume"] = "0.5" }
        };
        await env.Store.SaveAsync(set);

        var listed = await env.Store.ListAsync();
        Assert.Contains(listed, s => s.Id == "set-test1");

        await env.Store.RenameAsync("set-test1", "Renamed Set");
        var renamed = await env.Store.GetAsync("set-test1");
        Assert.Equal("Renamed Set", renamed!.Name);

        var copy = await env.Store.DuplicateAsync("set-test1", "Copy Set");
        Assert.NotEqual("set-test1", copy.Id);
        Assert.Equal("Copy Set", copy.Name);
        Assert.Equal(2, copy.Settings.Count);

        await env.Store.DeleteAsync("set-test1");
        Assert.Null(await env.Store.GetAsync("set-test1"));
        Assert.NotNull(await env.Store.GetAsync(copy.Id));
    }

    [Fact]
    public async Task CustomSet_RejectsUnknownAndInvalid()
    {
        var env = StoreEnv.Create();
        var bad = new CustomOptimizationSet
        {
            Id = "bad",
            Name = "Bad",
            Settings = { ["not_a_real_cvar"] = "1" }
        };
        var v = env.Store.Validate(bad);
        Assert.False(v.IsValid);
        Assert.Contains(v.Issues, i => i.Contains("Unknown", StringComparison.OrdinalIgnoreCase));

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Store.SaveAsync(bad));

        var invalidValue = new CustomOptimizationSet
        {
            Id = "bad2",
            Name = "Bad2",
            Settings = { ["fps_max"] = "not-a-number" }
        };
        Assert.False(env.Store.Validate(invalidValue).IsValid);
    }

    [Fact]
    public void CustomSet_RejectsDuplicateKeys_InValidation_ViaDictionarySemantics()
    {
        // Dictionary cannot hold duplicate keys; validation still checks empty/null
        var env = StoreEnv.Create();
        var set = new CustomOptimizationSet
        {
            Name = "ok",
            Settings = { ["fps_max"] = "0", ["FPS_MAX"] = "120" } // case-insensitive dict collapses
        };
        // Depending on dictionary construction - our model uses OrdinalIgnoreCase so second overwrites
        var normalized = new CustomOptimizationSet
        {
            Name = "ok",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fps_max"] = "0"
            }
        };
        Assert.True(env.Store.Validate(normalized).IsValid);
    }

    [Fact]
    public async Task CustomSet_RejectsUnsupportedAndFutureSchema()
    {
        var env = StoreEnv.Create();
        var future = new CustomOptimizationSet
        {
            Id = "fut",
            Name = "Future",
            SchemaVersion = 99,
            Settings = { ["fps_max"] = "0" }
        };
        Assert.False(env.Store.Validate(future).IsValid);

        var path = Path.Combine(env.Root, "future.json");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":99,"id":"x","name":"X","settings":{"fps_max":"0"}}
            """);
        var import = await env.Store.ImportAsync(path);
        Assert.False(import.Success);
        Assert.Contains("schema", import.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomSet_RejectsMalformedJson()
    {
        var env = StoreEnv.Create();
        var path = Path.Combine(env.Root, "bad.json");
        await File.WriteAllTextAsync(path, "{nope");
        var import = await env.Store.ImportAsync(path);
        Assert.False(import.Success);
    }

    [Fact]
    public async Task SaveAsProfile_UsesExistingProfileService()
    {
        var env = StoreEnv.Create();
        var profiles = new ProfileService(env.Paths, env.Log, new Cs2SettingCatalog());
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fps_max"] = "0",
            ["cl_teamid_overhead_always"] = "1"
        };
        var profile = new PerformanceProfile
        {
            Id = "custom-from-set",
            Name = "From Custom Selection",
            Description = "Phase 6 save-as-profile",
            IsCustom = true,
            Settings = map
        };
        await profiles.SaveCustomProfileAsync(profile);
        var loaded = await profiles.GetProfileAsync("custom-from-set");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Settings.Count);
        Assert.True(loaded.IsCustom);
    }

    [Fact]
    public async Task DiffGeneration_ForSelection_UsesSettingsService()
    {
        var settings = new RecordingSettings();
        var snap = await settings.ReadSettingsAsync();
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fps_max"] = "0",
            ["cl_hud_radar_scale"] = "1.2"
        };
        var diff = settings.CreateDiff(snap, desired, "custom selection preview");
        Assert.True(diff.ChangeCount >= 1);
        Assert.All(diff.Entries.Where(e => e.IsChange), e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.ConfigKey));
            Assert.False(string.IsNullOrWhiteSpace(e.Risk));
        });
        // Preview only — no apply
        Assert.Equal(0, settings.ApplyCount);
    }

    [Fact]
    public async Task Guided_SingleSetting_PreviewAndHistory()
    {
        var env = GuidedTestEnv.Create();
        var settings = new RecordingSettings();
        var bench = new ScriptedBench();
        var monitor = new FakeMonitor { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } };
        var svc = env.CreateGuidedService(settings: settings, benchmark: bench, monitor: monitor);

        var preview = await svc.PreviewAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            PreviewOnly = true,
            Label = "fps_max quick"
        });
        Assert.Equal(GuidedOptimizationStatus.Completed, preview.Status);
        Assert.True(preview.PreviewOnly);
        Assert.Equal(0, settings.ApplyCount);
        Assert.Contains(preview.SelectedSettingKeys, k => k.Equals("fps_max", StringComparison.OrdinalIgnoreCase));

        // Full keep path
        var runTask = svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0", ["volume"] = "0.5" },
            Label = "multi",
            CustomSetId = "set-abc",
            CustomSetName = "My multi",
            BenchmarkConfiguration = new BenchmarkConfiguration { DurationSeconds = 30, WarmupSeconds = 0, SampleIntervalMs = 50 }
        });
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        svc.ConfirmApply();
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingDecision, 8000);
        svc.Decide(GuidedUserDecision.Keep);
        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Completed, run.Status);
        Assert.Equal(2, run.SelectedSettingKeys.Count);
        Assert.Equal("set-abc", run.CustomSetId);
        Assert.Equal("My multi", run.CustomSetName);
        Assert.True(run.SelectedSettings.ContainsKey("fps_max"));

        var listed = await env.Store.ListAsync();
        Assert.Contains(listed, r => r.Id == run.Id && r.SelectedSettingKeys.Count == 2);
    }

    [Fact]
    public async Task GuidedHistory_V1Compatible_LoadsWithoutSelectedFields()
    {
        var env = GuidedTestEnv.Create();
        Directory.CreateDirectory(env.Paths.GuidedRunsDirectory);
        var path = Path.Combine(env.Paths.GuidedRunsDirectory, "legacy.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "id": "legacy_run",
              "startedAt": "2026-01-01T00:00:00Z",
              "status": "completed",
              "userDecision": "keep",
              "classification": "neutral",
              "optimizationLabel": "old"
            }
            """);
        var list = await env.Store.ListAsync();
        Assert.Contains(list, r => r.Id == "legacy_run");
        var legacy = list.First(r => r.Id == "legacy_run");
        Assert.NotNull(legacy.SelectedSettingKeys);
        Assert.NotNull(legacy.SelectedSettings);
    }

    [Fact]
    public void RiskParse_DefaultsLow()
    {
        Assert.Equal(RiskLevel.Low, IndividualOptimizationCatalogFilter.ParseRisk(null));
        Assert.Equal(RiskLevel.Low, IndividualOptimizationCatalogFilter.ParseRisk("Low"));
        Assert.Equal(RiskLevel.Medium, IndividualOptimizationCatalogFilter.ParseRisk("Medium"));
        Assert.Equal(RiskLevel.High, IndividualOptimizationCatalogFilter.ParseRisk("high risk"));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task WaitUntil(Func<bool> pred, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (pred()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("condition timeout");
    }

    private sealed class StoreEnv
    {
        public required string Root { get; init; }
        public required PathService Paths { get; init; }
        public required FileAppLog Log { get; init; }
        public required CustomOptimizationSetStore Store { get; init; }

        public static StoreEnv Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ff_cset_" + Guid.NewGuid().ToString("N"));
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            return new StoreEnv
            {
                Root = root,
                Paths = paths,
                Log = log,
                Store = new CustomOptimizationSetStore(paths, log, new Cs2SettingCatalog())
            };
        }
    }

    private sealed class GuidedTestEnv
    {
        public required string Root { get; init; }
        public required PathService Paths { get; init; }
        public required FileAppLog Log { get; init; }
        public required GuidedOptimizationStore Store { get; init; }

        public static GuidedTestEnv Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ff_cg_" + Guid.NewGuid().ToString("N"));
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            return new GuidedTestEnv
            {
                Root = root,
                Paths = paths,
                Log = log,
                Store = new GuidedOptimizationStore(paths, log)
            };
        }

        public GuidedOptimizationService CreateGuidedService(
            RecordingSettings? settings = null,
            ScriptedBench? benchmark = null,
            FakeMonitor? monitor = null)
        {
            settings ??= new RecordingSettings();
            benchmark ??= new ScriptedBench();
            monitor ??= new FakeMonitor();
            var detection = new FakeDetection
            {
                Info = new Cs2InstallInfo
                {
                    IsInstalled = true,
                    InstallPath = Root,
                    CfgDirectory = Path.Combine(Root, "cfg"),
                    DetectionMessage = "ok"
                }
            };
            Directory.CreateDirectory(detection.Info.CfgDirectory!);
            return new GuidedOptimizationService(
                detection,
                monitor,
                settings,
                new FakeProfiles(),
                benchmark,
                benchmark.Store,
                new FrameForge.Benchmark.BenchmarkCalculator(),
                Store,
                Log);
        }
    }

    private sealed class FakeDetection : ICs2DetectionService
    {
        public Cs2InstallInfo Info { get; set; } = Cs2InstallInfo.NotFound("x");
        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);
    }

    private sealed class FakeMonitor : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? Process { get; set; }
        public Cs2ProcessInfo? TryGetCs2Process() => Process;
    }

    private sealed class FakeProfiles : IProfileService
    {
        public IReadOnlyList<string> BuiltInProfileIds { get; } = new[] { "balanced" };
        public Task<IReadOnlyList<PerformanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PerformanceProfile>>(Array.Empty<PerformanceProfile>());
        public Task<PerformanceProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PerformanceProfile?>(null);
        public Task SaveCustomProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteCustomProfileAsync(string profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PerformanceProfile> DuplicateProfileAsync(string sourceProfileId, string? newName = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RenameCustomProfileAsync(string profileId, string newName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportProfileAsync(string profileId, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ProfileImportResult> ImportProfileAsync(string sourcePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProfileImportResult.Fail("n/a"));
        public ProfileValidationResult ValidateProfile(PerformanceProfile profile) => new();
    }

    private sealed class RecordingSettings : ICs2SettingsService
    {
        public int ApplyCount { get; private set; }
        public string ManagedConfigFileName => "frameforge_settings.cfg";
        public string AutoexecFileName => "autoexec.cfg";

        public Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new Cs2SettingCatalog();
            var settings = catalog.GetAll().Select(d => new Cs2SettingValue
            {
                Definition = d,
                CurrentValue = d.DefaultValue,
                RecommendedValue = d.RecommendedValue,
                Source = Cs2SettingSource.Default
            }).ToList();
            return Task.FromResult(new Cs2SettingsSnapshot
            {
                Cs2Available = true,
                CfgDirectory = "/tmp/cfg",
                ManagedConfigPath = "/tmp/cfg/frameforge_settings.cfg",
                AutoexecPath = "/tmp/cfg/autoexec.cfg",
                Message = "ok",
                Settings = settings,
                AffectedFilesOnApply = new[] { "/tmp/cfg/frameforge_settings.cfg" },
                ExecutionStatus = Cs2ConfigExecutionStatus.Wired
            });
        }

        public SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values) => new();

        public SettingsDiff CreateDiff(
            Cs2SettingsSnapshot current,
            IReadOnlyDictionary<string, string> desired,
            string reason,
            string? profileId = null,
            string? profileName = null)
        {
            var entries = desired.Select(kv => new SettingsDiffEntry
            {
                SettingId = kv.Key,
                ConfigKey = kv.Key,
                DisplayName = kv.Key,
                CurrentValue = current.Settings.FirstOrDefault(s =>
                    s.Definition.ConfigKey.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))?.CurrentValue,
                NewValue = kv.Value,
                Reason = reason,
                Risk = "Low"
            }).Where(e => e.IsChange).ToList();
            return new SettingsDiff
            {
                Title = reason,
                Entries = entries,
                AffectedFiles = current.AffectedFilesOnApply
            };
        }

        public Task<SettingsApplyResult> ApplySettingsAsync(
            IReadOnlyDictionary<string, string> desired,
            string reason,
            string? profileId = null,
            bool createBackup = true,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return Task.FromResult(SettingsApplyResult.Ok(
                "ok", "bak", new SettingsDiff { Title = reason }, new[] { "/tmp/cfg/frameforge_settings.cfg" },
                autoexecUpdated: true, executedByCs2: true));
        }

        public Task<SettingsApplyResult> ApplyDiffAsync(
            SettingsDiff diff,
            bool createBackup = true,
            CancellationToken cancellationToken = default)
        {
            var map = diff.Entries.Where(e => e.NewValue is not null)
                .ToDictionary(e => e.ConfigKey, e => e.NewValue!, StringComparer.OrdinalIgnoreCase);
            return ApplySettingsAsync(map, diff.Title, diff.ProfileId, createBackup, cancellationToken);
        }

        public Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Ok("restored", "bak", new SettingsDiff { Title = "r" }, Array.Empty<string>()));

        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
    }

    private sealed class ScriptedBench : IBenchmarkEngine
    {
        public int StartCount { get; private set; }
        public BenchmarkStatus Status { get; private set; } = BenchmarkStatus.Idle;
        public BenchmarkRun? CurrentRun { get; private set; }
        public BenchmarkProgress? LatestProgress { get; private set; }
        public InMemBenchStore Store { get; } = new();
        public event EventHandler? StatusChanged;
#pragma warning disable CS0067
        public event EventHandler<BenchmarkSample>? SampleCaptured;
#pragma warning disable CS0067
        public event EventHandler<BenchmarkProgress>? ProgressChanged;
#pragma warning restore CS0067
#pragma warning restore CS0067

        public async Task<BenchmarkRun> StartAsync(BenchmarkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            StartCount++;
            Status = BenchmarkStatus.Running;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            var cpu = StartCount == 1 ? 55.0 : 48.0;
            var samples = Enumerable.Range(0, 20).Select(i => new BenchmarkSample
            {
                IsWarmup = false,
                SystemCpuPercent = cpu,
                ProcessCpuPercent = cpu / 2,
                SystemMemoryPercent = 40,
                ProcessWorkingSetBytes = 400_000_000,
                Cs2ProcessPresent = true,
                ElapsedMs = i * configuration.SampleIntervalMs
            }).ToList();
            var calc = new FrameForge.Benchmark.BenchmarkCalculator();
            var run = new BenchmarkRun
            {
                Id = "bm" + StartCount,
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
                EndedAt = DateTimeOffset.UtcNow,
                Status = BenchmarkStatus.Completed,
                Configuration = configuration,
                SystemInformation = new BenchmarkSystemSnapshot { CpuName = "t", TotalRamBytes = 8L << 30 },
                Samples = samples,
                Result = calc.Calculate(samples),
                DurationSecondsActual = 30
            };
            await Store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
            CurrentRun = run;
            Status = BenchmarkStatus.Completed;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return run;
        }

        public void RequestStop() { }
        public void RequestCancel() { }
        public BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after) =>
            new FrameForge.Benchmark.BenchmarkCalculator().Compare(before, after);
        public BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after, BenchmarkCompareOptions options) =>
            new FrameForge.Benchmark.BenchmarkCalculator().Compare(before, after, options);
    }

    private sealed class InMemBenchStore : IBenchmarkStore
    {
        private readonly Dictionary<string, BenchmarkRun> _map = new(StringComparer.OrdinalIgnoreCase);
        public Task<string> SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default)
        {
            _map[run.Id] = run;
            return Task.FromResult(run.Id);
        }
        public Task<IReadOnlyList<BenchmarkRun>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkRun>>(_map.Values.ToList());
        public Task<BenchmarkRun?> GetAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.TryGetValue(runId, out var r) ? r : null);
        public Task DeleteAsync(string runId, CancellationToken cancellationToken = default)
        {
            _map.Remove(runId);
            return Task.CompletedTask;
        }
        public Task ExportJsonAsync(string runId, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportCsvAsync(string runId, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
