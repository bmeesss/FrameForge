namespace FrameForge.Core.Models;

/// <summary>
/// Qualitative expected relevance of a setting — NOT a measured FPS gain.
/// </summary>
public enum OptimizationExpectedImpact
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// User-facing picker categories mapped from <see cref="Cs2SettingCategory"/>.
/// </summary>
public enum IndividualOptimizationUiCategory
{
    Performance,
    Hud,
    Mouse,
    Audio,
    Communication,
    Gameplay,
    Other
}

/// <summary>
/// Filter / search criteria for the in-memory individual optimization catalog.
/// Filtering never touches disk.
/// </summary>
public sealed class IndividualOptimizationFilter
{
    public string? SearchText { get; set; }
    public IndividualOptimizationUiCategory? Category { get; set; }
    public RiskLevel? Risk { get; set; }
    public bool OnlyChangedFromCurrent { get; set; }
    public bool OnlyDifferFromRecommended { get; set; }
    public bool OnlySelected { get; set; }
}

/// <summary>
/// User-facing catalog item derived from a supported <see cref="Cs2SettingDefinition"/>.
/// </summary>
public sealed class IndividualOptimizationItem
{
    public string Id { get; init; } = string.Empty;
    public string ConfigKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Cs2SettingCategory SourceCategory { get; init; }
    public IndividualOptimizationUiCategory UiCategory { get; init; }
    public string CategoryLabel => UiCategory.ToString();
    public string Description { get; init; } = string.Empty;
    public string? CurrentValue { get; set; }
    public string? RecommendedValue { get; init; }
    public string? DefaultValue { get; init; }
    public string? PendingValue { get; set; }
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public string RiskLabel => Risk.ToString();
    public OptimizationExpectedImpact ExpectedImpact { get; init; } = OptimizationExpectedImpact.Unknown;
    public string ExpectedImpactLabel => ExpectedImpact.ToString();
    /// <summary>Intended qualitative effect — never a guaranteed FPS number.</summary>
    public string ExpectedImpactDescription { get; init; } = string.Empty;
    public bool Supported { get; init; } = true;
    public bool RequiresRestart { get; init; }
    public Cs2SettingValueKind ValueKind { get; init; }
    public IReadOnlyList<string>? AllowedValues { get; init; }
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public Cs2SettingSource Source { get; set; } = Cs2SettingSource.Default;

    public bool IsChangedFromCurrent =>
        !string.IsNullOrWhiteSpace(PendingValue) &&
        !string.Equals(
            (CurrentValue ?? string.Empty).Trim(),
            PendingValue.Trim(),
            StringComparison.OrdinalIgnoreCase);

    public bool DiffersFromRecommended =>
        !string.IsNullOrWhiteSpace(RecommendedValue) &&
        !string.Equals(
            (CurrentValue ?? string.Empty).Trim(),
            RecommendedValue.Trim(),
            StringComparison.OrdinalIgnoreCase);

    public string EffectiveTargetValue =>
        !string.IsNullOrWhiteSpace(PendingValue)
            ? PendingValue.Trim()
            : (RecommendedValue ?? DefaultValue ?? string.Empty);
}

/// <summary>
/// Schema for persisted custom optimization sets (separate from built-in profiles).
/// </summary>
public static class CustomOptimizationSetSchema
{
    public const int CurrentVersion = 1;
    public const string FileExtension = ".frameforge-optset.json";
}

/// <summary>
/// A user-saved temporary/custom combination of settings.
/// Not a built-in profile; optional "Save as Profile" uses IProfileService.
/// </summary>
public sealed class CustomOptimizationSet
{
    public int SchemaVersion { get; set; } = CustomOptimizationSetSchema.CurrentVersion;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Config key → value. Keys must be supported catalog keys.</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int SettingCount => Settings?.Count ?? 0;

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({SettingCount})";
}

public sealed class CustomOptimizationSetValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public List<string> Issues { get; init; } = new();
}

public sealed class CustomOptimizationSetImportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public CustomOptimizationSet? Set { get; init; }

    public static CustomOptimizationSetImportResult Ok(CustomOptimizationSet set) => new()
    {
        Success = true,
        Message = $"Imported set '{set.Name}'.",
        Set = set
    };

    public static CustomOptimizationSetImportResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}

