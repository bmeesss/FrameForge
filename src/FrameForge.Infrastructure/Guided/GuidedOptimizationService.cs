using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Guided;

/// <summary>
/// Safe orchestration: baseline benchmark → preview → confirm → apply (existing settings service)
/// → post benchmark → compare → keep/restore. No Windows tweaks, no injection.
/// </summary>
public sealed class GuidedOptimizationService : IGuidedOptimizationService
{
    private readonly ICs2DetectionService _detection;
    private readonly ICs2ProcessMonitor _processMonitor;
    private readonly ICs2SettingsService _settings;
    private readonly IProfileService _profiles;
    private readonly IBenchmarkEngine _benchmark;
    private readonly IBenchmarkStore _benchmarkStore;
    private readonly IBenchmarkCalculator _calculator;
    private readonly IGuidedOptimizationStore _store;
    private readonly ISystemFingerprintService? _fingerprint;
    private readonly IManagedConfigWatcher? _configWatcher;
    private readonly IAppLog _log;

    private readonly object _gate = new();
    private GuidedOptimizationStatus _status = GuidedOptimizationStatus.Idle;
    private GuidedOptimizationRun? _current;
    private TaskCompletionSource<bool>? _confirmTcs;
    private TaskCompletionSource<GuidedUserDecision>? _decisionTcs;
    private CancellationTokenSource? _runCts;
    private bool _cancelRequested;

    public GuidedOptimizationService(
        ICs2DetectionService detection,
        ICs2ProcessMonitor processMonitor,
        ICs2SettingsService settings,
        IProfileService profiles,
        IBenchmarkEngine benchmark,
        IBenchmarkStore benchmarkStore,
        IBenchmarkCalculator calculator,
        IGuidedOptimizationStore store,
        IAppLog log,
        ISystemFingerprintService? fingerprint = null,
        IManagedConfigWatcher? configWatcher = null)
    {
        _detection = detection;
        _processMonitor = processMonitor;
        _settings = settings;
        _profiles = profiles;
        _benchmark = benchmark;
        _benchmarkStore = benchmarkStore;
        _calculator = calculator;
        _store = store;
        _fingerprint = fingerprint;
        _configWatcher = configWatcher;
        _log = log;
    }

