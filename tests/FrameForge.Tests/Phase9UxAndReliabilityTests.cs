using FrameForge.Benchmark;
using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Json;
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

/// <summary>
/// Phase 9: benchmark progress/cancel/CS2-exit, intelligence export/import,
/// managed cfg watcher/hash, history reliability, guided confirm messaging.
/// </summary>
public sealed class Phase9UxAndReliabilityTests
{
    [Fact]
    public async Task Benchmark_ProgressEvents_ReportPhases_FromEngineOnly()
    {
        using var env = BenchEnv.Create();
        env.Monitor.Process = new Cs2ProcessInfo { ProcessId = 42, ProcessName = "cs2" };

        var phases = new List<BenchmarkUiPhase>();
        env.Engine.ProgressChanged += (_, p) => phases.Add(p.Phase);

        var cfg = new BenchmarkConfiguration
        {
            DurationSeconds = 30,
            WarmupSeconds = 2,
            SampleIntervalMs = 50
        };

        var run = await env.Engine.StartAsync(cfg);
        Assert.Equal(BenchmarkStatus.Completed, run.Status);
        Assert.Contains(phases, p => p == BenchmarkUiPhase.Preparing);
        Assert.Contains(phases, p => p == BenchmarkUiPhase.Warmup);
        Assert.Contains(phases, p => p == BenchmarkUiPhase.Benchmarking);
        Assert.Contains(phases, p => p == BenchmarkUiPhase.Finishing);
        Assert.Contains(phases, p => p == BenchmarkUiPhase.Analyzing);
        Assert.Contains(phases, p => p == BenchmarkUiPhase.Completed);
        Assert.NotNull(env.Engine.LatestProgress);
        Assert.True(env.Engine.LatestProgress!.SamplesCollected > 0);
        Assert.Contains("Unavailable", env.Engine.LatestProgress.UnavailableNote ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Benchmark_Cancel_ShowsStoppingThenCancelled()
    {
        using var env = BenchEnv.Create();
        env.Monitor.Process = new Cs2ProcessInfo { ProcessId = 7, ProcessName = "cs2" };

        var messages = new List<string>();
        env.Engine.ProgressChanged += (_, p) => messages.Add(p.Message);

        var cfg = new BenchmarkConfiguration
        {
            DurationSeconds = 60,
            WarmupSeconds = 5,
            SampleIntervalMs = 50
        };

        var start = env.Engine.StartAsync(cfg);
        await Task.Delay(120);
        env.Engine.RequestCancel();
        var run = await start;

        Assert.Equal(BenchmarkStatus.Cancelled, run.Status);
        Assert.Contains(messages, m => m.Contains("Stopping", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, m => m.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BenchmarkUiPhase.Cancelled, env.Engine.LatestProgress!.Phase);
    }

    [Fact]
    public async Task Benchmark_Cs2Exit_FailsWithoutRestart()
    {
        using var env = BenchEnv.Create();
        env.Monitor.Process = new Cs2ProcessInfo { ProcessId = 99, ProcessName = "cs2" };

        var cfg = new BenchmarkConfiguration
        {
            DurationSeconds = 60,
            WarmupSeconds = 1,
            SampleIntervalMs = 50
        };

        var start = env.Engine.StartAsync(cfg);
        await Task.Delay(150);
        // Simulate CS2 exit — do not restart
        env.Monitor.Process = null;
        var run = await start;

        Assert.Equal(BenchmarkStatus.Failed, run.Status);
        Assert.Contains("CS2", run.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not restart", run.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(run.Samples.Count > 0);
        Assert.Contains("did not restart", run.Result?.Notes ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedSection_Hash_IgnoresUserContentOutsideMarkers()
    {
        var userA =
            "bind w +forward" + Environment.NewLine +
            Cs2AutoexecIntegration.BeginMarker + Environment.NewLine +
            "exec frameforge_settings.cfg" + Environment.NewLine +
            Cs2AutoexecIntegration.EndMarker + Environment.NewLine +
            "echo hello";

        var userB =
            "bind mouse1 +attack" + Environment.NewLine +
            Cs2AutoexecIntegration.BeginMarker + Environment.NewLine +
            "exec frameforge_settings.cfg" + Environment.NewLine +
            Cs2AutoexecIntegration.EndMarker + Environment.NewLine +
            "sensitivity 1.5";

        var sa = ManagedConfigWatcher.ExtractManagedSection(userA);
        var sb = ManagedConfigWatcher.ExtractManagedSection(userB);
        Assert.Equal(ManagedConfigWatcher.Sha256Hex(sa), ManagedConfigWatcher.Sha256Hex(sb));

        var changed =
            "bind w +forward" + Environment.NewLine +
            Cs2AutoexecIntegration.BeginMarker + Environment.NewLine +
            "exec something_else.cfg" + Environment.NewLine +
            Cs2AutoexecIntegration.EndMarker;

        var sc = ManagedConfigWatcher.ExtractManagedSection(changed);
        Assert.NotEqual(ManagedConfigWatcher.Sha256Hex(sa), ManagedConfigWatcher.Sha256Hex(sc));
    }

    [Fact]
    public async Task Watcher_CaptureBaseline_AndDetectExternalManagedChange()
    {
        using var env = WatchEnv.Create();
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 0" + Environment.NewLine);
        await File.WriteAllTextAsync(env.AutoexecPath,
            "echo user" + Environment.NewLine +
            Cs2AutoexecIntegration.BeginMarker + Environment.NewLine +
            "exec frameforge_settings.cfg" + Environment.NewLine +
            Cs2AutoexecIntegration.EndMarker + Environment.NewLine);

        await env.Watcher.CaptureBaselineAsync();
        Assert.False(await env.Watcher.HasExternalChangesAsync());

        // User-only autoexec edit should not count as managed change for section hash
        await File.WriteAllTextAsync(env.AutoexecPath,
            "echo user-changed" + Environment.NewLine +
            Cs2AutoexecIntegration.BeginMarker + Environment.NewLine +
            "exec frameforge_settings.cfg" + Environment.NewLine +
            Cs2AutoexecIntegration.EndMarker + Environment.NewLine);
        Assert.False(await env.Watcher.HasExternalChangesAsync());

        // Managed cfg external edit
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 400" + Environment.NewLine);
        Assert.True(await env.Watcher.HasExternalChangesAsync());
    }

    [Fact]
    public async Task Watcher_Dispose_StopsCleanly()
    {
        using var env = WatchEnv.Create();
        await env.Watcher.StartAsync();
        env.Watcher.Dispose();
        Assert.False(env.Watcher.IsWatching);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => env.Watcher.StartAsync());
    }

    [Fact]
    public async Task Restore_Refuses_WhenExternalChangeSinceBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            var managed = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            await File.WriteAllTextAsync(managed, "fps_max 0" + Environment.NewLine);

            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var config = new Cs2ConfigService(log);
            var det = new FixedDetection(cfgDir);
            var appSettings = new AppSettingsService(paths, log);
            var backups = new BackupService(paths, log);
            var snaps = new SettingChangeSnapshotStore(paths, log);
            var settings = new Cs2SettingsService(catalog, config, det, backups, appSettings, log, snaps);
            var watcher = new ManagedConfigWatcher(det, paths, log);
            await watcher.CaptureBaselineAsync();

            await snaps.AppendAsync(new[]
            {
                new SettingChangeSnapshot
                {
                    SettingId = "video.fps_max",
                    ConfigKey = "fps_max",
                    PreviousValue = "400",
                    NewValue = "0",
                    File = managed,
                    Timestamp = DateTimeOffset.UtcNow
                }
            });

            var svc = new TargetedRestoreService(snaps, backups, settings, config, catalog, log, watcher);
            var assess = await svc.AssessAsync("fps_max");
            Assert.Equal(TargetedRestoreSafety.SafeToTargetRestore, assess.Safety);

            // External edit after assess
            await File.WriteAllTextAsync(managed, "fps_max 0" + Environment.NewLine + "// external" + Environment.NewLine);
            // Hash of full file changes even if key same
            Assert.True(await watcher.HasExternalChangesAsync());

            var result = await svc.RestoreAsync("fps_max");
            Assert.False(result.Success);
            Assert.True(result.Refused);
            Assert.Contains("externally", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Intelligence_ExportImport_PreviewValidateMerge_FingerprintSeparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-intel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var hw = new FixedHw();
            var fpSvc = new SystemFingerprintService(hw);
            var fp = await fpSvc.GetFingerprintAsync();
            var guided = new GuidedOptimizationStore(paths, log);

            // Seed one completed guided run with single setting
            var run = new GuidedOptimizationRun
            {
                Id = "gr_p9_1",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Status = GuidedOptimizationStatus.Completed,
                Classification = GuidedResultClassification.Improved,
                ClassificationReason = "test",
                InitialBenchmarkId = "b1",
                PostBenchmarkId = "b2",
                SystemFingerprintId = fp.FingerprintId,
                SelectedSettingKeys = new List<string> { "fps_max" },
                SelectedSettings = new Dictionary<string, string> { ["fps_max"] = "0" },
                ComparisonRows = new List<GuidedComparisonRow>
                {
                    new() { Metric = "System CPU", Unit = "%", Before = 50, After = 40, AbsoluteDifference = -10, IsAvailable = true }
                }
            };
            await guided.SaveAsync(run);

            var intel = new PerformanceIntelligenceService(guided, fpSvc, catalog, paths, log);
            await intel.RebuildAsync();
            var snaps = new SettingChangeSnapshotStore(paths, log);
            await snaps.AppendAsync(new[]
            {
                new SettingChangeSnapshot
                {
                    SettingId = "video.fps_max",
                    ConfigKey = "fps_max",
                    PreviousValue = "400",
                    NewValue = "0",
                    File = "x",
                    Timestamp = DateTimeOffset.UtcNow
                }
            });

            var export = new IntelligenceExportService(intel, snaps, fpSvc, catalog, paths, log);
            var package = await export.BuildExportAsync();
            Assert.True(package.Evidence.Count >= 1);
            Assert.DoesNotContain("user@", package.Notes, StringComparison.OrdinalIgnoreCase);

            var intelPath = Path.Combine(root, "out.frameforge-intelligence.json");
            await export.ExportIntelligenceAsync(intelPath);
            Assert.True(File.Exists(intelPath));

            // Bad JSON rejected
            var bad = Path.Combine(root, "bad.frameforge-intelligence.json");
            await File.WriteAllTextAsync(bad, "{ not json");
            var badPreview = await export.PreviewImportAsync(bad);
            Assert.False(badPreview.IsValid);

            // Future schema rejected
            var future = Path.Combine(root, "future.frameforge-intelligence.json");
            await File.WriteAllTextAsync(future, """{"schemaVersion":999,"records":[],"evidence":[],"fingerprints":[]}""");
            var fut = await export.PreviewImportAsync(future);
            Assert.False(fut.IsValid);

            // Foreign fingerprint package
            var foreignFp = "abcdef0123456789";
            var foreignPkg = new IntelligenceExportPackage
            {
                SchemaVersion = 1,
                Fingerprints =
                {
                    new SystemFingerprint { FingerprintId = foreignFp, CpuModel = "OtherCPU", GpuModel = "OtherGPU", OsVersion = "OS", Architecture = "X64" }
                },
                Evidence =
                {
                    new SettingTestEvidence
                    {
                        GuidedRunId = "imp_foreign_1",
                        SettingId = "video.fps_max",
                        SettingKey = "fps_max",
                        SettingName = "FPS Max",
                        EvidenceType = PerformanceEvidenceType.SingleSetting,
                        Classification = GuidedResultClassification.Regressed,
                        TestedAt = DateTimeOffset.UtcNow.AddDays(-1),
                        SystemFingerprintId = foreignFp,
                        InitialBenchmarkId = "x",
                        PostBenchmarkId = "y",
                        BenchmarkComplete = true,
                        ComparisonRows = new List<GuidedComparisonRow>
                        {
                            new() { Metric = "System CPU", Unit = "%", Before = 1, After = 2, AbsoluteDifference = 1, IsAvailable = true }
                        }
                    }
                },
                Records =
                {
                    new SettingPerformanceRecord
                    {
                        SettingId = "video.fps_max",
                        SettingKey = "fps_max",
                        SettingName = "FPS Max",
                        SystemFingerprintId = foreignFp,
                        TestCount = 1,
                        DirectTestCount = 1,
                        Confidence = PerformanceConfidence.Low,
                        LatestClassification = GuidedResultClassification.Regressed,
                        LastTestedAt = DateTimeOffset.UtcNow.AddDays(-1)
                    }
                }
            };
            var foreignPath = Path.Combine(root, "foreign.frameforge-intelligence.json");
            await FrameForgeJson.SerializeFileAsync(foreignPath, foreignPkg);

            var preview = await export.PreviewImportAsync(foreignPath);
            Assert.True(preview.IsValid, string.Join("; ", preview.Errors));
            Assert.Contains(preview.DifferentFingerprints, f => f == foreignFp);

            // Preview mode writes nothing
            var importsDir = Path.Combine(paths.PerformanceHistoryDirectory, "imports");
            var before = Directory.Exists(importsDir)
                ? Directory.EnumerateFiles(importsDir, "*.json").Count()
                : 0;
            var previewOnly = await export.ImportAsync(foreignPath, IntelligenceImportMode.Preview);
            Assert.True(previewOnly.Success);
            var after = Directory.Exists(importsDir)
                ? Directory.EnumerateFiles(importsDir, "*.json").Count()
                : 0;
            Assert.Equal(before, after);

            var merged = await export.ImportAsync(foreignPath, IntelligenceImportMode.Merge);
            Assert.True(merged.Success, merged.Message);

            var index = await intel.RebuildAsync();
            Assert.Contains(index.AllEvidence, e => e.SystemFingerprintId == foreignFp);
            Assert.Contains(index.Records, r => r.SystemFingerprintId == foreignFp && !r.IsCurrentSystem);
            // Current system records still only current fp
            Assert.All(index.CurrentSystemRecords, r =>
                Assert.Equal(fp.FingerprintId, r.SystemFingerprintId));

            // Snapshots export/import
            var snapPath = Path.Combine(root, "out.frameforge-snapshots.json");
            await export.ExportSnapshotsAsync(snapPath);
            var snapPreview = await export.PreviewImportAsync(snapPath);
            Assert.True(snapPreview.IsValid);
            Assert.True(snapPreview.SnapshotsToAdd >= 1);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Intelligence_Import_RejectsUnknownSettingAndDuplicateEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var hw = new FixedHw();
            var fpSvc = new SystemFingerprintService(hw);
            var guided = new GuidedOptimizationStore(paths, log);
            var intel = new PerformanceIntelligenceService(guided, fpSvc, catalog, paths, log);
            var snaps = new SettingChangeSnapshotStore(paths, log);
            var export = new IntelligenceExportService(intel, snaps, fpSvc, catalog, paths, log);

            var bad = new IntelligenceExportPackage
            {
                SchemaVersion = 1,
                Records =
                {
                    new SettingPerformanceRecord
                    {
                        SettingKey = "not_a_real_cvar_xyz",
                        SettingId = "nope",
                        SystemFingerprintId = "aaaaaaaaaaaaaaaa"
                    }
                },
                Evidence =
                {
                    new SettingTestEvidence
                    {
                        GuidedRunId = "r1",
                        SettingKey = "not_a_real_cvar_xyz",
                        SettingId = "nope",
                        TestedAt = DateTimeOffset.UtcNow,
                        EvidenceType = PerformanceEvidenceType.SingleSetting,
                        Classification = GuidedResultClassification.Neutral
                    },
                    new SettingTestEvidence
                    {
                        GuidedRunId = "r1",
                        SettingKey = "not_a_real_cvar_xyz",
                        SettingId = "nope",
                        TestedAt = DateTimeOffset.UtcNow,
                        EvidenceType = PerformanceEvidenceType.SingleSetting,
                        Classification = GuidedResultClassification.Neutral
                    }
                }
            };
            var path = Path.Combine(root, "badset.frameforge-intelligence.json");
            await FrameForgeJson.SerializeFileAsync(path, bad);
            var preview = await export.PreviewImportAsync(path);
            Assert.False(preview.IsValid);
            Assert.Contains(preview.Errors, e => e.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
                                               || e.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task CorruptHistory_IsQuarantined_NotSilentlyDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            Directory.CreateDirectory(paths.GuidedRunsDirectory);
            var badRun = Path.Combine(paths.GuidedRunsDirectory, "corrupt.json");
            await File.WriteAllTextAsync(badRun, "{ broken");

            var store = new GuidedOptimizationStore(paths, log);
            var list = await store.ListAsync();
            Assert.Empty(list);
            Assert.False(File.Exists(badRun));
            Assert.True(Directory.EnumerateFiles(paths.GuidedRunsDirectory, "*.corrupt.*").Any());

            // Snapshot store quarantine
            var snapPath = Path.Combine(paths.PerformanceHistoryDirectory, SettingChangeSnapshotStore.FileName);
            Directory.CreateDirectory(paths.PerformanceHistoryDirectory);
            await File.WriteAllTextAsync(snapPath, "!!!not-json");
            var snaps = new SettingChangeSnapshotStore(paths, log);
            var entries = await snaps.ListAsync();
            Assert.Empty(entries);
            Assert.True(Directory.EnumerateFiles(paths.PerformanceHistoryDirectory, "setting-change-snapshots.json.corrupt.*").Any());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task IntelligenceIndex_RebuildsFromGuidedRuns_WhenIndexCorrupt()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var hw = new FixedHw();
            var fpSvc = new SystemFingerprintService(hw);
            var fp = await fpSvc.GetFingerprintAsync();
            var guided = new GuidedOptimizationStore(paths, log);
            await guided.SaveAsync(new GuidedOptimizationRun
            {
                Id = "gr_rebuild",
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                Status = GuidedOptimizationStatus.Completed,
                Classification = GuidedResultClassification.Neutral,
                InitialBenchmarkId = "a",
                PostBenchmarkId = "b",
                SystemFingerprintId = fp.FingerprintId,
                SelectedSettingKeys = new List<string> { "fps_max" },
                SelectedSettings = new Dictionary<string, string> { ["fps_max"] = "300" },
                ComparisonRows = new List<GuidedComparisonRow>
                {
                    new() { Metric = "System CPU", Unit = "%", Before = 10, After = 10, AbsoluteDifference = 0, IsAvailable = true }
                }
            });

            var indexPath = Path.Combine(paths.PerformanceHistoryDirectory, PerformanceIntelligenceSchema.IndexFileName);
            Directory.CreateDirectory(paths.PerformanceHistoryDirectory);
            await File.WriteAllTextAsync(indexPath, "{corrupt-index");

            var intel = new PerformanceIntelligenceService(guided, fpSvc, catalog, paths, log);
            var index = await intel.RebuildAsync();
            Assert.True(index.Records.Count >= 1);
            Assert.True(File.Exists(indexPath)); // rewritten atomically via FrameForgeJson
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task AtomicFile_UsedForHistoryWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-atom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "history.json");
            await AtomicFile.WriteAllTextAsync(path, "{\"ok\":true}");
            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(path));
            await AtomicFile.WriteAllTextAsync(path, "{\"ok\":2}");
            Assert.Equal("{\"ok\":2}", await File.ReadAllTextAsync(path));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task GuidedConfirmMessage_IncludesSettingsFilesBackupAndRisk()
    {
        // Exercise BuildConfirmMessage via a full preview/run path messages
        var root = Path.Combine(Path.GetTempPath(), "ff-p9-gconfirm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cfgDir = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName),
                "fps_max 400" + Environment.NewLine);

            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var config = new Cs2ConfigService(log);
            var det = new FixedDetection(cfgDir);
            var appSettings = new AppSettingsService(paths, log);
            var backups = new BackupService(paths, log);
            var settings = new Cs2SettingsService(catalog, config, det, backups, appSettings, log);
            var store = new GuidedOptimizationStore(paths, log);
            var mon = new FakeMon { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } };
            var bench = new FastBenchEngine();
            var svc = new GuidedOptimizationService(
                det, mon, settings, new EmptyProfiles(), bench, bench.Store,
                new BenchmarkCalculator(), store, log);

            var messages = new List<string>();
            svc.ProgressChanged += (_, p) => messages.Add(p.Message);

            var task = svc.RunAsync(new GuidedOptimizationRequest
            {
                TargetKind = GuidedTargetKind.SettingsMap,
                DesiredSettings = new Dictionary<string, string> { ["fps_max"] = "0" },
                Label = "Phase9Confirm",
                RequireCs2ProcessForBenchmark = false,
                BenchmarkConfiguration = new BenchmarkConfiguration
                {
                    DurationSeconds = 30,
                    WarmupSeconds = 0,
                    SampleIntervalMs = 50
                }
            });

            await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 8000);
            var confirmMsg = messages.LastOrDefault(m => m.Contains("Confirm apply", StringComparison.OrdinalIgnoreCase))
                             ?? messages.LastOrDefault()
                             ?? "";
            Assert.Contains("fps_max", confirmMsg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("backup", confirmMsg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Benchmark plan", confirmMsg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Risk", confirmMsg, StringComparison.OrdinalIgnoreCase);

            svc.Cancel();
            await task;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition not met in " + timeoutMs + "ms");
    }

    private sealed class BenchEnv : IDisposable
    {
        public string Root { get; }
        public PathService Paths { get; }
        public FakeMon Monitor { get; }
        public BenchmarkEngine Engine { get; }

        private BenchEnv(string root, PathService paths, FakeMon mon, BenchmarkEngine engine)
        {
            Root = root;
            Paths = paths;
            Monitor = mon;
            Engine = engine;
        }

        public static BenchEnv Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ff-p9-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var mon = new FakeMon();
            var store = new BenchmarkStore(paths, log);
            var engine = new BenchmarkEngine(
                new OsPerformanceSampler(),
                mon,
                new FixedHw(),
                new FixedDetection(null),
                new NoopSettings(),
                new EmptyProfiles(),
                new AppSettingsService(paths, log),
                store,
                new BenchmarkCalculator(),
                log);
            return new BenchEnv(root, paths, mon, engine);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
    }

    private sealed class WatchEnv : IDisposable
    {
        public string Root { get; }
        public string ManagedPath { get; }
        public string AutoexecPath { get; }
        public ManagedConfigWatcher Watcher { get; }

        private WatchEnv(string root, string managed, string autoexec, ManagedConfigWatcher watcher)
        {
            Root = root;
            ManagedPath = managed;
            AutoexecPath = autoexec;
            Watcher = watcher;
        }

        public static WatchEnv Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ff-p9-watch-" + Guid.NewGuid().ToString("N"));
            var cfg = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfg);
            var managed = Path.Combine(cfg, Cs2SettingsService.ManagedFileName);
            var autoexec = Path.Combine(cfg, Cs2AutoexecIntegration.AutoexecFileName);
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var config = new Cs2ConfigService(log);
            var det = new FixedDetection(cfg);
            var appSettings = new AppSettingsService(paths, log);
            var backups = new BackupService(paths, log);
            var settings = new Cs2SettingsService(catalog, config, det, backups, appSettings, log);
            var watcher = new ManagedConfigWatcher(det, paths, log);
            return new WatchEnv(root, managed, autoexec, watcher);
        }

        public void Dispose()
        {
            Watcher.Dispose();
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
    }

    private sealed class FakeMon : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? Process { get; set; }
        public Cs2ProcessInfo? TryGetCs2Process() => Process;
    }

    private sealed class FixedDetection : ICs2DetectionService
    {
        private readonly string? _cfg;
        public FixedDetection(string? cfg) => _cfg = cfg;
        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_cfg))
            {
                return Task.FromResult(Cs2InstallInfo.NotFound("test"));
            }

            return Task.FromResult(new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = Path.GetDirectoryName(_cfg) ?? _cfg,
                CfgDirectory = _cfg,
                DetectionMessage = "test",
                SteamFound = true
            });
        }
    }

    private sealed class FixedHw : IHardwareInfoService
    {
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo
            {
                CpuName = "Phase9CPU",
                GpuName = "Phase9GPU",
                TotalRamBytes = 16L << 30,
                OsDescription = "TestOS",
                WindowsVersion = "TestOS",
                Architecture = "X64"
            });
    }

    private sealed class NoopSettings : ICs2SettingsService
    {
        public string ManagedConfigFileName => Cs2SettingsService.ManagedFileName;
        public string AutoexecFileName => Cs2AutoexecIntegration.AutoexecFileName;
        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
        public Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2SettingsSnapshot
            {
                Cs2Available = false,
                Message = "noop",
                Settings = Array.Empty<Cs2SettingValue>()
            });
        public SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values) => new();
        public SettingsDiff CreateDiff(
            Cs2SettingsSnapshot current,
            IReadOnlyDictionary<string, string> desired,
            string reason,
            string? profileId = null,
            string? profileName = null) => new() { Title = reason };
        public Task<SettingsApplyResult> ApplySettingsAsync(
            IReadOnlyDictionary<string, string> desired,
            string reason,
            string? profileId = null,
            bool createBackup = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Fail("noop"));
        public Task<SettingsApplyResult> ApplyDiffAsync(SettingsDiff diff, bool createBackup = true, CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Fail("noop"));
        public Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Fail("noop"));
    }

    private sealed class EmptyProfiles : IProfileService
    {
        public IReadOnlyList<string> BuiltInProfileIds { get; } = Array.Empty<string>();
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

    private sealed class FastBenchEngine : IBenchmarkEngine
    {
        public int StartCount { get; private set; }
        public BenchmarkStatus Status { get; private set; } = BenchmarkStatus.Idle;
        public BenchmarkRun? CurrentRun { get; private set; }
        public BenchmarkProgress? LatestProgress { get; private set; }
        public InMemBenchStore Store { get; } = new();
        public event EventHandler? StatusChanged;
#pragma warning disable CS0067
        public event EventHandler<BenchmarkSample>? SampleCaptured;
        public event EventHandler<BenchmarkProgress>? ProgressChanged;
#pragma warning restore CS0067

        public async Task<BenchmarkRun> StartAsync(BenchmarkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            StartCount++;
            Status = BenchmarkStatus.Running;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            var samples = Enumerable.Range(0, 12).Select(i => new BenchmarkSample
            {
                IsWarmup = false,
                SystemCpuPercent = 40,
                ProcessCpuPercent = 20,
                SystemMemoryPercent = 50,
                ProcessWorkingSetBytes = 300_000_000,
                Cs2ProcessPresent = true,
                ElapsedMs = i * configuration.SampleIntervalMs
            }).ToList();
            var calc = new BenchmarkCalculator();
            var run = new BenchmarkRun
            {
                Id = "p9bm" + StartCount,
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                EndedAt = DateTimeOffset.UtcNow,
                Status = BenchmarkStatus.Completed,
                Configuration = configuration,
                SystemInformation = new BenchmarkSystemSnapshot { CpuName = "t", TotalRamBytes = 8L << 30 },
                Samples = samples,
                Result = calc.Calculate(samples),
                DurationSecondsActual = 5
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
            new BenchmarkCalculator().Compare(before, after);
        public BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after, BenchmarkCompareOptions options) =>
            new BenchmarkCalculator().Compare(before, after, options);
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
