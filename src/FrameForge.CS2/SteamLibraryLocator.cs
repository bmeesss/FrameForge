namespace FrameForge.CS2;

/// <summary>
/// Locates Steam installation roots and additional library folders
/// via libraryfolders.vdf — never hardcodes a single Steam path.
/// </summary>
public static class SteamLibraryLocator
{
    public static IReadOnlyList<string> FindSteamRoots(string? customSteamPath = null)
    {
        var roots = new List<string>();
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(customSteamPath))
        {
            candidates.Add(customSteamPath);
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            candidates.Add(Path.Combine(programFilesX86, "Steam"));
            candidates.Add(Path.Combine(programFiles, "Steam"));
            candidates.Add(Path.Combine(localAppData, "Programs", "Steam"));

            // Common alternate drive letters without assuming a single root
            foreach (var drive in GetWindowsDriveRoots())
            {
                candidates.Add(Path.Combine(drive, "Steam"));
                candidates.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
                candidates.Add(Path.Combine(drive, "Program Files", "Steam"));
                candidates.Add(Path.Combine(drive, "Games", "Steam"));
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".steam", "root"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsSteamRoot(candidate))
            {
                roots.Add(Path.GetFullPath(candidate));
            }
        }

        return roots;
    }

    public static IReadOnlyList<string> FindLibraryFolders(IEnumerable<string> steamRoots)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in steamRoots)
        {
            libraries.Add(root);

            var vdfPaths = new[]
            {
                Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                Path.Combine(root, "config", "libraryfolders.vdf"),
                Path.Combine(root, "steamapps", "libraryfolders.vdf".ToLowerInvariant())
            };

            foreach (var vdf in vdfPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(vdf))
                {
                    continue;
                }

                foreach (var path in ParseLibraryFoldersVdf(File.ReadAllText(vdf)))
                {
                    if (Directory.Exists(path))
                    {
                        libraries.Add(Path.GetFullPath(path));
                    }
                }
            }
        }

        return libraries.ToList();
    }

    /// <summary>
    /// Minimal VDF parser focused on library folder "path" entries.
    /// Supports both legacy and modern libraryfolders.vdf formats.
    /// </summary>
    public static IReadOnlyList<string> ParseLibraryFoldersVdf(string content)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return results;
        }

        // Match: "path"   "D:\\SteamLibrary"
        // Also tolerate escaped separators and forward slashes.
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
                // Legacy: "1"		"D:\\SteamLibrary"
                pathValue = values[1];
            }

            if (string.IsNullOrWhiteSpace(pathValue))
            {
                continue;
            }

            var normalized = pathValue
                .Replace("\\\\", "\\")
                .Replace('/', Path.DirectorySeparatorChar)
                .Trim();

            if (normalized.Length > 0)
            {
                results.Add(normalized);
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLegacyIndexedPathLine(string line)
    {
        // "1"  "C:\path"  — first token is numeric index
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
                values.Add(segment.Replace("\\\"", "\""));
            }

            i++;
        }

        return values;
    }

    private static bool IsSteamRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        // A Steam root typically contains steamapps or steam.exe / steam.sh
        if (Directory.Exists(Path.Combine(path, "steamapps")))
        {
            return true;
        }

        if (File.Exists(Path.Combine(path, "steam.exe")))
        {
            return true;
        }

        if (File.Exists(Path.Combine(path, "steam.sh")))
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<string> GetWindowsDriveRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }
}
