namespace FrameForge.CS2;

/// <summary>
/// Locates Steam installation roots and additional library folders
/// via libraryfolders.vdf — never hardcodes a single Steam path.
/// </summary>
public static class SteamLibraryLocator
{
    public sealed class DiscoveryResult
    {
        public IReadOnlyList<string> SteamRoots { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Libraries { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> InaccessiblePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CandidateRootsConsidered { get; init; } = Array.Empty<string>();
    }

    public static DiscoveryResult Discover(string? customSteamPath = null)
    {
        var inaccessible = new List<string>();
        var candidateRoots = EnumerateSteamRootCandidates(customSteamPath).ToList();
        var roots = new List<string>();

        foreach (var candidate in candidateRoots)
        {
            try
            {
                if (IsSteamRoot(candidate))
                {
                    roots.Add(Path.GetFullPath(candidate));
                }
            }
            catch (UnauthorizedAccessException)
            {
                inaccessible.Add(candidate);
            }
            catch (IOException)
            {
                inaccessible.Add(candidate);
            }
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            libraries.Add(root);

            foreach (var vdf in EnumerateLibraryFolderVdfPaths(root))
            {
                try
                {
                    if (!File.Exists(vdf))
                    {
                        continue;
                    }

                    string content;
                    try
                    {
                        content = File.ReadAllText(vdf);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        inaccessible.Add(vdf);
                        continue;
                    }
                    catch (IOException)
                    {
                        inaccessible.Add(vdf);
                        continue;
                    }

                    foreach (var path in ParseLibraryFoldersVdf(content))
                    {
                        try
                        {
                            if (Directory.Exists(path))
                            {
                                libraries.Add(Path.GetFullPath(path));
                            }
                            else if (!string.IsNullOrWhiteSpace(path))
                            {
                                // Path declared but missing is normal (unplugged drive) — record as inaccessible-ish
                                inaccessible.Add(path);
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            inaccessible.Add(path);
                        }
                        catch (IOException)
                        {
                            inaccessible.Add(path);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    inaccessible.Add(vdf);
                }
                catch (IOException)
                {
                    inaccessible.Add(vdf);
                }
            }
        }

        return new DiscoveryResult
        {
            SteamRoots = roots,
            Libraries = libraries.ToList(),
            InaccessiblePaths = inaccessible
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CandidateRootsConsidered = candidateRoots
        };
    }

    public static IReadOnlyList<string> FindSteamRoots(string? customSteamPath = null) =>
        Discover(customSteamPath).SteamRoots;

    public static IReadOnlyList<string> FindLibraryFolders(IEnumerable<string> steamRoots)
    {
        // Preserve call-site API; re-run discovery only for the provided roots.
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            libraries.Add(root);
            foreach (var vdf in EnumerateLibraryFolderVdfPaths(root))
            {
                if (!SafeFileExists(vdf))
                {
                    continue;
                }

                string? content = null;
                try
                {
                    content = File.ReadAllText(vdf);
                }
                catch
                {
                    continue;
                }

                foreach (var path in ParseLibraryFoldersVdf(content))
                {
                    if (SafeDirectoryExists(path))
                    {
                        try
                        {
                            libraries.Add(Path.GetFullPath(path));
                        }
                        catch
                        {
                            // ignore bad path
                        }
                    }
                }
            }
        }

        return libraries.ToList();
    }

    public static IEnumerable<string> EnumerateSteamRootCandidates(string? customSteamPath = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(customSteamPath))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(customSteamPath.Trim()));
        }

        // Environment overrides used by some Steam installs / launchers
        foreach (var envName in new[] { "STEAM_PATH", "STEAMROOT", "SteamPath" })
        {
            var env = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(env))
            {
                candidates.Add(Environment.ExpandEnvironmentVariables(env.Trim()));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "Steam"));
            }

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "Steam"));
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Programs", "Steam"));
                candidates.Add(Path.Combine(localAppData, "Steam"));
            }

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidates.Add(Path.Combine(userProfile, "Steam"));
            }

            // Registry-based path (Windows only) — legitimate HKCU/HKLM read, no elevation.
            foreach (var regPath in TryReadWindowsSteamPathsFromRegistry())
            {
                candidates.Add(regPath);
            }

            // Probe fixed/removable drives for alternate libraries without assuming a single root.
            foreach (var drive in GetWindowsDriveRoots())
            {
                candidates.Add(Path.Combine(drive, "Steam"));
                candidates.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
                candidates.Add(Path.Combine(drive, "Program Files", "Steam"));
                candidates.Add(Path.Combine(drive, "Games", "Steam"));
                candidates.Add(Path.Combine(drive, "SteamLibrary"));
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".steam", "root"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"));
            candidates.Add(Path.Combine(home, "Steam"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Minimal VDF parser focused on library folder "path" entries.
    /// Supports both legacy and modern libraryfolders.vdf formats.
    /// Corrupted / partial content is tolerated — returns whatever paths can be parsed.
    /// </summary>
    public static IReadOnlyList<string> ParseLibraryFoldersVdf(string content)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return results;
        }

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase) &&
                !IsLegacyIndexedPathLine(line))
            {
                continue;
            }

            var values = ExtractQuotedValues(line);
            if (values.Count == 0)
            {
                continue;
            }

            string? pathValue = null;
            if (values.Count >= 2 && values[0].Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                pathValue = values[1];
            }
            else if (IsLegacyIndexedPathLine(line) && values.Count >= 2)
            {
                // Only treat as a path if the second token looks like a filesystem path
                var candidate = values[1];
                if (LooksLikePath(candidate))
                {
                    pathValue = candidate;
                }
            }

            if (string.IsNullOrWhiteSpace(pathValue))
            {
                continue;
            }

            var normalized = NormalizeVdfPath(pathValue);
            if (normalized.Length > 0)
            {
                results.Add(normalized);
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeVdfPath(string pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return string.Empty;
        }

        var normalized = pathValue.Trim();

        // Unescape common VDF sequences
        normalized = normalized
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);

        if (Path.DirectorySeparatorChar == '/')
        {
            normalized = normalized.Replace('\\', '/');
        }
        else
        {
            normalized = normalized.Replace('/', '\\');
        }

        return normalized.Trim();
    }

    private static bool LooksLikePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Windows drive, UNC, or Unix absolute / home-relative
        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            return true;
        }

        if (value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("~", StringComparison.Ordinal))
        {
            return true;
        }

        // Relative folder name used as library root
        return value.Contains('\\') || value.Contains('/') ||
               value.Contains("Steam", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyIndexedPathLine(string line)
    {
        var values = ExtractQuotedValues(line);
        return values.Count >= 2 && int.TryParse(values[0], out _);
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
            while (i < line.Length)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    i += 2;
                    continue;
                }

                if (line[i] == '"')
                {
                    break;
                }

                i++;
            }

            if (i <= line.Length)
            {
                var segment = line[start..Math.Min(i, line.Length)];
                values.Add(segment.Replace("\\\"", "\"", StringComparison.Ordinal));
            }

            i++;
        }

        return values;
    }

    private static IEnumerable<string> EnumerateLibraryFolderVdfPaths(string root)
    {
        yield return Path.Combine(root, "steamapps", "libraryfolders.vdf");
        yield return Path.Combine(root, "config", "libraryfolders.vdf");
        yield return Path.Combine(root, "steamapps", "libraryfolders.vdf");
    }

    private static bool IsSteamRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !SafeDirectoryExists(path))
        {
            return false;
        }

        if (SafeDirectoryExists(Path.Combine(path, "steamapps")))
        {
            return true;
        }

        if (SafeFileExists(Path.Combine(path, "steam.exe")))
        {
            return true;
        }

        if (SafeFileExists(Path.Combine(path, "steam.sh")))
        {
            return true;
        }

        // Some installs put steamapps one level deeper
        if (SafeDirectoryExists(Path.Combine(path, "steam", "steamapps")))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> TryReadWindowsSteamPathsFromRegistry()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        // Avoid a hard dependency on Microsoft.Win32.Registry package: resolve via reflection
        // when the assembly is present on Windows runtimes.
        Type? registryType;
        try
        {
            registryType = Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry")
                           ?? Type.GetType("Microsoft.Win32.Registry, System.Windows.Extensions")
                           ?? Type.GetType("Microsoft.Win32.Registry");
        }
        catch
        {
            return results;
        }

        if (registryType is null)
        {
            return results;
        }

        var getValue = registryType.GetMethod(
            "GetValue",
            new[] { typeof(string), typeof(string), typeof(object) });
        if (getValue is null)
        {
            return results;
        }

        string[] keys =
        [
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
        ];

        foreach (var key in keys)
        {
            try
            {
                var value = getValue.Invoke(null, new object?[] { key, "SteamPath", null }) as string
                            ?? getValue.Invoke(null, new object?[] { key, "InstallPath", null }) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            catch
            {
                // Registry may be restricted or unavailable; ignore
            }
        }

        return results;
    }

    private static IReadOnlyList<string> GetWindowsDriveRoots()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return results;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    results.Add(drive.RootDirectory.FullName);
                }
            }
            catch
            {
                // skip inaccessible drives
            }
        }

        return results;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
