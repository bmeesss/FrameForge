namespace FrameForge.Core.Models;

/// <summary>
/// Optimization readiness score derived only from detected conditions.
/// Does not estimate FPS or invent performance gains.
/// </summary>
public sealed class OptimizationScore
{
    public int Score { get; init; }
    public int MaxScore { get; init; } = 100;
    public IReadOnlyList<ScoreFactor> Factors { get; init; } = Array.Empty<ScoreFactor>();
    public string Summary { get; init; } = string.Empty;

    public double Percentage => MaxScore <= 0 ? 0 : (double)Score / MaxScore * 100.0;
}

public sealed class ScoreFactor
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Points { get; init; }
    public bool Awarded { get; init; }
    public string Reason { get; init; } = string.Empty;
}
