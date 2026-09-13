using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Read-only assessment facade over <see cref="ITargetedRestoreService"/>.
/// </summary>
public sealed class TargetedRestoreEvaluator : ITargetedRestoreEvaluator
{
    private readonly ITargetedRestoreService _service;

    public TargetedRestoreEvaluator(ITargetedRestoreService service)
    {
        _service = service;
    }

    public Task<TargetedRestoreAssessment> EvaluateAsync(
        string configKey,
        CancellationToken cancellationToken = default) =>
        _service.AssessAsync(configKey, cancellationToken);
}
