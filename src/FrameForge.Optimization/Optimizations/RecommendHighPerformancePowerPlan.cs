using FrameForge.Core.Models;

namespace FrameForge.Optimization.Optimizations;

/// <summary>
/// Safe advisory optimization: recommends enabling the OS high-performance power plan.
/// Does NOT modify the registry or call powercfg yet — foundation exposes preview only.
/// </summary>
public sealed class RecommendHighPerformancePowerPlan : OptimizationBase
{
    public const string OptimizationId = "sys.power.high-performance-recommend";

    public override string Id => OptimizationId;
    public override string Name => "High Performance Power Plan (Recommendation)";
    public override string Description =>
        "Recommends switching Windows to the High Performance power plan for lower latency. " +
        "Automatic application is deferred; this entry is advisory until a safe apply path is implemented.";
    public override OptimizationCategory Category => OptimizationCategory.System;
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override ExpectedImpact ExpectedImpact => ExpectedImpact.Medium;

    public override Task<bool> CanApplyAsync(CancellationToken cancellationToken = default)
    {
        // Advisory only for MVP foundation — cannot auto-apply yet.
        return Task.FromResult(false);
    }

    public override Task<OptimizationPreview> ExplainAsync(CancellationToken cancellationToken = default)
    {
        var preview = CreatePreview(
            canApply: false,
            summary: "Advisory recommendation. Automatic power plan changes are not enabled in this foundation build.",
            changes: new[]
            {
                "Would select the Windows High Performance power plan via powercfg (not executed yet).",
                "Would restore the previous plan on revert."
            },
            blockReason: "Automatic power plan switching is intentionally disabled until a safe Windows-only implementation ships. Follow the manual steps in the description.");

        return Task.FromResult(preview);
    }

    public override Task<OptimizationResult> ApplyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OptimizationResult.Fail(Id, "Not implemented: power plan switching is deferred for safety."));

    public override Task<OptimizationResult> RevertAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OptimizationResult.Fail(Id, "Nothing to revert."));
}
