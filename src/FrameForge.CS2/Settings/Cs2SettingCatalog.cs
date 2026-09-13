using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.CS2.Settings;

/// <summary>
/// Catalog of documented, user-facing CS2 configuration keys supported by FrameForge.
/// Video quality sliders that live only in binary/video.txt are intentionally excluded
/// until a safe documented format is available.
/// </summary>
public sealed class Cs2SettingCatalog : ICs2SettingCatalog
{
    private readonly IReadOnlyList<Cs2SettingDefinition> _all;
    private readonly Dictionary<string, Cs2SettingDefinition> _byId;
    private readonly Dictionary<string, Cs2SettingDefinition> _byKey;

    public Cs2SettingCatalog()
    {
        _all = BuildDefinitions();
        _byId = _all.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _byKey = _all.ToDictionary(d => d.ConfigKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Cs2SettingDefinition> GetAll() => _all;

    public Cs2SettingDefinition? GetById(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _byId.GetValueOrDefault(id);

    public Cs2SettingDefinition? GetByConfigKey(string configKey) =>
        string.IsNullOrWhiteSpace(configKey) ? null : _byKey.GetValueOrDefault(configKey);

    public IReadOnlyList<Cs2SettingDefinition> GetByCategory(Cs2SettingCategory category) =>
        _all.Where(d => d.Category == category).ToList();

    private static IReadOnlyList<Cs2SettingDefinition> BuildDefinitions() =>
        new List<Cs2SettingDefinition>
        {
            // ── Video / performance (cfg-backed) ──────────────────────────
            Def("video.fps_max", "fps_max", "FPS Limit", Cs2SettingCategory.Video,
                "Maximum frames per second. 0 removes the software cap (display/GPU may still limit).",
                Cs2SettingValueKind.Integer, "400", "0", min: 0, max: 1000,
                risk: "Low"),

            Def("video.fps_max_ui", "fps_max_ui", "UI FPS Limit", Cs2SettingCategory.Video,
                "Frame limit while in menus / UI. Lower values can reduce idle GPU load.",
                Cs2SettingValueKind.Integer, "200", "120", min: 0, max: 1000),

            Def("video.engine_low_latency", "engine_low_latency_sleep_after_client_tick",
                "Low Latency Sleep After Client Tick", Cs2SettingCategory.AdvancedVideo,
                "When true, the engine may sleep after the client tick to reduce input latency on high-refresh setups.",
                Cs2SettingValueKind.Boolean, "false", "true",
                allowed: BoolValues),

            // ── HUD / telemetry ───────────────────────────────────────────
            Def("hud.telemetry_frametime", "cl_hud_telemetry_frametime_show",
                "Show Frametime Telemetry", Cs2SettingCategory.Hud,
                "Displays frametime information on the HUD telemetry panel.",
                Cs2SettingValueKind.Enumeration, "0", "1",
                allowed: new[] { "0", "1" }),

            Def("hud.telemetry_ping", "cl_hud_telemetry_ping_show",
                "Show Ping Telemetry", Cs2SettingCategory.Hud,
                "Displays ping on the HUD telemetry panel.",
                Cs2SettingValueKind.Enumeration, "0", "1",
                allowed: new[] { "0", "1" }),

            Def("hud.telemetry_misdelivery", "cl_hud_telemetry_net_misdelivery_show",
                "Show Net Misdelivery Telemetry", Cs2SettingCategory.Hud,
                "Displays network misdelivery indicators on the HUD telemetry panel.",
                Cs2SettingValueKind.Enumeration, "0", "1",
                allowed: new[] { "0", "1" }),

            Def("hud.show_loadout", "cl_showloadout",
                "Show Loadout", Cs2SettingCategory.Hud,
                "Shows the weapon loadout HUD element.",
                Cs2SettingValueKind.Enumeration, "1", "1",
                allowed: new[] { "0", "1" }),

            Def("hud.radar_scale", "cl_hud_radar_scale",
                "Radar Scale", Cs2SettingCategory.Hud,
                "Scales the radar HUD element.",
                Cs2SettingValueKind.Decimal, "1", "1", min: 0.5, max: 1.3),

            Def("hud.color", "cl_hud_color",
                "HUD Color", Cs2SettingCategory.Hud,
                "HUD accent color index (game-defined palette).",
                Cs2SettingValueKind.Integer, "0", "0", min: 0, max: 12),

            // ── Game ─────────────────────────────────────────────────────
            Def("game.autowait", "cl_auto_cursor_defend",
                "Auto Cursor Defend", Cs2SettingCategory.Game,
                "Optional cursor assist toggle when supported by the client build. Leave default if unsure.",
                Cs2SettingValueKind.Enumeration, "0", "0",
                allowed: new[] { "0", "1" },
                supported: false), // keep definition for schema stability but mark unsupported if uncertain

            Def("game.joyscale", "joystick",
                "Joystick Enabled", Cs2SettingCategory.Game,
                "Enables joystick input. Competitive players typically leave this off.",
                Cs2SettingValueKind.Enumeration, "0", "0",
                allowed: new[] { "0", "1" }),

            Def("game.cl_vsnd_priority", "cl_sniper_delay_zoom_crosshair_color",
                "Sniper Zoom Crosshair Color Delay", Cs2SettingCategory.Game,
                "Placeholder reserved — not applied. Excluded from supported writes.",
                Cs2SettingValueKind.String, null, null,
                supported: false),

            // Re-add only well-known game toggles:
            Def("game.cl_teamid_overhead", "cl_teamid_overhead_always",
                "Always Show Team ID Overhead", Cs2SettingCategory.Game,
                "Always display teammate identification overhead markers.",
                Cs2SettingValueKind.Enumeration, "0", "1",
                allowed: new[] { "0", "1" }),

            Def("game.cl_use_opens_buy_menu", "cl_use_opens_buy_menu",
                "Use Opens Buy Menu", Cs2SettingCategory.Game,
                "Whether the use key can open the buy menu.",
                Cs2SettingValueKind.Enumeration, "1", "0",
                allowed: new[] { "0", "1" }),

            Def("game.mm_dedicated_search", "mm_dedicated_search_maxping",
                "Max Acceptable Matchmaking Ping", Cs2SettingCategory.Game,
                "Maximum preferred ping when searching for matchmaking servers.",
                Cs2SettingValueKind.Integer, "150", "80", min: 20, max: 350),

            // ── Keyboard / mouse ──────────────────────────────────────────
            Def("input.sensitivity", "sensitivity",
                "Mouse Sensitivity", Cs2SettingCategory.KeyboardMouse,
                "In-game mouse sensitivity multiplier.",
                Cs2SettingValueKind.Decimal, "1.0", null, min: 0.01, max: 20),

            Def("input.zoom_sensitivity", "zoom_sensitivity_ratio",
                "Zoom Sensitivity Ratio", Cs2SettingCategory.KeyboardMouse,
                "Sensitivity ratio while scoped / zoomed.",
                Cs2SettingValueKind.Decimal, "1.0", "1.0", min: 0.01, max: 5),

            Def("input.m_rawinput", "m_rawinput",
                "Raw Mouse Input", Cs2SettingCategory.KeyboardMouse,
                "Uses raw mouse input when enabled (1).",
                Cs2SettingValueKind.Enumeration, "1", "1",
                allowed: new[] { "0", "1" }),

            Def("input.m_customaccel", "m_customaccel",
                "Mouse Custom Acceleration", Cs2SettingCategory.KeyboardMouse,
                "Mouse acceleration mode. 0 disables custom acceleration.",
                Cs2SettingValueKind.Integer, "0", "0", min: 0, max: 3),

            // ── Audio ────────────────────────────────────────────────────
            Def("audio.volume", "volume",
                "Master Volume", Cs2SettingCategory.Audio,
                "Master audio volume (0.0 – 1.0).",
                Cs2SettingValueKind.Decimal, "1.0", "0.7", min: 0, max: 1),

            Def("audio.snd_voipvolume", "snd_voipvolume",
                "Voice Volume", Cs2SettingCategory.Audio,
                "Volume level for player voice communication.",
                Cs2SettingValueKind.Decimal, "1.0", "0.7", min: 0, max: 1),

            Def("audio.snd_headphone_eq", "snd_headphone_eq",
                "Headphone EQ", Cs2SettingCategory.Audio,
                "Headphone EQ preset index when supported.",
                Cs2SettingValueKind.Integer, "0", "1", min: 0, max: 3),

            Def("audio.snd_musicvolume", "snd_musicvolume_multiplier",
                "Music Volume Multiplier", Cs2SettingCategory.Audio,
                "Scales in-game music volume.",
                Cs2SettingValueKind.Decimal, "1.0", "0", min: 0, max: 1),

            // ── Communication ────────────────────────────────────────────
            Def("comm.cl_mute_enemy_team", "cl_mute_enemy_team",
                "Mute Enemy Team", Cs2SettingCategory.Communication,
                "Mutes text/voice from the enemy team when enabled.",
                Cs2SettingValueKind.Enumeration, "0", "0",
                allowed: new[] { "0", "1" }),

            Def("comm.cl_mute_all_but_friends", "cl_mute_all_but_friends_and_party",
                "Mute All But Friends And Party", Cs2SettingCategory.Communication,
                "Mutes players outside friends and party.",
                Cs2SettingValueKind.Enumeration, "0", "0",
                allowed: new[] { "0", "1" }),

            Def("comm.cl_sanitize_player_names", "cl_sanitize_player_names",
                "Sanitize Player Names", Cs2SettingCategory.Communication,
                "Replaces potentially offensive player names in the UI.",
                Cs2SettingValueKind.Enumeration, "0", "0",
                allowed: new[] { "0", "1" }),
        }
        .Where(d => d.IsSupported)
        .ToList();

    private static readonly string[] BoolValues = ["false", "true", "0", "1"];

    private static Cs2SettingDefinition Def(
        string id,
        string key,
        string name,
        Cs2SettingCategory category,
        string description,
        Cs2SettingValueKind kind,
        string? defaultValue,
        string? recommended,
        IReadOnlyList<string>? allowed = null,
        double? min = null,
        double? max = null,
        bool requiresRestart = false,
        bool supported = true,
        string risk = "Low") =>
        new()
        {
            Id = id,
            ConfigKey = key,
            DisplayName = name,
            Category = category,
            Description = description,
            ValueKind = kind,
            DefaultValue = defaultValue,
            RecommendedValue = recommended,
            AllowedValues = allowed,
            MinValue = min,
            MaxValue = max,
            RequiresRestart = requiresRestart,
            IsSupported = supported,
            RiskNote = risk
        };
}
