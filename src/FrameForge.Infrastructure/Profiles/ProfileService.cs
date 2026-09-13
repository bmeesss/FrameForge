using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;
using FrameForge.CS2.Settings;

namespace FrameForge.Infrastructure.Profiles;

/// <summary>
/// Loads/saves data-driven profiles with schema versioning, built-in protection,
/// and JSON import/export (.frameforge-profile.json).
/// </summary>
public sealed class ProfileService : IProfileService
{
    public const int SupportedSchemaVersion = ProfileSchema.CurrentVersion;

    private static readonly string[] BuiltInIds =
    [
        "competitive",
        "balanced",
        "quality"
    ];

    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly ICs2SettingCatalog? _settingCatalog;
    private readonly string? _builtInDirectory;

    public ProfileService(
        IPathService paths,
        IAppLog log,
        ICs2SettingCatalog? settingCatalog = null,
        string? builtInDirectory = null)
    {
        _paths = paths;
        _log = log;
        _settingCatalog = settingCatalog;
        _builtInDirectory = builtInDirectory ?? FindBuiltInDirectory();
        Directory.CreateDirectory(_paths.ProfilesDirectory);
        EnsureBuiltInDefaults();
    }

    public IReadOnlyList<string> BuiltInProfileIds => BuiltInIds;