    public GuidedOptimizationStatus Status
    {
        get { lock (_gate) return _status; }
        private set
        {
            lock (_gate)
            {
                if (_status == value)
                {
                    return;
                }

                _status = value;
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public GuidedOptimizationRun? CurrentRun
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<GuidedOptimizationProgress>? ProgressChanged;
    public event EventHandler? StatusChanged;

    public async Task<GuidedOptimizationRun> PreviewAsync(
        GuidedOptimizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = CreateRunShell(request);
        await StampFingerprintAsync(run, cancellationToken).ConfigureAwait(false);
        run.PreviewOnly = true;
        SetCurrent(run);
        SetStatus(run, GuidedOptimizationStatus.Preparing, "Preparing preview…", 5);

        try
        {
            var prepared = await PrepareTargetAsync(run, request, cancellationToken).ConfigureAwait(false);
            if (!prepared)
            {
                return await FinishAsync(run).ConfigureAwait(false);
            }

            SetStatus(run, GuidedOptimizationStatus.PreparingOptimization, "Generating diff (no changes written)…", 40);
            var snapshot = await _settings.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Cs2Available)
            {
                Fail(run, snapshot.Message);
                return await FinishAsync(run).ConfigureAwait(false);
            }

            var desired = await ResolveDesiredAsync(request, cancellationToken).ConfigureAwait(false);
            StampSelectedSettings(run, desired);
            var validation = _settings.ValidateSettings(desired);
            if (!validation.IsValid)
            {
                Fail(run, "Validation failed: " + string.Join("; ", validation.Issues.Select(i => i.Message)));
                return await FinishAsync(run).ConfigureAwait(false);
            }

            var diff = _settings.CreateDiff(
                snapshot,
                desired,
                request.ProfileName is null ? "Guided preview" : $"Profile:{request.ProfileName}",
                request.ProfileId,
                request.ProfileName);

            run.PreviewDiff = diff;
            run.AffectedFiles = diff.AffectedFiles.Count > 0
                ? diff.AffectedFiles
                : snapshot.AffectedFilesOnApply;

            SetStatus(run, GuidedOptimizationStatus.Completed, "Preview only — no files modified.", 100);
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Classification = GuidedResultClassification.Inconclusive;
            run.ClassificationReason = "Preview-only run; no apply or benchmarks.";
            return await FinishAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError("Guided preview failed.", ex);
            Fail(run, ex.Message);
            return await FinishAsync(run).ConfigureAwait(false);
        }
        finally
        {
            ClearWaiters();
        }
    }

    public async Task<GuidedOptimizationRun> RunAsync(
        GuidedOptimizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreviewOnly)
        {
            return await PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_status is not GuidedOptimizationStatus.Idle and not GuidedOptimizationStatus.Completed
                and not GuidedOptimizationStatus.Failed and not GuidedOptimizationStatus.Cancelled
                and not GuidedOptimizationStatus.Restored)
            {
                throw new InvalidOperationException($"Cannot start guided run while status is {_status}.");
            }
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _runCts?.Dispose();
            _runCts = linked;
            _cancelRequested = false;
            _confirmTcs = null;
            _decisionTcs = null;
        }

        var run = CreateRunShell(request);
        await StampFingerprintAsync(run, cancellationToken).ConfigureAwait(false);
        SetCurrent(run);

        try
        {
            SetStatus(run, GuidedOptimizationStatus.Preparing, "Preparing guided optimization…", 2);
            Report(run, "Close unnecessary background apps for a more repeatable benchmark when possible.");

            if (!await PrepareTargetAsync(run, request, linked.Token).ConfigureAwait(false))
            {
                return await FinishAsync(run).ConfigureAwait(false);
            }

            // CS2 install required for apply path
            var install = await _detection.DetectAsync(linked.Token).ConfigureAwait(false);
            if (!install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
            {
                Fail(run, install.DetectionMessage + " Guided apply requires a detected CS2 cfg directory.");
                return await FinishAsync(run).ConfigureAwait(false);
            }

            if (request.RequireCs2ProcessForBenchmark)
            {
                var proc = _processMonitor.TryGetCs2Process();
                if (proc is null)
                {
                    Fail(run,
                        "CS2 is not running. Start Counter-Strike 2 and load a map/menu before the baseline benchmark. FrameForge does not launch CS2 automatically.");
                    return await FinishAsync(run).ConfigureAwait(false);
                }
            }

            // ── Baseline benchmark ──────────────────────────────────────
            if (IsCancelRequested())
            {
                return await CancelledAsync(run).ConfigureAwait(false);
            }

            SetStatus(run, GuidedOptimizationStatus.BenchmarkingBefore, "Running baseline benchmark…", 10);
            var benchCfg = CloneBenchConfig(request.BenchmarkConfiguration, request.ProfileId, request.ProfileName, "baseline");
            var validationBench = BenchmarkConfiguration.Validate(benchCfg);
            if (!validationBench.IsValid)
            {
                Fail(run, "Invalid benchmark configuration: " + string.Join("; ", validationBench.Issues));
                return await FinishAsync(run).ConfigureAwait(false);
            }

            BenchmarkRun baseline;
            try
            {
                baseline = await _benchmark.StartAsync(benchCfg, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(run, "Baseline benchmark failed: " + ex.Message);
                return await FinishAsync(run).ConfigureAwait(false);
            }

            if (baseline.Status is BenchmarkStatus.Failed or BenchmarkStatus.Cancelled)
            {
                Fail(run, baseline.Error ?? $"Baseline benchmark ended as {baseline.Status}.");
                return await FinishAsync(run).ConfigureAwait(false);
            }

            run.InitialBenchmarkId = baseline.Id;
            Report(run, $"Baseline benchmark saved: {baseline.Id}");

            if (IsCancelRequested())
            {
                return await CancelledAsync(run).ConfigureAwait(false);
            }

            // ── Preview / diff ──────────────────────────────────────────
            SetStatus(run, GuidedOptimizationStatus.PreparingOptimization, "Preparing optimization preview…", 30);
            var snapshot = await _settings.ReadSettingsAsync(linked.Token).ConfigureAwait(false);
            if (!snapshot.Cs2Available)
            {
                Fail(run, snapshot.Message);
                return await FinishAsync(run).ConfigureAwait(false);
            }

            var desired = await ResolveDesiredAsync(request, linked.Token).ConfigureAwait(false);
            StampSelectedSettings(run, desired);
            if (desired.Count == 0)
            {
                Fail(run, "No settings selected to apply. Choose a profile or explicit settings.");
                return await FinishAsync(run).ConfigureAwait(false);
            }

            var validation = _settings.ValidateSettings(desired);
            if (!validation.IsValid)
            {
                Fail(run, "Validation failed: " + string.Join("; ", validation.Issues.Select(i => i.Message)));
                return await FinishAsync(run).ConfigureAwait(false);
            }

            var diff = _settings.CreateDiff(
                snapshot,
                desired,
                request.ProfileName is null ? "Guided optimization" : $"Profile:{request.ProfileName}",
                request.ProfileId,
                request.ProfileName);

            run.PreviewDiff = diff;
            run.AffectedFiles = diff.AffectedFiles.Count > 0
                ? diff.AffectedFiles.ToList()
                : snapshot.AffectedFilesOnApply.ToList();

            if (!diff.HasChanges)
            {
                // Still allow completing with neutral comparison if user wants — but nothing to apply
                Report(run, "Diff has no changes; configuration already matches target.");
                SetStatus(run, GuidedOptimizationStatus.Completed, "No changes to apply.", 100);
                run.CompletedAt = DateTimeOffset.UtcNow;
                run.Classification = GuidedResultClassification.Neutral;
                run.ClassificationReason = "Target settings already match current configuration; no apply performed.";
                run.UserDecision = GuidedUserDecision.Keep;
                return await FinishAsync(run).ConfigureAwait(false);
            }

            // ── Await confirmation ──────────────────────────────────────
            SetStatus(run, GuidedOptimizationStatus.AwaitingConfirmation,
                BuildConfirmMessage(run, diff), 40);

            var confirmed = await WaitForConfirmAsync(linked.Token).ConfigureAwait(false);
            if (!confirmed || IsCancelRequested())
            {
                return await CancelledAsync(run).ConfigureAwait(false);
            }

            // ── Apply (existing settings service: backup → write → verify → rollback) ──
            SetStatus(run, GuidedOptimizationStatus.Applying, "Creating backup…", 48);
            Report(run, "Creating backup before apply…");
            SetStatus(run, GuidedOptimizationStatus.Applying, "Applying settings…", 52);
            SettingsApplyResult apply;
            try
            {
                apply = await _settings.ApplyDiffAsync(diff, createBackup: true, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(run, "Apply threw: " + ex.Message);
                return await FinishAsync(run).ConfigureAwait(false);
            }

            run.ApplyMessage = apply.Message;
            run.ApplySucceeded = apply.Success;
            run.BackupId = apply.BackupId;

            if (!apply.Success)
            {
                // Settings service already rolls back on verify failure when backup exists
                if (apply.RolledBack)
                {
                    run.RestoredSuccessfully = true;
                    SetStatus(run, GuidedOptimizationStatus.Restored,
                        "Apply/verification failed; automatic rollback completed. " + apply.Message, 100);
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    run.Error = apply.Message;
                    run.Classification = GuidedResultClassification.Inconclusive;
                    run.ClassificationReason = "Apply failed and was rolled back; no post-benchmark.";
                    return await FinishAsync(run).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(apply.BackupId) &&
                    apply.Message.Contains("Backup failed", StringComparison.OrdinalIgnoreCase))
                {
                    Fail(run, "Backup failed; apply aborted. " + apply.Message);
                    return await FinishAsync(run).ConfigureAwait(false);
                }

                Fail(run, apply.Message);
                return await FinishAsync(run).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(apply.BackupId))
            {
                // Automatic backup disabled — still require a backup for guided safety
                Report(run, "Warning: apply succeeded without backup id (automatic backup may be off).");
            }

            SetStatus(run, GuidedOptimizationStatus.Applying, "Verifying applied settings…", 58);
            if (_configWatcher is not null)
            {
                try { await _configWatcher.CaptureBaselineAsync(linked.Token).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug($"Baseline capture after apply: {ex.Message}"); }
            }

            Report(run, $"Applied OK. Backup={apply.BackupId ?? "none"}. Files: {string.Join(", ", apply.WrittenFiles.Select(Path.GetFileName))}");

            if (IsCancelRequested())
            {
                // Changes already applied — leave config, mark cancelled after apply
                run.UserDecision = GuidedUserDecision.Cancelled;
                SetStatus(run, GuidedOptimizationStatus.Cancelled,
                    "Cancelled after apply; configuration left as applied. Use Restore from history if needed.", 100);
                run.CompletedAt = DateTimeOffset.UtcNow;
                return await FinishAsync(run).ConfigureAwait(false);
            }

            // ── Post benchmark (identical config) ───────────────────────
            SetStatus(run, GuidedOptimizationStatus.BenchmarkingAfter, "Running post-apply benchmark…", 70);
            if (request.RequireCs2ProcessForBenchmark && _processMonitor.TryGetCs2Process() is null)
            {
                Fail(run,
                    "CS2 closed during the run; post-benchmark cannot start. Changes remain applied. Choose restore from a manual backup if needed.");
                return await FinishAsync(run).ConfigureAwait(false);
            }

            var postCfg = CloneBenchConfig(request.BenchmarkConfiguration, request.ProfileId, request.ProfileName, "post");
            BenchmarkRun post;
            try
            {
                post = await _benchmark.StartAsync(postCfg, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(run, "Post-benchmark failed: " + ex.Message + " Changes remain applied; backup=" + (run.BackupId ?? "none"));
                return await FinishAsync(run).ConfigureAwait(false);
            }

            if (post.Status is BenchmarkStatus.Failed or BenchmarkStatus.Cancelled)
            {
                Fail(run, post.Error ?? $"Post-benchmark ended as {post.Status}. Changes remain applied.");
                return await FinishAsync(run).ConfigureAwait(false);
            }

            run.PostBenchmarkId = post.Id;
            Report(run, $"Post benchmark saved: {post.Id}");

            // ── Evaluate conditions BEFORE final compare (Phase 12 integrity gate) ──
            SetStatus(run, GuidedOptimizationStatus.Comparing, "Evaluating baseline vs post conditions…", 82);
            var before = await _benchmarkStore.GetAsync(baseline.Id, linked.Token).ConfigureAwait(false) ?? baseline;
            var after = await _benchmarkStore.GetAsync(post.Id, linked.Token).ConfigureAwait(false) ?? post;

            // Fingerprints from benchmark snapshots (source of truth — do not recompute)
            run.BaselineFingerprintId = before.SystemInformation?.SystemFingerprintId;
            run.PostFingerprintId = after.SystemInformation?.SystemFingerprintId;
            if (string.IsNullOrWhiteSpace(run.SystemFingerprintId))
            {
                run.SystemFingerprintId = run.BaselineFingerprintId ?? run.PostFingerprintId;
            }

            var conditionReport = BenchmarkConditionRules.Analyze(before, after, forcedDespiteMismatch: false);
            run.ConditionReport = conditionReport;
            run.ConditionWarnings = conditionReport.Warnings.Count > 0
                ? conditionReport.Warnings.Select(w => new GuidedConditionWarning
                {
                    Code = w.Code,
                    Message = w.Message,
                    IsBlocking = w.IsBlocking
                }).ToList()
                : BuildConditionWarnings(before, after).ToList();

            var forceCompare = false;
            if (conditionReport.HasSevereMismatch)
            {
                var why = BuildSevereMismatchMessage(conditionReport);
                run.ConditionStatusAtDecision = conditionReport.OverallStatus;
                run.ConditionReliabilityAtDecision = conditionReport.Reliability;
                SetStatus(
                    run,
                    GuidedOptimizationStatus.AwaitingConditionDecision,
                    why + " Choose Continue Comparison or Stop / Restore (default).",
                    84);
                Report(run, why);

                var gateDecision = await WaitForDecisionAsync(linked.Token).ConfigureAwait(false);
                run.ConditionDecision = gateDecision;

                if (gateDecision is GuidedUserDecision.StopRestore or GuidedUserDecision.Restore
                    or GuidedUserDecision.Cancelled)
                {
                    run.ConditionOverride = false;
                    run.ConditionOverrideReason = "User stopped at integrity gate: " + why;
                    run.ForcedCompareDespiteMismatch = false;
                    run.Classification = GuidedResultClassification.Inconclusive;
                    run.ClassificationReason =
                        "Stopped due to severe condition mismatch before final comparison. " + why;
                    run.UserDecision = GuidedUserDecision.StopRestore;

                    // Restore applied changes — do not classify as successful optimization
                    SetStatus(run, GuidedOptimizationStatus.Restoring,
                        "Stopping due to condition mismatch — restoring previous configuration…", 90);
                    var stopRestore = await TryRestoreAfterMismatchAsync(run, linked.Token).ConfigureAwait(false);
                    if (!stopRestore.ok)
                    {
                        Fail(run, "Stop/Restore after condition mismatch failed: " + stopRestore.message);
                        return await FinishAsync(run).ConfigureAwait(false);
                    }

                    run.RestoredSuccessfully = stopRestore.restored;
                    SetStatus(
                        run,
                        stopRestore.restored ? GuidedOptimizationStatus.Restored : GuidedOptimizationStatus.Cancelled,
                        stopRestore.restored
                            ? "Stopped due to condition mismatch; previous configuration restored. " + stopRestore.message
                            : "Stopped due to condition mismatch; " + stopRestore.message,
                        100);
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    return await FinishAsync(run).ConfigureAwait(false);
                }

                if (gateDecision is GuidedUserDecision.ContinueComparison or GuidedUserDecision.Keep)
                {
                    forceCompare = true;
                    run.ConditionOverride = true;
                    run.ForcedCompareDespiteMismatch = true;
                    run.ConditionOverrideReason =
                        "User continued comparison despite severe mismatch. " +
                        "A forced comparison does not become a valid performance proof. " + why;
                    Report(run, run.ConditionOverrideReason);
                }
                else
                {
                    // Unexpected — treat as stop
                    return await CancelledAsync(run).ConfigureAwait(false);
                }
            }
            else if (conditionReport.OverallStatus == ConditionMatchStatus.Warning)
            {
                Report(run, "Comparison may not be reliable because system conditions changed. Reliability: " +
                            conditionReport.Reliability);
            }
            else
            {
                Report(run, "Condition match: " + conditionReport.OverallStatus +
                            " · Reliability: " + conditionReport.Reliability + " (not statistical).");
            }

            // ── Final compare (after gate) ──────────────────────────────
            SetStatus(run, GuidedOptimizationStatus.Comparing, "Comparing baseline vs post…", 85);
            conditionReport = BenchmarkConditionRules.Analyze(before, after, forcedDespiteMismatch: forceCompare);
            run.ConditionReport = conditionReport;

            var comparison = _calculator.Compare(
                before,
                after,
                new BenchmarkCompareOptions { ForceCompareDespiteSevereMismatch = forceCompare });
            run.Comparison = comparison;
            run.ComparisonRows = BuildRows(comparison).ToList();
            var provisional = GuidedClassificationRules.Classify(run.ComparisonRows, out var reason);
            // Always gate: severe (with or without force) → Inconclusive for performance claims
            run.Classification = BenchmarkConditionRules.ApplyClassificationGate(
                provisional, conditionReport, out var gateSuffix);
            run.ClassificationReason = reason + gateSuffix;
            if (forceCompare)
            {
                run.Classification = GuidedResultClassification.Inconclusive;
                if (!run.ClassificationReason.Contains("despite condition mismatch", StringComparison.OrdinalIgnoreCase))
                {
                    run.ClassificationReason +=
                        " Comparison performed despite condition mismatch. Not a valid performance proof.";
                }
            }

            // ── Await keep / restore ────────────────────────────────────
            SetStatus(run, GuidedOptimizationStatus.AwaitingDecision,
                $"Comparison ready ({run.Classification}). Choose Keep Changes or Restore Previous State.", 90);

            var decision = await WaitForDecisionAsync(linked.Token).ConfigureAwait(false);
            run.UserDecision = decision;

            if (decision == GuidedUserDecision.Restore || decision == GuidedUserDecision.StopRestore)
            {
                SetStatus(run, GuidedOptimizationStatus.Restoring, "Restoring backup…", 95);
                var restoreResult = await TryRestoreAfterMismatchAsync(run, linked.Token).ConfigureAwait(false);
                if (!restoreResult.ok)
                {
                    Fail(run, "Restore failed: " + restoreResult.message);
                    return await FinishAsync(run).ConfigureAwait(false);
                }

                run.RestoredSuccessfully = restoreResult.restored;
                SetStatus(run, GuidedOptimizationStatus.Restored,
                    "Previous configuration restored. " + restoreResult.message, 100);
                run.CompletedAt = DateTimeOffset.UtcNow;
                return await FinishAsync(run).ConfigureAwait(false);
            }

            if (decision == GuidedUserDecision.Cancelled)
            {
                return await CancelledAsync(run).ConfigureAwait(false);
            }

            // Keep
            SetStatus(run, GuidedOptimizationStatus.Completed, "Changes kept. Guided run complete.", 100);
            run.CompletedAt = DateTimeOffset.UtcNow;
            return await FinishAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CancelledAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError("Guided optimization failed.", ex);
            Fail(run, ex.Message);
            return await FinishAsync(run).ConfigureAwait(false);
        }
        finally
        {
            ClearWaiters();
            lock (_gate)
            {
                _runCts?.Dispose();
                _runCts = null;
            }
        }
    }

    public void ConfirmApply()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
        {
            tcs = _confirmTcs;
        }

        tcs?.TrySetResult(true);
    }

    public void Cancel()
    {
        bool atConditionGate;
        lock (_gate)
        {
            atConditionGate = _status == GuidedOptimizationStatus.AwaitingConditionDecision;
            _cancelRequested = true;
            _confirmTcs?.TrySetResult(false);
            // Prefer safest stop at condition gate; set decision BEFORE any CTS cancel
            // so WaitForDecisionAsync does not race to Cancelled via token registration.
            _decisionTcs?.TrySetResult(
                atConditionGate
                    ? GuidedUserDecision.StopRestore
                    : GuidedUserDecision.Cancelled);
            // Do not cancel the run CTS at the integrity gate — restore must still run.
            if (!atConditionGate)
            {
                try { _runCts?.Cancel(); } catch { /* ignore */ }
            }
        }

        if (!atConditionGate)
        {
            try { _benchmark.RequestCancel(); } catch { /* ignore */ }
        }
    }

    public void ContinueComparison() => Decide(GuidedUserDecision.ContinueComparison);

    public void StopRestoreDueToConditionMismatch() => Decide(GuidedUserDecision.StopRestore);

    public void Decide(GuidedUserDecision decision)
    {
        if (decision is not (GuidedUserDecision.Keep or GuidedUserDecision.Restore or GuidedUserDecision.Cancelled
            or GuidedUserDecision.ContinueComparison or GuidedUserDecision.StopRestore))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        TaskCompletionSource<GuidedUserDecision>? tcs;
        lock (_gate)
        {
            tcs = _decisionTcs;
        }

        tcs?.TrySetResult(decision);
    }

    private async Task<bool> PrepareTargetAsync(
        GuidedOptimizationRun run,
        GuidedOptimizationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetKind == GuidedTargetKind.Profile)
        {
            if (string.IsNullOrWhiteSpace(request.ProfileId))
            {
                Fail(run, "Profile id is required.");
                return false;
            }

            var profile = await _profiles.GetProfileAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                Fail(run, $"Profile '{request.ProfileId}' was not found.");
                return false;
            }

            run.ProfileId = profile.Id;
            run.ProfileName = profile.Name;
            run.OptimizationLabel = profile.Name;

            var pv = _profiles.ValidateProfile(profile);
            if (!pv.IsValid)
            {
                Fail(run, "Profile validation failed: " + string.Join("; ", pv.Issues));
                return false;
            }
        }
        else
        {
            if (request.DesiredSettings.Count == 0)
            {
                Fail(run, "No settings selected.");
                return false;
            }

            run.OptimizationLabel = request.Label ?? "Custom settings";
        }

        return true;
    }

    private async Task<Dictionary<string, string>> ResolveDesiredAsync(
        GuidedOptimizationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetKind == GuidedTargetKind.SettingsMap)
        {
            return new Dictionary<string, string>(request.DesiredSettings, StringComparer.OrdinalIgnoreCase);
        }

        if (request.DesiredSettings.Count > 0)
        {
            return new Dictionary<string, string>(request.DesiredSettings, StringComparer.OrdinalIgnoreCase);
        }

        var profile = await _profiles.GetProfileAsync(request.ProfileId!, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, string>(profile?.Settings ?? new(), StringComparer.OrdinalIgnoreCase);
    }



    private async Task StampFingerprintAsync(GuidedOptimizationRun run, CancellationToken cancellationToken)
    {
        if (_fingerprint is null || !string.IsNullOrWhiteSpace(run.SystemFingerprintId))
        {
            return;
        }

        try
        {
            var fp = await _fingerprint.GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
            run.SystemFingerprintId = fp.FingerprintId;
        }
        catch
        {
            // non-fatal
        }
    }

    private static void StampSelectedSettings(GuidedOptimizationRun run, IReadOnlyDictionary<string, string> desired)
    {
        run.SelectedSettings = new Dictionary<string, string>(desired, StringComparer.OrdinalIgnoreCase);
        run.SelectedSettingKeys = desired.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(run.OptimizationLabel))
        {
            run.OptimizationLabel = desired.Count == 1
                ? desired.Keys.First()
                : $"{desired.Count} setting(s)";
        }
    }

