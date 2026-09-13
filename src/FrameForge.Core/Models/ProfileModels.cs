using System.Text.Json.Serialization;

namespace FrameForge.Core.Models;

/// <summary>
/// Current on-disk / export schema version for FrameForge profiles.
/// </summary>
public static class ProfileSchema
{
    public const int CurrentVersion = 1;
    public const string FileExtension = ".frameforge-profile.json";
}

/// <summary>
/// Data-driven performance profile (Competitive, Balanced, Quality, Custom).
/// </summary>
public sealed class PerformanceProfile
{
    /// <summary>Schema version for import/export compatibility.</summary>
    public int SchemaVersion { get; set; } = ProfileSchema.CurrentVersion;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public bool IsCustom { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Documented CS2 convar / config keys and values for this profile.
    /// Keys must match supported setting config keys.
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Back-compat alias used by older profile JSON files (cs2Settings).
    /// Not written on export — use Settings.
    /// </summary>
    [JsonPropertyName("cs2Settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Dictionary<string, string>? Cs2Settings
    {
        get => null; // never duplicate on write
        set
        {
            if (value is null || value.Count == 0)
            {
                return;
            }

            Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in value)
            {
                Settings[k] = v;
            }
        }
    }

    /// <summary>
    /// Optimization IDs recommended by this profile (optional advisory links).
    /// </summary>
    public List<string> RecommendedOptimizationIds { get; set; } = new();

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int SettingCount => Settings.Count;
}

public sealed class ProfileCatalog
{
    public IReadOnlyList<PerformanceProfile> Profiles { get; init; } = Array.Empty<PerformanceProfile>();
}

public sealed class ProfileValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public List<string> Issues { get; init; } = new();
}

public sealed class ProfileImportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public PerformanceProfile? Profile { get; init; }

    public static ProfileImportResult Ok(PerformanceProfile profile) => new()
    {
        Success = true,
        Message = $"Imported profile '{profile.Name}'.",
        Profile = profile
    };

    public static ProfileImportResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}
