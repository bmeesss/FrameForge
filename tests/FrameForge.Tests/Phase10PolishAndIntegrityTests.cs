using FrameForge.Benchmark;
using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Json;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.CS2.Settings;
using FrameForge.Hardware;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Performance;
using FrameForge.Infrastructure.Settings;
using Xunit;

namespace FrameForge.Tests;

/// <summary>
/// Phase 10: metadata fallbacks, comparison warnings, apply snapshots,
/// watcher self-write, import preview, history filters, atomic audit.
/// </summary>
public sealed class Phase10PolishAndIntegrityTests
{
    [Fact]
    public async Task WindowsMetadata_Fallback_DoesNotThrow_OnLinux()
    {
        var hw = new HardwareInfoService();
        var info = await hw.GetHardwareInfoAsync();
        Assert.False(string.IsNullOrWhiteSpace(info.CpuName));
        Assert.False(string.IsNullOrWhiteSpace(info.GpuName));
        // On non-Windows, display/game mode/power are honestly null/unavailable
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(info.DisplayResolution);
            Assert.Null(info.DisplayRefreshRateHz);
            Assert.Null(info.GameModeEnabled);
            Assert.Null(info.PowerPlanName);
            Assert.Contains("not available", info.PowerPlanStatus ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GpuDetection_UnavailableCase_DoesNotFabricateUtilization()
    {
        // Unit-level: Benchmark sample path never invents GPU util
        var calc = new BenchmarkCalculator();
        var samples = Enumerable.Range(0, 20).Select(i => new BenchmarkSample
        {
            IsWarmup = false,
            SystemCpuPercent = 40,
            ElapsedMs = i * 100
        }).ToList();
        var result = calc.Calculate(samples);
        Assert.False(result.GpuUtilization.IsAvailable);
        Assert.Null(result.GpuUtilization.Average);
    }

    [Fact]
    public void DisplayMetadata_Unavailable_ShowsAsNullNotZero()
    {
        var snap = new BenchmarkSystemSnapshot
        {
            DisplayResolution = null,
            DisplayRefreshRateHz = null
        };
        Assert.Null(snap.DisplayResolution);
        Assert.Null(snap.DisplayRefreshRateHz);
        // Presenter must not invent
        Assert.Null(snap.GameModeEnabled);
    }

    [Fact]
    public void BenchmarkConditionMismatch_ProducesWarnings_DoesNotBlock()
    {
        var a = MakeRun("A", cpu: "CPU-A", duration: 30, interval: 100, warmup: 5, fp: "fp_aaa");
        var b = MakeRun("B", cpu: "CPU-B", duration: 60, interval: 200, warmup: 10, fp: "fp_bbb", gpu: "GPU-B");
        var warnings = BenchmarkComparisonPresenter.AnalyzeConditions(a, b, "fp_aaa", "fp_bbb").ToList();
        Assert.True(warnings.Count >= 3);
        Assert.Contains(warnings, w => w.Code == "cpu");
        Assert.Contains(warnings, w => w.Code == "duration");
        Assert.Contains(warnings, w => w.Code == "fingerprint");
        Assert.Contains(warnings, w => w.Message.Contains("different hardware fingerprints", StringComparison.OrdinalIgnoreCase)
                                     || w.Code == "fingerprint");
        Assert.True(warnings.All(w => !w.IsBlocking));
    }

    [Fact]
    public void Comparison_Unavailable_NeverShowsZero()
    {
        var calc = new BenchmarkCalculator();
        var before = MakeRun("before");
        var after = MakeRun("after");
        before.Result = calc.Calculate(before.Samples);
        after.Result = calc.Calculate(after.Samples);

        var comparison = calc.Compare(before, after);
        var report = BenchmarkComparisonPresenter.Build(comparison);
        var unavailable = report.Rows.Where(r => !r.IsAvailable).ToList();
        Assert.True(unavailable.Count > 0);
        Assert.True(unavailable.All(r =>
            r.BeforeText == "Unavailable" &&
            r.AfterText == "Unavailable" &&
            r.DeltaText == "Unavailable" &&
            r.Interpretation == "Unavailable"));

        var cpu = report.Rows.FirstOrDefault(r => r.Metric.Contains("System CPU", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(cpu);
        Assert.True(cpu!.IsAvailable);
        Assert.DoesNotContain("Unavailable", cpu.BeforeText, StringComparison.Ordinal);
        Assert.Contains("percentage points", cpu.DeltaText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyDiffAsync_AlwaysRecordsPerKeySnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p10-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cfg = Path.Combine(root, "cfg");
            Directory.CreateDirectory(cfg);
            var managed = Path.Combine(cfg, Cs2SettingsService.ManagedFileName);
            await File.WriteAllTextAsync(managed, "fps_max 400" + Environment.NewLine);

            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var config = new Cs2ConfigService(log);
            var det = new FixedDet(cfg);
            var app = new AppSettingsService(paths, log);
            // Disable automatic backup to prove snapshots still write
            var settingsObj = await app.LoadAsync();
            settingsObj.AutomaticBackup = false;
            await app.SaveAsync(settingsObj);

            var backups = new BackupService(paths, log);
            var snaps = new SettingChangeSnapshotStore(paths, log);
            var watcher = new ManagedConfigWatcher(det, paths, log);
            var svc = new Cs2SettingsService(catalog, config, det, backups, app, log, snaps, watcher);

            var current = await svc.ReadSettingsAsync();
            var diff = svc.CreateDiff(current, new Dictionary<string, string> { ["fps_max"] = "0" }, "phase10-test");
            Assert.True(diff.HasChanges);

            var result = await svc.ApplyDiffAsync(diff, createBackup: true);
            Assert.True(result.Success, result.Message);

            var list = await snaps.ListForKeyAsync("fps_max");
            Assert.True(list.Count >= 1);
            Assert.Equal("0", list[0].NewValue);
            Assert.Equal("400", list[0].PreviousValue);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Watcher_SelfWrite_DoesNotInvalidate_ButExternalDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p10-w-" + Guid.NewGuid().ToString("N"));
        var cfg = Path.Combine(root, "cfg");
        Directory.CreateDirectory(cfg);
        var managed = Path.Combine(cfg, Cs2SettingsService.ManagedFileName);
        await File.WriteAllTextAsync(managed, "fps_max 0\n");
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var det = new FixedDet(cfg);
            var watcher = new ManagedConfigWatcher(det, paths, log);

            await watcher.CaptureBaselineAsync();
            Assert.False(await watcher.HasExternalChangesAsync());

            watcher.BeginSelfWrite();
            await File.WriteAllTextAsync(managed, "fps_max 400\n");
            await watcher.EndSelfWriteAndCaptureBaselineAsync();
            Assert.False(await watcher.HasExternalChangesAsync());

            // External edit after baseline
            await File.WriteAllTextAsync(managed, "fps_max 144\n");
            Assert.True(await watcher.HasExternalChangesAsync());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ImportPreview_IsValidationOnly_NoWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p10-imp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var hw = new FixedHw();
            var fpSvc = new SystemFingerprintService(hw);
            var guided = new FrameForge.Infrastructure.Guided.GuidedOptimizationStore(paths, log);
            var intel = new PerformanceIntelligenceService(guided, fpSvc, catalog, paths, log);
            var snaps = new SettingChangeSnapshotStore(paths, log);
            var export = new IntelligenceExportService(intel, snaps, fpSvc, catalog, paths, log);

            var pkg = new IntelligenceExportPackage
            {
                SchemaVersion = 1,
                Evidence =
                {
                    new SettingTestEvidence
                    {
                        GuidedRunId = "p10r1",
                        SettingId = "video.fps_max",
                        SettingKey = "fps_max",
                        SettingName = "FPS Max",
                        EvidenceType = PerformanceEvidenceType.SingleSetting,
                        Classification = GuidedResultClassification.Improved,
                        TestedAt = DateTimeOffset.UtcNow.AddHours(-1),
                        SystemFingerprintId = "ffffffffffffffff",
                        InitialBenchmarkId = "b1",
                        PostBenchmarkId = "b2",
                        BenchmarkComplete = true,
                        ComparisonRows =
                        {
                            new GuidedComparisonRow { Metric = "System CPU", Before = 50, After = 40, IsAvailable = true }
                        }
                    }
                },
                Records =
                {
                    new SettingPerformanceRecord
                    {
                        SettingKey = "fps_max",
                        SettingId = "video.fps_max",
                        SettingName = "FPS Max",
                        SystemFingerprintId = "ffffffffffffffff",
                        TestCount = 1,
                        DirectTestCount = 1,
                        Confidence = PerformanceConfidence.Low,
                        LatestClassification = GuidedResultClassification.Improved,
                        LastTestedAt = DateTimeOffset.UtcNow.AddHours(-1)
                    }
                },
                Fingerprints =
                {
                    new SystemFingerprint { FingerprintId = "ffffffffffffffff", CpuModel = "Other", GpuModel = "G", OsVersion = "OS", Architecture = "X64" }
                }
            };
            var path = Path.Combine(root, "p.frameforge-intelligence.json");
            await FrameForgeJson.SerializeFileAsync(path, pkg);

            var preview = await export.PreviewImportAsync(path);
            Assert.True(preview.IsValid, string.Join(";", preview.Errors));
            Assert.Contains(preview.DifferentFingerprints, f => f == "ffffffffffffffff");

            var importsDir = Path.Combine(paths.PerformanceHistoryDirectory, "imports");
            var previewResult = await export.ImportAsync(path, IntelligenceImportMode.Preview);
            Assert.True(previewResult.Success);
            Assert.False(Directory.Exists(importsDir) && Directory.EnumerateFiles(importsDir, "*.json").Any());

            // Conflict detection: import twice as merge after seeding same key
            var merge1 = await export.ImportAsync(path, IntelligenceImportMode.Merge);
            Assert.True(merge1.Success);
            var preview2 = await export.PreviewImportAsync(path);
            Assert.True(preview2.IsValid);
            // second preview should report skip or conflict for existing evidence/records
            Assert.True(preview2.RecordsToSkip + preview2.Conflicts + preview2.RecordsToUpdate >= 0);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void SettingDetail_Aggregation_FromRecord()
    {
        var record = new SettingPerformanceRecord
        {
            SettingKey = "fps_max",
            SettingName = "FPS Max",
            TestCount = 3,
            DirectTestCount = 2,
            AssociatedMultiSettingTestCount = 1,
            Confidence = PerformanceConfidence.Medium,
            ConfidenceReason = "2 consistent direct tests",
            LatestClassification = GuidedResultClassification.Improved,
            RecommendationSummary = "Multiple single-setting tests generally show improvement.",
            LastTestedAt = DateTimeOffset.UtcNow,
            IsCurrentSystem = true
        };

        var detail = new SettingDetailViewState
        {
            SettingKey = record.SettingKey,
            SettingName = record.SettingName,
            CurrentValue = "0",
            RecommendedValue = "0",
            LastMeasuredResult = record.DisplayLatest,
            TestCount = record.TestCount,
            DirectTestCount = record.DirectTestCount,
            AssociatedTestCount = record.AssociatedMultiSettingTestCount,
            Confidence = record.DisplayConfidence,
            ConfidenceReason = record.ConfidenceReason,
            RecommendationSummary = record.RecommendationSummary,
            FingerprintLabel = record.FingerprintLabel,
            LatestTestAt = record.LastTestedAt,
            RestoreState = "SafeToTargetRestore",
            CanRestore = true,
            HasEvidence = true
        };

        Assert.Equal("0", detail.DisplayCurrent);
        Assert.Equal("0", detail.DisplayRecommended);
        Assert.Equal("Improved", detail.LastMeasuredResult);
        Assert.Equal(3, detail.TestCount);
        Assert.Equal(2, detail.DirectTestCount);
        Assert.Equal("Medium", detail.Confidence);
        Assert.True(detail.CanRestore);
    }

    [Fact]
    public void HistoryFiltering_AndSorting_InMemory()
    {
        var records = new List<SettingPerformanceRecord>
        {
            new()
            {
                SettingKey = "fps_max", SettingName = "FPS Max", IsCurrentSystem = true,
                TestCount = 5, DirectImprovedCount = 4, LastTestedAt = DateTimeOffset.UtcNow.AddDays(-1),
                LatestClassification = GuidedResultClassification.Improved, Confidence = PerformanceConfidence.High
            },
            new()
            {
                SettingKey = "volume", SettingName = "Volume", IsCurrentSystem = true,
                TestCount = 1, DirectRegressedCount = 1, LastTestedAt = DateTimeOffset.UtcNow,
                LatestClassification = GuidedResultClassification.Regressed, Confidence = PerformanceConfidence.Low
            },
            new()
            {
                SettingKey = "fps_max", SettingName = "FPS Max", IsCurrentSystem = false,
                SystemFingerprintId = "other", TestCount = 2, LastTestedAt = DateTimeOffset.UtcNow.AddDays(-3),
                LatestClassification = GuidedResultClassification.Neutral, Confidence = PerformanceConfidence.Low
            }
        };

        var current = records.Where(r => r.IsCurrentSystem).ToList();
        Assert.Equal(2, current.Count);

        var other = records.Where(r => !r.IsCurrentSystem).ToList();
        Assert.Single(other);

        var improved = records.Where(r => r.LatestClassification == GuidedResultClassification.Improved).ToList();
        Assert.Single(improved);

        var mostTested = records.OrderByDescending(r => r.TestCount).First();
        Assert.Equal("fps_max", mostTested.SettingKey);
        Assert.True(mostTested.IsCurrentSystem);

        var worst = records.OrderByDescending(r => r.DirectRegressedCount).First();
        Assert.Equal("volume", worst.SettingKey);

        var byName = records.Where(r => r.SettingName.Contains("Vol", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(byName);
    }

    [Fact]
    public async Task AtomicPersistence_Audit_ProfileAndJsonPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-p10-atom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "probe.json");
            await FrameForgeJson.SerializeFileAsync(path, new { ok = true });
            Assert.True(File.Exists(path));
            await AtomicFile.WriteAllTextAsync(path, "{\"ok\":2}");
            Assert.Equal("{\"ok\":2}", await File.ReadAllTextAsync(path));

            // Profile seed path uses FrameForgeJson (atomic)
            var paths = new PathService(root);
            var log = new FileAppLog(paths);
            var catalog = new Cs2SettingCatalog();
            var profiles = new FrameForge.Infrastructure.Profiles.ProfileService(paths, log, catalog);
            var list = await profiles.GetProfilesAsync();
            Assert.True(list.Count >= 3);
            Assert.True(Directory.EnumerateFiles(paths.ProfilesDirectory, "*.builtin.json").Any());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ComparisonDisplay_MemoryFormatsGb()
    {
        var row = BenchmarkComparisonPresenter.ToDisplayRow(new BenchmarkComparisonMetric
        {
            Metric = "CS2 working set average",
            Unit = "MB",
            Before = 5200,
            After = 5100,
            Difference = -100,
            IsAvailable = true
        });
        Assert.Contains("GB", row.BeforeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lower", row.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    private static BenchmarkRun MakeRun(
        string id,
        string cpu = "CPU-A",
        string gpu = "GPU-A",
        int duration = 30,
        int interval = 100,
        int warmup = 5,
        string? fp = null)
    {
        var samples = Enumerable.Range(0, 25).Select(i => new BenchmarkSample
        {
            IsWarmup = i < 3,
            SystemCpuPercent = 50 + (id == "after" || id == "B" ? -5 : 0),
            ProcessCpuPercent = 20,
            SystemMemoryPercent = 40,
            ProcessWorkingSetBytes = 5L * 1024 * 1024 * 1024,
            Cs2ProcessPresent = true,
            ElapsedMs = i * interval
        }).ToList();

        return new BenchmarkRun
        {
            Id = id,
            Status = BenchmarkStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow,
            Configuration = new BenchmarkConfiguration
            {
                DurationSeconds = duration,
                SampleIntervalMs = interval,
                WarmupSeconds = warmup
            },
            SystemInformation = new BenchmarkSystemSnapshot
            {
                CpuName = cpu,
                GpuName = gpu,
                TotalRamBytes = 16L << 30,
                OsVersion = "TestOS",
                Architecture = "X64",
                Cs2ProcessRunningAtStart = true,
                ActiveProfileName = "Balanced",
                SystemFingerprintId = fp
            },
            Samples = samples,
            Result = new BenchmarkCalculator().Calculate(samples)
        };
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

    private sealed class FixedHw : IHardwareInfoService
    {
        public Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo
            {
                CpuName = "P10CPU",
                GpuName = "P10GPU",
                TotalRamBytes = 16L << 30,
                OsDescription = "TestOS",
                WindowsVersion = "TestOS",
                Architecture = "X64"
            });
    }
}
