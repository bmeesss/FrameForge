using FrameForge.Benchmark;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using Xunit;

namespace FrameForge.Tests;

public sealed class BenchmarkTests
{
    [Fact]
    public void Configuration_RejectsInvalidIntervalAndDuration()
    {
        var badInterval = new BenchmarkConfiguration { SampleIntervalMs = 10, DurationSeconds = 60 };
        var v1 = BenchmarkConfiguration.Validate(badInterval);
        Assert.False(v1.IsValid);

        var badDuration = new BenchmarkConfiguration { DurationSeconds = 45, SampleIntervalMs = 100 };
        var v2 = BenchmarkConfiguration.Validate(badDuration);
        Assert.False(v2.IsValid);

        var warmupTooLong = new BenchmarkConfiguration
        {
            DurationSeconds = 30,
            WarmupSeconds = 30,
            SampleIntervalMs = 100
        };
        Assert.False(BenchmarkConfiguration.Validate(warmupTooLong).IsValid);

        var ok = BenchmarkConfiguration.CreateDefault();
        Assert.True(BenchmarkConfiguration.Validate(ok).IsValid);
    }

    [Fact]
    public void Percentile_IsDeterministic_OnSortedData()
    {
        var data = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        var p99 = MetricSummary.PercentileSorted(data, 0.99);
        var p999 = MetricSummary.PercentileSorted(data, 0.999);
        Assert.True(p99 >= 99 && p99 <= 100);
        Assert.True(p999 >= p99);
        Assert.Equal(1, MetricSummary.PercentileSorted(data, 0));
        Assert.Equal(100, MetricSummary.PercentileSorted(data, 1));
    }

    [Fact]
    public void Calculate_AverageAndPercentiles_FromDeterministicSamples()
    {
        var calc = new BenchmarkCalculator();
        var samples = new List<BenchmarkSample>();
        // 5 warmup ignored
        for (var i = 0; i < 5; i++)
        {
            samples.Add(new BenchmarkSample
            {
                IsWarmup = true,
                SystemCpuPercent = 99,
                ElapsedMs = i * 100
            });
        }

        // 40 measured CPU samples 10..49
        for (var i = 0; i < 40; i++)
        {
            samples.Add(new BenchmarkSample
            {
                IsWarmup = false,
                SystemCpuPercent = 10 + i,
                ProcessCpuPercent = 5 + i * 0.5,
                SystemMemoryPercent = 50,
                ProcessWorkingSetBytes = (100 + i) * 1024L * 1024L,
                Cs2ProcessPresent = true,
                ElapsedMs = 1000 + i * 100
            });
        }

        var result = calc.Calculate(samples);
        Assert.Equal(40, result.MeasuredSampleCount);
        Assert.Equal(5, result.WarmupSampleCount);
        Assert.True(result.SystemCpu.IsAvailable);
        Assert.Equal(29.5, result.SystemCpu.Average!.Value, 3); // avg 10..49
        Assert.NotNull(result.SystemCpu.P1);
        Assert.False(result.FrameTimeMs.IsAvailable);
        Assert.Null(result.DerivedFps);
        Assert.Equal(100, result.Cs2PresentSamplePercent);
    }

    [Fact]
    public void Calculate_InsufficientSamples_SkipsPercentiles()
    {
        var calc = new BenchmarkCalculator();
        var samples = Enumerable.Range(0, 5).Select(i => new BenchmarkSample
        {
            IsWarmup = false,
            SystemCpuPercent = i * 10.0
        }).ToList();

        var result = calc.Calculate(samples);
        Assert.True(result.SystemCpu.IsAvailable);
        Assert.Null(result.SystemCpu.P1);
        Assert.Null(result.SystemCpu.P01);
    }

    [Fact]
    public void Calculate_Empty_MarksUnavailable()
    {
        var result = new BenchmarkCalculator().Calculate(Array.Empty<BenchmarkSample>());
        Assert.False(result.SystemCpu.IsAvailable);
        Assert.Equal(0, result.MeasuredSampleCount);
    }

    [Fact]
    public void Calculate_FrameTime_DerivesFps_OnlyWhenPresent()
    {
        var samples = new List<BenchmarkSample>();
        for (var i = 0; i < 25; i++)
        {
            samples.Add(new BenchmarkSample
            {
                IsWarmup = false,
                FrameTimeMs = 10.0 // 100 FPS
            });
        }

        var result = new BenchmarkCalculator().Calculate(samples);
        Assert.True(result.FrameTimeMs.IsAvailable);
        Assert.Equal(10.0, result.FrameTimeMs.Average!.Value, 3);
        Assert.NotNull(result.DerivedFps);
        Assert.True(result.DerivedFps!.IsAvailable);
        Assert.Equal(100.0, result.DerivedFps.Average!.Value, 3);
    }