    public async Task<IReadOnlyList<PerformanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, PerformanceProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in GetEmbeddedDefaults())
        {
            map[profile.Id] = Clone(profile);
        }

        foreach (var dir in new[] { _builtInDirectory, _paths.ProfilesDirectory })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var profile = await FrameForgeJson.DeserializeFileAsync<PerformanceProfile>(file, cancellationToken)
                        .ConfigureAwait(false);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
                    {
                        continue;
                    }

                    NormalizeLoadedProfile(profile);

                    // Never let on-disk copies overwrite built-in protection flags for reserved ids
                    if (IsBuiltInId(profile.Id))
                    {
                        var embedded = GetEmbeddedDefaults().First(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
                        profile = Clone(embedded);
                    }

                    map[profile.Id] = profile;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Failed to load profile '{file}': {ex.Message}");
                }
            }
        }

        return map.Values
            .OrderBy(p => p.IsCustom)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PerformanceProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var all = await GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(p => p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SaveCustomProfileAsync(PerformanceProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new ArgumentException("Profile Id is required.", nameof(profile));
        }

        if (IsBuiltInId(profile.Id))
        {
            throw new InvalidOperationException($"Built-in profile '{profile.Id}' cannot be overwritten.");
        }

        var validation = ValidateProfile(profile);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Profile validation failed: " + string.Join("; ", validation.Issues));
        }

        var now = DateTimeOffset.UtcNow;
        var toSave = Clone(profile);
        toSave.IsBuiltIn = false;
        toSave.IsCustom = true;
        toSave.SchemaVersion = SupportedSchemaVersion;
        toSave.UpdatedAt = now;
        if (toSave.CreatedAt == default)
        {
            toSave.CreatedAt = now;
        }

        // Ensure case-insensitive settings dictionary
        toSave.Settings = new Dictionary<string, string>(toSave.Settings, StringComparer.OrdinalIgnoreCase);

        var path = Path.Combine(_paths.ProfilesDirectory, $"{SanitizeFileName(toSave.Id)}.json");
        await FrameForgeJson.SerializeFileAsync(path, toSave, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Saved custom profile {toSave.Id}");
    }

    public async Task DeleteCustomProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile id is required.", nameof(profileId));
        }

        if (IsBuiltInId(profileId))
        {
            throw new InvalidOperationException($"Built-in profile '{profileId}' cannot be deleted.");
        }

        var path = Path.Combine(_paths.ProfilesDirectory, $"{SanitizeFileName(profileId)}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
            _log.LogInformation($"Deleted custom profile {profileId}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<PerformanceProfile> DuplicateProfileAsync(
        string sourceProfileId,
        string? newName = null,
        CancellationToken cancellationToken = default)
    {
        var source = await GetProfileAsync(sourceProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Profile '{sourceProfileId}' was not found.");

        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        var copy = Clone(source);
        copy.Id = id;
        copy.Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} Copy" : newName.Trim();
        copy.IsBuiltIn = false;
        copy.IsCustom = true;
        copy.SchemaVersion = SupportedSchemaVersion;
        copy.CreatedAt = DateTimeOffset.UtcNow;
        copy.UpdatedAt = copy.CreatedAt;

        await SaveCustomProfileAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy;
    }

    public async Task RenameCustomProfileAsync(string profileId, string newName, CancellationToken cancellationToken = default)
    {
        if (IsBuiltInId(profileId))
        {
            throw new InvalidOperationException("Built-in profiles cannot be renamed.");
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Name is required.", nameof(newName));
        }

        var profile = await GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Profile '{profileId}' was not found.");

        profile.Name = newName.Trim();
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveCustomProfileAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportProfileAsync(string profileId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var profile = await GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Profile '{profileId}' was not found.");

        var export = Clone(profile);
        export.SchemaVersion = SupportedSchemaVersion;
        // Exported built-ins become templates (still marked built-in for info, but import will re-id if needed)
        await FrameForgeJson.SerializeFileAsync(destinationPath, export, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Exported profile {profileId} to {destinationPath}");
    }

    public async Task<ProfileImportResult> ImportProfileAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return ProfileImportResult.Fail("Import file was not found.");
        }

        PerformanceProfile? profile;
        try
        {
            profile = await FrameForgeJson.DeserializeFileAsync<PerformanceProfile>(sourcePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ProfileImportResult.Fail($"Invalid JSON: {ex.Message}");
        }

        if (profile is null)
        {
            return ProfileImportResult.Fail("Profile file was empty or invalid.");
        }

        NormalizeLoadedProfile(profile);

        if (profile.SchemaVersion <= 0)
        {
            profile.SchemaVersion = 1; // tolerate legacy files without schemaVersion
        }

        if (profile.SchemaVersion > SupportedSchemaVersion)
        {
            return ProfileImportResult.Fail(
                $"Unsupported schema version {profile.SchemaVersion}. This app supports up to {SupportedSchemaVersion}.");
        }

        var validation = ValidateProfile(profile);
        if (!validation.IsValid)
        {
            return ProfileImportResult.Fail("Invalid profile: " + string.Join("; ", validation.Issues));
        }

        // Never import over a built-in id
        if (string.IsNullOrWhiteSpace(profile.Id) || IsBuiltInId(profile.Id))
        {
            profile.Id = "imported-" + Guid.NewGuid().ToString("N")[..8];
        }

        // Ensure unique id
        var existing = await GetProfileAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            profile.Id = "imported-" + Guid.NewGuid().ToString("N")[..8];
        }

        profile.IsBuiltIn = false;
        profile.IsCustom = true;
        profile.SchemaVersion = SupportedSchemaVersion;
        profile.CreatedAt = DateTimeOffset.UtcNow;
        profile.UpdatedAt = profile.CreatedAt;

        try
        {
            await SaveCustomProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ProfileImportResult.Fail(ex.Message);
        }

        return ProfileImportResult.Ok(profile);
    }

    public ProfileValidationResult ValidateProfile(PerformanceProfile profile)
    {
        var result = new ProfileValidationResult();
        if (profile is null)
        {
            result.Issues.Add("Profile is null.");
            return result;
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            result.Issues.Add("Profile name is required.");
        }

        if (profile.Name?.Length > 80)
        {
            result.Issues.Add("Profile name is too long.");
        }

        if (profile.SchemaVersion > SupportedSchemaVersion)
        {
            result.Issues.Add($"Schema version {profile.SchemaVersion} is not supported.");
        }

        if (profile.Settings is null)
        {
            result.Issues.Add("Settings dictionary is required.");
            return result;
        }

        foreach (var (key, value) in profile.Settings)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Issues.Add("Settings contains an empty key.");
                continue;
            }

            if (value is null)
            {
                result.Issues.Add($"Setting '{key}' has a null value.");
                continue;
            }

            if (_settingCatalog is not null)
            {
                var def = _settingCatalog.GetByConfigKey(key) ?? _settingCatalog.GetById(key);
                if (def is null)
                {
                    result.Issues.Add($"Unknown setting key '{key}'.");
                    continue;
                }

                if (!def.IsSupported)
                {
                    result.Issues.Add($"Setting '{key}' is not supported.");
                    continue;
                }

                if (!Cs2SettingsService.IsValidValue(def, value.Trim(), out var reason))
                {
                    result.Issues.Add($"Invalid value for '{key}': {reason}");
                }
            }
        }

        return result;
    }

    private void EnsureBuiltInDefaults()
    {
        // Seed read-only copies under profiles dir for discoverability, but GetProfiles always prefers embedded.
        foreach (var profile in GetEmbeddedDefaults())
        {
            var path = Path.Combine(_paths.ProfilesDirectory, $"{profile.Id}.builtin.json");
            if (!File.Exists(path))
            {
                try
                {
                    File.WriteAllText(path, FrameForgeJson.Serialize(profile));
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Could not seed profile {profile.Id}: {ex.Message}");
                }
            }
        }
    }

    public static IReadOnlyList<PerformanceProfile> GetEmbeddedDefaults()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        return new[]
        {
            new PerformanceProfile
            {
                SchemaVersion = SupportedSchemaVersion,
                Id = "competitive",
                Name = "Competitive",
                Description =
                    "Prioritizes competitive clarity and a lower client-side rendering workload. " +
                    "Does not claim a specific FPS gain. Enables useful HUD telemetry and uncapped fps_max.",
                IsBuiltIn = true,
                IsCustom = false,
                CreatedAt = now,
                UpdatedAt = now,
                Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fps_max"] = "0",
                    ["fps_max_ui"] = "120",
                    ["engine_low_latency_sleep_after_client_tick"] = "true",
                    ["cl_hud_telemetry_frametime_show"] = "1",
                    ["cl_hud_telemetry_ping_show"] = "1",
                    ["cl_hud_telemetry_net_misdelivery_show"] = "1",
                    ["cl_teamid_overhead_always"] = "1",
                    ["cl_use_opens_buy_menu"] = "0",
                    ["mm_dedicated_search_maxping"] = "80",
                    ["m_rawinput"] = "1",
                    ["m_customaccel"] = "0",
                    ["snd_menumusic_volume"] = "0",
                    ["volume"] = "0.7"
                },
                RecommendedOptimizationIds =
                {
                    "cs2.cfg.competitive-telemetry",
                    "cs2.launch.options-hint",
                    "sys.power.high-performance-recommend"
                },
                Metadata = { ["audience"] = "competitive" }
            },
            new PerformanceProfile
            {
                SchemaVersion = SupportedSchemaVersion,
                Id = "balanced",
                Name = "Balanced",
                Description =
                    "Balances visual comfort and performance with a moderate frame cap and essential telemetry.",
                IsBuiltIn = true,
                IsCustom = false,
                CreatedAt = now,
                UpdatedAt = now,
                Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fps_max"] = "400",
                    ["fps_max_ui"] = "200",
                    ["engine_low_latency_sleep_after_client_tick"] = "false",
                    ["cl_hud_telemetry_frametime_show"] = "1",
                    ["cl_hud_telemetry_ping_show"] = "1",
                    ["cl_hud_telemetry_net_misdelivery_show"] = "0",
                    ["cl_teamid_overhead_always"] = "0",
                    ["cl_use_opens_buy_menu"] = "1",
                    ["mm_dedicated_search_maxping"] = "120",
                    ["m_rawinput"] = "1",
                    ["m_customaccel"] = "0",
                    ["snd_menumusic_volume"] = "0.3",
                    ["volume"] = "0.8"
                },
                RecommendedOptimizationIds =
                {
                    "cs2.cfg.balanced",
                    "cs2.launch.options-hint"
                },
                Metadata = { ["audience"] = "general" }
            },
            new PerformanceProfile
            {
                SchemaVersion = SupportedSchemaVersion,
                Id = "quality",
                Name = "Quality",
                Description =
                    "Favors smoother presentation and higher UI frame budget with a conservative fps_max.",
                IsBuiltIn = true,
                IsCustom = false,
                CreatedAt = now,
                UpdatedAt = now,
                Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fps_max"] = "200",
                    ["fps_max_ui"] = "200",
                    ["engine_low_latency_sleep_after_client_tick"] = "false",
                    ["cl_hud_telemetry_frametime_show"] = "0",
                    ["cl_hud_telemetry_ping_show"] = "0",
                    ["cl_hud_telemetry_net_misdelivery_show"] = "0",
                    ["cl_teamid_overhead_always"] = "0",
                    ["cl_use_opens_buy_menu"] = "1",
                    ["mm_dedicated_search_maxping"] = "150",
                    ["m_rawinput"] = "1",
                    ["m_customaccel"] = "0",
                    ["snd_menumusic_volume"] = "0.5",
                    ["volume"] = "1.0"
                },
                RecommendedOptimizationIds =
                {
                    "cs2.cfg.quality"
                },
                Metadata = { ["audience"] = "quality" }
            }
        };
    }

    private static void NormalizeLoadedProfile(PerformanceProfile profile)
    {
        // Migrate legacy Cs2Settings-only payloads: System.Text.Json maps both to Settings via property.
        profile.Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile.Settings.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            profile.Settings = new Dictionary<string, string>(profile.Settings, StringComparer.OrdinalIgnoreCase);
        }

        if (profile.SchemaVersion <= 0)
        {
            profile.SchemaVersion = 1;
        }
    }

    private static bool IsBuiltInId(string id) =>
        BuiltInIds.Any(b => b.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static PerformanceProfile Clone(PerformanceProfile p) => new()
    {
        SchemaVersion = p.SchemaVersion,
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        IsBuiltIn = p.IsBuiltIn,
        IsCustom = p.IsCustom,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        Settings = new Dictionary<string, string>(p.Settings ?? new(), StringComparer.OrdinalIgnoreCase),
        RecommendedOptimizationIds = p.RecommendedOptimizationIds?.ToList() ?? new List<string>(),
        Metadata = new Dictionary<string, string>(p.Metadata ?? new(), StringComparer.OrdinalIgnoreCase)
    };

    private static string? FindBuiltInDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Profiles"),
            Path.Combine(AppContext.BaseDirectory, "assets", "profiles"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "profiles"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "profiles")
        };

        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (Directory.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string SanitizeFileName(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
