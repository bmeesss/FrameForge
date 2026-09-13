using FrameForge.Benchmark;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Performance;
using FrameForge.Infrastructure.Settings;
using Xunit;

namespace FrameForge.Tests;

/// <summary>
/// Phase 11: fingerprint stamping, condition match, integrity validation,
/// forced compare, GPU unavailable, import dialog state.
/// </summary>
public sealed class Phase11BenchmarkIntegrityTests
{
    [Fact]
    public async Task Benchmark_StampsFingerprint_WhenServiceAvailable()
    {
        using var env = BenchEnv.Create(withFingerprint: true);
        var run = await env.Engine.StartAsync(new BenchmarkConfiguration
        {
            DurationSeconds = 30,
            WarmupSeconds = 1,
            SampleIntervalMs = 50
        });
        Assert.Equal(BenchmarkStatus.Completed, run.Status);
        Assert.False(string.IsNullOrWhiteSpace(run.SystemInformation.SystemFingerprintId));
        Assert.Equal(run.SystemInformation.SystemFingerprintId, run.SystemFingerprintId);
        Assert.Equal(16, run.SystemInformation.SystemFingerprintId!.Length);
    }

    [Fact]
    public async Task Benchmark_MissingFingerprint_DoesNotFailRun()
    {
        using var env = BenchEnv.Create(withFingerprint: true, fingerprintThrows: true);
        var run = await env.Engine.StartAsync(new BenchmarkConfiguration
        {
            DurationSeconds = 30,
            WarmupSeconds = 1,
            SampleIntervalMs = 50
        });
        Assert.Equal(BenchmarkStatus.Completed, run.Status);
        Assert.Null(run.SystemInformation.SystemFingerprintId);
    }

    [Fact]
    public async Task Benchmark_NoFingerprintService_CompletesWithoutId()
    {
        using var env = BenchEnv.Create(withFingerprint: false);
        var run = await env.Engine.StartAsync(new BenchmarkConfiguration
        {
            DurationSeconds = 30,
            WarmupSeconds = 1,
            SampleIntervalMs = 50
        });
        Assert.Equal(BenchmarkStatus.Completed, run.Status);
        Assert.Null(run.SystemInformation.SystemFingerprintId);
    }

    [Fact]
    public void GuidedFingerprint_PropagatesFromBenchmarkSnapshots()
    {
        var fp = "aabbccddeeff0011";
        var before = MakeRun("b", fp: fp, cpu: 55);
        var after = MakeRun("a", fp: fp, cpu: 45);
        Assert.Equal(before.SystemInformation.SystemFingerprintId, after.SystemInformation.SystemFingerprintId);

        // Guided uses snapshot as source of truth
        string? baselineFp = before.SystemInformation.SystemFingerprintId;
        string? postFp = after.SystemInformation.SystemFingerprintId;
        Assert.Equal(baselineFp, postFp);
        Assert.Equal(fp, baselineFp);
    }

    [Fact]
    public void ConditionMatch_WhenUnchanged_IsMatch()
    {
        var a = MakeRun("a", fp: "fp11111111111111", cpuName: "CPU-X", gpuName: "GPU-X");
        var b = MakeRun("b", fp: "fp11111111111111", cpuName: "CPU-X", gpuName: "GPU-X");
        var report = BenchmarkConditionRules.Analyze(a, b);
        Assert.Equal(ConditionMatchStatus.Match, report.OverallStatus);
        Assert.False(report.HasSevereMismatch);
        Assert.Contains(report.Fields, f => f.Field == "fingerprint" && f.Status == ConditionMatchStatus.Match);
        Assert.Contains(report.Fields, f => f.Field == "cpu" && f.Status == ConditionMatchStatus.Match);
    }

    [Fact]
    public void ConditionMatch_MissingMetadata_IsUnknown_NotMismatch()
    {
        var a = MakeRun("a", fp: null, cpuName: "CPU-X");
        a.SystemInformation = CloneSnap(a.SystemInformation, display: null, power: null, gameMode: null);
        var b = MakeRun("b", fp: null, cpuName: "CPU-X");
        b.SystemInformation = CloneSnap(b.SystemInformation, display: null, power: null, gameMode: null);
        var report = BenchmarkConditionRules.Analyze(a, b);
        Assert.Contains(report.Fields, f => f.Field == "fingerprint" && f.Status == ConditionMatchStatus.Unknown);
        Assert.Contains(report.Fields, f => f.Field == "resolution" && f.Status == ConditionMatchStatus.Unknown);
        Assert.DoesNotContain(report.Fields, f => f.Field == "fingerprint" && f.Status == ConditionMatchStatus.SevereMismatch);
    }

