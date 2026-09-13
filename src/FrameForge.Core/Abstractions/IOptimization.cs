using FrameForge.Core.Models;

namespace FrameForge.Core.Abstractions;

/// <summary>
/// Contract for a single reversible optimization.
/// Implementations must never touch game memory, inject DLLs, or bypass anti-cheat.
/// </summary>
public interface IOptimization
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    OptimizationCategory Category { get; }
    RiskLevel RiskLevel { get; }
    ExpectedImpact ExpectedImpact { get; }
    IReadOnlyList<BackupRequirement> BackupRequirements { get; }

    Task<bool> CanApplyAsync(CancellationToken cancellationToken = default);
    Task<OptimizationPreview> ExplainAsync(CancellationToken cancellationToken = default);
    Task<OptimizationResult> ApplyAsync(CancellationToken cancellationToken = default);
    Task<OptimizationResult> RevertAsync(CancellationToken cancellationToken = default);
}
