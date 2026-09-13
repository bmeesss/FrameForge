namespace FrameForge.Core.Models;

public enum OptimizationCategory
{
    System,
    Cs2Config,
    Graphics,
    Network,
    Storage,
    Profile
}

public enum RiskLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum ExpectedImpact
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum OptimizationStatus
{
    Unknown,
    Available,
    Applied,
    Failed,
    NotApplicable,
    Reverted
}

/// <summary>
/// Describes files or values that must be backed up before applying an optimization.
/// </summary>
public sealed class BackupRequirement
{
    public string Key { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public bool Required { get; init; } = true;
}

public sealed class OptimizationPreview
{
    public string OptimizationId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
    public RiskLevel RiskLevel { get; init; }
    public ExpectedImpact ExpectedImpact { get; init; }
    public bool CanApply { get; init; }
    public string? BlockReason { get; init; }
}

public sealed class OptimizationResult
{
    public string OptimizationId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? BackupId { get; init; }
    public IReadOnlyDictionary<string, string?> PreviousValues { get; init; }
        = new Dictionary<string, string?>();
    public IReadOnlyList<string> AffectedFiles { get; init; } = Array.Empty<string>();
    public Exception? Exception { get; init; }

    public static OptimizationResult Ok(string id, string message, string? backupId = null,
        IReadOnlyDictionary<string, string?>? previous = null,
        IReadOnlyList<string>? files = null) => new()
    {
        OptimizationId = id,
        Success = true,
        Message = message,
        BackupId = backupId,
        PreviousValues = previous ?? new Dictionary<string, string?>(),
        AffectedFiles = files ?? Array.Empty<string>()
    };

    public static OptimizationResult Fail(string id, string message, Exception? ex = null) => new()
    {
        OptimizationId = id,
        Success = false,
        Message = message,
        Exception = ex
    };
}

public sealed class PipelineStepResult
{
    public string StepName { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class OptimizationPipelineResult
{
    public bool Success { get; init; }
    public string? BackupId { get; init; }
    public IReadOnlyList<PipelineStepResult> Steps { get; init; } = Array.Empty<PipelineStepResult>();
    public IReadOnlyList<OptimizationResult> Applied { get; init; } = Array.Empty<OptimizationResult>();
    public string? ErrorMessage { get; init; }
    public bool RolledBack { get; init; }
}
