namespace FrameForge.Core.Models;

/// <summary>
/// Result of legitimate Steam library CS2 detection.
/// </summary>
public sealed record Cs2InstallInfo
{
    public bool IsInstalled { get; init; }
    public string? InstallPath { get; init; }
    public string? SteamLibraryPath { get; init; }
    public string? GameInfoPath { get; init; }
    public string? CfgDirectory { get; init; }
    public string DetectionMessage { get; init; } = "Not detected";
    public IReadOnlyList<string> SearchedLibraries { get; init; } = Array.Empty<string>();

    public static Cs2InstallInfo NotFound(string message, IReadOnlyList<string>? searched = null) => new()
    {
        IsInstalled = false,
        DetectionMessage = message,
        SearchedLibraries = searched ?? Array.Empty<string>()
    };
}
