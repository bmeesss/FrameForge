using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.CS2;

/// <summary>
/// Detects Counter-Strike 2 via Steam library folders and appmanifest_730.acf.
/// Does not interact with the running game process or anti-cheat.
/// Normal "not found" cases return a result object — they do not throw.
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

        try
        {
            string? customSteam = null;
            string? customCs2 = null;
            if (_settingsService is not null)
            {
                try
                {
                    var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
                    customSteam = settings.CustomSteamPath;
                    customCs2 = settings.CustomCs2Path;
                }
                catch (Exception ex)
                {
                    _log?.LogWarning($"Settings load failed during detection: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(customCs2))
            {
                try
                {
                    if (Directory.Exists(customCs2))
                    {
                        var info = BuildInfoFromInstallPath(
                            customCs2,
                            steamLibrary: null,
                            searched: new[] { customCs2 },
                            steamRoots: Array.Empty<string>());
                        if (info.IsInstalled)
                        {
                            _log?.LogInformation($"CS2 found via custom path: {customCs2}");
                            return info with { SteamFound = true };
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return Cs2InstallInfo.NotFound(
                        "Custom CS2 path is inaccessible.",
                        searched: new[] { customCs2 },
                        inaccessible: new[] { customCs2 });
                }
                catch (IOException)
                {
                    return Cs2InstallInfo.NotFound(
                        "Custom CS2 path could not be read.",
                        searched: new[] { customCs2 },
                        inaccessible: new[] { customCs2 });
                }
            }

            var discovery = SteamLibraryLocator.Discover(customSteam);
            var searched = discovery.Libraries.ToList();
            var roots = discovery.SteamRoots.ToList();
            var inaccessible = discovery.InaccessiblePaths.ToList();

            if (roots.Count == 0 && searched.Count == 0)
            {
                _log?.LogWarning("No Steam library folders found.");
                return Cs2InstallInfo.NotFound(
                    "Steam installation was not found. Configure a custom Steam or CS2 path in Settings.",
                    searched,
                    steamRoots: discovery.CandidateRootsConsidered.Take(12).ToList(),
                    inaccessible: inaccessible,
                    steamFound: false);
            }

            foreach (var library in searched)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Cs2InstallInfo? install;
                try
                {
                    install = TryDetectInLibrary(library);
                }
                catch (UnauthorizedAccessException)
                {
                    inaccessible.Add(library);
                    continue;
                }
                catch (IOException)
                {
                    inaccessible.Add(library);
                    continue;
                }

                if (install is not null)
                {
                    _log?.LogInformation($"CS2 detected at {install.InstallPath}");
                    return install with
                    {
                        SearchedLibraries = searched,
                        SearchedSteamRoots = roots,
                        InaccessiblePaths = inaccessible,
                        SteamFound = true,
                        SteamRootPath = roots.FirstOrDefault()
                    };
                }
            }

            _log?.LogInformation("CS2 was not found in any Steam library.");
            var message = roots.Count > 0
                ? "Steam was found, but Counter-Strike 2 was not installed in any detected library."
                : "Counter-Strike 2 was not found in the detected Steam libraries.";

            return Cs2InstallInfo.NotFound(
                message,
                searched,
                steamRoots: roots,
                inaccessible: inaccessible,
                steamFound: roots.Count > 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected failures still return a result object rather than crashing the UI.
            _log?.LogError("CS2 detection failed unexpectedly.", ex);
            return Cs2InstallInfo.NotFound($"CS2 detection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts detection against a single Steam library root.
    /// Exposed for unit tests. Returns null when CS2 is not present (not an error).
    /// </summary>
    public static Cs2InstallInfo? TryDetectInLibrary(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return null;
        }

        if (!Directory.Exists(libraryPath))
        {
            return null;
        }

        var steamApps = ResolveSteamAppsDirectory(libraryPath);
        if (steamApps is null)
        {
            return null;
        }

        var manifestPath = Path.Combine(steamApps, $"appmanifest_{Cs2AppId}.acf");
        string? installDirName = GameFolderName;

        if (File.Exists(manifestPath))
        {
            try
            {
                installDirName = ReadInstallDirFromManifest(File.ReadAllText(manifestPath)) ?? GameFolderName;
            }
            catch (IOException)
            {
                // Keep default folder name
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
        }

        var common = Path.Combine(steamApps, "common");
        var candidates = new List<string>
        {
            Path.Combine(common, installDirName),
            Path.Combine(common, GameFolderName),
            Path.Combine(common, "Counter-Strike 2"),
            Path.Combine(common, "Counter-Strike Global Offensive")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = BuildInfoFromInstallPath(candidate, libraryPath, searched: null, steamRoots: null);
            if (info.IsInstalled)
            {
                return info;
            }
        }

        // Manifest present but install folder missing
        if (File.Exists(manifestPath))
        {
            return null;
        }

        return null;
    }

    public static Cs2InstallInfo BuildInfoFromInstallPath(
        string installPath,
        string? steamLibrary,
        IReadOnlyList<string>? searched,
        IReadOnlyList<string>? steamRoots = null)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return Cs2InstallInfo.NotFound("Install path is empty.", searched, steamRoots);
        }

        try
        {
            if (!Directory.Exists(installPath))
            {
                return Cs2InstallInfo.NotFound("Install path does not exist.", searched, steamRoots);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Cs2InstallInfo.NotFound(
                "Install path is inaccessible.",
                searched,
                steamRoots,
                inaccessible: new[] { installPath });
        }
        catch (IOException)
        {
            return Cs2InstallInfo.NotFound(
                "Install path could not be read.",
                searched,
                steamRoots,
                inaccessible: new[] { installPath });
        }

        // CS2 layout: <install>/game/csgo/  (legacy CS:GO folder name retained by Valve)
        var gameCsgo = Path.Combine(installPath, Cs2SubFolder, Cs2GameDir);
        var legacyCsgo = Path.Combine(installPath, Cs2GameDir);
        string? gameRoot = null;

        if (SafeDirectoryExists(gameCsgo))
        {
            gameRoot = gameCsgo;
        }
        else if (SafeDirectoryExists(legacyCsgo))
        {
            gameRoot = legacyCsgo;
        }

        var hasBinary =
            SafeFileExists(Path.Combine(installPath, "game", "bin", "win64", "cs2.exe")) ||
            SafeFileExists(Path.Combine(installPath, "game", "bin", "linuxsteamrt64", "cs2")) ||
            SafeFileExists(Path.Combine(installPath, "game", "bin", "linuxsteamrt64", "cs2.sh")) ||
            SafeFileExists(Path.Combine(installPath, "cs2.exe"));

        if (gameRoot is null && !hasBinary)
        {
            return Cs2InstallInfo.NotFound("Path exists but does not look like a CS2 install.", searched, steamRoots);
        }

        var cfgDir = gameRoot is not null
            ? Path.Combine(gameRoot, "cfg")
            : Path.Combine(installPath, Cs2SubFolder, Cs2GameDir, "cfg");

        var gameInfo = gameRoot is not null
            ? Path.Combine(gameRoot, "gameinfo.gi")
            : null;

        string fullInstall;
        try
        {
            fullInstall = Path.GetFullPath(installPath);
        }
        catch
        {
            fullInstall = installPath;
        }

        return new Cs2InstallInfo
        {
            IsInstalled = true,
            InstallPath = fullInstall,
            SteamLibraryPath = steamLibrary is null ? null : SafeFullPath(steamLibrary),
            GameInfoPath = gameInfo is not null && SafeFileExists(gameInfo) ? gameInfo : gameInfo,
            CfgDirectory = cfgDir,
            DetectionMessage = "Counter-Strike 2 detected.",
            SearchedLibraries = searched ?? Array.Empty<string>(),
            SearchedSteamRoots = steamRoots ?? Array.Empty<string>(),
            SteamFound = true
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

            var values = ExtractQuotedValues(line);
            if (values.Count >= 2 && values[0].Equals("installdir", StringComparison.OrdinalIgnoreCase))
            {
                return values[1];
            }
        }

        return null;
    }

    private static string? ResolveSteamAppsDirectory(string libraryPath)
    {
        var direct = Path.Combine(libraryPath, "steamapps");
        if (SafeDirectoryExists(direct))
        {
            return direct;
        }

        // library path may already be the steamapps folder
        if (SafeFileExists(Path.Combine(libraryPath, $"appmanifest_{Cs2AppId}.acf")) ||
            SafeDirectoryExists(Path.Combine(libraryPath, "common")))
        {
            return libraryPath;
        }

        // Nested steam/steamapps (some Linux layouts)
        var nested = Path.Combine(libraryPath, "steam", "steamapps");
        if (SafeDirectoryExists(nested))
        {
            return nested;
        }

        return null;
    }

    private static List<string> ExtractQuotedValues(string line)
    {
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
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    i += 2;
                    continue;
                }

                i++;
            }

            values.Add(line[start..Math.Min(i, line.Length)]);
            i++;
        }

        return values;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
