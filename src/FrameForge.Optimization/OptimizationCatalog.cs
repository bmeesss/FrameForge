using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.Optimization.Optimizations;

namespace FrameForge.Optimization;

/// <summary>
/// Registers built-in safe optimizations available to the pipeline and UI.
/// </summary>
public sealed class OptimizationCatalog : IOptimizationCatalog
{
    private readonly IReadOnlyList<IOptimization> _items;
    private readonly Dictionary<string, IOptimization> _byId;

    public OptimizationCatalog(
        ICs2ConfigService configService,
        ICs2DetectionService detectionService)
    {
        var competitiveSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Well-known, user-facing settings only — no undocumented keys.
            ["fps_max"] = "0",
            ["engine_low_latency_sleep_after_client_tick"] = "true",
            ["cl_hud_telemetry_frametime_show"] = "1",
            ["cl_hud_telemetry_ping_show"] = "1",
            ["cl_hud_telemetry_net_misdelivery_show"] = "1"
        };

        var balancedSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fps_max"] = "400",
            ["cl_hud_telemetry_frametime_show"] = "1",
            ["cl_hud_telemetry_ping_show"] = "1"
        };

        var qualitySettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fps_max"] = "200",
            ["cl_hud_telemetry_frametime_show"] = "0"
        };

        _items = new IOptimization[]
        {
            new RecommendHighPerformancePowerPlan(),
            new SafeCs2LaunchOptionsHint(),
            new Cs2ConfigValueOptimization(
                id: "cs2.cfg.competitive-telemetry",
                name: "Competitive Telemetry & Latency Hints",
                description: "Writes documented competitive-friendly cfg values (fps_max, telemetry HUD).",
                values: competitiveSettings,
                configService: configService,
                detectionService: detectionService,
                relativeCfgPath: "frameforge_competitive.cfg",
                risk: RiskLevel.Low,
                impact: ExpectedImpact.Medium),
            new Cs2ConfigValueOptimization(
                id: "cs2.cfg.balanced",
                name: "Balanced CS2 Config Preset",
                description: "Writes a balanced set of documented CS2 cfg values.",
                values: balancedSettings,
                configService: configService,
                detectionService: detectionService,
                relativeCfgPath: "frameforge_balanced.cfg",
                risk: RiskLevel.Low,
                impact: ExpectedImpact.Low),
            new Cs2ConfigValueOptimization(
                id: "cs2.cfg.quality",
                name: "Quality CS2 Config Preset",
                description: "Writes quality-oriented documented CS2 cfg values with a moderate fps cap.",
                values: qualitySettings,
                configService: configService,
                detectionService: detectionService,
                relativeCfgPath: "frameforge_quality.cfg",
                risk: RiskLevel.Low,
                impact: ExpectedImpact.Low)
        };

        _byId = _items.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IOptimization> GetAll() => _items;

    public IOptimization? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _byId.TryGetValue(id, out var opt) ? opt : null;
    }
}