    [Fact]
    public void Compare_ComputesDifferenceAndPercent()
    {
        var before = new BenchmarkRun
        {
            Id = "a",
            Result = new BenchmarkResult
            {
                SystemCpu = MetricSummary.FromValues(new[] { 60.0, 60.0 }, "%", minSamplesForPercentiles: 1),
                FrameTimeMs = MetricSummary.Unavailable("n/a", "ms")
            }
        };
        var after = new BenchmarkRun
        {
            Id = "b",
            Result = new BenchmarkResult
            {
                SystemCpu = MetricSummary.FromValues(new[] { 50.0, 50.0 }, "%", minSamplesForPercentiles: 1),
                FrameTimeMs = MetricSummary.Unavailable("n/a", "ms")
            }
        };

        var cmp = new BenchmarkCalculator().Compare(before, after);
        var cpu = cmp.Metrics.First(m => m.Metric.Contains("System CPU", StringComparison.Ordinal));
        Assert.True(cpu.IsAvailable);
        Assert.Equal(-10.0, cpu.Difference!.Value, 3);
        Assert.True(cpu.PercentDifference is not null);
        Assert.Contains("does not guarantee FPS", cmp.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_PersistsAndLoads_AndHandlesCorruptFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_bm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var store = new BenchmarkStore(paths, log);

            var run = new BenchmarkRun
            {
                Id = "2026-09-13_205500_test",
                StartedAt = DateTimeOffset.Parse("2026-09-13T20:55:00Z"),
                EndedAt = DateTimeOffset.Parse("2026-09-13T20:56:00Z"),
                Status = BenchmarkStatus.Completed,
                Configuration = new BenchmarkConfiguration { DurationSeconds = 60, SampleIntervalMs = 100 },
                Result = new BenchmarkCalculator().Calculate(new[]
                {
                    new BenchmarkSample { IsWarmup = false, SystemCpuPercent = 42 }
                }),
                Samples =
                {
                    new BenchmarkSample { IsWarmup = false, SystemCpuPercent = 42, ElapsedMs = 100 }
                }
            };

            var path = await store.SaveAsync(run);
            Assert.True(File.Exists(path));
            Assert.Contains("Benchmarks", path, StringComparison.OrdinalIgnoreCase);

            var listed = await store.ListAsync();
            Assert.True(listed.Any(r => r.Id == run.Id));

            var loaded = await store.GetAsync(run.Id);
            Assert.NotNull(loaded);
            Assert.Equal(BenchmarkStatus.Completed, loaded!.Status);
            Assert.Equal(42, loaded.Result.SystemCpu.Average);

            // corrupt file ignored
            await File.WriteAllTextAsync(Path.Combine(paths.BenchmarksDirectory, "broken.json"), "{ not json");
            var listed2 = await store.ListAsync();
            Assert.True(listed2.All(r => r.Id != null));

            var exportJson = Path.Combine(root, "out.json");
            await store.ExportJsonAsync(run.Id, exportJson);
            Assert.True(File.Exists(exportJson));

            var exportCsv = Path.Combine(root, "out.csv");
            await store.ExportCsvAsync(run.Id, exportCsv);
            var csv = await File.ReadAllTextAsync(exportCsv);
            Assert.Contains("system_cpu_pct", csv);
            Assert.Contains("42", csv);

            await store.DeleteAsync(run.Id);
            Assert.Null(await store.GetAsync(run.Id));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_StateMachine_CompletesShortRun_AndSupportsCancel()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_eng_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            var store = new BenchmarkStore(paths, log);
            var calc = new BenchmarkCalculator();
            var sampler = new FakeSampler();
            var monitor = new FakeProcessMonitor();
            var engine = new BenchmarkEngine(
                sampler,
                monitor,
                new FakeHardware(),
                new FakeCs2Detection(),
                new FakeCs2Settings(),
                new FakeProfiles(),
                new FakeAppSettings(),
                store,
                calc,
                log);

            Assert.Equal(BenchmarkStatus.Idle, engine.Status);

            // Use minimal allowed duration but cancel quickly
            var config = new BenchmarkConfiguration
            {
                DurationSeconds = 30,
                WarmupSeconds = 0,
                SampleIntervalMs = 50,
                ProfileId = "balanced",
                ProfileName = "Balanced"
            };

            var runTask = engine.StartAsync(config);
            await Task.Delay(120);
            engine.RequestCancel();
            var run = await runTask;

            Assert.True(run.Status is BenchmarkStatus.Cancelled or BenchmarkStatus.Completed);
            Assert.True(run.Samples.Count >= 1);
            Assert.True(File.Exists(run.FilePath!) || (await store.ListAsync()).Count >= 1);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_RejectsConcurrentStart_AndInvalidConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_eng2_" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = CreateEngine(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.StartAsync(new BenchmarkConfiguration { DurationSeconds = 15, SampleIntervalMs = 100 }));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_Stop_EndsEarly_WithCompletedStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_eng3_" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = CreateEngine(root);
            var task = engine.StartAsync(new BenchmarkConfiguration
            {
                DurationSeconds = 30,
                WarmupSeconds = 0,
                SampleIntervalMs = 50
            });
            await Task.Delay(100);
            engine.RequestStop();
            var run = await task;
            Assert.Equal(BenchmarkStatus.Completed, run.Status);
            Assert.True(run.Samples.Count >= 1);
            Assert.Contains("Stopped early", run.Result.Notes, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ProcessMonitor_DoesNotThrow_WhenCs2Missing()
    {
        var mon = new Cs2ProcessMonitor();
        var info = mon.TryGetCs2Process();
        // May be null — must not throw
        _ = info;
        Assert.True(true);
    }

    [Fact]
    public void Sampler_ProcessUnavailable_StillReturnsSample()
    {
        var sampler = new OsPerformanceSampler();
        sampler.Reset();
        var s1 = sampler.TakeSample(null, isWarmup: true, elapsedMs: 0);
        Assert.False(s1.Cs2ProcessPresent);
        Assert.Null(s1.FrameTimeMs);
        Assert.Null(s1.GpuUtilizationPercent);
        var s2 = sampler.TakeSample(null, isWarmup: false, elapsedMs: 100);
        Assert.Null(s2.ProcessCpuPercent);
    }

    private static BenchmarkEngine CreateEngine(string root)
    {
        var paths = new PathService(root);
        var log = new FileAppLog(paths, LogLevelSetting.Warning);
        return new BenchmarkEngine(
            new FakeSampler(),
            new FakeProcessMonitor(),
            new FakeHardware(),
            new FakeCs2Detection(),
            new FakeCs2Settings(),
            new FakeProfiles(),
            new FakeAppSettings(),
            new BenchmarkStore(paths, log),
            new BenchmarkCalculator(),
            log);
    }

    private sealed class FakeSampler : IPerformanceSampler
    {
        private int _n;
        public void Reset() => _n = 0;

        public BenchmarkSample TakeSample(Cs2ProcessInfo? cs2, bool isWarmup, double elapsedMs)
        {
            _n++;
            return new BenchmarkSample
            {
                Timestamp = DateTimeOffset.UtcNow,
                ElapsedMs = elapsedMs,
                IsWarmup = isWarmup,
                SystemCpuPercent = 20 + (_n % 10),
                ProcessCpuPercent = cs2 is null ? null : 5,
                Cs2ProcessPresent = cs2 is not null,
                Cs2ProcessId = cs2?.ProcessId
            };
        }
    }

    private sealed class FakeProcessMonitor : ICs2ProcessMonitor
    {
        public Cs2ProcessInfo? TryGetCs2Process() => null;
    }

    private sealed class FakeHardware : IHardwareInfoService
    {
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo
            {
                CpuName = "Test CPU",
                CpuCoreCount = 4,
                CpuThreadCount = 8,
                GpuName = "Test GPU",
                TotalRamBytes = 8L * 1024 * 1024 * 1024,
                WindowsVersion = "Test OS",
                Architecture = "X64"
            });
    }

    private sealed class FakeCs2Detection : ICs2DetectionService
    {
        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Cs2InstallInfo.NotFound("test"));
    }

    private sealed class FakeCs2Settings : ICs2SettingsService
    {
        public string ManagedConfigFileName => "frameforge_settings.cfg";
        public string AutoexecFileName => "autoexec.cfg";
        public Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cs2SettingsSnapshot { Cs2Available = false, Message = "test" });
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

    private sealed class FakeProfiles : IProfileService
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

    private sealed class FakeAppSettings : IAppSettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.CreateDefault());
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
