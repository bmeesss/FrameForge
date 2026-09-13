using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Aggregates local guided optimization history into per-setting performance records.
/// Cache invalidated on demand; optional disk index under PerformanceHistory/.
/// No cloud / telemetry.
/// </summary>
public sealed class PerformanceIntelligenceService : IPerformanceIntelligenceService
{
    private readonly IGuidedOptimizationStore _guidedStore;
    private readonly ISystemFingerprintService _fingerprint;
    private readonly ICs2SettingCatalog _catalog;
    private readonly IPathService _paths;
    private readonly IAppLog _log;

    private readonly object _gate = new();
    private PerformanceIntelligenceIndex? _cache;
    private bool _stale = true;

    public PerformanceIntelligenceService(
        IGuidedOptimizationStore guidedStore,
        ISystemFingerprintService fingerprint,
        ICs2SettingCatalog catalog,
        IPathService paths,
        IAppLog log)
    {
        _guidedStore = guidedStore;
        _fingerprint = fingerprint;
        _catalog = catalog;
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.PerformanceHistoryDirectory);
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _stale = true;
        }
    }

    public async Task<PerformanceIntelligenceIndex> GetIndexAsync(
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!forceRebuild && !_stale && _cache is not null)
            {
                return _cache;
            }
        }

        return await RebuildAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PerformanceIntelligenceIndex> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var fp = await _fingerprint.GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var runs = await _guidedStore.ListAsync(cancellationToken).ConfigureAwait(false);

        var evidence = new List<SettingTestEvidence>();
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAnalyzable(run))
            {
                continue;
            }

            var keys = ResolveKeys(run);
            if (keys.Count == 0)
            {
                continue;
            }

            var evidenceType = keys.Count == 1
                ? PerformanceEvidenceType.SingleSetting
                : PerformanceEvidenceType.MultiSetting;

            var complete = !string.IsNullOrWhiteSpace(run.InitialBenchmarkId) &&
                           !string.IsNullOrWhiteSpace(run.PostBenchmarkId) &&
                           (run.ComparisonRows?.Count ?? 0) > 0;

            foreach (var key in keys)
            {
                var def = _catalog.GetByConfigKey(key) ?? _catalog.GetById(key);
                run.SelectedSettings.TryGetValue(key, out var applied);
                evidence.Add(new SettingTestEvidence
                {
                    GuidedRunId = run.Id,
                    SettingId = def?.Id ?? key,
                    SettingKey = def?.ConfigKey ?? key,
                    SettingName = def?.DisplayName ?? key,
                    EvidenceType = evidenceType,
                    Classification = run.Classification,
                    TestedAt = run.CompletedAt ?? run.StartedAt,
                    InitialBenchmarkId = run.InitialBenchmarkId,
                    PostBenchmarkId = run.PostBenchmarkId,
                    BackupId = run.BackupId,
                    SystemFingerprintId = fp.FingerprintId,
                    AppliedValue = applied,
                    AllSettingKeysInRun = keys,
                    ComparisonRows = run.ComparisonRows?.ToList() ?? new List<GuidedComparisonRow>(),
                    BenchmarkComplete = complete,
                    ClassificationReason = run.ClassificationReason
                });
            }
        }

        var records = BuildRecords(evidence, fp);
        var index = new PerformanceIntelligenceIndex
        {
            SchemaVersion = PerformanceIntelligenceSchema.CurrentVersion,
            BuiltAt = DateTimeOffset.UtcNow,
            CurrentFingerprint = fp,
            Records = records
                .OrderBy(r => r.SettingName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            AllEvidence = evidence
                .OrderByDescending(e => e.TestedAt)
                .ToList(),
            GuidedRunsAnalyzed = runs.Count
        };

        try
        {
            var path = Path.Combine(
                _paths.PerformanceHistoryDirectory,
                PerformanceIntelligenceSchema.IndexFileName);
            await FrameForgeJson.SerializeFileAsync(path, index, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not write performance index: {ex.Message}");
        }

        lock (_gate)
        {
            _cache = index;
            _stale = false;
        }

        _log.LogInformation(
            $"Performance intelligence rebuilt: {records.Count} setting record(s), {evidence.Count} evidence row(s).");
        return index;
    }

    public SettingPerformanceRecord? GetRecord(string settingKeyOrId)
    {
        if (string.IsNullOrWhiteSpace(settingKeyOrId))
        {
            return null;
        }

        PerformanceIntelligenceIndex? cache;
        lock (_gate)
        {
            cache = _cache;
        }

        if (cache is null)
        {
            return null;
        }

        return cache.Records.FirstOrDefault(r =>
            r.SettingKey.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase) ||
            r.SettingId.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<SettingPerformanceRecord> GetAllRecords()
    {
        lock (_gate)
        {
            return _cache?.Records.ToList() ?? new List<SettingPerformanceRecord>();
        }
    }

    public IReadOnlyList<SettingTestEvidence> GetEvidenceForSetting(string settingKeyOrId)
    {
        var record = GetRecord(settingKeyOrId);
        if (record is null)
        {
            return Array.Empty<SettingTestEvidence>();
        }

        return record.DirectEvidence
            .Concat(record.AssociatedEvidence)
            .OrderByDescending(e => e.TestedAt)
            .ToList();
    }

    public string GetRecommendationBlurb(string settingKeyOrId)
    {
        var record = GetRecord(settingKeyOrId);
        if (record is null)
        {
            return "No local benchmark evidence yet.";
        }

        return record.RecommendationSummary;
    }

    private static bool IsAnalyzable(GuidedOptimizationRun run)
    {
        if (run.PreviewOnly)
        {
            return false;
        }

        // Need a completed-ish outcome with classification attempt
        var terminal = run.Status is GuidedOptimizationStatus.Completed
            or GuidedOptimizationStatus.Restored
            or GuidedOptimizationStatus.Failed
            or GuidedOptimizationStatus.Cancelled;
        if (!terminal)
        {
            return false;
        }

        return (run.ComparisonRows?.Count ?? 0) > 0 ||
               run.Classification != GuidedResultClassification.Inconclusive ||
               !string.IsNullOrWhiteSpace(run.InitialBenchmarkId);
    }

    private static List<string> ResolveKeys(GuidedOptimizationRun run)
    {
        var keys = new List<string>();
        if (run.SelectedSettingKeys is { Count: > 0 })
        {
            keys.AddRange(run.SelectedSettingKeys.Where(k => !string.IsNullOrWhiteSpace(k)));
        }
        else if (run.SelectedSettings is { Count: > 0 })
        {
            keys.AddRange(run.SelectedSettings.Keys);
        }
        else if (run.PreviewDiff?.Entries is { Count: > 0 } entries)
        {
            keys.AddRange(entries
                .Where(e => e.IsChange &&
                            !e.SettingId.Equals("integration.autoexec", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.ConfigKey)
                .Where(k => !string.IsNullOrWhiteSpace(k)));
        }

        return keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<SettingPerformanceRecord> BuildRecords(
        List<SettingTestEvidence> evidence,
        SystemFingerprint fp)
    {
        var groups = evidence.GroupBy(e => e.SettingKey, StringComparer.OrdinalIgnoreCase);
        var list = new List<SettingPerformanceRecord>();

        foreach (var g in groups)
        {
            var items = g.OrderByDescending(e => e.TestedAt).ToList();
            var direct = items.Where(e => e.EvidenceType == PerformanceEvidenceType.SingleSetting).ToList();
            var multi = items.Where(e => e.EvidenceType == PerformanceEvidenceType.MultiSetting).ToList();

            var record = new SettingPerformanceRecord
            {
                SettingId = items[0].SettingId,
                SettingKey = items[0].SettingKey,
                SettingName = items[0].SettingName,
                SystemFingerprintId = fp.FingerprintId,
                SystemFingerprint = fp,
                TestCount = items.Count,
                DirectTestCount = direct.Count,
                AssociatedMultiSettingTestCount = multi.Count,
                DirectEvidence = direct,
                AssociatedEvidence = multi,
                LastTestedAt = items[0].TestedAt,
                LatestClassification = items[0].Classification
            };

            Tally(items, record, directOnly: false);
            TallyDirect(direct, record);

            // Valid direct = complete benchmark + not inconclusive for confidence scoring
            var validDirect = direct.Where(IsValidDirect).ToList();
            var improved = validDirect.Count(e => e.Classification == GuidedResultClassification.Improved);
            var regressed = validDirect.Count(e => e.Classification == GuidedResultClassification.Regressed);
            var mixed = validDirect.Count(e => e.Classification == GuidedResultClassification.Mixed);
            var neutral = validDirect.Count(e => e.Classification == GuidedResultClassification.Neutral);

            var decisiveImproved = improved;
            var decisiveRegressed = regressed;
            var conflicting = 0;
            if (decisiveImproved > 0 && decisiveRegressed > 0)
            {
                conflicting = Math.Min(decisiveImproved, decisiveRegressed);
            }

            if (mixed > 0)
            {
                conflicting += mixed;
            }

            var majority = Math.Max(decisiveImproved, decisiveRegressed);
            if (majority == 0)
            {
                majority = neutral;
            }

            record.Confidence = PerformanceConfidenceRules.Compute(
                validDirect.Count,
                majority,
                conflicting,
                multi.Count,
                out var confReason);
            record.ConfidenceReason = confReason;
            record.AggregateDirectClassification = AggregateClassification(validDirect);
            record.AverageMetricChanges = AggregateMetrics(validDirect.Concat(
                // also fold multi into averages but they don't drive confidence
                multi.Where(e => e.BenchmarkComplete)).ToList());
            record.RecommendationSummary = PerformanceConfidenceRules.BuildRecommendationSummary(record);

            list.Add(record);
        }

        return list;
    }

    private static bool IsValidDirect(SettingTestEvidence e) =>
        e.EvidenceType == PerformanceEvidenceType.SingleSetting &&
        e.BenchmarkComplete &&
        e.Classification != GuidedResultClassification.Inconclusive;

    private static void Tally(List<SettingTestEvidence> items, SettingPerformanceRecord record, bool directOnly)
    {
        foreach (var e in items)
        {
            switch (e.Classification)
            {
                case GuidedResultClassification.Improved: record.ImprovedCount++; break;
                case GuidedResultClassification.Regressed: record.RegressedCount++; break;
                case GuidedResultClassification.Neutral: record.NeutralCount++; break;
                case GuidedResultClassification.Mixed: record.MixedCount++; break;
                default: record.InconclusiveCount++; break;
            }
        }
    }

    private static void TallyDirect(List<SettingTestEvidence> direct, SettingPerformanceRecord record)
    {
        foreach (var e in direct)
        {
            switch (e.Classification)
            {
                case GuidedResultClassification.Improved: record.DirectImprovedCount++; break;
                case GuidedResultClassification.Regressed: record.DirectRegressedCount++; break;
                case GuidedResultClassification.Neutral: record.DirectNeutralCount++; break;
                case GuidedResultClassification.Mixed: record.DirectMixedCount++; break;
                default: record.DirectInconclusiveCount++; break;
            }
        }
    }

    private static GuidedResultClassification? AggregateClassification(List<SettingTestEvidence> validDirect)
    {
        if (validDirect.Count == 0)
        {
            return null;
        }

        var imp = validDirect.Count(e => e.Classification == GuidedResultClassification.Improved);
        var reg = validDirect.Count(e => e.Classification == GuidedResultClassification.Regressed);
        var mix = validDirect.Count(e => e.Classification == GuidedResultClassification.Mixed);
        var neu = validDirect.Count(e => e.Classification == GuidedResultClassification.Neutral);

        if (imp > 0 && reg == 0 && mix == 0)
        {
            return GuidedResultClassification.Improved;
        }

        if (reg > 0 && imp == 0 && mix == 0)
        {
            return GuidedResultClassification.Regressed;
        }

        if ((imp > 0 && reg > 0) || mix > 0)
        {
            return GuidedResultClassification.Mixed;
        }

        if (neu > 0)
        {
            return GuidedResultClassification.Neutral;
        }

        return GuidedResultClassification.Inconclusive;
    }

    private static AverageMetricChanges AggregateMetrics(List<SettingTestEvidence> items)
    {
        var avg = new AverageMetricChanges();

        void Set(string needle, Action<double> setVal, Action<int> setCount)
        {
            double s = 0;
            var n = 0;
            foreach (var e in items)
            {
                foreach (var row in e.ComparisonRows.Where(r =>
                             r.IsAvailable &&
                             r.AbsoluteDifference is not null &&
                             r.Metric.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                {
                    s += row.AbsoluteDifference!.Value;
                    n++;
                }
            }

            if (n > 0)
            {
                setVal(s / n);
                setCount(n);
            }
        }

        Set("System CPU", v => avg.SystemCpuDelta = v, c => avg.SystemCpuSampleCount = c);
        Set("process CPU", v => avg.ProcessCpuDelta = v, c => avg.ProcessCpuSampleCount = c);
        Set("System memory", v => avg.SystemMemoryDelta = v, c => avg.SystemMemorySampleCount = c);
        Set("working set", v => avg.ProcessWorkingSetMbDelta = v, c => avg.ProcessWorkingSetSampleCount = c);
        Set("frame-time", v => avg.FrameTimeMsDelta = v, c => avg.FrameTimeSampleCount = c);
        Set("GPU", v => avg.GpuUtilizationDelta = v, c => avg.GpuUtilizationSampleCount = c);

        return avg;
    }
}