    private static GuidedOptimizationRun CreateRunShell(GuidedOptimizationRequest request)
    {
        var selected = new Dictionary<string, string>(request.DesiredSettings, StringComparer.OrdinalIgnoreCase);
        return new GuidedOptimizationRun
        {
            Id = GuidedOptimizationStore.CreateId(DateTimeOffset.Now),
            StartedAt = DateTimeOffset.UtcNow,
            Status = GuidedOptimizationStatus.Preparing,
            ProfileId = request.ProfileId,
            ProfileName = request.ProfileName,
            OptimizationLabel = request.Label,
            CustomSetId = request.CustomSetId,
            CustomSetName = request.CustomSetName,
            SelectedSettingKeys = selected.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            SelectedSettings = selected,
            BenchmarkConfiguration = request.BenchmarkConfiguration,
            PreviewOnly = request.PreviewOnly
        };
    }

    private void SetCurrent(GuidedOptimizationRun run)
    {
        lock (_gate)
        {
            _current = run;
        }
    }

    private void SetStatus(GuidedOptimizationRun run, GuidedOptimizationStatus status, string message, double? pct)
    {
        run.Status = status;
        run.ProgressLog.Add($"{DateTimeOffset.Now:HH:mm:ss} [{status}] {message}");
        Status = status;
        ProgressChanged?.Invoke(this, new GuidedOptimizationProgress
        {
            Status = status,
            Message = message,
            ProgressPercent = pct
        });
        _log.LogInformation($"Guided[{run.Id}] {status}: {message}");
    }