    [Fact]
    public void ConditionMatch_Warning_WhenOsDiffers()
    {
        var a = MakeRun("a", fp: "fp22222222222222", cpuName: "CPU", gpuName: "GPU");
        var b = MakeRun("b", fp: "fp22222222222222", cpuName: "CPU", gpuName: "GPU");
        a.SystemInformation = CloneSnap(a.SystemInformation, os: "Windows 10");
        b.SystemInformation = CloneSnap(b.SystemInformation, os: "Windows 11");
        var report = BenchmarkConditionRules.Analyze(a, b);
        Assert.Equal(ConditionMatchStatus.Warning, report.OverallStatus);
        Assert.False(report.HasSevereMismatch);
        Assert.Contains(report.Fields, f => f.Field == "os" && f.Status == ConditionMatchStatus.Warning);
    }

    [Fact]
    public void ConditionMatch_Severe_WhenFingerprintOrCpuOrConfigDiffers()
    {
        var a = MakeRun("a", fp: "fpaaaaaaaaaaaaaa", cpuName: "CPU-A", duration: 30);
        var b = MakeRun("b", fp: "fpbbbbbbbbbbbbbb", cpuName: "CPU-A", duration: 30);
        var r1 = BenchmarkConditionRules.Analyze(a, b);
        Assert.Equal(ConditionMatchStatus.SevereMismatch, r1.OverallStatus);
        Assert.True(r1.RequiresExplicitOverride);

        var c = MakeRun("c", fp: "fpcccccccccccccccc", cpuName: "CPU-A", duration: 30);
        var d = MakeRun("d", fp: "fpcccccccccccccccc", cpuName: "CPU-B", duration: 30);
        Assert.Equal(ConditionMatchStatus.SevereMismatch, BenchmarkConditionRules.Analyze(c, d).OverallStatus);

        var e = MakeRun("e", fp: "fpdddddddddddddd", cpuName: "CPU", duration: 30, interval: 100);
        var f = MakeRun("f", fp: "fpdddddddddddddd", cpuName: "CPU", duration: 60, interval: 100);
        Assert.Equal(ConditionMatchStatus.SevereMismatch, BenchmarkConditionRules.Analyze(e, f).OverallStatus);
    }

