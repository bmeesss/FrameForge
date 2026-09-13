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

            var runFp = !string.IsNullOrWhiteSpace(run.SystemFingerprintId)
                ? run.SystemFingerprintId!
                : fp.FingerprintId;

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
                    SystemFingerprintId = runFp,
                    AppliedValue = applied,
                    AllSettingKeysInRun = keys,
                    ComparisonRows = run.ComparisonRows?.ToList() ?? new List<GuidedComparisonRow>(),
                    BenchmarkComplete = complete,
                    ClassificationReason = run.ClassificationReason
                });
            }
        }

        // Merge imported evidence archives (fingerprints stay original — never reassigned).
        await MergeImportedEvidenceAsync(evidence, knownSeed: null, cancellationToken).ConfigureAwait(false);

        var records = BuildRecords(evidence, fp);
        var ordered = records
            .OrderByDescending(r => r.IsCurrentSystem)
            .ThenBy(r => r.SettingName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = new List<SystemFingerprint> { fp };
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fp.FingerprintId };
        foreach (var id in ordered.Select(r => r.SystemFingerprintId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (knownIds.Add(id))
            {
                known.Add(new SystemFingerprint
                {
                    FingerprintId = id,
                    CpuModel = "previous-or-unknown",
                    GpuModel = "previous-or-unknown",
                    OsVersion = "previous-or-unknown",
                    Architecture = "previous-or-unknown"
                });
            }
        }

        // Prefer fingerprint objects from import archives when available
        await EnrichKnownFingerprintsAsync(known, cancellationToken).ConfigureAwait(false);

        var index = new PerformanceIntelligenceIndex
        {
            SchemaVersion = PerformanceIntelligenceSchema.CurrentVersion,
            BuiltAt = DateTimeOffset.UtcNow,
            CurrentFingerprint = fp,
            KnownFingerprints = known,
            Records = ordered,
            CurrentSystemRecords = ordered.Where(r => r.IsCurrentSystem).ToList(),
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
            TryQuarantineCorruptIndex();
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

    public SettingPerformanceRecord? GetRecord(string settingKeyOrId, bool currentSystemOnly = true)
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

        var source = currentSystemOnly ? cache.CurrentSystemRecords.Concat(cache.Records.Where(r => r.IsCurrentSystem)) : cache.Records;
        // prefer current-system match
        return source.FirstOrDefault(r =>
            (r.SettingKey.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase) ||
             r.SettingId.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase)) &&
            (!currentSystemOnly || r.IsCurrentSystem))
            ?? (currentSystemOnly
                ? null
                : cache.Records.FirstOrDefault(r =>
                    r.SettingKey.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase) ||
                    r.SettingId.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<SettingPerformanceRecord> GetAllRecords(bool currentSystemOnly = true)
    {
        lock (_gate)
        {
            if (_cache is null)
            {
                return Array.Empty<SettingPerformanceRecord>();
            }

            return currentSystemOnly
                ? _cache.Records.Where(r => r.IsCurrentSystem).ToList()
                : _cache.Records.ToList();
        }
    }

    public IReadOnlyList<SettingTestEvidence> GetEvidenceForSetting(string settingKeyOrId, bool currentSystemOnly = true)
    {
        PerformanceIntelligenceIndex? cache;
        lock (_gate) { cache = _cache; }
        if (cache is null)
        {
            return Array.Empty<SettingTestEvidence>();
        }

        IEnumerable<SettingTestEvidence> q = cache.AllEvidence.Where(e =>
            e.SettingKey.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase) ||
            e.SettingId.Equals(settingKeyOrId, StringComparison.OrdinalIgnoreCase));

        if (currentSystemOnly && cache.CurrentFingerprint is not null)
        {
            var id = cache.CurrentFingerprint.FingerprintId;
            q = q.Where(e =>
                string.IsNullOrWhiteSpace(e.SystemFingerprintId) ||
                e.SystemFingerprintId.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        return q.OrderByDescending(e => e.TestedAt).ToList();
    }

    public string GetRecommendationBlurb(string settingKeyOrId, bool currentSystemOnly = true)
    {
        var record = GetRecord(settingKeyOrId, currentSystemOnly);
        if (record is null)
        {
            return "No local benchmark evidence yet.";
        }

        return record.RecommendationSummary;
    }

    public IReadOnlyList<SystemFingerprint> GetKnownFingerprints()
    {
        lock (_gate)
        {
            return _cache?.KnownFingerprints.ToList() ?? new List<SystemFingerprint>();
        }
    }

    private sealed class KeyFpComparer : IEqualityComparer<(string Key, string Fp)>
    {
        public bool Equals((string Key, string Fp) x, (string Key, string Fp) y) =>
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Fp, y.Fp, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Fp) obj) =>
            HashCode.Combine(
                obj.Key?.ToLowerInvariant(),
                obj.Fp?.ToLowerInvariant());
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
        var groups = evidence.GroupBy(
            e => (Key: e.SettingKey, Fp: e.SystemFingerprintId ?? "unknown"),
            new KeyFpComparer());
        var list = new List<SettingPerformanceRecord>();

        foreach (var g in groups)
        {
            var items = g.OrderByDescending(e => e.TestedAt).ToList();
            var direct = items.Where(e => e.EvidenceType == PerformanceEvidenceType.SingleSetting).ToList();
            var multi = items.Where(e => e.EvidenceType == PerformanceEvidenceType.MultiSetting).ToList();
            var fpId = items[0].SystemFingerprintId ?? "unknown";
            var isCurrent = fpId.Equals(fp.FingerprintId, StringComparison.OrdinalIgnoreCase);

            var record = new SettingPerformanceRecord
            {
                SettingId = items[0].SettingId,
                SettingKey = items[0].SettingKey,
                SettingName = items[0].SettingName,
                SystemFingerprintId = fpId,
                SystemFingerprint = isCurrent ? fp : new SystemFingerprint { FingerprintId = fpId, CpuModel = "previous", GpuModel = "previous", OsVersion = "previous", Architecture = "previous" },
                IsCurrentSystem = isCurrent,
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

    private async Task MergeImportedEvidenceAsync(
        List<SettingTestEvidence> evidence,
        List<SystemFingerprint>? knownSeed,
        CancellationToken cancellationToken)
    {
        var importDir = Path.Combine(_paths.PerformanceHistoryDirectory, "imports");
        if (!Directory.Exists(importDir))
        {
            return;
        }

        var seen = new HashSet<string>(
            evidence.Select(e => e.GuidedRunId + "|" + e.SettingKey),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(importDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var package = await FrameForgeJson.DeserializeFileAsync<IntelligenceExportPackage>(file, cancellationToken)
                    .ConfigureAwait(false);
                if (package?.Evidence is null)
                {
                    continue;
                }

                foreach (var e in package.Evidence)
                {
                    if (string.IsNullOrWhiteSpace(e.GuidedRunId) || string.IsNullOrWhiteSpace(e.SettingKey))
                    {
                        continue;
                    }

                    var key = e.GuidedRunId + "|" + e.SettingKey;
                    if (seen.Add(key))
                    {
                        evidence.Add(e);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Skipping corrupt intelligence import '{file}': {ex.Message}");
                try
                {
                    File.Move(file, file + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}");
                }
                catch { /* ignore */ }
            }
        }
    }

    private async Task EnrichKnownFingerprintsAsync(
        List<SystemFingerprint> known,
        CancellationToken cancellationToken)
    {
        var importDir = Path.Combine(_paths.PerformanceHistoryDirectory, "imports");
        if (!Directory.Exists(importDir))
        {
            return;
        }

        var byId = known.ToDictionary(k => k.FingerprintId, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(importDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var package = await FrameForgeJson.DeserializeFileAsync<IntelligenceExportPackage>(file, cancellationToken)
                    .ConfigureAwait(false);
                if (package?.Fingerprints is null)
                {
                    continue;
                }

                foreach (var fp in package.Fingerprints)
                {
                    if (string.IsNullOrWhiteSpace(fp.FingerprintId))
                    {
                        continue;
                    }

                    if (!byId.ContainsKey(fp.FingerprintId))
                    {
                        known.Add(fp);
                        byId[fp.FingerprintId] = fp;
                    }
                    else if (byId[fp.FingerprintId].CpuModel is "previous-or-unknown" or "unknown")
                    {
                        var idx = known.FindIndex(k => k.FingerprintId.Equals(fp.FingerprintId, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                        {
                            known[idx] = fp;
                            byId[fp.FingerprintId] = fp;
                        }
                    }
                }
            }
            catch
            {
                // already logged in merge
            }
        }
    }

    private void TryQuarantineCorruptIndex()
    {
        var path = Path.Combine(_paths.PerformanceHistoryDirectory, PerformanceIntelligenceSchema.IndexFileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            // Only quarantine if file cannot be re-read
            _ = FrameForgeJson.DeserializeFileAsync<PerformanceIntelligenceIndex>(path, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            try
            {
                File.Move(path, path + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}");
                _log.LogWarning("Quarantined corrupt performance intelligence index.");
            }
            catch { /* ignore */ }
        }
    }

}