    private void Report(GuidedOptimizationRun run, string message)
    {
        run.ProgressLog.Add($"{DateTimeOffset.Now:HH:mm:ss} {message}");
        ProgressChanged?.Invoke(this, new GuidedOptimizationProgress
        {
            Status = run.Status,
            Message = message,
            Detail = message
        });
    }

    private void Fail(GuidedOptimizationRun run, string error)
    {
        run.Error = error;
        run.CompletedAt = DateTimeOffset.UtcNow;
        SetStatus(run, GuidedOptimizationStatus.Failed, error, 100);
    }

    private async Task<GuidedOptimizationRun> CancelledAsync(GuidedOptimizationRun run)
    {
        run.UserDecision = GuidedUserDecision.Cancelled;
        run.CompletedAt = DateTimeOffset.UtcNow;
        SetStatus(run, GuidedOptimizationStatus.Cancelled, "Guided run cancelled.", 100);
        return await FinishAsync(run).ConfigureAwait(false);
    }

    private async Task<GuidedOptimizationRun> FinishAsync(GuidedOptimizationRun run)
    {
        try
        {
            await _store.SaveAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError("Failed to persist guided run.", ex);
            run.Error = (run.Error is null ? "" : run.Error + " ") + "Save failed: " + ex.Message;
        }

        return run;
    }

