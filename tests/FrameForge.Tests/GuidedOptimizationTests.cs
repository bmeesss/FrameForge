using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.Infrastructure.Guided;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using Xunit;

namespace FrameForge.Tests;

public sealed class GuidedOptimizationTests
{
    [Fact]
    public void Classification_Improved_WhenAllBetter()
    {
        var rows = new[]
        {
            Row("System CPU average", 60, 50),
            Row("CS2 process CPU average", 40, 30)
        };
        var c = GuidedClassificationRules.Classify(rows, out var reason);
        Assert.Equal(GuidedResultClassification.Improved, c);
        Assert.Contains("Improved", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classification_Regressed_WhenAllWorse()
    {
        var rows = new[] { Row("System CPU average", 40, 55) };
        Assert.Equal(GuidedResultClassification.Regressed, GuidedClassificationRules.Classify(rows, out _));
    }

    [Fact]
    public void Classification_Neutral_WithinNoise()
    {
        var rows = new[] { Row("System CPU average", 50, 50.5) }; // < 1pp and < 3%
        Assert.Equal(GuidedResultClassification.Neutral, GuidedClassificationRules.Classify(rows, out _));
    }

    [Fact]
    public void Classification_Mixed_And_Inconclusive()
    {
        var mixed = new[]
        {
            Row("System CPU average", 60, 50),
            Row("System memory average", 40, 55)
        };
        Assert.Equal(GuidedResultClassification.Mixed, GuidedClassificationRules.Classify(mixed, out _));

        var none = Array.Empty<GuidedComparisonRow>();
        Assert.Equal(GuidedResultClassification.Inconclusive, GuidedClassificationRules.Classify(none, out _));

        var unavailable = new[]
        {
            new GuidedComparisonRow { Metric = "x", IsAvailable = false }
        };
        Assert.Equal(GuidedResultClassification.Inconclusive, GuidedClassificationRules.Classify(unavailable, out _));
    }

    [Fact]
    public async Task PreviewOnly_DoesNotApply_OrBenchmark()
    {
        var env = TestEnv.Create();
        var settings = new RecordingSettingsService();
        var svc = env.CreateService(settings: settings, requireProcess: false);

        var run = await svc.PreviewAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            PreviewOnly = true,
            Label = "test"
        });

        Assert.Equal(GuidedOptimizationStatus.Completed, run.Status);
        Assert.True(run.PreviewOnly);
        Assert.Equal(0, settings.ApplyCount);
        Assert.NotNull(run.PreviewDiff);
        Assert.Null(run.InitialBenchmarkId);
    }

