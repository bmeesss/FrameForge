using FrameForge.Benchmark;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Guided;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Performance;
using Xunit;

namespace FrameForge.Tests;

/// <summary>
/// Phase 12: guided integrity gate, history reliability filters, condition report export.
/// </summary>
public sealed class Phase12GuidedIntegrityAndHistoryTests
{
    [Fact]
    public async Task SevereMismatch_EntersAwaitingConditionDecision()
    {
        using var env = GuidedEnv.Create(mismatchGpu: true);
        var task = env.Service.RunAsync(env.Request());
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConfirmation, 10000);
        env.Service.ConfirmApply();
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConditionDecision, 15000);

        Assert.Equal(GuidedOptimizationStatus.AwaitingConditionDecision, env.Service.Status);
        var log = string.Join("\n", env.Service.CurrentRun?.ProgressLog ?? new List<string>());
        Assert.Contains("mismatch", log + env.LastProgress, StringComparison.OrdinalIgnoreCase);

        env.Service.StopRestoreDueToConditionMismatch();
        var run = await task;
        Assert.Equal(GuidedUserDecision.StopRestore, run.ConditionDecision);
        Assert.False(run.ConditionOverride);
        Assert.Equal(ConditionMatchStatus.SevereMismatch, run.ConditionStatusAtDecision);
        Assert.Equal(GuidedResultClassification.Inconclusive, run.Classification);
        Assert.True(
            run.Status is GuidedOptimizationStatus.Restored or GuidedOptimizationStatus.Cancelled
                or GuidedOptimizationStatus.Failed,
            run.Status + " " + run.Error);
    }

    [Fact]
    public async Task DefaultStop_ViaCancel_RestoresAndDoesNotOverride()
    {
        using var env = GuidedEnv.Create(mismatchGpu: true);
        var task = env.Service.RunAsync(env.Request());
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConfirmation, 10000);
        env.Service.ConfirmApply();
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConditionDecision, 15000);

        // Escape / Cancel at the integrity gate maps to StopRestore (safest default)
        env.Service.Cancel();
        var run = await task;

        Assert.False(run.ConditionOverride);
        Assert.Equal(GuidedResultClassification.Inconclusive, run.Classification);
        Assert.Contains("condition mismatch", run.ClassificationReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(env.Settings.RestoreCount >= 1, "Stop at gate should attempt restore");
        Assert.True(
            run.RestoredSuccessfully ||
            run.Status is GuidedOptimizationStatus.Restored or GuidedOptimizationStatus.Failed
                or GuidedOptimizationStatus.Cancelled);
    }

    [Fact]
    public async Task ContinueComparison_StoresOverride_StillInconclusive()
    {
        using var env = GuidedEnv.Create(mismatchGpu: true);
        var task = env.Service.RunAsync(env.Request());
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConfirmation, 10000);
        env.Service.ConfirmApply();
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConditionDecision, 15000);

        env.Service.ContinueComparison();
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingDecision, 15000);

        Assert.True(env.Service.CurrentRun?.ConditionOverride == true);
        Assert.True(env.Service.CurrentRun?.ForcedCompareDespiteMismatch == true);
        Assert.Equal(GuidedResultClassification.Inconclusive, env.Service.CurrentRun!.Classification);
        var reasonBlob = (env.Service.CurrentRun.ClassificationReason ?? "") +
                         (env.Service.CurrentRun.ConditionOverrideReason ?? "");
        Assert.Contains("despite condition mismatch", reasonBlob, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a valid performance proof", reasonBlob, StringComparison.OrdinalIgnoreCase);

        env.Service.Decide(GuidedUserDecision.Keep);
        var run = await task;
        Assert.True(run.ConditionOverride);
        Assert.Equal(GuidedUserDecision.ContinueComparison, run.ConditionDecision);
        Assert.Equal(GuidedResultClassification.Inconclusive, run.Classification);
        Assert.Equal(GuidedOptimizationStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Stop_TriggersRestore_AndKeepsBenchmarks()
    {
        using var env = GuidedEnv.Create(mismatchGpu: true);
        var task = env.Service.RunAsync(env.Request());
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConfirmation, 10000);
        env.Service.ConfirmApply();
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConditionDecision, 15000);

        env.Service.StopRestoreDueToConditionMismatch();
        var run = await task;

        Assert.False(string.IsNullOrWhiteSpace(run.InitialBenchmarkId));
        Assert.False(string.IsNullOrWhiteSpace(run.PostBenchmarkId));
        Assert.True(env.Settings.RestoreCount >= 1);
        if (run.ApplySucceeded)
        {
            Assert.True(run.RestoredSuccessfully || run.Status == GuidedOptimizationStatus.Failed);
        }
    }

    [Fact]
    public async Task RestoreFailure_IsSurfaced()
    {
        using var env = GuidedEnv.Create(mismatchGpu: true, restoreFails: true);
        var task = env.Service.RunAsync(env.Request());
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConfirmation, 10000);
        env.Service.ConfirmApply();
        await WaitUntil(() => env.Service.Status == GuidedOptimizationStatus.AwaitingConditionDecision, 15000);

        env.Service.StopRestoreDueToConditionMismatch();
        var run = await task;

        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
        Assert.Contains("Restore", run.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoryFiltering_ByFingerprint_Reliability_ConditionStatus()
    {
        var rows = new List<HistoryReliabilityRow>
        {
            new()
            {
                Id = "1", Date = DateTimeOffset.UtcNow, SettingOrProfile = "FPS Max",
                IsCurrentSystem = true, FingerprintId = "fp_current_aaaa",
                ConditionReliability = ComparisonReliability.High,
                ConditionStatus = ConditionMatchStatus.Match,
                Classification = "Improved", Confidence = "Medium"
            },
            new()
            {
                Id = "2", Date = DateTimeOffset.UtcNow.AddDays(-1), SettingOrProfile = "Volume",
                IsCurrentSystem = false, FingerprintId = "fp_other_bbbb",
                ConditionReliability = ComparisonReliability.Low,
                ConditionStatus = ConditionMatchStatus.SevereMismatch,
                Classification = "Inconclusive", Confidence = "Low"
            },
            new()
            {
                Id = "3", Date = DateTimeOffset.UtcNow.AddHours(-3), SettingOrProfile = "Sensitivity",
                IsCurrentSystem = true, FingerprintId = "fp_current_aaaa",
                ConditionReliability = ComparisonReliability.Medium,
                ConditionStatus = ConditionMatchStatus.Warning,
                Classification = "Neutral", Confidence = "Low"
            }
        };

        var current = rows.Where(r => r.IsCurrentSystem).ToList();
        Assert.Equal(2, current.Count);
        Assert.All(current, r => Assert.Equal("Current system", r.DisplaySystem));

        var other = rows.Where(r => !r.IsCurrentSystem).ToList();
        Assert.Single(other);
        Assert.Equal("Different system", other[0].DisplaySystem);

        var high = rows.Where(r => r.ConditionReliability == ComparisonReliability.High).ToList();
        Assert.Single(high);

        var hideSevere = rows.Where(r => r.ConditionStatus != ConditionMatchStatus.SevereMismatch).ToList();
        Assert.Equal(2, hideSevere.Count);

        var byFp = rows.Where(r => r.FingerprintId.Contains("other", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(byFp);
    }

    [Fact]
    public void ConditionReport_Generation_AndPiiExclusion()
    {
        var a = MakeBench("a", "fp_aaaaaaaaaaaaaa", "CPU-A", "GPU-A");
        var b = MakeBench("b", "fp_bbbbbbbbbbbbbb", "CPU-A", "GPU-B");
        a.SystemInformation = WithPiiTrap(a.SystemInformation!);
        var report = BenchmarkConditionRules.Analyze(a, b, forcedDespiteMismatch: true);
        var pkg = ConditionReportExporter.Build(a, b, report, userOverrode: true, overrideReason: "user continued");

        Assert.Equal(ConditionReportExportSchema.CurrentVersion, pkg.SchemaVersion);
        Assert.Equal("fp_aaaaaaaaaaaaaa", pkg.FingerprintA);
        Assert.Equal("fp_bbbbbbbbbbbbbb", pkg.FingerprintB);
        Assert.True(pkg.UserOverrodeMismatch);
        Assert.Contains(pkg.SevereMismatchMessages, m => m.Contains("GPU", StringComparison.OrdinalIgnoreCase)
            || m.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("does not become a valid performance proof", pkg.ForcedComparisonNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConditionMatchStatus.SevereMismatch, pkg.OverallStatus);

        // PII trap: email-like profile name scrubbed
        Assert.Null(pkg.BenchmarkA!.ActiveProfileName);
        Assert.False(ConditionReportExporter.LooksLikePii(pkg.BenchmarkA.CpuName));
    }

    [Fact]
    public async Task ConditionReport_Export_WritesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p12-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var hw = new FixedHw();
            var fp = new SystemFingerprintService(hw);
            var guided = new GuidedOptimizationStore(paths, log);
            var intel = new PerformanceIntelligenceService(guided, fp, catalog, paths, log);
            var snaps = new SettingChangeSnapshotStore(paths, log);
            var export = new IntelligenceExportService(intel, snaps, fp, catalog, paths, log);

            var a = MakeBench("a", "fp1", "C", "G");
            var b = MakeBench("b", "fp1", "C", "G");
            var dest = Path.Combine(root, "out.frameforge-condition-report.json");
            await export.ExportConditionReportAsync(dest, a, b);
            Assert.True(File.Exists(dest));
            var pkg = await FrameForgeJson.DeserializeFileAsync<ConditionReportExportPackage>(dest);
            Assert.NotNull(pkg);
            Assert.Equal(ConditionMatchStatus.Match, pkg!.OverallStatus);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task SchemaCompatibility_V2Loads_V3FieldsOptional()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p12-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var store = new GuidedOptimizationStore(paths, log);
            var v2 = new GuidedOptimizationRun
            {
                Id = "v2run",
                SchemaVersion = 2,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CompletedAt = DateTimeOffset.UtcNow,
                Status = GuidedOptimizationStatus.Completed,
                Classification = GuidedResultClassification.Neutral,
                SelectedSettingKeys = new List<string> { "fps_max" },
                SelectedSettings = new Dictionary<string, string> { ["fps_max"] = "0" }
            };
            await store.SaveAsync(v2);

            var path = Directory.EnumerateFiles(paths.GuidedRunsDirectory, "*.json").First();
            var text = await File.ReadAllTextAsync(path);
            // Force schema 2 on disk regardless of save bump
            text = System.Text.RegularExpressions.Regex.Replace(
                text, "\"schemaVersion\"\\s*:\\s*\\d+", "\"schemaVersion\": 2");
            await File.WriteAllTextAsync(path, text);

            var loaded = await store.ListAsync();
            Assert.Contains(loaded, r => r.Id == "v2run");
            var run = loaded.First(r => r.Id == "v2run");
            Assert.False(run.ConditionOverride);
            Assert.Equal(GuidedUserDecision.None, run.ConditionDecision);
            Assert.Null(run.ConditionStatusAtDecision);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ContinueStillProducesInconclusive_GateUnit()
    {
        var a = MakeBench("a", "fp_a", "CPU", "GPU-1");
        var b = MakeBench("b", "fp_a", "CPU", "GPU-2");
        var report = BenchmarkConditionRules.Analyze(a, b, forcedDespiteMismatch: true);
        var gated = BenchmarkConditionRules.ApplyClassificationGate(
            GuidedResultClassification.Improved, report, out _);
        Assert.Equal(GuidedResultClassification.Inconclusive, gated);
        Assert.True(report.ForcedDespiteMismatch);
    }

    private static BenchmarkSystemSnapshot WithPiiTrap(BenchmarkSystemSnapshot s) => new()
    {
        CpuName = s.CpuName,
        GpuName = s.GpuName,
        TotalRamBytes = s.TotalRamBytes,
        OsVersion = s.OsVersion,
        Architecture = s.Architecture,
        SystemFingerprintId = s.SystemFingerprintId,
        ActiveProfileName = "user@example.com",
        Cs2ProcessRunningAtStart = s.Cs2ProcessRunningAtStart,
        DisplayResolution = s.DisplayResolution,
        PowerPlan = s.PowerPlan
    };

    private static BenchmarkRun MakeBench(string id, string fp, string cpu, string gpu)
    {
        var samples = Enumerable.Range(0, 20).Select(i => new BenchmarkSample
        {
            IsWarmup = false,
            SystemCpuPercent = 40,
            ProcessCpuPercent = 20,
            SystemMemoryPercent = 50,
            ProcessWorkingSetBytes = 3L << 30,
            Cs2ProcessPresent = true,
            ElapsedMs = i * 100
        }).ToList();
        return new BenchmarkRun
        {
            Id = id,
            Status = BenchmarkStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow,
            DurationSecondsActual = 30,
            Configuration = new BenchmarkConfiguration
            {
                DurationSeconds = 30,
                SampleIntervalMs = 100,
                WarmupSeconds = 5
            },
            SystemInformation = new BenchmarkSystemSnapshot
            {
                CpuName = cpu,
                GpuName = gpu,
                TotalRamBytes = 16L << 30,
                OsVersion = "TestOS",
                Architecture = "X64",
                SystemFingerprintId = fp,
                Cs2ProcessRunningAtStart = true,
                ActiveProfileName = "Balanced",
                DisplayResolution = "1920x1080",
                DisplayRefreshRateHz = 144,
                PowerPlan = "Balanced",
                GameModeEnabled = true
            },
            Samples = samples,
            Result = new BenchmarkCalculator().Calculate(samples)
        };
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            await Task.Delay(30);
        }

        throw new TimeoutException("Condition not met within " + timeoutMs + "ms; last chance check failed.");
    }

    private sealed class GuidedEnv : IDisposable
    {
        public string Root { get; }
        public GuidedOptimizationService Service { get; }
        public RecordingSettings Settings { get; }
        public string LastProgress { get; private set; } = string.Empty;

        private GuidedEnv(string root, GuidedOptimizationService svc, RecordingSettings settings)
        {
            Root = root;
            Service = svc;
            Settings = settings;
            svc.ProgressChanged += (_, p) => LastProgress = p.Message;
        }

        public GuidedOptimizationRequest Request() => new()
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = new Dictionary<string, string> { ["fps_max"] = "0" },
            Label = "Phase12Gate",
            RequireCs2ProcessForBenchmark = false,
            BenchmarkConfiguration = new BenchmarkConfiguration
            {
                DurationSeconds = 30,
                WarmupSeconds = 0,
                SampleIntervalMs = 50
            }
        };

        public static GuidedEnv Create(bool mismatchGpu = false, bool restoreFails = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ff-p12-g-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "cfg"));

            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var settings = new RecordingSettings { RestoreFails = restoreFails };
            var store = new GuidedOptimizationStore(paths, log);
            var bench = new MismatchBenchEngine(mismatchGpu);
            var mon = new FakeMon { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } };
            var det = new FixedDet(Path.Combine(root, "cfg"));
            var svc = new GuidedOptimizationService(
                det, mon, settings, new EmptyProfiles(), bench, bench.Store,
                new BenchmarkCalculator(), store, log,
                new SystemFingerprintService(new FixedHw()));
            return new GuidedEnv(root, svc, settings);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
    }

    private sealed class MismatchBenchEngine : IBenchmarkEngine
    {
        private readonly bool _mismatchGpu;
        private int _n;
        public InMemStore Store { get; } = new();
        public BenchmarkStatus Status { get; private set; } = BenchmarkStatus.Idle;
        public BenchmarkRun? CurrentRun { get; private set; }
        public BenchmarkProgress? LatestProgress { get; private set; }
        public event EventHandler? StatusChanged;
#pragma warning disable CS0067
        public event EventHandler<BenchmarkSample>? SampleCaptured;
        public event EventHandler<BenchmarkProgress>? ProgressChanged;
#pragma warning restore CS0067

        public MismatchBenchEngine(bool mismatchGpu) => _mismatchGpu = mismatchGpu;

        public async Task<BenchmarkRun> StartAsync(BenchmarkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            _n++;
            Status = BenchmarkStatus.Running;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            var samples = Enumerable.Range(0, 12).Select(i => new BenchmarkSample
            {
                IsWarmup = false,
                SystemCpuPercent = _n == 1 ? 55 : 45,
                ProcessCpuPercent = 20,
                SystemMemoryPercent = 40,
                ProcessWorkingSetBytes = 400_000_000,
                Cs2ProcessPresent = true,
                ElapsedMs = i * configuration.SampleIntervalMs
            }).ToList();
            var calc = new BenchmarkCalculator();
            var gpu = _mismatchGpu && _n == 2 ? "GPU-POST-DIFFERENT" : "GPU-BASE";
            var run = new BenchmarkRun
            {
                Id = "p12bm" + _n,
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                EndedAt = DateTimeOffset.UtcNow,
                Status = BenchmarkStatus.Completed,
                Configuration = configuration,
                SystemInformation = new BenchmarkSystemSnapshot
                {
                    CpuName = "CPU-SAME",
                    GpuName = gpu,
                    TotalRamBytes = 16L << 30,
                    OsVersion = "TestOS",
                    Architecture = "X64",
                    SystemFingerprintId = "fp_same_system_01",
                    Cs2ProcessRunningAtStart = true,
                    ActiveProfileName = configuration.ProfileName
                },
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

    private sealed class InMemStore : IBenchmarkStore
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

    private sealed class RecordingSettings : ICs2SettingsService
    {
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool RestoreFails { get; set; }
        public string? AutoBackupId { get; set; } = "bak_p12";

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
                AffectedFilesOnApply = new[] { "/tmp/cfg/frameforge_settings.cfg", "/tmp/cfg/autoexec.cfg" },
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
                CurrentValue = "400",
                NewValue = kv.Value,
                Reason = reason,
                Risk = "Low"
            }).ToList();
            return new SettingsDiff
            {
                Title = reason,
                ProfileId = profileId,
                ProfileName = profileName,
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
                "applied",
                AutoBackupId,
                new SettingsDiff { Title = reason },
                new[] { "/tmp/cfg/frameforge_settings.cfg" },
                autoexecUpdated: true,
                executedByCs2: true));
        }

        public Task<SettingsApplyResult> ApplyDiffAsync(
            SettingsDiff diff,
            bool createBackup = true,
            CancellationToken cancellationToken = default)
        {
            var map = diff.Entries
                .Where(e => e.NewValue is not null)
                .ToDictionary(e => e.ConfigKey, e => e.NewValue!, StringComparer.OrdinalIgnoreCase);
            return ApplySettingsAsync(map, diff.Title, diff.ProfileId, createBackup, cancellationToken);
        }

        public Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            if (RestoreFails)
            {
                return Task.FromResult(SettingsApplyResult.Fail("Simulated restore failure"));
            }

            return Task.FromResult(SettingsApplyResult.Ok(
                "restored",
                AutoBackupId,
                new SettingsDiff { Title = "restore" },
                new[] { "/tmp/cfg/frameforge_settings.cfg" }));
        }

        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
    }

    private sealed class FixedDet : ICs2DetectionService
    {
        private readonly string _cfg;
        public FixedDet(string cfg) => _cfg = cfg;
        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = Path.GetDirectoryName(_cfg) ?? _cfg,
                CfgDirectory = _cfg,
                DetectionMessage = "test",
                SteamFound = true
            });
    }

    private sealed class FakeMon : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? Process { get; set; }
        public Cs2ProcessInfo? TryGetCs2Process() => Process;
    }

    private sealed class FixedHw : IHardwareInfoService
    {
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo
            {
                CpuName = "P12CPU",
                GpuName = "P12GPU",
                TotalRamBytes = 16L << 30,
                OsDescription = "TestOS",
                WindowsVersion = "TestOS",
                Architecture = "X64"
            });
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
}
