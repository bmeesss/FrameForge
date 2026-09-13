using FrameForge.CS2;
using Xunit;

namespace FrameForge.Tests;

public sealed class Cs2DetectionTests
{
    [Fact]
    public void ParseLibraryFoldersVdf_ReadsModernPathEntries()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                    "label"     ""
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                }
            }
            """;

        var paths = SteamLibraryLocator.ParseLibraryFoldersVdf(vdf);
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, p => p.Contains("Steam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("SteamLibrary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseLibraryFoldersVdf_ReadsLegacyIndexedPaths()
    {
        const string vdf = """
            "LibraryFolders"
            {
                "0"     "C:\\Steam"
                "1"     "E:\\Games\\Steam"
            }
            """;

        var paths = SteamLibraryLocator.ParseLibraryFoldersVdf(vdf);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void ParseLibraryFoldersVdf_EmptyOrCorrupt_ReturnsEmptyOrPartial()
    {
        Assert.Empty(SteamLibraryLocator.ParseLibraryFoldersVdf(""));
        Assert.Empty(SteamLibraryLocator.ParseLibraryFoldersVdf("   "));
        // Corrupted content should not throw
        var partial = SteamLibraryLocator.ParseLibraryFoldersVdf("\"path\" \"C:\\\\SteamLibrary\"\n{{{{ broken");
        Assert.NotEmpty(partial);
    }

    [Fact]
    public void ParseLibraryFoldersVdf_NormalizesEscapedPaths()
    {
        var paths = SteamLibraryLocator.ParseLibraryFoldersVdf("\"path\"\t\t\"D:\\\\Games\\\\SteamLibrary\"");
        Assert.Single(paths);
        Assert.Contains("SteamLibrary", paths[0]);
    }

    [Fact]
    public void ReadInstallDirFromManifest_ParsesInstallDir()
    {
        const string acf = """
            "AppState"
            {
                "appid"     "730"
                "Universe"  "1"
                "name"      "Counter-Strike 2"
                "installdir"        "Counter-Strike Global Offensive"
            }
            """;

        var dir = Cs2DetectionService.ReadInstallDirFromManifest(acf);
        Assert.Equal("Counter-Strike Global Offensive", dir);
    }

    [Fact]
    public void TryDetectInLibrary_FindsSyntheticInstall()
    {
        var root = CreateSyntheticCs2Layout();
        try
        {
            var library = Path.Combine(root, "SteamLibrary");
            var info = Cs2DetectionService.TryDetectInLibrary(library);
            Assert.NotNull(info);
            Assert.True(info!.IsInstalled);
            Assert.NotNull(info.InstallPath);
            Assert.NotNull(info.CfgDirectory);
            Assert.True(info.CfgDirectory!.Contains("cfg", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void TryDetectInLibrary_MultipleLibraries_FindsCorrectOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_multi_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Empty library
            var libA = Path.Combine(root, "LibA");
            Directory.CreateDirectory(Path.Combine(libA, "steamapps", "common"));

            // CS2 library
            var libB = Path.Combine(root, "LibB");
            CreateSyntheticCs2LayoutAt(libB);

            Assert.Null(Cs2DetectionService.TryDetectInLibrary(libA));
            var found = Cs2DetectionService.TryDetectInLibrary(libB);
            Assert.NotNull(found);
            Assert.True(found!.IsInstalled);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void TryDetectInLibrary_MissingLibrary_ReturnsNull()
    {
        Assert.Null(Cs2DetectionService.TryDetectInLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void BuildInfoFromInstallPath_InvalidPath_NotInstalled()
    {
        var info = Cs2DetectionService.BuildInfoFromInstallPath(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            steamLibrary: null,
            searched: null);
        Assert.False(info.IsInstalled);
        Assert.False(string.IsNullOrWhiteSpace(info.DetectionMessage));
    }

    [Fact]
    public async Task DetectAsync_WhenSteamMissing_ReturnsClearNotFound()
    {
        // Use an isolated settings root with nonsense custom steam path so discovery finds nothing useful.
        var root = Path.Combine(Path.GetTempPath(), "ff_det_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new FrameForge.Infrastructure.Paths.PathService(root);
            var log = new FrameForge.Infrastructure.Logging.FileAppLog(paths);
            var settings = new FrameForge.Infrastructure.Settings.AppSettingsService(paths, log);
            await settings.SaveAsync(new FrameForge.Core.Models.AppSettings
            {
                CustomSteamPath = Path.Combine(root, "no-steam-here"),
                CustomCs2Path = Path.Combine(root, "no-cs2-here")
            });

            var service = new Cs2DetectionService(settings, log);
            var info = await service.DetectAsync();
            Assert.False(info.IsInstalled);
            Assert.False(string.IsNullOrWhiteSpace(info.DetectionMessage));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public void Discover_DoesNotThrow_OnEmptyEnvironment()
    {
        var result = SteamLibraryLocator.Discover(customSteamPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.NotNull(result.SteamRoots);
        Assert.NotNull(result.Libraries);
        Assert.NotNull(result.InaccessiblePaths);
    }

    [Fact]
    public void EnumerateSteamRootCandidates_IncludesCustomPath()
    {
        var custom = Path.Combine("Z:", "MySteam");
        var candidates = SteamLibraryLocator.EnumerateSteamRootCandidates(custom).ToList();
        Assert.Contains(candidates, c => c.Contains("MySteam", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateSyntheticCs2Layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_cs2_" + Guid.NewGuid().ToString("N"));
        CreateSyntheticCs2LayoutAt(Path.Combine(root, "SteamLibrary"));
        return root;
    }

    private static void CreateSyntheticCs2LayoutAt(string library)
    {
        var steamapps = Path.Combine(library, "steamapps");
        var common = Path.Combine(steamapps, "common", "Counter-Strike Global Offensive");
        var cfg = Path.Combine(common, "game", "csgo", "cfg");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_730.acf"), """
            "AppState"
            {
                "appid" "730"
                "installdir" "Counter-Strike Global Offensive"
            }
            """);
        File.WriteAllText(Path.Combine(cfg, "autoexec.cfg"), "fps_max 0");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
