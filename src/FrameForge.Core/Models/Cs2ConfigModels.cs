namespace FrameForge.Core.Models;

/// <summary>
/// A single key/value pair from a CS2 configuration file (cfg / convar style).
/// </summary>
public sealed class Cs2ConfigEntry
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Comment { get; init; }
    public bool IsCommentOnly { get; init; }
    public string RawLine { get; init; } = string.Empty;
}

/// <summary>
/// Parsed representation of a CS2 .cfg file.
/// </summary>
public sealed class Cs2ConfigDocument
{
    public string FilePath { get; init; } = string.Empty;
    public List<Cs2ConfigEntry> Entries { get; init; } = new();

    public IDictionary<string, string> ToDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (entry.IsCommentOnly || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            dict[entry.Key] = entry.Value;
        }

        return dict;
    }
}
