using FrameForge.Core.Models;

namespace FrameForge.Optimization.Optimizations;

/// <summary>
/// Advisory optimization describing legitimate Steam launch options.
/// Does not modify Steam files automatically in the foundation build.
/// </summary>
public sealed class SafeCs2LaunchOptionsHint : OptimizationBase
{
    public const string OptimizationId = "cs2.launch.options-hint";

    public override string Id => OptimizationId;
    public override string Name => "Recommended Steam Launch Options (Hint)";
    public override string Description =>
        "Suggests commonly used legitimate CS2 launch options such as -novid and -fullscreen. " +
        "Automatic Steam config editing is not performed in this foundation build.";
    public override OptimizationCategory Category => OptimizationCategory.Cs2Config;
    public override RiskLevel RiskLevel => RiskLevel.None;
    public override ExpectedImpact ExpectedImpact => ExpectedImpact.Low;

    public override Task<bool> CanApplyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public override Task<OptimizationPreview> ExplainAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreatePreview(
            canApply: false,
            summary: "Manual recommendation only.",
            changes: new[]
            {
                "Suggested options: -novid -fullscreen",
                "Apply via Steam → CS2 → Properties → Launch Options."
            },
            blockReason: "Automatic Steam launch-option editing is deferred. Apply manually if desired."));

    public override Task<OptimizationResult> ApplyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OptimizationResult.Fail(Id, "Not implemented: advisory only."));

    public override Task<OptimizationResult> RevertAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OptimizationResult.Fail(Id, "Nothing to revert."));
}
