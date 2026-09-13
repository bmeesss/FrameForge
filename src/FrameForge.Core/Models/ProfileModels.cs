namespace FrameForge.Core.Models;

/// <summary>
/// Data-driven performance profile (Competitive, Balanced, Quality, Custom).
/// </summary>
public sealed class PerformanceProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsBuiltIn { get; init; }
    public bool IsCustom { get; init; }

    /// <summary>
    /// Documented CS2 convar / config keys and values for this profile.
    /// Only well-known, user-facing configuration keys should appear here.
    /// </summary>
    public Dictionary<string, string> Cs2Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optimization IDs recommended by this profile.
    /// </summary>
    public List<string> RecommendedOptimizationIds { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileCatalog
{
    public IReadOnlyList<PerformanceProfile> Profiles { get; init; } = Array.Empty<PerformanceProfile>();
}
