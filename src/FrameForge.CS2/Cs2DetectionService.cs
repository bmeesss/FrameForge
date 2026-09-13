using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.CS2;

/// <summary>
/// Detects Counter-Strike 2 via Steam library folders and appmanifest_730.acf.
/// Does not interact with the running game process or anti-cheat.
/// </summary>
public sealed class Cs2DetectionService : ICs2DetectionService
{
    public const int Cs2AppId = 730;
    public const string GameFolderName = "Counter-Strike Global Offensive";
    public const string Cs2SubFolder = "game";
    public const string Cs2GameDir = "csgo";

    private readonly IAppSettingsService? _settingsService;
    private readonly IAppLog? _log;

    public Cs2DetectionService(IAppSettingsService? settingsService = null, IAppLog? log = null)
    {
        _settingsService = settingsService;
        _log = log;
    }

    public async Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log?.LogInformation("Starting CS2 detection via Steam libraries.");

        string? customSteam = null;
        string? customCs2 = null;
        if (_settingsService is not null)
        {
            var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            customSteam = settings.CustomSteamPath;
            customCs2 = settings.CustomCs2Path;
        }

        if (!string.IsNullOrWhiteSpace(customCs2) && Directory.Exists(customCs2))
        {
            var info = BuildInfoFromInstallPath(customCs2, steamLibrary: null, searched: new[] { customCs2 });
            if (info.IsInstalled)
            {
                _log?.LogInformation($"CS2 found via custom path: {customCs2}");
                return info;
            }
        }

        var steamRoots = SteamLibraryLocator.FindSteamRoots(customSteam);
        var libraries = SteamLibraryLocator.FindLibraryFolders(steamRoots);
        var searched = libraries.ToList();

        if (libraries.Count == 0)
        {
            _log?.LogWarning("No Steam library folders found.");
            return Cs2InstallInfo.NotFound(
                "Steam installation was not found. Configure a custom Steam or CS2 path in Settings.",
                searched);
        }

        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var install = TryDetectInLibrary(library);
            if (install is not null)
            {
                _log?.LogInformation($"CS2 detected at {install.InstallPath}");
                return install with { SearchedLibraries = searched };
            }
        }

        _log?.LogInformation("CS2 was not found in any Steam library.");
        return Cs2InstallInfo.NotFound(
            "Counter-Strike 2 was not found in the detected Steam libraries.",
            searched);
    }

    /// <summary>
    /// Attempts detection against a single Steam library root.
    /// Exposed for unit tests.
    /// </summary>
    public static Cs2InstallInfo? TryDetectInLibrary(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
        {
            return null;
        }

        var steamApps = Path.Combine(libraryPath, "steamapps");
        if (!Directory.Exists(steamApps))
        {
            // library path may already be the steamapps folder
            steamApps = libraryPath;
        }

        var manifestPath = Path.Combine(steamApps, $"appmanifest_{Cs2AppId}.acf");
        string? installDirName = GameFolderName;

        if (File.Exists(manifestPath))
        {
            installDirName = ReadInstallDirFromManifest(File.ReadAllText(manifestPath)) ?? GameFolderName;
        }

        var common = Path.Combine(steamApps, "common");
        var candidates = new List<string>
        {
            Path.Combine(common, installDirName),
            Path.Combine(common, GameFolderName),
            Path.Combine(common, "Counter-Strike 2")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = BuildInfoFromInstallPath(candidate, libraryPath, searched: null);
            if (info.IsInstalled)
            {
                return info;
            }
        }

        return null;
    }

    public static Cs2InstallInfo BuildInfoFromInstallPath(
        string installPath,
        string? steamLibrary,
        IReadOnlyList<string>? searched)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return Cs2InstallInfo.NotFound("Install path does not exist.", searched);
        }

        // CS2 layout: <install>/game/csgo/  (legacy CS:GO folder name retained by Valve)
        var gameCsgo = Path.Combine(installPath, Cs2SubFolder, Cs2GameDir);
        var legacyCsgo = Path.Combine(installPath, Cs2GameDir);
        string? gameRoot = null;

        if (Directory.Exists(gameCsgo))
        {
            gameRoot = gameCsgo;
        }
        else if (Directory.Exists(legacyCsgo))
        {
            gameRoot = legacyCsgo;
        }

        // Also accept presence of cs2.exe / cs2.sh as confirmation
        var hasBinary =
            File.Exists(Path.Combine(installPath, "game", "bin", "win64", "cs2.exe")) ||
            File.Exists(Path.Combine(installPath, "game", "bin", "linuxsteamrt64", "cs2")) ||
            File.Exists(Path.Combine(installPath, "cs2.exe"));

        if (gameRoot is null && !hasBinary)
        {
            return Cs2InstallInfo.NotFound("Path exists but does not look like a CS2 install.", searched);
        }

        var cfgDir = gameRoot is not null
            ? Path.Combine(gameRoot, "cfg")
            : Path.Combine(installPath, Cs2SubFolder, Cs2GameDir, "cfg");

        var gameInfo = gameRoot is not null
            ? Path.Combine(gameRoot, "gameinfo.gi")
            : null;

        return new Cs2InstallInfo
        {
            IsInstalled = true,
            InstallPath = Path.GetFullPath(installPath),
            SteamLibraryPath = steamLibrary is null ? null : Path.GetFullPath(steamLibrary),
            GameInfoPath = gameInfo is not null && File.Exists(gameInfo) ? gameInfo : gameInfo,
            CfgDirectory = cfgDir,
            DetectionMessage = "Counter-Strike 2 detected.",
            SearchedLibraries = searched ?? Array.Empty<string>()
        };
    }

    /// <summary>
    /// Reads installdir from an appmanifest_730.acf file.
    /// </summary>
    public static string? ReadInstallDirFromManifest(string acfContent)
    {
        if (string.IsNullOrWhiteSpace(acfContent))
        {
            return null;
        }

        foreach (var rawLine in acfContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.Contains("installdir", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = new List<string>();
            var i = 0;
            while (i < line.Length)
            {
                if (line[i] != '"')
                {
                    i++;
                    continue;
                }

                i++;
                var start = i;
                while (i < line.Length && line[i] != '"')
                {
                    i++;
                }

                values.Add(line[start..Math.Min(i, line.Length)]);
                i++;
            }

            if (values.Count >= 2 && values[0].Equals("installdir", StringComparison.OrdinalIgnoreCase))
            {
                return values[1];
            }
        }

        return null;
    }
}