    [Fact]
    public async Task FullRun_RequiresCs2Process_WhenConfigured()
    {
        var env = TestEnv.Create();
        var monitor = new FakeProcessMonitor { Process = null };
        var svc = env.CreateService(monitor: monitor, requireProcess: true);

        var run = await svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            RequireCs2ProcessForBenchmark = true,
            BenchmarkConfiguration = ShortBench()
        });

        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
        Assert.Contains("not running", run.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullRun_Confirm_Apply_Keep_Succeeds()
    {
        var env = TestEnv.Create();
        var settings = new RecordingSettingsService { AutoBackupId = "bak_guided_1" };
        var bench = new ScriptedBenchmarkEngine();
        var monitor = new FakeProcessMonitor { Process = new Cs2ProcessInfo { ProcessId = 42, ProcessName = "cs2" } };
        var svc = env.CreateService(settings: settings, benchmark: bench, monitor: monitor);

        var request = new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0", ["volume"] = "0.5" },
            RequireCs2ProcessForBenchmark = true,
            BenchmarkConfiguration = ShortBench()
        };

        var runTask = svc.RunAsync(request);

        // Wait until awaiting confirmation
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        Assert.Equal(1, bench.StartCount); // baseline done
        svc.ConfirmApply();

        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingDecision, 8000);
        Assert.True(settings.ApplyCount >= 1);
        Assert.Equal(2, bench.StartCount); // baseline + post
        svc.Decide(GuidedUserDecision.Keep);

        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Completed, run.Status);
        Assert.Equal(GuidedUserDecision.Keep, run.UserDecision);
        Assert.Equal("bak_guided_1", run.BackupId);
        Assert.False(string.IsNullOrWhiteSpace(run.InitialBenchmarkId));
        Assert.False(string.IsNullOrWhiteSpace(run.PostBenchmarkId));
        Assert.True(run.ApplySucceeded);
        Assert.NotEmpty(run.ComparisonRows);

        var listed = await env.Store.ListAsync();
        Assert.Contains(listed, r => r.Id == run.Id);
    }

    [Fact]
    public async Task FullRun_Restore_Decision_CallsRestore()
    {
        var env = TestEnv.Create();
        var settings = new RecordingSettingsService { AutoBackupId = "bak_r" };
        var bench = new ScriptedBenchmarkEngine();
        var monitor = new FakeProcessMonitor { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } };
        var svc = env.CreateService(settings: settings, benchmark: bench, monitor: monitor);

        var runTask = svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "120" },
            BenchmarkConfiguration = ShortBench()
        });

        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        svc.ConfirmApply();
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingDecision, 8000);
        svc.Decide(GuidedUserDecision.Restore);

        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Restored, run.Status);
        Assert.Equal(GuidedUserDecision.Restore, run.UserDecision);
        Assert.True(run.RestoredSuccessfully);
        Assert.Equal(1, settings.RestoreCount);
    }

    [Fact]
    public async Task BackupFailure_StopsWithoutApply()
    {
        var env = TestEnv.Create();
        var settings = new RecordingSettingsService
        {
            ApplyResultFactory = () => SettingsApplyResult.Fail("Backup failed; apply aborted: disk full")
        };
        var bench = new ScriptedBenchmarkEngine();
        var monitor = new FakeProcessMonitor { Process = new Cs2ProcessInfo { ProcessId = 7, ProcessName = "cs2" } };
        var svc = env.CreateService(settings: settings, benchmark: bench, monitor: monitor);

        var runTask = svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            BenchmarkConfiguration = ShortBench()
        });

        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        svc.ConfirmApply();
        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
        Assert.Contains("Backup failed", run.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyVerifyFailure_WithRollback_MarksRestored()
    {
        var env = TestEnv.Create();
        var settings = new RecordingSettingsService
        {
            ApplyResultFactory = () => SettingsApplyResult.Fail("Verification failed", backupId: "bak_x", rolledBack: true)
        };
        var bench = new ScriptedBenchmarkEngine();
        var monitor = new FakeProcessMonitor { Process = new Cs2ProcessInfo { ProcessId = 3, ProcessName = "cs2" } };
        var svc = env.CreateService(settings: settings, benchmark: bench, monitor: monitor);

        var runTask = svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            BenchmarkConfiguration = ShortBench()
        });
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        svc.ConfirmApply();
        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Restored, run.Status);
        Assert.True(run.RestoredSuccessfully);
        Assert.Null(run.PostBenchmarkId);
    }

    [Fact]
    public async Task Cancel_BeforeConfirm_LeavesNoApply()
    {
        var env = TestEnv.Create();
        var settings = new RecordingSettingsService();
        var bench = new ScriptedBenchmarkEngine();
        var monitor = new FakeProcessMonitor { Process = new Cs2ProcessInfo { ProcessId = 9, ProcessName = "cs2" } };
        var svc = env.CreateService(settings: settings, benchmark: bench, monitor: monitor);

        var runTask = svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            BenchmarkConfiguration = ShortBench()
        });
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        svc.Cancel();
        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Cancelled, run.Status);
        Assert.Equal(0, settings.ApplyCount);
    }

    [Fact]
    public async Task MissingCs2Install_Fails()
    {
        var env = TestEnv.Create();
        var detection = new FakeDetection { Info = Cs2InstallInfo.NotFound("missing") };
        var svc = env.CreateService(detection: detection, monitor: new FakeProcessMonitor
        {
            Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" }
        });

        var run = await svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            BenchmarkConfiguration = ShortBench()
        });
        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
        Assert.Contains("cfg", run.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_HandlesCorruptHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_g_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var store = new GuidedOptimizationStore(paths, log);
            Directory.CreateDirectory(paths.GuidedRunsDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.GuidedRunsDirectory, "bad.json"), "{nope");

            var run = new GuidedOptimizationRun
            {
                Id = "good_run",
                StartedAt = DateTimeOffset.UtcNow,
                Status = GuidedOptimizationStatus.Completed
            };
            await store.SaveAsync(run);
            var list = await store.ListAsync();
            Assert.Contains(list, r => r.Id == "good_run");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task EmptyDesired_FailsValidationPath()
    {
        var env = TestEnv.Create();
        var svc = env.CreateService(
            monitor: new FakeProcessMonitor { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } },
            benchmark: new ScriptedBenchmarkEngine());

        // Settings map empty
        var run = await svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { },
            BenchmarkConfiguration = ShortBench()
        });
        // Fails during prepare before benchmark
        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
    }

    private static GuidedComparisonRow Row(string name, double before, double after) => new()
    {
        Metric = name,
        Unit = "%",
        Before = before,
        After = after,
        AbsoluteDifference = after - before,
        PercentDifference = before == 0 ? null : 100.0 * (after - before) / before,
        IsAvailable = true,
        PreferredDirection = "lower"
    };

    private static BenchmarkConfiguration ShortBench() => new()
    {
        DurationSeconds = 30,
        WarmupSeconds = 0,
        SampleIntervalMs = 50
    };

    private static async Task WaitUntil(Func<bool> pred, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (pred())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition not met: " + pred.Method.Name);
    }

    private sealed class TestEnv
    {
        public required string Root { get; init; }
        public required PathService Paths { get; init; }
        public required FileAppLog Log { get; init; }
        public required GuidedOptimizationStore Store { get; init; }

        public static TestEnv Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ff_guided_" + Guid.NewGuid().ToString("N"));
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            return new TestEnv
            {
                Root = root,
                Paths = paths,
                Log = log,
                Store = new GuidedOptimizationStore(paths, log)
            };
        }

        public GuidedOptimizationService CreateService(
            RecordingSettingsService? settings = null,
            ScriptedBenchmarkEngine? benchmark = null,
            FakeProcessMonitor? monitor = null,
            FakeDetection? detection = null,
            bool requireProcess = true)
        {
            settings ??= new RecordingSettingsService();
            benchmark ??= new ScriptedBenchmarkEngine();
            monitor ??= new FakeProcessMonitor();
            detection ??= new FakeDetection
            {
                Info = new Cs2InstallInfo
                {
                    IsInstalled = true,
                    InstallPath = Root,
                    CfgDirectory = Path.Combine(Root, "cfg"),
                    DetectionMessage = "ok"
                }
            };
            if (!string.IsNullOrWhiteSpace(detection.Info.CfgDirectory))
            {
                Directory.CreateDirectory(detection.Info.CfgDirectory);
            }

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

    private sealed class FakeProcessMonitor : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? Process { get; set; }
        public Cs2ProcessInfo? TryGetCs2Process() => Process;
    }

    private sealed class FakeProfiles : IProfileService
    {
        public IReadOnlyList<string> BuiltInProfileIds { get; } = new[] { "balanced" };
        public Task<IReadOnlyList<PerformanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PerformanceProfile>>(new[]
            {
                new PerformanceProfile { Id = "balanced", Name = "Balanced", Settings = { ["fps_max"] = "400" } }
            });
        public Task<PerformanceProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PerformanceProfile?>(new PerformanceProfile
            {
                Id = profileId,
                Name = profileId,
                Settings = { ["fps_max"] = "400" }
            });
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

    private sealed class RecordingSettingsService : ICs2SettingsService
    {
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }
        public string? AutoBackupId { get; set; } = "bak_default";
        public Func<SettingsApplyResult>? ApplyResultFactory { get; set; }

        public string ManagedConfigFileName => "frameforge_settings.cfg";
        public string AutoexecFileName => "autoexec.cfg";

        public Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default)
        {
            var catalog = new FrameForge.CS2.Settings.Cs2SettingCatalog();
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

        public SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values)
        {
            var issues = new List<SettingsValidationIssue>();
            foreach (var (k, v) in values)
            {
                if (k.Equals("fps_max", StringComparison.OrdinalIgnoreCase) && !long.TryParse(v, out _))
                {
                    issues.Add(new SettingsValidationIssue { ConfigKey = k, Message = "bad", IsError = true });
                }
            }

            return new SettingsValidationResult { Issues = issues };
        }

        public SettingsDiff CreateDiff(
            Cs2SettingsSnapshot current,
            IReadOnlyDictionary<string, string> desired,
            string reason,
            string? profileId = null,
            string? profileName = null)
        {
            var entries = new List<SettingsDiffEntry>();
            foreach (var (k, v) in desired)
            {
                var cur = current.Settings.FirstOrDefault(s => s.Definition.ConfigKey.Equals(k, StringComparison.OrdinalIgnoreCase));
                var cv = cur?.CurrentValue;
                if (string.Equals(cv, v, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new SettingsDiffEntry
                {
                    SettingId = k,
                    ConfigKey = k,
                    DisplayName = k,
                    CurrentValue = cv,
                    NewValue = v,
                    Reason = reason,
                    Risk = "Low"
                });
            }

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
            if (ApplyResultFactory is not null)
            {
                return Task.FromResult(ApplyResultFactory());
            }

            return Task.FromResult(SettingsApplyResult.Ok(
                "applied",
                AutoBackupId,
                new SettingsDiff { Title = reason, Entries = Array.Empty<SettingsDiffEntry>() },
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
            return Task.FromResult(SettingsApplyResult.Ok(
                "restored",
                AutoBackupId,
                new SettingsDiff { Title = "restore" },
                new[] { "/tmp/cfg/frameforge_settings.cfg" }));
        }

        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
    }

    private sealed class ScriptedBenchmarkEngine : IBenchmarkEngine
    {
        public int StartCount { get; private set; }
        public BenchmarkStatus Status { get; private set; } = BenchmarkStatus.Idle;
        public BenchmarkRun? CurrentRun { get; private set; }
        public InMemoryBenchmarkStore Store { get; } = new();

        public event EventHandler? StatusChanged;
#pragma warning disable CS0067 // reserved for IBenchmarkEngine parity
        public event EventHandler<BenchmarkSample>? SampleCaptured;
#pragma warning restore CS0067

        public async Task<BenchmarkRun> StartAsync(BenchmarkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            StartCount++;
            Status = BenchmarkStatus.Running;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);

            var cpu = StartCount == 1 ? 60.0 : 50.0;
            var samples = Enumerable.Range(0, 25).Select(i => new BenchmarkSample
            {
                IsWarmup = false,
                SystemCpuPercent = cpu,
                ProcessCpuPercent = cpu / 2,
                SystemMemoryPercent = 40,
                ProcessWorkingSetBytes = 500_000_000,
                Cs2ProcessPresent = true,
                ElapsedMs = i * configuration.SampleIntervalMs
            }).ToList();

            var calc = new FrameForge.Benchmark.BenchmarkCalculator();
            var run = new BenchmarkRun
            {
                Id = "bm_" + StartCount + "_" + Guid.NewGuid().ToString("N")[..4],
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-configuration.DurationSeconds),
                EndedAt = DateTimeOffset.UtcNow,
                Status = BenchmarkStatus.Completed,
                Configuration = configuration,
                SystemInformation = new BenchmarkSystemSnapshot
                {
                    CpuName = "TestCPU",
                    GpuName = "TestGPU",
                    OsVersion = "TestOS",
                    TotalRamBytes = 8L << 30,
                    Cs2ProcessRunningAtStart = true,
                    ActiveProfileId = configuration.ProfileId
                },
                Samples = samples,
                Result = calc.Calculate(samples),
                DurationSecondsActual = configuration.DurationSeconds
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
    }

    private sealed class InMemoryBenchmarkStore : IBenchmarkStore
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
