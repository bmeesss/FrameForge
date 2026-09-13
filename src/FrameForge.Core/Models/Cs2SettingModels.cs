namespace FrameForge.Core.Models;

public enum Cs2SettingCategory
{
    Video,
    AdvancedVideo,
    Game,
    KeyboardMouse,
    Hud,
    Audio,
    Communication
}

public enum Cs2SettingValueKind
{
    Boolean,
    Integer,
    Decimal,
    Enumeration,
    String
}

public enum Cs2SettingSource
{
    /// <summary>Not present in any known cfg file — using definition default.</summary>
    Default,

    /// <summary>Value from FrameForge-managed cfg.</summary>
    FrameForgeManaged,

    /// <summary>Value from the user's autoexec.cfg.</summary>
    Autoexec,

    /// <summary>Value from another cfg under the CS2 cfg directory.</summary>
    UserConfig,

    /// <summary>Value supplied by an in-memory edit / pending apply.</summary>
    Pending,

    /// <summary>CS2 install or cfg directory unavailable.</summary>
    Unavailable
}

/// <summary>
/// Static definition of a supported CS2 configuration key.
/// Only documented, user-facing keys belong here.
/// </summary>
public sealed class Cs2SettingDefinition
{
    public string Id { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Cs2SettingCategory Category { get; init; }
    public string Description { get; init; } = string.Empty;
    public Cs2SettingValueKind ValueKind { get; init; } = Cs2SettingValueKind.String;
    public string? DefaultValue { get; init; }
    public string? RecommendedValue { get; init; }
    public IReadOnlyList<string>? AllowedValues { get; init; }
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public bool IsSupported { get; init; } = true;
    public bool RequiresRestart { get; init; }
    public string RiskNote { get; init; } = "Low";
}

/// <summary>
/// A setting definition paired with its resolved current value.
/// </summary>
public sealed class Cs2SettingValue
{
    public required Cs2SettingDefinition Definition { get; init; }
    public string? CurrentValue { get; init; }
    public string? RecommendedValue { get; init; }
    public Cs2SettingSource Source { get; init; } = Cs2SettingSource.Default;
    public bool IsSupported => Definition.IsSupported;
    public bool RequiresRestart => Definition.RequiresRestart;

    public string Id => Definition.Id;
    public string DisplayName => Definition.DisplayName;
    public Cs2SettingCategory Category => Definition.Category;
    public string Description => Definition.Description;
}

/// <summary>
/// Snapshot of all supported settings resolved from disk.
/// </summary>
public sealed class Cs2SettingsSnapshot
{
    public bool Cs2Available { get; init; }
    public string? CfgDirectory { get; init; }
    public string? ManagedConfigPath { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<Cs2SettingValue> Settings { get; init; } = Array.Empty<Cs2SettingValue>();
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string?> ToValueMap()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Settings)
        {
            map[s.Definition.ConfigKey] = s.CurrentValue;
        }

        return map;
    }
}

public sealed class SettingsValidationIssue
{
    public string SettingId { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; } = true;
}

public sealed class SettingsValidationResult
{
    public bool IsValid => Issues.All(i => !i.IsError);
    public IReadOnlyList<SettingsValidationIssue> Issues { get; init; } = Array.Empty<SettingsValidationIssue>();
}

public sealed class SettingsDiffEntry
{
    public string SettingId { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Cs2SettingCategory Category { get; init; }
    public string? CurrentValue { get; init; }
    public string? NewValue { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Risk { get; init; } = "Low";
    public bool RequiresRestart { get; init; }
    public bool IsChange => !string.Equals(CurrentValue, NewValue, StringComparison.Ordinal);
}

public sealed class SettingsDiff
{
    public string Title { get; init; } = "Settings change";
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public IReadOnlyList<SettingsDiffEntry> Entries { get; init; } = Array.Empty<SettingsDiffEntry>();
    public int ChangeCount => Entries.Count(e => e.IsChange);
    public bool HasChanges => ChangeCount > 0;
}

public sealed class SettingsApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? BackupId { get; init; }
    public bool RolledBack { get; init; }
    public SettingsDiff? Diff { get; init; }
    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    public static SettingsApplyResult Ok(string message, string? backupId, SettingsDiff diff, IReadOnlyList<string> files) => new()
    {
        Success = true,
        Message = message,
        BackupId = backupId,
        Diff = diff,
        WrittenFiles = files
    };

    public static SettingsApplyResult Fail(string message, string? backupId = null, bool rolledBack = false, SettingsDiff? diff = null) => new()
    {
        Success = false,
        Message = message,
        BackupId = backupId,
        RolledBack = rolledBack,
        Diff = diff
    };
}
