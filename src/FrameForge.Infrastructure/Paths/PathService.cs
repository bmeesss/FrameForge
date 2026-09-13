using FrameForge.Core.Abstractions;

namespace FrameForge.Infrastructure.Paths;

/// <summary>
/// Resolves FrameForge local data directories under the user profile.
/// </summary>
public sealed class PathService : IPathService
{
    public PathService(string? rootOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride))
        {
            AppDataDirectory = Path.GetFullPath(rootOverride);
        }
        else
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
            }

            AppDataDirectory = Path.Combine(baseDir, "FrameForge");
        }

        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(BenchmarksDirectory);
        Directory.CreateDirectory(GuidedRunsDirectory);
        Directory.CreateDirectory(CustomOptimizationSetsDirectory);
        Directory.CreateDirectory(PerformanceHistoryDirectory);
    }

    public string AppDataDirectory { get; }
    public string BackupsDirectory => Path.Combine(AppDataDirectory, "Backups");
    public string LogsDirectory => Path.Combine(AppDataDirectory, "Logs");
    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
    public string ProfilesDirectory => Path.Combine(AppDataDirectory, "Profiles");
    public string BackupMetadataPath => Path.Combine(BackupsDirectory, "metadata.json");
    public string BenchmarksDirectory => Path.Combine(AppDataDirectory, "Benchmarks");
    public string GuidedRunsDirectory => Path.Combine(AppDataDirectory, "GuidedRuns");
    public string CustomOptimizationSetsDirectory => Path.Combine(AppDataDirectory, "CustomOptimizationSets");
    public string PerformanceHistoryDirectory => Path.Combine(AppDataDirectory, "PerformanceHistory");
}
