using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Profiles;

/// <summary>
/// Loads data-driven profiles from JSON (built-in assets + user custom profiles).
/// </summary>
public sealed class ProfileService : IProfileService
{
    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly string? _builtInDirectory;

    public ProfileService(IPathService paths, IAppLog log, string? builtInDirectory = null)
    {
        _paths = paths;
        _log = log;
        _builtInDirectory = builtInDirectory ?? FindBuiltInDirectory();
        Directory.CreateDirectory(_paths.ProfilesDirectory);
        EnsureBuiltInDefaults();
    }

    public async Task<IReadOnlyList<PerformanceProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, PerformanceProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in GetEmbeddedDefaults())
        {
            map[profile.Id] = profile;
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

        var toSave = new PerformanceProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            IsBuiltIn = false,
            IsCustom = true,
            Cs2Settings = new Dictionary<string, string>(profile.Cs2Settings, StringComparer.OrdinalIgnoreCase),
            RecommendedOptimizationIds = profile.RecommendedOptimizationIds.ToList(),
            Metadata = new Dictionary<string, string>(profile.Metadata, StringComparer.OrdinalIgnoreCase)
        };

        var path = Path.Combine(_paths.ProfilesDirectory, $"{SanitizeFileName(toSave.Id)}.json");
        await FrameForgeJson.SerializeFileAsync(path, toSave, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Saved custom profile {toSave.Id}");
    }

    private void EnsureBuiltInDefaults()
    {
        foreach (var profile in GetEmbeddedDefaults())
        {
            var path = Path.Combine(_paths.ProfilesDirectory, $"{profile.Id}.json");
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

    /// <summary>
    /// Built-in profiles embedded so the app works even without asset copy.
    /// </summary>
    public static IReadOnlyList<PerformanceProfile> GetEmbeddedDefaults() =>
        new[]
        {
            new PerformanceProfile
            {
                Id = "competitive",
                Name = "Competitive",
                Description = "Prioritizes responsiveness and clarity for competitive play.",
                IsBuiltIn = true,
                Cs2Settings =
                {
                    ["fps_max"] = "0",
                    ["cl_hud_telemetry_frametime_show"] = "1",
                    ["cl_hud_telemetry_ping_show"] = "1"
                },
                RecommendedOptimizationIds =
                {
                    "cs2.cfg.competitive-telemetry",
                    "cs2.launch.options-hint",
                    "sys.power.high-performance-recommend"
                }
            },
            new PerformanceProfile
            {
                Id = "balanced",
                Name = "Balanced",
                Description = "Balanced mix of performance and visual comfort.",
                IsBuiltIn = true,
                Cs2Settings =
                {
                    ["fps_max"] = "400",
                    ["cl_hud_telemetry_frametime_show"] = "1"
                },
                RecommendedOptimizationIds =
                {
                    "cs2.cfg.balanced",
                    "cs2.launch.options-hint"
                }
            },
            new PerformanceProfile
            {
                Id = "quality",
                Name = "Quality",
                Description = "Favors smoother presentation with a moderate frame cap.",
                IsBuiltIn = true,
                Cs2Settings =
                {
                    ["fps_max"] = "200",
                    ["cl_hud_telemetry_frametime_show"] = "0"
                },
                RecommendedOptimizationIds =
                {
                    "cs2.cfg.quality"
                }
            },
            new PerformanceProfile
            {
                Id = "custom",
                Name = "Custom",
                Description = "Empty starting point for your own configuration profile.",
                IsBuiltIn = true,
                IsCustom = true,
                Cs2Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                RecommendedOptimizationIds = new List<string>()
            }
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
                // ignore invalid paths
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
