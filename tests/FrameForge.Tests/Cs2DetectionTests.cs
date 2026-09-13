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
        Assert.True(paths.Count == 2);
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
    public void ParseLibraryFoldersVdf_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(SteamLibraryLocator.ParseLibraryFoldersVdf(""));
        Assert.Empty(SteamLibraryLocator.ParseLibraryFoldersVdf("   "));
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
    }

    private static string CreateSyntheticCs2Layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_cs2_" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "SteamLibrary");
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
        return root;
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
