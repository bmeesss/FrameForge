using FrameForge.Core.Models;

namespace FrameForge.Core.Abstractions;

public interface IHardwareInfoService
{
    Task<HardwareInfo> GetHardwareInfoAsync(CancellationToken cancellationToken = default);
}

public interface ICs2DetectionService
{
    Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default);
}

public interface ICs2ConfigService
{
    Task<Cs2ConfigDocument> ReadAsync(string filePath, CancellationToken cancellationToken = default);
    Task WriteAsync(Cs2ConfigDocument document, CancellationToken cancellationToken = default);
    Task WriteValuesAsync(string filePath, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default);
    Cs2ConfigDocument Parse(string content, string? filePath = null);
    string Serialize(Cs2ConfigDocument document);
}

public interface IBackupService
{
    Task<BackupEntry> CreateBackupAsync(
        string description,
        IEnumerable<string> optimizationIds,
        IEnumerable<string> affectedFiles,
        IReadOnlyDictionary<string, string?> previousValues,
        string? profileId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupEntry?> GetBackupAsync(string backupId, CancellationToken cancellationToken = default);
    Task<BackupRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string backupId, CancellationToken cancellationToken = default);
}

public interface IOptimizationScoreService
{
    Task<OptimizationScore> CalculateAsync(CancellationToken cancellationToken = default);
}

public interface ICs2SettingCatalog
{
    IReadOnlyList<Cs2SettingDefinition> GetAll();
    Cs2SettingDefinition? GetById(string id);
    Cs2SettingDefinition? GetByConfigKey(string configKey);
    IReadOnlyList<Cs2SettingDefinition> GetByCategory(Cs2SettingCategory category);
}

public interface ICs2SettingsService
{
    Task<Cs2SettingsSnapshot> ReadSettingsAsync(CancellationToken cancellationToken = default);

    SettingsValidationResult ValidateSettings(IReadOnlyDictionary<string, string> values);

    SettingsDiff CreateDiff(
        Cs2SettingsSnapshot current,
        IReadOnlyDictionary<string, string> desired,
        string reason,
        string? profileId = null,
        string? profileName = null);

    Task<SettingsApplyResult> ApplySettingsAsync(
        IReadOnlyDictionary<string, string> desired,
        string reason,
        string? profileId = null,
        bool createBackup = true,
        CancellationToken cancellationToken = default);

    Task<SettingsApplyResult> ApplyDiffAsync(
        SettingsDiff diff,
        bool createBackup = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the most recent FrameForge settings backup (managed cfg + autoexec snapshots).
    /// </summary>
    Task<SettingsApplyResult> RestoreLastFrameForgeChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects whether CS2 will execute the managed config (autoexec FRAMEFORGE section).
    /// Does not modify Steam launch options.
    /// </summary>
    Task<Cs2SettingsSnapshot> DetectExecutionStatusAsync(CancellationToken cancellationToken = default);

    string ManagedConfigFileName { get; }
    string AutoexecFileName { get; }
}

public interface IProfileService
{
    Task<IReadOnlyList<PerformanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<PerformanceProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task SaveCustomProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default);
    Task DeleteCustomProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PerformanceProfile> DuplicateProfileAsync(string sourceProfileId, string? newName = null, CancellationToken cancellationToken = default);
    Task RenameCustomProfileAsync(string profileId, string newName, CancellationToken cancellationToken = default);
    Task ExportProfileAsync(string profileId, string destinationPath, CancellationToken cancellationToken = default);
    Task<ProfileImportResult> ImportProfileAsync(string sourcePath, CancellationToken cancellationToken = default);
    ProfileValidationResult ValidateProfile(PerformanceProfile profile);
    IReadOnlyList<string> BuiltInProfileIds { get; }
}

public interface IAppSettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public interface IAppLog
{
    void LogTrace(string message);
    void LogDebug(string message);
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}

public interface IOptimizationCatalog
{
    IReadOnlyList<IOptimization> GetAll();
    IOptimization? GetById(string id);
}

public interface IOptimizationPipeline
{
    Task<OptimizationPipelineResult> ExecuteAsync(
        IEnumerable<string> optimizationIds,
        CancellationToken cancellationToken = default);
}

public interface IDashboardService
{
    Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IPathService
{
    string AppDataDirectory { get; }
    string BackupsDirectory { get; }
    string LogsDirectory { get; }
    string SettingsFilePath { get; }
    string ProfilesDirectory { get; }
    string BackupMetadataPath { get; }
    string BenchmarksDirectory { get; }
}

/// <summary>
/// External CS2 process observation only (no memory read, no injection).
/// </summary>
public interface ICs2ProcessMonitor
{
    Cs2ProcessInfo? TryGetCs2Process();
}

/// <summary>
/// OS-level performance sampling. Never reads CS2 memory or hooks graphics APIs.
/// </summary>
public interface IPerformanceSampler
{
    void Reset();
    BenchmarkSample TakeSample(Cs2ProcessInfo? cs2, bool isWarmup, double elapsedMs);
}

/// <summary>Persists benchmark runs as local JSON only (no network).</summary>
public interface IBenchmarkStore
{
    Task<string> SaveAsync(BenchmarkRun run, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkRun>> ListAsync(CancellationToken cancellationToken = default);
    Task<BenchmarkRun?> GetAsync(string runId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string runId, CancellationToken cancellationToken = default);
    Task ExportJsonAsync(string runId, string destinationPath, CancellationToken cancellationToken = default);
    Task ExportCsvAsync(string runId, string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>Controlled external benchmark session engine.</summary>
public interface IBenchmarkEngine
{
    BenchmarkStatus Status { get; }
    BenchmarkRun? CurrentRun { get; }
    event EventHandler? StatusChanged;
    event EventHandler<BenchmarkSample>? SampleCaptured;

    Task<BenchmarkRun> StartAsync(BenchmarkConfiguration configuration, CancellationToken cancellationToken = default);
    void RequestStop();
    void RequestCancel();
    BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after);
}

/// <summary>Pure calculation helpers exposed for tests and the engine.</summary>
public interface IBenchmarkCalculator
{
    BenchmarkResult Calculate(IReadOnlyList<BenchmarkSample> samples);
    BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after);
}
