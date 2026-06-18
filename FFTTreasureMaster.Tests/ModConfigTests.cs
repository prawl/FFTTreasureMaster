using System.IO;
using FFTTreasureMaster.Configuration;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Config round-trip: write a Config.json to a temp dir, load it via
/// Configurable&lt;Config&gt;.FromFile, and assert Enabled survives the round-trip.
///
/// Invariants:
///   (1) Default Config has Enabled == true (on out of the box -- it's the whole mod).
///   (2) FromFile on a missing path creates a new Config with the default value (true).
///   (3) A Config.json written with Enabled=true  round-trips back as true.
///   (4) A Config.json written with Enabled=false round-trips back as false.
///   (5) FromFile on corrupt JSON silently returns a default Config (no throw).
/// </summary>
public class ModConfigTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tm_cfg_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void DefaultConfig_EnabledIsTrue()
    {
        var c = new Config();
        Assert.True(c.Enabled);
    }

    [Fact]
    public void FromFile_MissingPath_ReturnsDefaultTrue()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        var c    = Configurable<Config>.FromFile(path, "Test");
        Assert.True(c.Enabled);
    }

    [Fact]
    public void RoundTrip_TrueValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.Enabled = true;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.True(loaded.Enabled);
    }

    [Fact]
    public void RoundTrip_FalseValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.Enabled = false;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.False(loaded.Enabled);
    }

    [Fact]
    public void FromFile_CorruptJson_ReturnsDefaultNoThrow()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ this is not valid json !!!");

        var ex = Record.Exception(() =>
        {
            var c = Configurable<Config>.FromFile(path, "Test");
            Assert.True(c.Enabled);   // corrupt load falls back to default (true)
        });
        Assert.Null(ex);
    }
}
