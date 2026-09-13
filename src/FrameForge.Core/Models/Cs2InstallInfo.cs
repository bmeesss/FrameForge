namespace FrameForge.Core.Models;

/// <summary>
/// Result of legitimate Steam library CS2 detection.
/// </summary>
public sealed record Cs2InstallInfo
{
    public bool IsInstalled { get; init; }
    public string? InstallPath { get; init; }
    public string? SteamLibraryPath { get; init; }
    public string? SteamRootPath { get; init; }
    public string? GameInfoPath { get; init; }
    public string? CfgDirectory { get; init; }
    public string DetectionMessage { get; init; } = "Not detected";
    public IReadOnlyList<string> SearchedLibraries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SearchedSteamRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InaccessiblePaths { get; init; } = Array.Empty<string>();
    public bool SteamFound { get; init; }

    public static Cs2InstallInfo NotFound(
        string message,
        IReadOnlyList<string>? searched = null,
        IReadOnlyList<string>? steamRoots = null,
        IReadOnlyList<string>? inaccessible = null,
        bool steamFound = false) => new()
    {
        IsInstalled = false,
        DetectionMessage = message,
        SearchedLibraries = searched ?? Array.Empty<string>(),
        SearchedSteamRoots = steamRoots ?? Array.Empty<string>(),
        InaccessiblePaths = inaccessible ?? Array.Empty<string>(),
        SteamFound = steamFound
    };
}
