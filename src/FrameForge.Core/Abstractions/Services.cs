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

public interface IProfileService
{
    Task<IReadOnlyList<PerformanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
    Task<PerformanceProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task SaveCustomProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default);
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
}
