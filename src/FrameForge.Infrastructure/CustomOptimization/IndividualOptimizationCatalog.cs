using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.CustomOptimization;

/// <summary>
/// User-facing catalog built from supported CS2 setting definitions.
/// No performance claims beyond qualitative expected-impact labels.
/// </summary>
public sealed class IndividualOptimizationCatalog : IIndividualOptimizationCatalog
{
    private readonly ICs2SettingCatalog _settings;

    public IndividualOptimizationCatalog(ICs2SettingCatalog settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<IndividualOptimizationItem> BuildCatalog(Cs2SettingsSnapshot? snapshot = null)
    {
        var byKey = snapshot?.Settings
            .ToDictionary(s => s.Definition.ConfigKey, s => s, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Cs2SettingValue>(StringComparer.OrdinalIgnoreCase);

        var list = new List<IndividualOptimizationItem>();
        foreach (var def in _settings.GetAll().Where(d => d.IsSupported))
        {
            byKey.TryGetValue(def.ConfigKey, out var live);
            var (impact, impactDesc) = IndividualOptimizationCatalogFilter.InferExpectedImpact(def);
            list.Add(new IndividualOptimizationItem
            {
                Id = def.Id,
                ConfigKey = def.ConfigKey,
                Name = def.DisplayName,
                SourceCategory = def.Category,
                UiCategory = IndividualOptimizationCatalogFilter.MapUiCategory(def.Category),
                Description = def.Description,
                CurrentValue = live?.CurrentValue ?? def.DefaultValue,
                RecommendedValue = def.RecommendedValue,
                DefaultValue = def.DefaultValue,
                PendingValue = null,
                Risk = IndividualOptimizationCatalogFilter.ParseRisk(def.RiskNote),
                ExpectedImpact = impact,
                ExpectedImpactDescription = impactDesc,
                Supported = def.IsSupported,
                RequiresRestart = def.RequiresRestart,
                ValueKind = def.ValueKind,
                AllowedValues = def.AllowedValues,
                MinValue = def.MinValue,
                MaxValue = def.MaxValue,
                Source = live?.Source ?? Cs2SettingSource.Default
            });
        }

        return list
            .OrderBy(i => i.UiCategory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<IndividualOptimizationItem> Filter(
        IEnumerable<IndividualOptimizationItem> items,
        IndividualOptimizationFilter filter) =>
        IndividualOptimizationCatalogFilter.Apply(items, filter);

    public IndividualOptimizationItem? GetByConfigKey(string configKey, Cs2SettingsSnapshot? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(configKey))
        {
            return null;
        }

        return BuildCatalog(snapshot)
            .FirstOrDefault(i => i.ConfigKey.Equals(configKey, StringComparison.OrdinalIgnoreCase));
    }
}