    private bool IsCancelRequested()
    {
        lock (_gate) return _cancelRequested;
    }

    private async Task<bool> WaitForConfirmAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _confirmTcs = tcs;
        }

        await using var reg = cancellationToken.Register(() => tcs.TrySetResult(false));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<GuidedUserDecision> WaitForDecisionAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GuidedUserDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _decisionTcs = tcs;
        }

        await using var reg = cancellationToken.Register(() => tcs.TrySetResult(GuidedUserDecision.Cancelled));
        return await tcs.Task.ConfigureAwait(false);
    }

    private void ClearWaiters()
    {
        lock (_gate)
        {
            _confirmTcs = null;
            _decisionTcs = null;
        }
    }

    private static string BuildSevereMismatchMessage(BenchmarkConditionReport report)
    {
        var parts = report.Fields
            .Where(f => f.Status == ConditionMatchStatus.SevereMismatch)
            .Select(f => f.Message)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(report.Summary))
        {
            parts.Add(report.Summary);
        }

        var detail = parts.Count > 0 ? string.Join(" ", parts) : "Severe condition mismatch between baseline and post benchmarks.";
        return "Severe condition mismatch — comparison is not a valid performance proof. " + detail;
    }

    /// <summary>
    /// Restores the last FrameForge-managed changes after a condition-gate stop or user restore.
    /// Returns ok=false when restore explicitly failed; ok=true with restored=false when nothing to restore.
    /// </summary>
    private async Task<(bool ok, bool restored, string message)> TryRestoreAfterMismatchAsync(
        GuidedOptimizationRun run,
        CancellationToken cancellationToken)
    {
        try
        {
            // Prefer completing restore even if the user cancelled the wait — safety first.
            var token = cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            var result = await _settings.RestoreLastFrameForgeChangesAsync(token).ConfigureAwait(false);
            if (!result.Success)
            {
                return (false, false, result.Message ?? "Restore failed.");
            }

            run.RestoredSuccessfully = true;
            return (true, true, result.Message ?? "Previous configuration restored.");
        }
        catch (Exception ex)
        {
            _log.LogError("Restore after condition mismatch failed.", ex);
            return (false, false, ex.Message);
        }
    }

    private static BenchmarkConfiguration CloneBenchConfig(
        BenchmarkConfiguration source,
        string? profileId,
        string? profileName,
        string label) =>
        new()
        {
            DurationSeconds = source.DurationSeconds,
            WarmupSeconds = source.WarmupSeconds,
            SampleIntervalMs = source.SampleIntervalMs,
            ProfileId = profileId ?? source.ProfileId,
            ProfileName = profileName ?? source.ProfileName,
            Label = label
        };

    private static string BuildConfirmMessage(GuidedOptimizationRun run, SettingsDiff diff)
    {
        var files = string.Join(", ", run.AffectedFiles.Select(Path.GetFileName));
        if (string.IsNullOrWhiteSpace(files))
        {
            files = "(managed cfg + autoexec FRAMEFORGE section)";
        }

        var name = !string.IsNullOrWhiteSpace(run.CustomSetName)
            ? run.CustomSetName!
            : (!string.IsNullOrWhiteSpace(run.ProfileName) ? run.ProfileName! : "Custom selection");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Confirm apply — {name}");
        sb.AppendLine($"Settings to change: {diff.ChangeCount}");
        sb.AppendLine($"Files: {files}");
        sb.AppendLine("Backup: a full FrameForge backup will be created before any write.");
        var cfg = run.BenchmarkConfiguration;
        sb.AppendLine(
            $"Benchmark plan: baseline {cfg.DurationSeconds}s → apply → post {cfg.DurationSeconds}s " +
            $"(interval {cfg.SampleIntervalMs}ms, warm-up {cfg.WarmupSeconds}s).");
        sb.AppendLine("Risk: reversible via backup; FrameForge never injects into CS2.");
        sb.AppendLine("Changes:");
        var shown = 0;
        foreach (var c in diff.Entries.Where(e => e.IsChange).Take(12))
        {
            sb.AppendLine($"  • {c.DisplayName} ({c.ConfigKey}): '{c.CurrentValue ?? "—"}' → '{c.NewValue}' [risk: {c.Risk}]");
            shown++;
        }

        if (diff.ChangeCount > shown)
        {
            sb.AppendLine($"  … and {diff.ChangeCount - shown} more.");
        }

        sb.Append("Waiting for explicit confirmation…");
        return sb.ToString();
    }

    private static IEnumerable<GuidedConditionWarning> BuildConditionWarnings(BenchmarkRun before, BenchmarkRun after)
    {
        var a = before.SystemInformation;
        var b = after.SystemInformation;
        var cfgA = before.Configuration;
        var cfgB = after.Configuration;

        if (cfgA.DurationSeconds != cfgB.DurationSeconds ||
            cfgA.SampleIntervalMs != cfgB.SampleIntervalMs ||
            cfgA.WarmupSeconds != cfgB.WarmupSeconds)
        {
            yield return new GuidedConditionWarning
            {
                Code = "bench-config",
                Message = "Benchmark duration/interval/warm-up differs between runs."
            };
        }

        if (!string.Equals(a.CpuName, b.CpuName, StringComparison.OrdinalIgnoreCase))
        {
            yield return new GuidedConditionWarning { Code = "cpu", Message = $"CPU label changed: {a.CpuName} → {b.CpuName}" };
        }

        if (!string.Equals(a.GpuName, b.GpuName, StringComparison.OrdinalIgnoreCase))
        {
            yield return new GuidedConditionWarning { Code = "gpu", Message = $"GPU label changed: {a.GpuName} → {b.GpuName}" };
        }

        if (a.TotalRamBytes != b.TotalRamBytes && a.TotalRamBytes > 0 && b.TotalRamBytes > 0)
        {
            yield return new GuidedConditionWarning { Code = "ram", Message = "Reported RAM total differs between runs." };
        }

        if (!string.Equals(a.OsVersion, b.OsVersion, StringComparison.OrdinalIgnoreCase))
        {
            yield return new GuidedConditionWarning { Code = "os", Message = $"OS label changed: {a.OsVersion} → {b.OsVersion}" };
        }

        if (a.Cs2ProcessRunningAtStart != b.Cs2ProcessRunningAtStart)
        {
            yield return new GuidedConditionWarning
            {
                Code = "cs2-process",
                Message = "CS2 process presence at start differs between baseline and post runs."
            };
        }

        if (!string.Equals(a.ActiveProfileId, b.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
        {
            yield return new GuidedConditionWarning
            {
                Code = "profile",
                Message = $"Active profile metadata differs: {a.ActiveProfileId} → {b.ActiveProfileId}"
            };
        }

        if (a.PowerPlan is not null && b.PowerPlan is not null &&
            !string.Equals(a.PowerPlan, b.PowerPlan, StringComparison.OrdinalIgnoreCase))
        {
            yield return new GuidedConditionWarning { Code = "power", Message = $"Power plan changed: {a.PowerPlan} → {b.PowerPlan}" };
        }

        if (a.DisplayRefreshRateHz is not null && b.DisplayRefreshRateHz is not null &&
            a.DisplayRefreshRateHz != b.DisplayRefreshRateHz)
        {
            yield return new GuidedConditionWarning
            {
                Code = "refresh",
                Message = $"Display refresh differs: {a.DisplayRefreshRateHz} → {b.DisplayRefreshRateHz} Hz"
            };
        }

        if (a.DisplayResolution is not null && b.DisplayResolution is not null &&
            !string.Equals(a.DisplayResolution, b.DisplayResolution, StringComparison.OrdinalIgnoreCase))
        {
            yield return new GuidedConditionWarning
            {
                Code = "resolution",
                Message = $"Resolution differs: {a.DisplayResolution} → {b.DisplayResolution}"
            };
        }
    }

    private static IEnumerable<GuidedComparisonRow> BuildRows(BenchmarkComparison comparison)
    {
        foreach (var m in comparison.Metrics)
        {
            var row = new GuidedComparisonRow
            {
                Metric = m.Metric,
                Unit = m.Unit,
                Before = m.Before,
                After = m.After,
                AbsoluteDifference = m.Difference,
                PercentDifference = m.PercentDifference,
                IsAvailable = m.IsAvailable,
                PreferredDirection = "lower"
            };
            row = new GuidedComparisonRow
            {
                Metric = row.Metric,
                Unit = row.Unit,
                Before = row.Before,
                After = row.After,
                AbsoluteDifference = row.AbsoluteDifference,
                PercentDifference = row.PercentDifference,
                IsAvailable = row.IsAvailable,
                PreferredDirection = row.PreferredDirection,
                Interpretation = GuidedClassificationRules.InterpretRow(row)
            };
            yield return row;
        }
    }
}