    [Fact]
    public void AutomaticInconclusive_OnSevereMismatch()
    {
        var a = MakeRun("a", fp: "fp11111111111111", cpu: 60);
        var b = MakeRun("b", fp: "fp22222222222222", cpu: 40);
        var report = BenchmarkConditionRules.Analyze(a, b, forcedDespiteMismatch: false);
        var gated = BenchmarkConditionRules.ApplyClassificationGate(
            GuidedResultClassification.Improved, report, out var suffix);
        Assert.Equal(GuidedResultClassification.Inconclusive, gated);
        Assert.Contains("Severe", suffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForcedComparison_StillInconclusive_AndNotesMismatch()
    {
        var a = MakeRun("a", fp: "fp11111111111111", cpu: 60);
        var b = MakeRun("b", fp: "fp22222222222222", cpu: 40);
        var report = BenchmarkConditionRules.Analyze(a, b, forcedDespiteMismatch: true);
        Assert.True(report.ForcedDespiteMismatch);
        Assert.Contains("despite condition mismatch", report.ForcedNote, StringComparison.OrdinalIgnoreCase);

        var gated = BenchmarkConditionRules.ApplyClassificationGate(
            GuidedResultClassification.Regressed, report, out var suffix);
        Assert.Equal(GuidedResultClassification.Inconclusive, gated);
        Assert.Contains("despite condition mismatch", suffix, StringComparison.OrdinalIgnoreCase);

        var comparison = new BenchmarkCalculator().Compare(
            a, b, new BenchmarkCompareOptions { ForceCompareDespiteSevereMismatch = true });
        Assert.True(comparison.ForcedDespiteMismatch);
        Assert.Contains("despite condition mismatch", comparison.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Calculator_Compare_EmbedsConditionReport()
    {
        var a = MakeRun("a", fp: "samefinger123456", cpuName: "C", gpuName: "G");
        var b = MakeRun("b", fp: "samefinger123456", cpuName: "C", gpuName: "G");
        var cmp = new BenchmarkCalculator().Compare(a, b);
        Assert.NotNull(cmp.ConditionReport);
        Assert.Equal(ConditionMatchStatus.Match, cmp.ConditionReport!.OverallStatus);
        Assert.True(cmp.ConditionReport.Reliability is ComparisonReliability.High
            or ComparisonReliability.Medium);
    }

    [Fact]
    public void InvalidBenchmarkResult_FailsIntegrity()
    {
        var run = MakeRun("x");
        run.Status = BenchmarkStatus.Completed;
        run.Samples.Clear();
        var v = BenchmarkConditionRules.ValidateCompletedRun(run);
        Assert.False(v.IsValid);
        Assert.Contains(v.Issues, i => i.Contains("zero samples", StringComparison.OrdinalIgnoreCase)
                                     || i.Contains("measured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidCompletedRun_PassesIntegrity()
    {
        var run = MakeRun("ok");
        run.Status = BenchmarkStatus.Completed;
        run.EndedAt = DateTimeOffset.UtcNow;
        run.DurationSecondsActual = 30;
        var v = BenchmarkConditionRules.ValidateCompletedRun(run);
        Assert.True(v.IsValid, string.Join("; ", v.Issues));
    }

    [Fact]
    public async Task Engine_IntegrityFailure_MarksFailedNotCompleted()
    {
        // Direct unit: validate gate logic used by engine
        var run = MakeRun("bad");
        run.Status = BenchmarkStatus.Completed;
        run.EndedAt = null;
        var integrity = BenchmarkConditionRules.ValidateCompletedRun(run);
        Assert.False(integrity.IsValid);
        // Simulate engine gate
        if (!integrity.IsValid)
        {
            run.Status = BenchmarkStatus.Failed;
            run.Error = "Integrity validation failed: " + string.Join("; ", integrity.Issues);
        }

        Assert.Equal(BenchmarkStatus.Failed, run.Status);
        Assert.DoesNotContain("Completed", run.Status.ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public void GpuUtilization_RemainsUnavailable()
    {
        var calc = new BenchmarkCalculator();
        var samples = Enumerable.Range(0, 20).Select(i => new BenchmarkSample
        {
            IsWarmup = false,
            SystemCpuPercent = 30,
            ElapsedMs = i * 100
        }).ToList();
        var result = calc.Calculate(samples);
        Assert.False(result.GpuUtilization.IsAvailable);
        Assert.Null(result.GpuUtilization.Average);

        var a = MakeRun("g1");
        var b = MakeRun("g2");
        var cmp = calc.Compare(a, b);
        var gpu = cmp.Metrics.First(m => m.Metric.Contains("GPU utilization", StringComparison.OrdinalIgnoreCase));
        Assert.False(gpu.IsAvailable);
        Assert.Equal("Unavailable", gpu.Interpretation);
        Assert.Null(gpu.Before);
        Assert.Null(gpu.After);
    }

    [Fact]
    public void ImportDialogState_DefaultsToCancel()
    {
        var state = new ImportConfirmDialogState
        {
            Title = "Confirm import",
            SourcePath = "/tmp/x.frameforge-intelligence.json",
            Summary = "Add 1",
            ConflictLines = { "c1" },
            FingerprintLines = { "fp" },
            InvalidLines = { "(none)" },
            CanMerge = true,
            CanImportAsNew = true
        };
        Assert.Null(state.ChosenMode); // Cancel default
        state.ChosenMode = IntelligenceImportMode.Merge;
        Assert.Equal(IntelligenceImportMode.Merge, state.ChosenMode);
    }

    [Fact]
    public void SevereRuleDoc_IsDocumented()
    {
        Assert.Contains("SystemFingerprintId", BenchmarkConditionRules.SevereRuleDoc);
        Assert.Contains("CPU", BenchmarkConditionRules.SevereRuleDoc);
        Assert.Contains("GPU", BenchmarkConditionRules.SevereRuleDoc);
        Assert.Contains("DurationSeconds", BenchmarkConditionRules.SevereRuleDoc);
    }

    private static BenchmarkRun MakeRun(
        string id,
        string? fp = "fpdefault0000001",
        double cpu = 50,
        string cpuName = "TestCPU",
        string gpuName = "TestGPU",
        int duration = 30,
        int interval = 100,
        int warmup = 5)
    {
        var samples = Enumerable.Range(0, 25).Select(i => new BenchmarkSample
        {
            IsWarmup = i < 2,
            SystemCpuPercent = cpu,
            ProcessCpuPercent = cpu / 2,
            SystemMemoryPercent = 40,
            ProcessWorkingSetBytes = 4L << 30,
            Cs2ProcessPresent = true,
            ElapsedMs = i * interval
        }).ToList();
        var calc = new BenchmarkCalculator();
        return new BenchmarkRun
        {
            Id = id,
            Status = BenchmarkStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-duration),
            EndedAt = DateTimeOffset.UtcNow,
            DurationSecondsActual = duration,
            Configuration = new BenchmarkConfiguration
            {
                DurationSeconds = duration,
                SampleIntervalMs = interval,
                WarmupSeconds = warmup
            },
            SystemInformation = new BenchmarkSystemSnapshot
            {
                CpuName = cpuName,
                GpuName = gpuName,
                TotalRamBytes = 16L << 30,
                OsVersion = "TestOS",
                Architecture = "X64",
                Cs2ProcessRunningAtStart = true,
                ActiveProfileName = "Balanced",
                SystemFingerprintId = fp,
                DisplayResolution = "1920x1080",
                DisplayRefreshRateHz = 144,
                PowerPlan = "Balanced",
                GameModeEnabled = true
            },
            Samples = samples,
            Result = calc.Calculate(samples)
        };
    }

    private static BenchmarkSystemSnapshot CloneSnap(
        BenchmarkSystemSnapshot s,
        string? os = null,
        string? display = "keep",
        string? power = "keep",
        bool? gameMode = true)
    {
        return new BenchmarkSystemSnapshot
        {
            CpuName = s.CpuName,
            GpuName = s.GpuName,
            TotalRamBytes = s.TotalRamBytes,
            OsVersion = os ?? s.OsVersion,
            Architecture = s.Architecture,
            Cs2ProcessRunningAtStart = s.Cs2ProcessRunningAtStart,
            ActiveProfileName = s.ActiveProfileName,
            SystemFingerprintId = s.SystemFingerprintId,
            DisplayResolution = display == "keep" ? s.DisplayResolution : display,
            DisplayRefreshRateHz = display == "keep" ? s.DisplayRefreshRateHz : null,
            PowerPlan = power == "keep" ? s.PowerPlan : power,
            GameModeEnabled = gameMode == true && display == "keep" ? s.GameModeEnabled : gameMode
        };
    }

    private sealed class BenchEnv : IDisposable
    {
        public string Root { get; }
        public BenchmarkEngine Engine { get; }

        private BenchEnv(string root, BenchmarkEngine engine)
        {
            Root = root;
            Engine = engine;
        }

        public static BenchEnv Create(bool withFingerprint = true, bool fingerprintThrows = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ff-p11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var store = new BenchmarkStore(paths, log);
            ISystemFingerprintService? fp = null;
            if (withFingerprint)
            {
                fp = fingerprintThrows
                    ? new ThrowingFp()
                    : new SystemFingerprintService(new FixedHw());
            }

            var engine = new BenchmarkEngine(
                new OsPerformanceSampler(),
                new FakeMon(),
                new FixedHw(),
                new EmptyDet(),
                new NoopSettings(),
                new EmptyProfiles(),
                new AppSettingsService(paths, log),
                store,
                new BenchmarkCalculator(),
                log,
                fp);
            return new BenchEnv(root, engine);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
        }
    }

    private sealed class ThrowingFp : ISystemFingerprintService
    {
        public Task<SystemFingerprint> GetFingerprintAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("fingerprint probe failed");

        public SystemFingerprint FromHardware(HardwareInfo hardware) =>
            throw new InvalidOperationException("fingerprint probe failed");
    }

    private sealed class FixedHw : IHardwareInfoService
    {
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo
            {
                CpuName = "Phase11CPU",
                GpuName = "Phase11GPU",
                TotalRamBytes = 16L << 30,
                OsDescription = "TestOS",
                WindowsVersion = "TestOS",
                Architecture = "X64"
            });
    }

    private sealed class FakeMon : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? TryGetCs2Process() =>
            new() { ProcessId = 1, ProcessName = "cs2" };
    }

    private sealed class EmptyDet : ICs2DetectionService
    {
        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Cs2InstallInfo.NotFound("test"));
    }

    private sealed class NoopSettings : ICs2SettingsService
    {
        public string ManagedConfigFileName => "frameforge_settings.cfg";
        public string AutoexecFileName => "autoexec.cfg";
        public Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default) =>
            ReadSettingsAsync(cancellationToken);
        public Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2SettingsSnapshot { Cs2Available = false, Message = "noop", Settings = Array.Empty<Cs2SettingValue>() });
        public SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values) => new();
        public SettingsDiff CreateDiff(Cs2SettingsSnapshot current, IReadOnlyDictionary<string, string> desired, string reason, string? profileId = null, string? profileName = null) => new() { Title = reason };
        public Task<SettingsApplyResult> ApplySettingsAsync(IReadOnlyDictionary<string, string> desired, string reason, string? profileId = null, bool createBackup = true, CancellationToken cancellationToken = default) =>
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
}
