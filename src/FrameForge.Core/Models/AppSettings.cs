namespace FrameForge.Core.Models;

public enum LogLevelSetting
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}

public enum ThemeSetting
{
    Dark,
    Light,
    System
}

/// <summary>
/// User-configurable application settings persisted as JSON.
/// </summary>
public sealed class AppSettings
{
    public bool AutomaticBackup { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public LogLevelSetting LoggingLevel { get; set; } = LogLevelSetting.Information;
    public ThemeSetting Theme { get; set; } = ThemeSetting.Dark;
    public string? ActiveProfileId { get; set; } = "balanced";
    public string? CustomCs2Path { get; set; }
    public string? CustomSteamPath { get; set; }

    /// <summary>
    /// Id of the most recent FrameForge-managed CS2 settings backup (for Reset).
    /// </summary>
    public string? LastSettingsBackupId { get; set; }

    public static AppSettings CreateDefault() => new();
}