/// <summary>
/// In-memory helpers for catalog filtering (no I/O).
/// </summary>
public static class IndividualOptimizationCatalogFilter
{
    public static IReadOnlyList<IndividualOptimizationItem> Apply(
        IEnumerable<IndividualOptimizationItem> source,
        IndividualOptimizationFilter? filter)
    {
        filter ??= new IndividualOptimizationFilter();
        IEnumerable<IndividualOptimizationItem> q = source;

        if (filter.Category is { } cat)
        {
            q = q.Where(i => i.UiCategory == cat);
        }

        if (filter.Risk is { } risk)
        {
            q = q.Where(i => i.Risk == risk);
        }

        if (filter.OnlyChangedFromCurrent)
        {
            q = q.Where(i => i.IsChangedFromCurrent);
        }

        if (filter.OnlyDifferFromRecommended)
        {
            q = q.Where(i => i.DiffersFromRecommended);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var term = filter.SearchText.Trim();
            q = q.Where(i =>
                i.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.ConfigKey.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.CategoryLabel.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return q
            .OrderBy(i => i.UiCategory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IndividualOptimizationUiCategory MapUiCategory(Cs2SettingCategory category) =>
        category switch
        {
            Cs2SettingCategory.Video or Cs2SettingCategory.AdvancedVideo =>
                IndividualOptimizationUiCategory.Performance,
            Cs2SettingCategory.Hud => IndividualOptimizationUiCategory.Hud,
            Cs2SettingCategory.KeyboardMouse => IndividualOptimizationUiCategory.Mouse,
            Cs2SettingCategory.Audio => IndividualOptimizationUiCategory.Audio,
            Cs2SettingCategory.Communication => IndividualOptimizationUiCategory.Communication,
            Cs2SettingCategory.Game => IndividualOptimizationUiCategory.Gameplay,
            _ => IndividualOptimizationUiCategory.Other
        };

    public static RiskLevel ParseRisk(string? riskNote)
    {
        if (string.IsNullOrWhiteSpace(riskNote))
        {
            return RiskLevel.Low;
        }

        if (Enum.TryParse<RiskLevel>(riskNote.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        if (riskNote.Contains("high", StringComparison.OrdinalIgnoreCase))
        {
            return RiskLevel.High;
        }

        if (riskNote.Contains("medium", StringComparison.OrdinalIgnoreCase))
        {
            return RiskLevel.Medium;
        }

        return RiskLevel.Low;
    }

    /// <summary>
    /// Qualitative impact labels from category/description — never numeric FPS claims.
    /// </summary>
    public static (OptimizationExpectedImpact Impact, string Description) InferExpectedImpact(
        Cs2SettingDefinition def)
    {
        var ui = MapUiCategory(def.Category);
        return ui switch
        {
            IndividualOptimizationUiCategory.Performance =>
                (OptimizationExpectedImpact.Medium,
                    "May affect client frame budget or latency behaviour. Measured results come only from benchmarks."),
            IndividualOptimizationUiCategory.Mouse =>
                (OptimizationExpectedImpact.Low,
                    "Affects input feel; not a frame-rate optimization."),
            IndividualOptimizationUiCategory.Hud =>
                (OptimizationExpectedImpact.Low,
                    "Affects HUD visibility/clarity; typically negligible performance impact."),
            IndividualOptimizationUiCategory.Audio =>
                (OptimizationExpectedImpact.Low,
                    "Affects audio levels; not a frame-rate optimization."),
            IndividualOptimizationUiCategory.Communication =>
                (OptimizationExpectedImpact.Low,
                    "Affects chat/voice muting and name display."),
            IndividualOptimizationUiCategory.Gameplay =>
                (OptimizationExpectedImpact.Low,
                    "Affects gameplay UX preferences (buy menu, team ID, matchmaking ping preference)."),
            _ => (OptimizationExpectedImpact.Unknown,
                    "Intended effect is configuration convenience; measure with the benchmark engine.")
        };
    }
}
