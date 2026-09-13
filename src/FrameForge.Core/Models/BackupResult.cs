namespace FrameForge.Core.Models;

public sealed class BackupRestoreResult
{
    public bool Success { get; init; }
    public string BackupId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> RestoredFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailedFiles { get; init; } = Array.Empty<string>();

    public static BackupRestoreResult Ok(string id, IReadOnlyList<string> restored, string? message = null) => new()
    {
        Success = true,
        BackupId = id,
        RestoredFiles = restored,
        Message = message ?? $"Restored {restored.Count} file(s)."
    };

    public static BackupRestoreResult Fail(string id, string message, IReadOnlyList<string>? restored = null, IReadOnlyList<string>? failed = null) => new()
    {
        Success = false,
        BackupId = id,
        Message = message,
        RestoredFiles = restored ?? Array.Empty<string>(),
        FailedFiles = failed ?? Array.Empty<string>()
    };
}
