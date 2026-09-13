using FrameForge.CS2;
using Xunit;

namespace FrameForge.Tests;

public sealed class ConfigurationParsingTests
{
    [Fact]
    public void Parse_ReadsKeyValueAndComments()
    {
        const string cfg = """
            // FrameForge test
            fps_max 0
            cl_hud_telemetry_ping_show 1 // ping
            sensitivity "1.25"

            // trailing
            """;

        var service = new Cs2ConfigService();
        var doc = service.Parse(cfg, "test.cfg");
        var map = doc.ToDictionary();

        Assert.Equal("0", map["fps_max"]);
        Assert.Equal("1", map["cl_hud_telemetry_ping_show"]);
        Assert.Equal("1.25", map["sensitivity"]);
        Assert.True(doc.Entries.Any(e => e.IsCommentOnly));
    }

    [Fact]
    public void Parse_PreservesUnknownAndCorruptLines()
    {
        const string cfg = """
            fps_max 0
            this is not a valid key value line !!!
            # also not a standard comment form
            volume 0.5
            """;

        var service = new Cs2ConfigService();
        var doc = service.Parse(cfg);
        var serialized = service.Serialize(doc);

        Assert.Contains("this is not a valid key value line !!!", serialized);
        Assert.Contains("# also not a standard comment form", serialized);
        Assert.Equal("0", doc.ToDictionary()["fps_max"]);
        Assert.Equal("0.5", doc.ToDictionary()["volume"]);
    }

    [Fact]
    public void Serialize_RoundTripsValues()
    {
        var service = new Cs2ConfigService();
        var original = service.Parse("fps_max 400\ncl_interp 0.015625\n");
        var text = service.Serialize(original);
        var again = service.Parse(text).ToDictionary();

        Assert.Equal("400", again["fps_max"]);
        Assert.Equal("0.015625", again["cl_interp"]);
    }

    [Fact]
    public async Task WriteValues_UpdatesExistingAndAppendsNew_WithSidecarBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "autoexec.cfg");
        try
        {
            await File.WriteAllTextAsync(path, "fps_max 300\nvolume 0.5\n// keep me\n");
            var service = new Cs2ConfigService();
            await service.WriteValuesAsync(path, new Dictionary<string, string>
            {
                ["fps_max"] = "0",
                ["cl_hud_telemetry_ping_show"] = "1"
            });

            var doc = await service.ReadAsync(path);
            var map = doc.ToDictionary();
            Assert.Equal("0", map["fps_max"]);
            Assert.Equal("0.5", map["volume"]);
            Assert.Equal("1", map["cl_hud_telemetry_ping_show"]);

            // comments preserved
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("keep me", text);

            // side-car backup should exist
            var backups = Directory.GetFiles(dir, "autoexec.cfg.frameforge.bak.*");
            Assert.NotEmpty(backups);
            Assert.Equal("fps_max 300\nvolume 0.5\n// keep me\n", await File.ReadAllTextAsync(backups[0]));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Write_MissingFile_CreatesWithoutRequiringPriorBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff_cfg_new_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "new.cfg");
        try
        {
            var service = new Cs2ConfigService();
            await service.WriteValuesAsync(path, new Dictionary<string, string> { ["fps_max"] = "200" });
            Assert.True(File.Exists(path));
            var map = (await service.ReadAsync(path)).ToDictionary();
            Assert.Equal("200", map["fps_max"]);
            Assert.Empty(Directory.GetFiles(dir, "*.frameforge.bak.*"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsEmptyDocument()
    {
        var service = new Cs2ConfigService();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cfg");
        var doc = await service.ReadAsync(path);
        Assert.Empty(doc.Entries);
        Assert.Equal(path, doc.FilePath);
    }

    [Theory]
    [InlineData("fps_max 0", "fps_max", "0")]
    [InlineData("name \"hello world\"", "name", "hello world")]
    [InlineData("  cl_foo   bar  // c", "cl_foo", "bar")]
    public void Parse_Theory_KeyValues(string line, string key, string value)
    {
        var map = new Cs2ConfigService().Parse(line).ToDictionary();
        Assert.Equal(value, map[key]);
    }
}
