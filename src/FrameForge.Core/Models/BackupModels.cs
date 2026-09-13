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

    /// <summary>
    /// Paths that did not exist when the backup was taken. Restore deletes these files
    /// so a newly created managed cfg / autoexec returns to the prior absence state.
    /// </summary>
    public List<string> CreatedFiles { get; init; } = new();

    /// <summary>
    /// Per-key change metadata captured at backup time (Phase 7).
    /// Does not replace full file snapshots; enables future targeted-restore assessment.
    /// </summary>
    public List<SettingChangeSnapshot> SettingChangeSnapshots { get; init; } = new();
}


/// <summary>
/// Root metadata.json document for the backup store.
/// </summary>
public sealed class BackupMetadataStore
{
    public int Version { get; set; } = 1;
    public List<BackupEntry> Backups { get; set; } = new();
}
