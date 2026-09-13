using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Guided;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Performance;
using FrameForge.Infrastructure.Settings;
using Xunit;

namespace FrameForge.Tests;

public sealed class PerformanceIntelligenceTests
{
    [Fact]
    public void Fingerprint_IsStable_ForSameHardware()
    {
        var hw = new HardwareInfo
        {
            CpuName = "Test CPU 8C",
            GpuName = "Test GPU",
            TotalRamBytes = 16L << 30,
            OsDescription = "TestOS 1.0",
            Architecture = "X64"
        };
        var svc = new SystemFingerprintService(new FixedHardware(hw));
        var a = svc.FromHardware(hw);
        var b = svc.FromHardware(hw);
        Assert.Equal(a.FingerprintId, b.FingerprintId);
        Assert.False(string.IsNullOrWhiteSpace(a.FingerprintId));
        Assert.Equal(16, a.FingerprintId.Length);
    }

    [Fact]
    public void Fingerprint_Changes_WhenCpuChanges()
    {
        var svc = new SystemFingerprintService(new FixedHardware(new HardwareInfo()));
        var a = svc.FromHardware(new HardwareInfo { CpuName = "CPU-A", GpuName = "G", TotalRamBytes = 8L << 30, OsDescription = "OS", Architecture = "X64" });
        var b = svc.FromHardware(new HardwareInfo { CpuName = "CPU-B", GpuName = "G", TotalRamBytes = 8L << 30, OsDescription = "OS", Architecture = "X64" });
        Assert.NotEqual(a.FingerprintId, b.FingerprintId);
    }

    [Fact]
    public void Fingerprint_ExcludesSensitiveLookingValues()
    {
        Assert.True(SystemFingerprintService.LooksSensitive("user@example.com"));
        Assert.True(SystemFingerprintService.LooksSensitive("192.168.1.10"));
        Assert.True(SystemFingerprintService.LooksSensitive("AA:BB:CC:DD:EE:FF"));
        Assert.False(SystemFingerprintService.LooksSensitive("AMD Ryzen 7"));

        var fp = new SystemFingerprintService(new FixedHardware(new HardwareInfo())).FromHardware(
            new HardwareInfo
            {
                CpuName = "user@example.com",
                GpuName = "AA:BB:CC:DD:EE:FF",
                WindowsVersion = "10.0.1.5",
                Architecture = "X64",
                TotalRamBytes = 8L << 30
            });
        Assert.Equal("unknown", fp.CpuModel);
        Assert.Equal("unknown", fp.GpuModel);
        // IPv4-looking OS version also scrubbed
        Assert.Equal("unknown", fp.OsVersion);
    }

    [Fact]
    public void Fingerprint_Unknown_WhenMissingFields()
    {
        var fp = new SystemFingerprintService(new FixedHardware(new HardwareInfo())).FromHardware(new HardwareInfo());
        Assert.Equal("unknown", fp.CpuModel);
        Assert.Equal("unknown", fp.GpuModel);
        Assert.Null(fp.TotalRamBytes);
        Assert.False(string.IsNullOrWhiteSpace(fp.FingerprintId));
    }

