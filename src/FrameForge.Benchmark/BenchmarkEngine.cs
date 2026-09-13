using System.Diagnostics;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Benchmark;

/// <summary>
/// Controlled external benchmark session. Samples OS counters only.
/// Does not modify settings, inject into CS2, or hook graphics APIs.
/// </summary>
public sealed class BenchmarkEngine : IBenchmarkEngine
{
    private readonly IPerformanceSampler _sampler;
    private readonly ICs2ProcessMonitor _processMonitor;
    private readonly IHardwareInfoService _hardware;
    private readonly ICs2DetectionService _cs2Detection;
    private readonly ICs2SettingsService _cs2Settings;
    private readonly IProfileService _profiles;
    private readonly IAppSettingsService _appSettings;
    private readonly IBenchmarkStore _store;
    private readonly IBenchmarkCalculator _calculator;
    private readonly IAppLog _log;

    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private BenchmarkStatus _status = BenchmarkStatus.Idle;
    private BenchmarkRun? _current;
    private bool _stopRequested;
    private bool _cancelRequested;
    private BenchmarkProgress? _latestProgress;
    private bool _cs2ExitDetected;

    public BenchmarkEngine(
        IPerformanceSampler sampler,
        ICs2ProcessMonitor processMonitor,
        IHardwareInfoService hardware,
        ICs2DetectionService cs2Detection,
        ICs2SettingsService cs2Settings,
        IProfileService profiles,
        IAppSettingsService appSettings,
        IBenchmarkStore store,
        IBenchmarkCalculator calculator,
        IAppLog log)
    {
        _sampler = sampler;
        _processMonitor = processMonitor;
        _hardware = hardware;
        _cs2Detection = cs2Detection;
        _cs2Settings = cs2Settings;
        _profiles = profiles;
        _appSettings = appSettings;
        _store = store;
        _calculator = calculator;
        _log = log;
    }

    public BenchmarkStatus Status
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

    public BenchmarkRun? CurrentRun
    {
        get { lock (_gate) return _current; }
    }

    public BenchmarkProgress? LatestProgress
    {
        get { lock (_gate) return _latestProgress; }
    }

    public event EventHandler? StatusChanged;
    public event EventHandler<BenchmarkSample>? SampleCaptured;
    public event EventHandler<BenchmarkProgress>? ProgressChanged;

    public async Task<BenchmarkRun> StartAsync(
        BenchmarkConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = BenchmarkConfiguration.Validate(configuration);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Invalid benchmark configuration: " + string.Join("; ", validation.Issues));
        }

        lock (_gate)
        {
            if (_status is BenchmarkStatus.Preparing or BenchmarkStatus.Running or BenchmarkStatus.Stopping)
            {
                throw new InvalidOperationException($"Cannot start while status is {_status}.");
            }
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _runCts?.Dispose();
            _runCts = linked;
            _stopRequested = false;
            _cancelRequested = false;
            _cs2ExitDetected = false;
            _latestProgress = null;
        }

        Status = BenchmarkStatus.Preparing;
        _sampler.Reset();

        var run = new BenchmarkRun
        {
            Id = BenchmarkStore.CreateId(DateTimeOffset.Now),
            StartedAt = DateTimeOffset.UtcNow,
            Status = BenchmarkStatus.Preparing,
            Configuration = configuration,
            SystemInformation = await CaptureSystemSnapshotAsync(configuration, linked.Token)
                .ConfigureAwait(false),
            Samples = new List<BenchmarkSample>()
        };

        lock (_gate)
        {
            _current = run;
        }

        PublishProgress(run, configuration, BenchmarkUiPhase.Preparing, "Preparing benchmark…", 0, false, false);

        _log.LogInformation(
            $"Benchmark {run.Id} preparing (duration={configuration.DurationSeconds}s, interval={configuration.SampleIntervalMs}ms, warmup={configuration.WarmupSeconds}s).");

