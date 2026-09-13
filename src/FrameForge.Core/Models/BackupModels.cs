namespace FrameForge.Core.Models;

/// <summary>
/// Metadata for a single backup entry stored under Backups/.
/// </summary>
public sealed class BackupEntry
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string Description { get; init; } = string.Empty;
    public List<string> OptimizationIds { get; init; } = new();
    public List<string> AffectedFiles { get; init; } = new();
    public Dictionary<string, string?> PreviousValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ProfileId { get; init; }
    public Dictionary<string, string> FileSnapshots { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Root metadata.json document for the backup store.
/// </summary>
public sealed class BackupMetadataStore
{
    public int Version { get; set; } = 1;
    public List<BackupEntry> Backups { get; set; } = new();
}
