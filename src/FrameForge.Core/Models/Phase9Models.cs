namespace FrameForge.Core.Models;

/// <summary>
/// UI-facing benchmark phase derived from actual engine state + sample flags.
/// Never invented from guessed wall-clock alone.
/// </summary>
public enum BenchmarkUiPhase
{
    Idle,
    Preparing,
    Warmup,
    Benchmarking,
    Finishing,
    Analyzing,
    Completed,
    Failed,
    Cancelled,
    Stopping
}

/// <summary>
/// Observable progress from <see cref="IBenchmarkEngine"/> — values come from real samples/state.
/// </summary>
public sealed class BenchmarkProgress
{
    public BenchmarkStatus EngineStatus { get; init; } = BenchmarkStatus.Idle;
    public BenchmarkUiPhase Phase { get; init; } = BenchmarkUiPhase.Idle;
    public string Message { get; init; } = string.Empty;
    public double ElapsedSeconds { get; init; }
    public double? RemainingSeconds { get; init; }
    public int SamplesCollected { get; init; }
    public int WarmupSamples { get; init; }
    public int MeasuredSamples { get; init; }
    public int SampleIntervalMs { get; init; }
    public int PlannedDurationSeconds { get; init; }
    public int WarmupSeconds { get; init; }
    public bool IsWarmup { get; init; }
    public bool Cs2ProcessPresent { get; init; }
    public string Cs2ProcessStatus { get; init; } = "Unknown";
    public string? UnavailableNote { get; init; }

    /// <summary>
    /// 0–100 only when elapsed and planned duration are known from the active run.
    /// Null when not meaningful (idle / unknown).
    /// </summary>
    public double? ProgressPercent { get; init; }
}

/// <summary>Schema for intelligence export packages (local only, no PII).</summary>
public static class IntelligenceExportSchema
{
    public const int CurrentVersion = 1;
    public const string IntelligenceExtension = ".frameforge-intelligence.json";
    public const string SnapshotsExtension = ".frameforge-snapshots.json";
}

/// <summary>
/// Export package for performance intelligence. Must never contain PII fields.
/// </summary>
public sealed class IntelligenceExportPackage
{
    public int SchemaVersion { get; set; } = IntelligenceExportSchema.CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Notes { get; set; } =
        "FrameForge local intelligence export. No usernames, emails, IPs, MACs, serials, Steam IDs, or secrets.";
    public List<SystemFingerprint> Fingerprints { get; set; } = new();
    public List<SettingPerformanceRecord> Records { get; set; } = new();
    public List<SettingTestEvidence> Evidence { get; set; } = new();
}

/// <summary>Export package for per-key setting change snapshots.</summary>
public sealed class SnapshotExportPackage
{
    public int SchemaVersion { get; set; } = IntelligenceExportSchema.CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SettingChangeSnapshot> Snapshots { get; set; } = new();
}

public enum IntelligenceImportMode
{
    /// <summary>Show preview only — no writes.</summary>
    Preview,

    /// <summary>Append as new history entries (new ids if needed).</summary>
    ImportAsNew,

    /// <summary>Merge with existing by id/key+fingerprint where safe.</summary>
    Merge
}

public sealed class IntelligenceImportPreview
{
    public bool IsValid { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = new();
    public int RecordsToAdd { get; init; }
    public int RecordsToUpdate { get; init; }
    public int RecordsToSkip { get; init; }
    public int EvidenceToAdd { get; init; }
    public int SnapshotsToAdd { get; init; }
    public int Conflicts { get; init; }
    public List<string> ConflictDetails { get; init; } = new();
    public List<string> DifferentFingerprints { get; init; } = new();
    public IntelligenceExportPackage? Package { get; init; }
    public SnapshotExportPackage? SnapshotPackage { get; init; }
}

public sealed class IntelligenceImportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IntelligenceImportPreview? Preview { get; init; }

    public static IntelligenceImportResult Ok(string message, IntelligenceImportPreview preview) => new()
    {
        Success = true,
        Message = message,
        Preview = preview
    };

    public static IntelligenceImportResult Fail(string message, IntelligenceImportPreview? preview = null) => new()
    {
        Success = false,
        Message = message,
        Preview = preview
    };
}

/// <summary>Managed cfg file integrity snapshot (hash of FrameForge-managed content only).</summary>
public sealed class ManagedFileHashRecord
{
    public string FilePath { get; set; } = string.Empty;
    public string? ContentHashSha256 { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Hash of FRAMEFORGE BEGIN…END section only (autoexec).</summary>
    public string? ManagedSectionHashSha256 { get; set; }
    public long? FileLength { get; set; }
}

public sealed class ManagedFileHashStoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ManagedFileHashRecord> Files { get; set; } = new();
}

/// <summary>Notification that a FrameForge-managed file changed externally.</summary>
public sealed class ExternalConfigChangeEventArgs : EventArgs
{
    public string FilePath { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = "Changed";
    public bool ManagedSectionChanged { get; init; }
    public bool UserSectionOnly { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}