        try
        {
            Status = BenchmarkStatus.Running;
            run.Status = BenchmarkStatus.Running;
            await RunLoopAsync(run, configuration, linked.Token).ConfigureAwait(false);

            bool cancel;
            bool stop;
            bool cs2Exit;
            lock (_gate)
            {
                cancel = _cancelRequested;
                stop = _stopRequested;
                cs2Exit = _cs2ExitDetected;
            }

            if (cancel)
            {
                PublishProgress(run, configuration, BenchmarkUiPhase.Stopping, "Stopping benchmark…",
                    (DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds, false,
                    run.Samples.LastOrDefault()?.Cs2ProcessPresent ?? false);
                run.Status = BenchmarkStatus.Cancelled;
                run.Error = "Cancelled by user.";
                run.EndedAt = DateTimeOffset.UtcNow;
                run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
                PublishProgress(run, configuration, BenchmarkUiPhase.Analyzing, "Analyzing collected samples…",
                    run.DurationSecondsActual, false, false);
                run.Result = _calculator.Calculate(run.Samples);
                Status = BenchmarkStatus.Cancelled;
                PublishProgress(run, configuration, BenchmarkUiPhase.Cancelled, "Benchmark cancelled.",
                    run.DurationSecondsActual, false, false);
            }
            else if (cs2Exit)
            {
                Status = BenchmarkStatus.Stopping;
                run.Status = BenchmarkStatus.Stopping;
                run.EndedAt = DateTimeOffset.UtcNow;
                run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
                PublishProgress(run, configuration, BenchmarkUiPhase.Analyzing, "Analyzing partial samples…",
                    run.DurationSecondsActual, false, false);
                run.Result = _calculator.Calculate(run.Samples);
                run.Result = CloneResultWithNote(run.Result,
                    "CS2 exited during session. Partial samples only. FrameForge did not restart CS2.");
                run.Status = BenchmarkStatus.Failed;
                if (string.IsNullOrWhiteSpace(run.Error))
                {
                    run.Error = "CS2 process closed during benchmark.";
                }
                Status = BenchmarkStatus.Failed;
                PublishProgress(run, configuration, BenchmarkUiPhase.Failed, run.Error,
                    run.DurationSecondsActual, false, false);
            }
            else
            {
                Status = BenchmarkStatus.Stopping;
                run.Status = BenchmarkStatus.Stopping;
                PublishProgress(run, configuration, BenchmarkUiPhase.Finishing, "Finishing benchmark…",
                    (DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds, false,
                    run.Samples.LastOrDefault()?.Cs2ProcessPresent ?? false);
                run.EndedAt = DateTimeOffset.UtcNow;
                run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
                PublishProgress(run, configuration, BenchmarkUiPhase.Analyzing, "Analyzing result…",
                    run.DurationSecondsActual, false, false);
                run.Result = _calculator.Calculate(run.Samples);
                if (stop)
                {
                    run.Result = CloneResultWithNote(
                        run.Result,
                        "Stopped early by user; aggregates use measured (non-warmup) samples only.");
                }

                run.Status = BenchmarkStatus.Completed;
                Status = BenchmarkStatus.Completed;
                PublishProgress(run, configuration, BenchmarkUiPhase.Completed, "Benchmark completed.",
                    run.DurationSecondsActual, false,
                    run.Samples.LastOrDefault()?.Cs2ProcessPresent ?? false);
            }

            try
            {
                await _store.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError("Failed to persist benchmark run.", ex);
                run.Error = (run.Error is null ? "" : run.Error + " ") + "Save failed: " + ex.Message;
            }

            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _cancelRequested)
        {
            PublishProgress(run, configuration, BenchmarkUiPhase.Stopping, "Stopping benchmark…",
                (DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds, false,
                run.Samples.LastOrDefault()?.Cs2ProcessPresent ?? false);
            run.Status = BenchmarkStatus.Cancelled;
            run.Error = "Cancelled by user.";
            run.EndedAt = DateTimeOffset.UtcNow;
            run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
            run.Result = _calculator.Calculate(run.Samples);
            Status = BenchmarkStatus.Cancelled;
            PublishProgress(run, configuration, BenchmarkUiPhase.Cancelled, "Benchmark cancelled.",
                run.DurationSecondsActual, false, false);
            try { await _store.SaveAsync(run, CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            return run;
        }
        catch (Exception ex)
        {
            _log.LogError("Benchmark failed.", ex);
            run.Status = BenchmarkStatus.Failed;
            run.Error = ex.Message;
            run.EndedAt = DateTimeOffset.UtcNow;
            run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
            run.Result = _calculator.Calculate(run.Samples);
            Status = BenchmarkStatus.Failed;
            try { await _store.SaveAsync(run, CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            return run;
        }
        finally
        {
            lock (_gate)
            {
                _runCts?.Dispose();
                _runCts = null;
            }

            if (Status is not (BenchmarkStatus.Completed or BenchmarkStatus.Failed or BenchmarkStatus.Cancelled))
            {
                Status = BenchmarkStatus.Idle;
            }
        }
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
        }

        _log.LogInformation("Benchmark stop requested.");
    }

    public void RequestCancel()
    {
        lock (_gate)
        {
            _cancelRequested = true;
            try { _runCts?.Cancel(); } catch { /* ignore */ }
        }

        _log.LogInformation("Benchmark cancel requested.");
        var cur = CurrentRun;
        if (cur is not null)
        {
            PublishProgress(
                cur,
                cur.Configuration,
                BenchmarkUiPhase.Stopping,
                "Stopping benchmark…",
                cur.Samples.Count > 0 ? cur.Samples[^1].ElapsedMs / 1000.0 : 0,
                false,
                cur.Samples.LastOrDefault()?.Cs2ProcessPresent ?? false);
        }
    }

    public BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after) =>
        _calculator.Compare(before, after);

    private async Task RunLoopAsync(
        BenchmarkRun run,
        BenchmarkConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var totalMs = configuration.Duration.TotalMilliseconds;
        var warmupMs = configuration.Warmup.TotalMilliseconds;
        var interval = configuration.SampleInterval;
        var sawCs2 = run.SystemInformation.Cs2ProcessRunningAtStart;
        var missingCs2Streak = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool stop;
            bool cancel;
            lock (_gate)
            {
                stop = _stopRequested;
                cancel = _cancelRequested;
            }
            if (stop || cancel)
            {
                break;
            }

            var elapsed = sw.Elapsed.TotalMilliseconds;
            if (elapsed >= totalMs)
            {
                break;
            }

            var isWarmup = elapsed < warmupMs;
            Cs2ProcessInfo? cs2 = null;
            try
            {
                cs2 = _processMonitor.TryGetCs2Process();
            }
            catch (Exception ex)
            {
                _log.LogDebug($"CS2 process probe failed: {ex.Message}");
            }

            if (cs2 is not null)
            {
                sawCs2 = true;
                missingCs2Streak = 0;
            }
            else if (sawCs2)
            {
                missingCs2Streak++;
                // Require a few consecutive misses to avoid transient probe glitches
                if (missingCs2Streak >= 3)
                {
                    _cs2ExitDetected = true;
                    run.Error =
                        "CS2 process closed during benchmark. FrameForge does not restart CS2 automatically. Run stored as Failed.";
                    _log.LogWarning(run.Error);
                    PublishProgress(
                        run,
                        configuration,
                        BenchmarkUiPhase.Failed,
                        run.Error,
                        elapsed / 1000.0,
                        isWarmup,
                        false);
                    break;
                }
            }

            BenchmarkSample sample;
            try
            {
                sample = _sampler.TakeSample(cs2, isWarmup, elapsed);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Sample failed: {ex.Message}");
                sample = new BenchmarkSample
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    ElapsedMs = elapsed,
                    IsWarmup = isWarmup,
                    Cs2ProcessPresent = cs2 is not null,
                    Cs2ProcessId = cs2?.ProcessId
                };
            }

            run.Samples.Add(sample);
            SampleCaptured?.Invoke(this, sample);

            var phase = isWarmup ? BenchmarkUiPhase.Warmup : BenchmarkUiPhase.Benchmarking;
            var msg = isWarmup
                ? $"Warm-up sample {run.Samples.Count(s => s.IsWarmup)}…"
                : $"Benchmarking sample {run.Samples.Count(s => !s.IsWarmup)}…";
            PublishProgress(run, configuration, phase, msg, elapsed / 1000.0, isWarmup, cs2 is not null);

            var remaining = totalMs - sw.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            var delay = interval;
            if (delay.TotalMilliseconds > remaining)
            {
                delay = TimeSpan.FromMilliseconds(Math.Max(1, remaining));
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private void PublishProgress(
        BenchmarkRun run,
        BenchmarkConfiguration configuration,
        BenchmarkUiPhase phase,
        string message,
        double elapsedSeconds,
        bool isWarmup,
        bool cs2Present)
    {
        var total = configuration.DurationSeconds;
        var remaining = total > 0 ? Math.Max(0, total - elapsedSeconds) : (double?)null;
        double? pct = null;
        if (total > 0 && phase is not BenchmarkUiPhase.Idle and not BenchmarkUiPhase.Preparing)
        {
            pct = Math.Clamp(100.0 * elapsedSeconds / total, 0, 100);
        }

        var progress = new BenchmarkProgress
        {
            EngineStatus = Status,
            Phase = phase,
            Message = message,
            ElapsedSeconds = elapsedSeconds,
            RemainingSeconds = remaining,
            SamplesCollected = run.Samples.Count,
            WarmupSamples = run.Samples.Count(s => s.IsWarmup),
            MeasuredSamples = run.Samples.Count(s => !s.IsWarmup),
            SampleIntervalMs = configuration.SampleIntervalMs,
            PlannedDurationSeconds = configuration.DurationSeconds,
            WarmupSeconds = configuration.WarmupSeconds,
            IsWarmup = isWarmup,
            Cs2ProcessPresent = cs2Present,
            Cs2ProcessStatus = cs2Present ? "CS2 running" : (sawCs2Hint(run) ? "CS2 not detected" : "CS2 not running"),
            UnavailableNote =
                "Frame-time / GPU utilization: Unavailable (no injection / no graphics hook).",
            ProgressPercent = pct
        };

        lock (_gate)
        {
            _latestProgress = progress;
        }

        ProgressChanged?.Invoke(this, progress);
    }

    private static bool sawCs2Hint(BenchmarkRun run) =>
        run.SystemInformation.Cs2ProcessRunningAtStart ||
        run.Samples.Any(s => s.Cs2ProcessPresent);

    private async Task<BenchmarkSystemSnapshot> CaptureSystemSnapshotAsync(
        BenchmarkConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var hw = await _hardware.GetHardwareInfoAsync(cancellationToken).ConfigureAwait(false);
        var app = await _appSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profileId = configuration.ProfileId ?? app.ActiveProfileId;
        string? profileName = configuration.ProfileName;
        if (profileName is null && profileId is not null)
        {
            try
            {
                var p = await _profiles.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
                profileName = p?.Name;
            }
            catch
            {
                // ignore
            }
        }

        Dictionary<string, string> settingsMap = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var snap = await _cs2Settings.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var s in snap.Settings)
            {
                if (s.CurrentValue is not null)
                {
                    settingsMap[s.Definition.ConfigKey] = s.CurrentValue;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not snapshot FrameForge settings: {ex.Message}");
        }

        string? installPath = null;
        string? versionHint = null;
        try
        {
            var install = await _cs2Detection.DetectAsync(cancellationToken).ConfigureAwait(false);
            if (install.IsInstalled)
            {
                installPath = install.InstallPath;
                versionHint = install.GameInfoPath is not null
                    ? Path.GetFileName(install.GameInfoPath)
                    : install.DetectionMessage;
            }
        }
        catch
        {
            // ignore
        }

        var proc = _processMonitor.TryGetCs2Process();

        return new BenchmarkSystemSnapshot
        {
            CpuName = hw.CpuName,
            CpuCoreCount = hw.CpuCoreCount,
            CpuThreadCount = hw.CpuThreadCount,
            GpuName = hw.GpuName,
            GpuDetectionNotes = hw.GpuDetectionNotes,
            TotalRamBytes = hw.TotalRamBytes,
            OsVersion = hw.WindowsVersion,
            Architecture = hw.Architecture,
            PowerPlan = hw.PowerPlanName,
            PowerPlanStatus = hw.PowerPlanStatus,
            DisplayRefreshRateHz = hw.DisplayRefreshRateHz,
            DisplayResolution = hw.DisplayResolution,
            GameModeEnabled = hw.GameModeEnabled,
            Cs2InstallPath = installPath,
            Cs2VersionHint = versionHint,
            Cs2ProcessRunningAtStart = proc is not null,
            Cs2ProcessIdAtStart = proc?.ProcessId,
            Cs2ProcessStartTime = proc?.StartTime,
            ActiveProfileId = profileId,
            ActiveProfileName = profileName,
            ActiveFrameForgeSettings = settingsMap,
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static BenchmarkResult CloneResultWithNote(BenchmarkResult r, string extraNote) => new()
    {
        SystemCpu = r.SystemCpu,
        ProcessCpu = r.ProcessCpu,
        SystemMemory = r.SystemMemory,
        ProcessMemoryMb = r.ProcessMemoryMb,
        GpuUtilization = r.GpuUtilization,
        CpuTemperature = r.CpuTemperature,
        GpuTemperature = r.GpuTemperature,
        FrameTimeMs = r.FrameTimeMs,
        DerivedFps = r.DerivedFps,
        MeasuredSampleCount = r.MeasuredSampleCount,
        WarmupSampleCount = r.WarmupSampleCount,
        TotalSampleCount = r.TotalSampleCount,
        Cs2PresentSamplePercent = r.Cs2PresentSamplePercent,
        Notes = string.IsNullOrWhiteSpace(r.Notes) ? extraNote : r.Notes + " " + extraNote
    };
}
