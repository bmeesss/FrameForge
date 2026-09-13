using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;
using FrameForge.CS2.Settings;

namespace FrameForge.Infrastructure.CustomOptimization;

/// <summary>
/// Local JSON persistence for custom optimization sets.
/// Separate from built-in/custom profiles; never executes imported data.
/// </summary>
public sealed class CustomOptimizationSetStore : ICustomOptimizationSetStore
{
    public const int SupportedSchemaVersion = CustomOptimizationSetSchema.CurrentVersion;

    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly ICs2SettingCatalog _catalog;

    public CustomOptimizationSetStore(
        IPathService paths,
        IAppLog log,
        ICs2SettingCatalog catalog)
    {
        _paths = paths;
        _log = log;
        _catalog = catalog;
        Directory.CreateDirectory(_paths.CustomOptimizationSetsDirectory);
    }

    public async Task<IReadOnlyList<CustomOptimizationSet>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<CustomOptimizationSet>();
        if (!Directory.Exists(_paths.CustomOptimizationSetsDirectory))
        {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.CustomOptimizationSetsDirectory, "*.json")
                     .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var set = await FrameForgeJson.DeserializeFileAsync<CustomOptimizationSet>(file, cancellationToken)
                    .ConfigureAwait(false);
                if (set is null || string.IsNullOrWhiteSpace(set.Id))
                {
                    continue;
                }

                Normalize(set);
                list.Add(set);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Skipping corrupt custom optimization set '{file}': {ex.Message}");
            }
        }

        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<CustomOptimizationSet?> GetAsync(string setId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(setId))
        {
            return null;
        }

        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(s => s.Id.Equals(setId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> SaveAsync(CustomOptimizationSet set, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (string.IsNullOrWhiteSpace(set.Id))
        {
            set.Id = "set-" + Guid.NewGuid().ToString("N")[..10];
        }

        Normalize(set);
        var validation = Validate(set);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Custom optimization set validation failed: " + string.Join("; ", validation.Issues));
        }

        var now = DateTimeOffset.UtcNow;
        set.SchemaVersion = SupportedSchemaVersion;
        set.UpdatedAt = now;
        if (set.CreatedAt == default)
        {
            set.CreatedAt = now;
        }

        var path = GetPath(set.Id);
        await FrameForgeJson.SerializeFileAsync(path, set, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Saved custom optimization set {set.Id}");
        return path;
    }

    public async Task DeleteAsync(string setId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(setId))
        {
            throw new ArgumentException("Set id is required.", nameof(setId));
        }

        var path = GetPath(setId);
        if (File.Exists(path))
        {
            File.Delete(path);
            _log.LogInformation($"Deleted custom optimization set {setId}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<CustomOptimizationSet> DuplicateAsync(
        string setId,
        string? newName = null,
        CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(setId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Custom set '{setId}' was not found.");

        var copy = Clone(source);
        copy.Id = "set-" + Guid.NewGuid().ToString("N")[..10];
        copy.Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} Copy" : newName.Trim();
        copy.CreatedAt = DateTimeOffset.UtcNow;
        copy.UpdatedAt = copy.CreatedAt;
        await SaveAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy;
    }

    public async Task RenameAsync(string setId, string newName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Name is required.", nameof(newName));
        }

        var set = await GetAsync(setId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Custom set '{setId}' was not found.");

        set.Name = newName.Trim();
        set.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(set, cancellationToken).ConfigureAwait(false);
    }

    public CustomOptimizationSetValidationResult Validate(CustomOptimizationSet set)
    {
        var result = new CustomOptimizationSetValidationResult();
        if (set is null)
        {
            result.Issues.Add("Set is null.");
            return result;
        }

        if (string.IsNullOrWhiteSpace(set.Name))
        {
            result.Issues.Add("Name is required.");
        }

        if (set.Name?.Length > 80)
        {
            result.Issues.Add("Name is too long (max 80).");
        }

        if (set.SchemaVersion > SupportedSchemaVersion)
        {
            result.Issues.Add($"Schema version {set.SchemaVersion} is not supported.");
        }

        if (set.Settings is null)
        {
            result.Issues.Add("Settings dictionary is required.");
            return result;
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in set.Settings)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                result.Issues.Add("Settings contains an empty key.");
                continue;
            }

            if (!seenKeys.Add(key))
            {
                result.Issues.Add($"Duplicate setting key '{key}'.");
                continue;
            }

            if (value is null)
            {
                result.Issues.Add($"Setting '{key}' has a null value.");
                continue;
            }

            var def = _catalog.GetByConfigKey(key) ?? _catalog.GetById(key);
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

        return result;
    }

    public async Task<CustomOptimizationSetImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return CustomOptimizationSetImportResult.Fail("Import file was not found.");
        }

        CustomOptimizationSet? set;
        try
        {
            set = await FrameForgeJson.DeserializeFileAsync<CustomOptimizationSet>(sourcePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CustomOptimizationSetImportResult.Fail($"Invalid JSON: {ex.Message}");
        }

        if (set is null)
        {
            return CustomOptimizationSetImportResult.Fail("Set file was empty or invalid.");
        }

        Normalize(set);

        if (set.SchemaVersion <= 0)
        {
            set.SchemaVersion = 1;
        }

        if (set.SchemaVersion > SupportedSchemaVersion)
        {
            return CustomOptimizationSetImportResult.Fail(
                $"Unsupported schema version {set.SchemaVersion}. This app supports up to {SupportedSchemaVersion}.");
        }

        var validation = Validate(set);
        if (!validation.IsValid)
        {
            return CustomOptimizationSetImportResult.Fail(
                "Invalid set: " + string.Join("; ", validation.Issues));
        }

        if (string.IsNullOrWhiteSpace(set.Id) ||
            await GetAsync(set.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            set.Id = "imported-" + Guid.NewGuid().ToString("N")[..8];
        }

        set.SchemaVersion = SupportedSchemaVersion;
        set.CreatedAt = DateTimeOffset.UtcNow;
        set.UpdatedAt = set.CreatedAt;

        try
        {
            await SaveAsync(set, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CustomOptimizationSetImportResult.Fail(ex.Message);
        }

        return CustomOptimizationSetImportResult.Ok(set);
    }

    public async Task ExportAsync(string setId, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var set = await GetAsync(setId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Custom set '{setId}' was not found.");

        var export = Clone(set);
        export.SchemaVersion = SupportedSchemaVersion;
        await FrameForgeJson.SerializeFileAsync(destinationPath, export, cancellationToken).ConfigureAwait(false);
        _log.LogInformation($"Exported custom set {setId} to {destinationPath}");
    }

    private string GetPath(string id) =>
        Path.Combine(_paths.CustomOptimizationSetsDirectory, Sanitize(id) + ".json");

    private static void Normalize(CustomOptimizationSet set)
    {
        set.Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (set.Settings.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            set.Settings = new Dictionary<string, string>(set.Settings, StringComparer.OrdinalIgnoreCase);
        }

        if (set.SchemaVersion <= 0)
        {
            set.SchemaVersion = 1;
        }
    }

    private static CustomOptimizationSet Clone(CustomOptimizationSet s) => new()
    {
        SchemaVersion = s.SchemaVersion,
        Id = s.Id,
        Name = s.Name,
        Description = s.Description,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        Settings = new Dictionary<string, string>(s.Settings ?? new(), StringComparer.OrdinalIgnoreCase)
    };

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id;
    }
}
