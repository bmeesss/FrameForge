namespace FrameForge.Core.Models;

/// <summary>
/// Diagnostics for the internal transaction recovery of a configuration write.
///
/// The recovery snapshot is separate from the user-visible backup: it is created for
/// every apply (even with <c>AutomaticBackup</c> disabled) and removed again after a
/// verified success. When it fails, the apply is a hard failure and must be reported as
/// such — the CS2 cfg files may be in a partially modified state.
/// </summary>
public sealed class ApplyRecoveryInfo
{
    public static ApplyRecoveryInfo None { get; } = new();

    /// <summary>A recovery snapshot was created before the first mutation.</summary>
    public bool SnapshotCreated { get; init; }

    public string? SnapshotId { get; init; }

    /// <summary>Restoring the snapshot was attempted (apply failed).</summary>
    public bool RestoreAttempted { get; init; }

    /// <summary>Every tracked file was restored and verified byte-for-byte.</summary>
    public bool RestoreSucceeded { get; init; }

    /// <summary>The temporary snapshot directory was removed after a verified success.</summary>
    public bool CleanedUp { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> RestoredFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FailedFiles { get; init; } = Array.Empty<string>();

    /// <summary>True when recovery was attempted and did not fully succeed.</summary>
    public bool RestoreFailed => RestoreAttempted && !RestoreSucceeded;

    public override string ToString() =>
        !SnapshotCreated
            ? "recovery: none"
            : $"recovery: snapshot={SnapshotId}, restoreAttempted={RestoreAttempted}, " +
              $"restoreSucceeded={RestoreSucceeded}, cleanedUp={CleanedUp}";
}
