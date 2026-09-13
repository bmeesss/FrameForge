using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Optimization;

/// <summary>
/// Shared base for reversible optimizations.
/// </summary>
public abstract class OptimizationBase : IOptimization
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract OptimizationCategory Category { get; }
    public abstract RiskLevel RiskLevel { get; }
    public abstract ExpectedImpact ExpectedImpact { get; }
    public virtual IReadOnlyList<BackupRequirement> BackupRequirements { get; } = Array.Empty<BackupRequirement>();

    public abstract Task<bool> CanApplyAsync(CancellationToken cancellationToken = default);
    public abstract Task<OptimizationPreview> ExplainAsync(CancellationToken cancellationToken = default);
    public abstract Task<OptimizationResult> ApplyAsync(CancellationToken cancellationToken = default);
    public abstract Task<OptimizationResult> RevertAsync(CancellationToken cancellationToken = default);

    protected OptimizationPreview CreatePreview(
        bool canApply,
        string summary,
        IEnumerable<string>? changes = null,
        string? blockReason = null) =>
        new()
        {
            OptimizationId = Id,
            Name = Name,
            Summary = summary,
            Changes = changes?.ToList() ?? new List<string>(),
            RiskLevel = RiskLevel,
            ExpectedImpact = ExpectedImpact,
            CanApply = canApply,
            BlockReason = blockReason
        };
}
