using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Guided;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Performance;
using Xunit;

namespace FrameForge.Tests;

public sealed class TargetedRestoreAndRetestTests
{
    [Fact]
    public async Task SafeRestore_WritesPreviousValue_AndPreservesOtherKeys()
    {
        var env = RestoreEnv.Create();
        var managed = env.ManagedPath;
        await File.WriteAllTextAsync(managed,
            "fps_max 0" + Environment.NewLine + "volume 0.5" + Environment.NewLine);

        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                SettingId = "video.fps_max",
                ConfigKey = "fps_max",
                PreviousValue = "400",
                NewValue = "0",
                File = managed,
                BackupId = "optional",
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        // Seed a dummy backup id optional — assessment allows missing backup if values match
        var assess = await env.Service.AssessAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.SafeToTargetRestore, assess.Safety);
        Assert.Equal("400", assess.RestoreValue);
        Assert.Equal("0", assess.CurrentValue);

        var result = await env.Service.RestoreAsync("fps_max");
        Assert.True(result.Success, result.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.BackupId));

        var doc = await env.Config.ReadAsync(managed);
        var map = doc.ToDictionary();
        Assert.Equal("400", map["fps_max"]);
        Assert.Equal("0.5", map["volume"]); // unrelated preserved
    }

    [Fact]
    public async Task Unsafe_WhenCurrentDiffersFromApplied()
    {
        var env = RestoreEnv.Create();
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 144" + Environment.NewLine);

        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                SettingId = "video.fps_max",
                ConfigKey = "fps_max",
                PreviousValue = "300",
                NewValue = "240",
                File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        var assess = await env.Service.AssessAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.UnsafeToTargetRestore, assess.Safety);
        Assert.Contains("differs from the last FrameForge-managed value", assess.Message, StringComparison.OrdinalIgnoreCase);

        var result = await env.Service.RestoreAsync("fps_max");
        Assert.False(result.Success);
        Assert.True(result.Refused);
        // file unchanged
        var text = await File.ReadAllTextAsync(env.ManagedPath);
        Assert.Contains("144", text);
    }

    [Fact]
    public async Task MissingSnapshot_NotTracked()
    {
        var env = RestoreEnv.Create();
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 0\n");
        var assess = await env.Service.AssessAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.NotTracked, assess.Safety);
        var result = await env.Service.RestoreAsync("fps_max");
        Assert.True(result.Refused);
    }

    [Fact]
    public async Task CorruptedSnapshot_InsufficientEvidence()
    {
        var env = RestoreEnv.Create();
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 0\n");
        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                ConfigKey = "fps_max",
                // missing PreviousValue / NewValue
                File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        var assess = await env.Service.AssessAsync("fps_max");
        Assert.True(
            assess.Safety is TargetedRestoreSafety.InsufficientEvidence
                or TargetedRestoreSafety.InsufficientMetadata
                or TargetedRestoreSafety.NotTracked);
    }

    [Fact]
    public async Task MultipleSnapshots_UsesLatestChronological()
    {
        var env = RestoreEnv.Create();
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 100\n");

        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                ConfigKey = "fps_max",
                SettingId = "video.fps_max",
                PreviousValue = "400",
                NewValue = "0",
                File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow.AddHours(-2)
            },
            new SettingChangeSnapshot
            {
                ConfigKey = "fps_max",
                SettingId = "video.fps_max",
                PreviousValue = "200",
                NewValue = "100",
                File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            }
        });

        var assess = await env.Service.AssessAsync("fps_max");
        Assert.Equal(TargetedRestoreSafety.SafeToTargetRestore, assess.Safety);
        Assert.Equal("200", assess.RestoreValue);
        Assert.Equal("100", assess.AppliedValue);
    }

    [Fact]
    public async Task BackupFailure_StopsWithoutWrite()
    {
        var env = RestoreEnv.Create(failBackup: true);
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 0\n");
        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                ConfigKey = "fps_max",
                SettingId = "video.fps_max",
                PreviousValue = "400",
                NewValue = "0",
                File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        var before = await File.ReadAllTextAsync(env.ManagedPath);
        var result = await env.Service.RestoreAsync("fps_max");
        Assert.False(result.Success);
        Assert.Contains("Backup failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(env.ManagedPath));
    }

    [Fact]
    public async Task Autoexec_NotModified_OnTargetedRestore()
    {
        var env = RestoreEnv.Create();
        var autoexec = Path.Combine(env.CfgDir, "autoexec.cfg");
        var userLine = "bind \"x\" \"say hello from user\"";
        await File.WriteAllTextAsync(autoexec,
            userLine + Environment.NewLine +
            "// FRAMEFORGE BEGIN" + Environment.NewLine +
            "exec frameforge_settings.cfg" + Environment.NewLine +
            "// FRAMEFORGE END" + Environment.NewLine);
        await File.WriteAllTextAsync(env.ManagedPath, "fps_max 0\n");

        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                ConfigKey = "fps_max",
                SettingId = "video.fps_max",
                PreviousValue = "300",
                NewValue = "0",
                File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        var beforeAuto = await File.ReadAllTextAsync(autoexec);
        var result = await env.Service.RestoreAsync("fps_max");
        Assert.True(result.Success, result.Message);
        var afterAuto = await File.ReadAllTextAsync(autoexec);
        Assert.Equal(beforeAuto, afterAuto);
        Assert.Contains(userLine, afterAuto);
    }

    [Fact]
    public async Task RestoreSet_MultipleKeys()
    {
        var env = RestoreEnv.Create();
        await File.WriteAllTextAsync(env.ManagedPath,
            "fps_max 0" + Environment.NewLine + "volume 0.2" + Environment.NewLine);

        await env.SnapStore.AppendAsync(new[]
        {
            new SettingChangeSnapshot
            {
                ConfigKey = "fps_max", SettingId = "video.fps_max",
                PreviousValue = "400", NewValue = "0", File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow
            },
            new SettingChangeSnapshot
            {
                ConfigKey = "volume", SettingId = "audio.volume",
                PreviousValue = "0.8", NewValue = "0.2", File = env.ManagedPath,
                Timestamp = DateTimeOffset.UtcNow
            }
        });

        var batch = await env.Service.RestoreSetAsync(new[] { "fps_max", "volume" });
        Assert.True(batch.AllSucceeded, batch.Summary);
        var map = (await env.Config.ReadAsync(env.ManagedPath)).ToDictionary();
        Assert.Equal("400", map["fps_max"]);
        Assert.Equal("0.8", map["volume"]);
    }

    [Fact]
    public async Task Retest_CreatesGuidedRun_AndUpdatesIntelligence()
    {
        var env = RestoreEnv.Create();
        var guided = new GuidedOptimizationStore(env.Paths, env.Log);
        var settings = new RecordingSettingsForGuided(env);
        var bench = new ScriptedBenchEngine();
        var monitor = new FakeMon { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } };
        var fp = new SystemFingerprintService(new FixedHw());
        var guidedSvc = new GuidedOptimizationService(
            new FakeDet
            {
                Info = new Cs2InstallInfo
                {
                    IsInstalled = true,
                    InstallPath = env.Root,
                    CfgDirectory = env.CfgDir,
                    DetectionMessage = "ok"
                }
            },
            monitor,
            settings,
            new EmptyProfiles(),
            bench,
            bench.Store,
            new FrameForge.Benchmark.BenchmarkCalculator(),
            guided,
            env.Log,
            fp);

        var intel = new PerformanceIntelligenceService(
            guided, fp, new Cs2SettingCatalog(), env.Paths, env.Log);

        var runTask = guidedSvc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            Label = "Retest fps_max (Recommended)",
            BenchmarkConfiguration = new BenchmarkConfiguration
            {
                DurationSeconds = 30, WarmupSeconds = 0, SampleIntervalMs = 50
            }
        });

        await WaitUntil(() => guidedSvc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        guidedSvc.ConfirmApply();
        await WaitUntil(() => guidedSvc.Status == GuidedOptimizationStatus.AwaitingDecision, 8000);
        guidedSvc.Decide(GuidedUserDecision.Keep);
        var run = await runTask;

        Assert.Equal(GuidedOptimizationStatus.Completed, run.Status);
        Assert.False(string.IsNullOrWhiteSpace(run.SystemFingerprintId));
        Assert.Contains(run.SelectedSettingKeys, k => k.Equals("fps_max", StringComparison.OrdinalIgnoreCase));

        intel.InvalidateCache();
        var index = await intel.RebuildAsync();
        var rec = index.CurrentSystemRecords.FirstOrDefault(r => r.SettingKey == "fps_max")
                  ?? index.Records.FirstOrDefault(r => r.SettingKey == "fps_max");
        Assert.NotNull(rec);
        Assert.True(rec!.DirectTestCount >= 1);
        Assert.True(rec.IsCurrentSystem);
    }

    [Fact]
    public async Task FingerprintSeparation_DoesNotMergeSystems()
    {
        var env = RestoreEnv.Create();
        var guided = new GuidedOptimizationStore(env.Paths, env.Log);
        var fp = new SystemFingerprintService(new FixedHw());
        var current = await fp.GetFingerprintAsync();

        await guided.SaveAsync(new GuidedOptimizationRun
        {
            Id = "cur",
            Status = GuidedOptimizationStatus.Completed,
            Classification = GuidedResultClassification.Improved,
            SelectedSettingKeys = { "fps_max" },
            SelectedSettings = { ["fps_max"] = "0" },
            SystemFingerprintId = current.FingerprintId,
            InitialBenchmarkId = "b1",
            PostBenchmarkId = "b2",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ComparisonRows =
            {
                new GuidedComparisonRow
                {
                    Metric = "System CPU average", Unit = "%",
                    Before = 50, After = 40, AbsoluteDifference = -10, IsAvailable = true
                }
            }
        });

        await guided.SaveAsync(new GuidedOptimizationRun
        {
            Id = "old",
            Status = GuidedOptimizationStatus.Completed,
            Classification = GuidedResultClassification.Regressed,
            SelectedSettingKeys = { "fps_max" },
            SelectedSettings = { ["fps_max"] = "0" },
            SystemFingerprintId = "deadbeefdeadbeef",
            InitialBenchmarkId = "b3",
            PostBenchmarkId = "b4",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-30),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ComparisonRows =
            {
                new GuidedComparisonRow
                {
                    Metric = "System CPU average", Unit = "%",
                    Before = 40, After = 55, AbsoluteDifference = 15, IsAvailable = true
                }
            }
        });

        var intel = new PerformanceIntelligenceService(
            guided, fp, new Cs2SettingCatalog(), env.Paths, env.Log);
        var index = await intel.RebuildAsync();

        var currentRecs = index.Records.Where(r => r.SettingKey == "fps_max" && r.IsCurrentSystem).ToList();
        var otherRecs = index.Records.Where(r => r.SettingKey == "fps_max" && !r.IsCurrentSystem).ToList();
        Assert.Single(currentRecs);
        Assert.Single(otherRecs);
        Assert.Equal(GuidedResultClassification.Improved, currentRecs[0].LatestClassification);
        Assert.Equal(GuidedResultClassification.Regressed, otherRecs[0].LatestClassification);
        Assert.Equal("Different system fingerprint", otherRecs[0].FingerprintLabel);

        // Default GetRecord is current-only
        var only = intel.GetRecord("fps_max", currentSystemOnly: true);
        Assert.NotNull(only);
        Assert.True(only!.IsCurrentSystem);
        Assert.Equal(1, only.DirectTestCount);
    }

    [Fact]
    public async Task CancelledRetest_DoesNotApply()
    {
        var env = RestoreEnv.Create();
        var guided = new GuidedOptimizationStore(env.Paths, env.Log);
        var settings = new RecordingSettingsForGuided(env);
        var bench = new ScriptedBenchEngine();
        var svc = new GuidedOptimizationService(
            new FakeDet
            {
                Info = new Cs2InstallInfo
                {
                    IsInstalled = true, InstallPath = env.Root, CfgDirectory = env.CfgDir, DetectionMessage = "ok"
                }
            },
            new FakeMon { Process = new Cs2ProcessInfo { ProcessId = 2, ProcessName = "cs2" } },
            settings,
            new EmptyProfiles(),
            bench,
            bench.Store,
            new FrameForge.Benchmark.BenchmarkCalculator(),
            guided,
            env.Log);

        var runTask = svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            BenchmarkConfiguration = new BenchmarkConfiguration { DurationSeconds = 30, WarmupSeconds = 0 }
        });
        await WaitUntil(() => svc.Status == GuidedOptimizationStatus.AwaitingConfirmation, 5000);
        svc.Cancel();
        var run = await runTask;
        Assert.Equal(GuidedOptimizationStatus.Cancelled, run.Status);
        Assert.Equal(0, settings.ApplyCount);
    }

    [Fact]
    public async Task FailedRetest_WhenCs2Missing()
    {
        var env = RestoreEnv.Create();
        var guided = new GuidedOptimizationStore(env.Paths, env.Log);
        var svc = new GuidedOptimizationService(
            new FakeDet { Info = Cs2InstallInfo.NotFound("gone") },
            new FakeMon { Process = new Cs2ProcessInfo { ProcessId = 1, ProcessName = "cs2" } },
            new RecordingSettingsForGuided(env),
            new EmptyProfiles(),
            new ScriptedBenchEngine(),
            new ScriptedBenchEngine().Store,
            new FrameForge.Benchmark.BenchmarkCalculator(),
            guided,
            env.Log);

        var run = await svc.RunAsync(new GuidedOptimizationRequest
        {
            TargetKind = GuidedTargetKind.SettingsMap,
            DesiredSettings = { ["fps_max"] = "0" },
            BenchmarkConfiguration = new BenchmarkConfiguration { DurationSeconds = 30, WarmupSeconds = 0 }
        });
        Assert.Equal(GuidedOptimizationStatus.Failed, run.Status);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task WaitUntil(Func<bool> pred, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (pred()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("timeout");
    }

    private sealed class RestoreEnv
    {
        public required string Root { get; init; }
        public required string CfgDir { get; init; }
        public required string ManagedPath { get; init; }
        public required PathService Paths { get; init; }
        public required FileAppLog Log { get; init; }
        public required SettingChangeSnapshotStore SnapStore { get; init; }
        public required Cs2ConfigService Config { get; init; }
        public required TargetedRestoreService Service { get; init; }

        public static RestoreEnv Create(bool failBackup = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ff_tr_" + Guid.NewGuid().ToString("N"));
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var cfg = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfg);
            var managed = Path.Combine(cfg, "frameforge_settings.cfg");
            File.WriteAllText(managed, "");

            IBackupService backups = failBackup
                ? new FailingBackup()
                : new BackupService(paths, log);

            var snap = new SettingChangeSnapshotStore(paths, log);
            var config = new Cs2ConfigService();
            var settings = new LiveFileSettings(cfg, managed, config);
            var service = new TargetedRestoreService(
                snap, backups, settings, config, new Cs2SettingCatalog(), log);

            return new RestoreEnv
            {
                Root = root,
                CfgDir = cfg,
                ManagedPath = managed,
                Paths = paths,
                Log = log,
                SnapStore = snap,
                Config = config,
                Service = service
            };
        }
    }

    private sealed class FailingBackup : IBackupService
    {
        public Task<BackupEntry> CreateBackupAsync(
            string description,
            IEnumerable<string> optimizationIds,
            IEnumerable<string> affectedFiles,
            IReadOnlyDictionary<string, string?> previousValues,
            string? profileId = null,
            IEnumerable<SettingChangeSnapshot>? settingChangeSnapshots = null,
            CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");

        public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupEntry>>(Array.Empty<BackupEntry>());
        public Task<BackupEntry?> GetBackupAsync(string backupId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackupEntry?>(null);
        public Task<BackupRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupRestoreResult.Fail(backupId, "n/a"));
        public Task DeleteAsync(string backupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LiveFileSettings : ICs2SettingsService
    {
        private readonly string _cfg;
        private readonly string _managed;
        private readonly ICs2ConfigService _config;
        private readonly ICs2SettingCatalog _catalog = new Cs2SettingCatalog();

        public LiveFileSettings(string cfg, string managed, ICs2ConfigService config)
        {
            _cfg = cfg;
            _managed = managed;
            _config = config;
        }

        public string ManagedConfigFileName => "frameforge_settings.cfg";
        public string AutoexecFileName => "autoexec.cfg";

        public async Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_managed))
            {
                var doc = await _config.ReadAsync(_managed, cancellationToken).ConfigureAwait(false);
                foreach (var kv in doc.ToDictionary())
                {
                    map[kv.Key] = kv.Value;
                }
            }

            var settings = _catalog.GetAll().Select(d => new Cs2SettingValue
            {
                Definition = d,
                CurrentValue = map.TryGetValue(d.ConfigKey, out var v) ? v : d.DefaultValue,
                RecommendedValue = d.RecommendedValue,
                Source = map.ContainsKey(d.ConfigKey) ? Cs2SettingSource.FrameForgeManaged : Cs2SettingSource.Default
            }).ToList();

            return new Cs2SettingsSnapshot
            {
                Cs2Available = true,
                CfgDirectory = _cfg,
                ManagedConfigPath = _managed,
                AutoexecPath = Path.Combine(_cfg, "autoexec.cfg"),
                Settings = settings,
                Message = "ok",
                ManagedConfigExists = File.Exists(_managed),
                ExecutionStatus = Cs2ConfigExecutionStatus.Wired
            };
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

    private sealed class RecordingSettingsForGuided : ICs2SettingsService
    {
        private readonly RestoreEnv _env;
        public int ApplyCount { get; private set; }

        public RecordingSettingsForGuided(RestoreEnv env) => _env = env;

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
                CfgDirectory = _env.CfgDir,
                ManagedConfigPath = _env.ManagedPath,
                AutoexecPath = Path.Combine(_env.CfgDir, "autoexec.cfg"),
                Settings = settings,
                AffectedFilesOnApply = new[] { _env.ManagedPath },
                ExecutionStatus = Cs2ConfigExecutionStatus.Wired,
                Message = "ok"
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
                ConfigKey = kv.Key,
                SettingId = kv.Key,
                DisplayName = kv.Key,
                CurrentValue = current.Settings.FirstOrDefault(s =>
                    s.Definition.ConfigKey.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))?.CurrentValue,
                NewValue = kv.Value,
                Risk = "Low",
                Reason = reason
            }).Where(e => e.IsChange).ToList();
            return new SettingsDiff { Title = reason, Entries = entries, AffectedFiles = current.AffectedFilesOnApply };
        }

        public Task<SettingsApplyResult> ApplySettingsAsync(
            IReadOnlyDictionary<string, string> desired, string reason, string? profileId = null,
            bool createBackup = true, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return Task.FromResult(SettingsApplyResult.Ok("ok", "bak", new SettingsDiff { Title = reason },
                new[] { _env.ManagedPath }, true, true));
        }

        public Task<SettingsApplyResult> ApplyDiffAsync(SettingsDiff diff, bool createBackup = true, CancellationToken cancellationToken = default)
        {
            var map = diff.Entries.Where(e => e.NewValue is not null)
                .ToDictionary(e => e.ConfigKey, e => e.NewValue!, StringComparer.OrdinalIgnoreCase);
            return ApplySettingsAsync(map, diff.Title, diff.ProfileId, createBackup, cancellationToken);
        }

        public Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SettingsApplyResult.Ok("r", "bak", new SettingsDiff { Title = "r" }, Array.Empty<string>()));
        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
    }

    private sealed class ScriptedBenchEngine : IBenchmarkEngine
    {
        public int StartCount { get; private set; }
        public BenchmarkStatus Status { get; private set; } = BenchmarkStatus.Idle;
        public BenchmarkRun? CurrentRun { get; private set; }
        public BenchmarkProgress? LatestProgress { get; private set; }
        public InMemStore Store { get; } = new();
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
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            var cpu = StartCount == 1 ? 60.0 : 50.0;
            var samples = Enumerable.Range(0, 15).Select(i => new BenchmarkSample
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

    private sealed class FakeDet : ICs2DetectionService
    {
        public Cs2InstallInfo Info { get; set; } = Cs2InstallInfo.NotFound("x");
        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);
    }

    private sealed class FakeMon : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? Process { get; set; }
        public Cs2ProcessInfo? TryGetCs2Process() => Process;
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

    private sealed class FixedHw : IHardwareInfoService
    {
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo
            {
                CpuName = "Phase8CPU",
                GpuName = "Phase8GPU",
                TotalRamBytes = 16L << 30,
                OsDescription = "TestOS",
                Architecture = "X64"
            });
    }
}