    [Fact]
    public void Confidence_Rules_DocumentedThresholds()
    {
        Assert.Equal(PerformanceConfidence.Unknown,
            PerformanceConfidenceRules.Compute(0, 0, 0, 0, out var r0));
        Assert.Contains("0 valid", r0, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(PerformanceConfidence.Unknown,
            PerformanceConfidenceRules.Compute(0, 0, 0, 3, out _));

        Assert.Equal(PerformanceConfidence.Low,
            PerformanceConfidenceRules.Compute(1, 1, 0, 0, out _));

        Assert.Equal(PerformanceConfidence.Medium,
            PerformanceConfidenceRules.Compute(3, 3, 0, 0, out _));

        Assert.Equal(PerformanceConfidence.High,
            PerformanceConfidenceRules.Compute(4, 4, 0, 0, out _));

        Assert.Equal(PerformanceConfidence.Low,
            PerformanceConfidenceRules.Compute(4, 2, 2, 0, out var conflict));
        Assert.Contains("conflict", conflict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Aggregation_SingleSetting_IsDirectEvidence()
    {
        var env = IntelEnv.Create();
        await env.SaveRun(CreateRun(
            id: "r1",
            keys: new[] { "fps_max" },
            classification: GuidedResultClassification.Improved,
            complete: true));

        var index = await env.Intel.RebuildAsync();
        var rec = index.Records.FirstOrDefault(r => r.SettingKey == "fps_max");
        Assert.NotNull(rec);
        Assert.Equal(1, rec!.DirectTestCount);
        Assert.Equal(0, rec.AssociatedMultiSettingTestCount);
        Assert.Equal(1, rec.DirectImprovedCount);
        Assert.Equal(PerformanceConfidence.Low, rec.Confidence);
        Assert.Single(rec.DirectEvidence);
        Assert.Equal(PerformanceEvidenceType.SingleSetting, rec.DirectEvidence[0].EvidenceType);
    }

    [Fact]
    public async Task Aggregation_MultiSetting_IsAssociated_NotCausal()
    {
        var env = IntelEnv.Create();
        await env.SaveRun(CreateRun(
            id: "m1",
            keys: new[] { "fps_max", "cl_hud_radar_scale" },
            classification: GuidedResultClassification.Improved,
            complete: true));

        var index = await env.Intel.RebuildAsync();
        var fps = index.Records.First(r => r.SettingKey == "fps_max");
        Assert.Equal(0, fps.DirectTestCount);
        Assert.Equal(1, fps.AssociatedMultiSettingTestCount);
        Assert.Equal(PerformanceConfidence.Unknown, fps.Confidence);
        Assert.Contains("multi-setting", fps.RecommendationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fps.DirectEvidence, e => true);
        Assert.Single(fps.AssociatedEvidence);
        Assert.Equal(PerformanceEvidenceType.MultiSetting, fps.AssociatedEvidence[0].EvidenceType);
    }

    [Fact]
    public async Task Aggregation_MixedResults_AndInsufficientData()
    {
        var env = IntelEnv.Create();
        await env.SaveRun(CreateRun("a", new[] { "fps_max" }, GuidedResultClassification.Improved, true));
        await env.SaveRun(CreateRun("b", new[] { "fps_max" }, GuidedResultClassification.Regressed, true));

        var index = await env.Intel.RebuildAsync();
        var rec = index.Records.First(r => r.SettingKey == "fps_max");
        Assert.Equal(2, rec.DirectTestCount);
        Assert.Equal(PerformanceConfidence.Low, rec.Confidence);
        Assert.Contains("mixed", rec.RecommendationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GuidedResultClassification.Mixed, rec.AggregateDirectClassification);
    }

    [Fact]
    public async Task Aggregation_MissingMetrics_StillRecordsClassification()
    {
        var env = IntelEnv.Create();
        var run = CreateRun("nometric", new[] { "volume" }, GuidedResultClassification.Neutral, complete: true);
        run.ComparisonRows.Clear(); // missing metrics
        // still has benchmark ids so BenchmarkComplete becomes false in evidence
        await env.SaveRun(run);

        var index = await env.Intel.RebuildAsync();
        var rec = index.Records.First(r => r.SettingKey == "volume");
        Assert.True(rec.HasEvidence);
        // incomplete benchmarks → not valid direct → confidence unknown/low path
        Assert.True(rec.Confidence is PerformanceConfidence.Unknown or PerformanceConfidence.Low);
        Assert.Null(rec.AverageMetricChanges.SystemCpuDelta);
    }

    [Fact]
    public async Task Aggregation_MetricAverages_OnlyWhenPresent()
    {
        var env = IntelEnv.Create();
        var run = CreateRun("cpu", new[] { "fps_max" }, GuidedResultClassification.Improved, true);
        run.ComparisonRows =
        [
            new GuidedComparisonRow
            {
                Metric = "System CPU average",
                Unit = "%",
                Before = 60,
                After = 50,
                AbsoluteDifference = -10,
                IsAvailable = true
            },
            new GuidedComparisonRow
            {
                Metric = "Frame-time ms",
                Unit = "ms",
                IsAvailable = false
            }
        ];
        await env.SaveRun(run);
        var index = await env.Intel.RebuildAsync();
        var rec = index.Records.First(r => r.SettingKey == "fps_max");
        Assert.NotNull(rec.AverageMetricChanges.SystemCpuDelta);
        Assert.Equal(-10, rec.AverageMetricChanges.SystemCpuDelta!.Value, 3);
        Assert.Null(rec.AverageMetricChanges.FrameTimeMsDelta);
    }

    [Fact]
    public async Task HistoryLoading_OldSchemaV1_Compatible()
    {
        var env = IntelEnv.Create();
        Directory.CreateDirectory(env.Paths.GuidedRunsDirectory);
        var path = Path.Combine(env.Paths.GuidedRunsDirectory, "old.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "id": "legacy_intel",
              "startedAt": "2026-02-01T00:00:00Z",
              "completedAt": "2026-02-01T00:05:00Z",
              "status": "completed",
              "classification": "improved",
              "userDecision": "keep",
              "initialBenchmarkId": "b1",
              "postBenchmarkId": "b2",
              "previewDiff": {
                "title": "x",
                "entries": [
                  {
                    "settingId": "video.fps_max",
                    "configKey": "fps_max",
                    "displayName": "FPS Limit",
                    "currentValue": "400",
                    "newValue": "0",
                    "risk": "Low"
                  }
                ]
              },
              "comparisonRows": [
                {
                  "metric": "System CPU average",
                  "unit": "%",
                  "before": 55,
                  "after": 50,
                  "absoluteDifference": -5,
                  "isAvailable": true
                }
              ]
            }
            """);

        var index = await env.Intel.RebuildAsync();
        Assert.Contains(index.AllEvidence, e => e.GuidedRunId == "legacy_intel");
        var rec = index.Records.FirstOrDefault(r => r.SettingKey == "fps_max");
        Assert.NotNull(rec);
    }

    [Fact]
    public async Task Cache_NotRebuilt_UntilInvalidate()
    {
        var env = IntelEnv.Create();
        await env.SaveRun(CreateRun("c1", new[] { "fps_max" }, GuidedResultClassification.Improved, true));
        var first = await env.Intel.GetIndexAsync(forceRebuild: true);
        var built = first.BuiltAt;

        await env.SaveRun(CreateRun("c2", new[] { "fps_max" }, GuidedResultClassification.Neutral, true));
        var cached = await env.Intel.GetIndexAsync(forceRebuild: false);
        Assert.Equal(built, cached.BuiltAt);
        Assert.Equal(1, cached.Records.First(r => r.SettingKey == "fps_max").DirectTestCount);

        env.Intel.InvalidateCache();
        var rebuilt = await env.Intel.GetIndexAsync(forceRebuild: false);
        Assert.Equal(2, rebuilt.Records.First(r => r.SettingKey == "fps_max").DirectTestCount);
    }

    [Fact]
    public async Task Index_PersistsUnderPerformanceHistory()
    {
        var env = IntelEnv.Create();
        await env.SaveRun(CreateRun("p1", new[] { "fps_max" }, GuidedResultClassification.Improved, true));
        await env.Intel.RebuildAsync();
        var path = Path.Combine(env.Paths.PerformanceHistoryDirectory, PerformanceIntelligenceSchema.IndexFileName);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task PerKeySnapshot_Recorded_OnBackupMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_scs_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var backups = new BackupService(paths, log);
            var file = Path.Combine(root, "managed.cfg");
            await File.WriteAllTextAsync(file, "fps_max 400");

            var snaps = new[]
            {
                new SettingChangeSnapshot
                {
                    SettingId = "video.fps_max",
                    ConfigKey = "fps_max",
                    PreviousValue = "400",
                    NewValue = "0",
                    File = file,
                    Timestamp = DateTimeOffset.UtcNow
                }
            };

            var entry = await backups.CreateBackupAsync(
                "test",
                new[] { "cs2.settings.apply" },
                new[] { file },
                new Dictionary<string, string?> { ["fps_max"] = "400" },
                settingChangeSnapshots: snaps);

            Assert.Single(entry.SettingChangeSnapshots);
            Assert.Equal("fps_max", entry.SettingChangeSnapshots[0].ConfigKey);
            Assert.Equal(entry.Id, entry.SettingChangeSnapshots[0].BackupId);

            var store = new SettingChangeSnapshotStore(paths, log);
            await store.AppendAsync(entry.SettingChangeSnapshots);
            var latest = await store.GetLatestForKeyAsync("fps_max");
            Assert.NotNull(latest);
            Assert.Equal("0", latest!.NewValue);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task TargetedRestore_NotTracked_WhenNoSnapshot()
    {
        var env = IntelEnv.Create();
        var eval = env.CreateEvaluator();
        var result = await eval.EvaluateAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.NotTracked, result.Safety);
        Assert.True(result.PreferFullBackupRestore);
    }

    [Fact]
    public async Task TargetedRestore_Unsafe_WhenLiveSourceNotManaged()
    {
        var env = IntelEnv.Create();
        var snapStore = new SettingChangeSnapshotStore(env.Paths, env.Log);
        await snapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                SettingId = "video.fps_max",
                ConfigKey = "fps_max",
                PreviousValue = "400",
                NewValue = "0",
                File = "/tmp/cfg/frameforge_settings.cfg",
                BackupId = "bak_missing",
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        // backup missing → InsufficientMetadata
        var svc = new TargetedRestoreService(
            snapStore,
            new BackupService(env.Paths, env.Log),
            new StubSettings { Source = Cs2SettingSource.Autoexec, Value = "120" },
            new Cs2ConfigService(),
            new Cs2SettingCatalog(),
            env.Log);
        var eval = new TargetedRestoreEvaluator(svc);

        var result = await eval.EvaluateAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.InsufficientMetadata, result.Safety);
    }

    [Fact]
    public async Task TargetedRestore_Unsafe_WhenLiveValueDiverged()
    {
        var env = IntelEnv.Create();
        var paths = env.Paths;
        var log = env.Log;
        var backups = new BackupService(paths, log);
        var cfg = Path.Combine(env.Root, "cfg");
        Directory.CreateDirectory(cfg);
        var managed = Path.Combine(cfg, "frameforge_settings.cfg");
        await File.WriteAllTextAsync(managed, "fps_max 999");

        var entry = await backups.CreateBackupAsync(
            "x",
            new[] { "cs2.settings.apply" },
            new[] { managed },
            new Dictionary<string, string?> { ["fps_max"] = "400" },
            settingChangeSnapshots: new[]
            {
                new SettingChangeSnapshot
                {
                    SettingId = "video.fps_max",
                    ConfigKey = "fps_max",
                    PreviousValue = "400",
                    NewValue = "0",
                    File = managed
                }
            });

        var snapStore = new SettingChangeSnapshotStore(paths, log);
        await snapStore.AppendAsync(entry.SettingChangeSnapshots);

        var svc = new TargetedRestoreService(
            snapStore,
            backups,
            new StubSettings
            {
                Source = Cs2SettingSource.FrameForgeManaged,
                Value = "999",
                ManagedPath = managed,
                Available = true
            },
            new Cs2ConfigService(),
            new Cs2SettingCatalog(),
            log);
        var eval = new TargetedRestoreEvaluator(svc);

        var result = await eval.EvaluateAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.UnsafeToTargetRestore, result.Safety);
        Assert.Contains("differs from the last FrameForge-managed value", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecommendationBlurb_NoEvidence()
    {
        var env = IntelEnv.Create();
        await env.Intel.RebuildAsync();
        Assert.Equal("No local benchmark evidence yet.", env.Intel.GetRecommendationBlurb("fps_max"));
    }

    [Fact]
    public async Task ConsistentDirectTests_RaiseConfidence()
    {
        var env = IntelEnv.Create();
        for (var i = 0; i < 4; i++)
        {
            await env.SaveRun(CreateRun($"h{i}", new[] { "fps_max" }, GuidedResultClassification.Improved, true));
        }

        var index = await env.Intel.RebuildAsync();
        var rec = index.Records.First(r => r.SettingKey == "fps_max");
        Assert.Equal(PerformanceConfidence.High, rec.Confidence);
        Assert.Contains("improvement", rec.RecommendationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not a guarantee", rec.RecommendationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guaranteed FPS", rec.RecommendationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+10 FPS", rec.RecommendationSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static GuidedOptimizationRun CreateRun(
        string id,
        IReadOnlyList<string> keys,
        GuidedResultClassification classification,
        bool complete)
    {
        var settings = keys.ToDictionary(k => k, k => "1", StringComparer.OrdinalIgnoreCase);
        return new GuidedOptimizationRun
        {
            Id = id,
            SchemaVersion = GuidedOptimizationSchema.CurrentVersion,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Status = GuidedOptimizationStatus.Completed,
            UserDecision = GuidedUserDecision.Keep,
            Classification = classification,
            SelectedSettingKeys = keys.ToList(),
            SelectedSettings = settings,
            InitialBenchmarkId = complete ? "bm_before_" + id : null,
            PostBenchmarkId = complete ? "bm_after_" + id : null,
            ComparisonRows = complete
                ?
                [
                    new GuidedComparisonRow
                    {
                        Metric = "System CPU average",
                        Unit = "%",
                        Before = 50,
                        After = classification == GuidedResultClassification.Improved ? 40 : 55,
                        AbsoluteDifference = classification == GuidedResultClassification.Improved ? -10 : 5,
                        IsAvailable = true
                    }
                ]
                : new List<GuidedComparisonRow>(),
            ClassificationReason = classification.ToString()
        };
    }

    private sealed class IntelEnv
    {
        public required string Root { get; init; }
        public required PathService Paths { get; init; }
        public required FileAppLog Log { get; init; }
        public required GuidedOptimizationStore Guided { get; init; }
        public required PerformanceIntelligenceService Intel { get; init; }

        public static IntelEnv Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ff_intel_" + Guid.NewGuid().ToString("N"));
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var guided = new GuidedOptimizationStore(paths, log);
            var hw = new FixedHardware(new HardwareInfo
            {
                CpuName = "IntelTestCPU",
                GpuName = "IntelTestGPU",
                TotalRamBytes = 16L << 30,
                OsDescription = "TestOS",
                Architecture = "X64"
            });
            var intel = new PerformanceIntelligenceService(
                guided,
                new SystemFingerprintService(hw),
                new Cs2SettingCatalog(),
                paths,
                log);
            return new IntelEnv
            {
                Root = root,
                Paths = paths,
                Log = log,
                Guided = guided,
                Intel = intel
            };
        }

        public Task SaveRun(GuidedOptimizationRun run) => Guided.SaveAsync(run);

        public TargetedRestoreEvaluator CreateEvaluator() =>
            new(new TargetedRestoreService(
                new SettingChangeSnapshotStore(Paths, Log),
                new BackupService(Paths, Log),
                new StubSettings(),
                new Cs2ConfigService(),
                new Cs2SettingCatalog(),
                Log));
    }

    private sealed class FixedHardware : IHardwareInfoService
    {
        private readonly HardwareInfo _info;
        public FixedHardware(HardwareInfo info) => _info = info;
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_info);
    }

    private sealed class StubSettings : ICs2SettingsService
    {
        public Cs2SettingSource Source { get; set; } = Cs2SettingSource.Default;
        public string? Value { get; set; } = "400";
        public string? ManagedPath { get; set; }
        public bool Available { get; set; }

        public string ManagedConfigFileName => "frameforge_settings.cfg";
        public string AutoexecFileName => "autoexec.cfg";

        public Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new Cs2SettingCatalog();
            var settings = catalog.GetAll().Select(d => new Cs2SettingValue
            {
                Definition = d,
                CurrentValue = d.ConfigKey == "fps_max" ? Value : d.DefaultValue,
                RecommendedValue = d.RecommendedValue,
                Source = d.ConfigKey == "fps_max" ? Source : Cs2SettingSource.Default
            }).ToList();
            return Task.FromResult(new Cs2SettingsSnapshot
            {
                Cs2Available = Available,
                ManagedConfigPath = ManagedPath,
                CfgDirectory = ManagedPath is null ? null : Path.GetDirectoryName(ManagedPath),
                Settings = settings,
                Message = "stub"
            });
        }

        public SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values) => new();
        public SettingsDiff CreateDiff(Cs2SettingsSnapshot current, IReadOnlyDictionary<string, string> desired, string reason, string? profileId = null, string? profileName = null) => new();
        public Task<SettingsApplyResult> ApplySettingsAsync(IReadOnlyDictionary<string, string> desired, string reason, string? profileId = null, bool createBackup = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Fail("n/a"));
        public Task<SettingsApplyResult> ApplyDiffAsync(SettingsDiff diff, bool createBackup = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Fail("n/a"));
        public Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Fail("n/a"));
        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
    }
}
