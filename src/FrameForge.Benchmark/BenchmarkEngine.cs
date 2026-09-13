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

    public event EventHandler? StatusChanged;
    public event EventHandler<BenchmarkSample>? SampleCaptured;

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

        _log.LogInformation(
            $"Benchmark {run.Id} preparing (duration={configuration.DurationSeconds}s, interval={configuration.SampleIntervalMs}ms, warmup={configuration.WarmupSeconds}s).");

        try
        {
            Status = BenchmarkStatus.Running;
            run.Status = BenchmarkStatus.Running;
            await RunLoopAsync(run, configuration, linked.Token).ConfigureAwait(false);

            bool cancel;
            bool stop;
            lock (_gate)
            {
                cancel = _cancelRequested;
                stop = _stopRequested;
            }

            if (cancel)
            {
                run.Status = BenchmarkStatus.Cancelled;
                run.Error = "Cancelled by user.";
                run.EndedAt = DateTimeOffset.UtcNow;
                run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
                run.Result = _calculator.Calculate(run.Samples);
                Status = BenchmarkStatus.Cancelled;
            }
            else
            {
                Status = BenchmarkStatus.Stopping;
                run.Status = BenchmarkStatus.Stopping;
                run.EndedAt = DateTimeOffset.UtcNow;
                run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
                run.Result = _calculator.Calculate(run.Samples);
                if (stop)
                {
                    run.Result = CloneResultWithNote(
                        run.Result,
                        "Stopped early by user; aggregates use measured (non-warmup) samples only.");
                }

                run.Status = BenchmarkStatus.Completed;
                Status = BenchmarkStatus.Completed;
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
            run.Status = BenchmarkStatus.Cancelled;
            run.Error = "Cancelled.";
            run.EndedAt = DateTimeOffset.UtcNow;
            run.DurationSecondsActual = (run.EndedAt.Value - run.StartedAt).TotalSeconds;
            run.Result = _calculator.Calculate(run.Samples);
            Status = BenchmarkStatus.Cancelled;
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool stop;
            lock (_gate) { stop = _stopRequested; }
            if (stop)
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
        var (powerPlan, powerStatus) = TryDetectPowerPlan();

        return new BenchmarkSystemSnapshot
        {
            CpuName = hw.CpuName,
            CpuCoreCount = hw.CpuCoreCount,
            CpuThreadCount = hw.CpuThreadCount,
            GpuName = hw.GpuName,
            TotalRamBytes = hw.TotalRamBytes,
            OsVersion = hw.WindowsVersion,
            Architecture = hw.Architecture,
            PowerPlan = powerPlan,
            PowerPlanStatus = powerStatus,
            DisplayRefreshRateHz = null, // requires Win32 display APIs — left unavailable honestly
            DisplayResolution = null,
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

    private static (string? plan, string? status) TryDetectPowerPlan()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "Power plan detection is Windows-oriented; not available on this OS.");
        }

        // Avoid shelling out aggressively; report honest unavailable without powercfg dependency.
        return (null, "Power plan not queried in this build (safe default — no automatic powercfg).");
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
