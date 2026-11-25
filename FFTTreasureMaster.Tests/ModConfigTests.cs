using System;
using System.IO;
using System.Text.Json;
using FFTTreasureMaster.Configuration;
using Reloaded.Mod.Interfaces;
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
///   (5) FromFile on a corrupt, empty, null or locked file returns a default Config with LoadError
///       set (no throw, file untouched); a good or missing file leaves LoadError null.
///   (6) Default Config has HideClaimedTiles == true (dim-on-claim ships on by default).
///   (7) FromFile on a missing path returns HideClaimedTiles == true.
///   (8) A Config.json written with HideClaimedTiles=true  round-trips back as true.
///   (9) A Config.json written with HideClaimedTiles=false round-trips back as false.
///  (10) Default Config has AllUnitsTreasureHunter == false (the setting is opt-in, since it
///       changes battle balance).
///  (11) FromFile on a missing path returns AllUnitsTreasureHunter == false.
///  (12) A Config.json written with AllUnitsTreasureHunter=true  round-trips back as true.
///  (13) A Config.json written with AllUnitsTreasureHunter=false round-trips back as false.
///  (14) RareTreasureOnly follows the same four checks as AllUnitsTreasureHunter (default off).
///  (15) Every toggle default equals its Tuning fallback constant.
///  (16) Save never writes LoadError; DescribeConfig is Ok on a clean load and names source and
///       reason on a failed one.
///  (17) TileStyle defaults to the Tuning fallback, saves as a name, and any unreadable value
///       loads the default without touching the other settings or setting LoadError.
/// </summary>
public class ModConfigTests : IDisposable
{
    private static string TempDir() => Scratch.Dir("tm_cfg_");

    public void Dispose() => Scratch.Sweep();

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
        var c = Configurable<Config>.FromFile(path, "Test");
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
    public void FromFile_CorruptJson_DefaultsReportErrorAndFileUntouched()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ this is not valid json !!!");
        var before = File.ReadAllBytes(path);

        Config? c = null;
        var ex = Record.Exception(() => c = Configurable<Config>.FromFile(path, "Test"));

        Assert.Null(ex);
        Assert.True(c!.Enabled);
        Assert.False(c.RareTreasureOnly);
        Assert.NotNull(c.LoadError);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void FromFile_NullLiteral_DefaultsAndReportsError()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "null");
        var c = Configurable<Config>.FromFile(path, "Test");
        Assert.True(c.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(c.LoadError));
    }

    [Fact]
    public void FromFile_EmptyFile_ReportsError()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "");
        Assert.NotNull(Configurable<Config>.FromFile(path, "Test").LoadError);
    }

    [Fact]
    public void FromFile_FileLockedByAnotherHandle_ReportsErrorNoThrow()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{}");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Config? c = null;
        var ex = Record.Exception(() => c = Configurable<Config>.FromFile(path, "Test"));

        Assert.Null(ex);
        Assert.NotNull(c!.LoadError);
    }

    [Fact]
    public void FromFile_GoodFileAndMissingPath_LoadErrorIsNull()
    {
        var good = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(good, "{ \"Enabled\": false }");
        var loaded = Configurable<Config>.FromFile(good, "Test");
        Assert.False(loaded.Enabled);
        Assert.Null(loaded.LoadError);
        Assert.Null(Configurable<Config>.FromFile(Path.Combine(TempDir(), "Config.json"), "Test").LoadError);
    }

    [Fact]
    public void Save_NeverWritesLoadError()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ nope");
        var c = Configurable<Config>.FromFile(path, "Test");
        Assert.NotNull(c.LoadError);
        c.Save();
        Assert.DoesNotContain("LoadError", File.ReadAllText(path));
    }

    [Fact]
    public void DescribeConfig_GoodConfig_IsOkAndOpensWithLoaded()
    {
        var (ok, line) = Mod.DescribeConfig(new Config(), "src.json");
        Assert.True(ok);
        Assert.StartsWith("Configuration loaded", line);
    }

    [Fact]
    public void DescribeConfig_FailedConfig_NamesSourceAndReason()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ nope");
        var c = Configurable<Config>.FromFile(path, "Test");

        var (ok, line) = Mod.DescribeConfig(c, path);

        Assert.False(ok);
        Assert.Contains(path, line);
        Assert.Contains(c.LoadError!, line);
        Assert.Contains("default", line);
        Assert.Contains("RareTreasureOnly=", line);
    }

    // ── HideClaimedTiles invariants ───────────────────────────────────────────

    [Fact]
    public void DefaultConfig_HideClaimedTilesIsTrue()
    {
        var c = new Config();
        Assert.True(c.HideClaimedTiles);
    }

    [Fact]
    public void FromFile_MissingPath_HideClaimedTilesDefaultTrue()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        var c = Configurable<Config>.FromFile(path, "Test");
        Assert.True(c.HideClaimedTiles);
    }

    [Fact]
    public void RoundTrip_HideClaimedTiles_TrueValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.HideClaimedTiles = true;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.True(loaded.HideClaimedTiles);
    }

    [Fact]
    public void RoundTrip_HideClaimedTiles_FalseValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.HideClaimedTiles = false;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.False(loaded.HideClaimedTiles);
    }

    // ── AllUnitsTreasureHunter invariants (default OFF, unlike the other two toggles) ──

    [Fact]
    public void DefaultConfig_AllUnitsTreasureHunterIsFalse()
    {
        var c = new Config();
        Assert.False(c.AllUnitsTreasureHunter);
    }

    [Fact]
    public void FromFile_MissingPath_AllUnitsTreasureHunterDefaultFalse()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        var c = Configurable<Config>.FromFile(path, "Test");
        Assert.False(c.AllUnitsTreasureHunter);
    }

    [Fact]
    public void RoundTrip_AllUnitsTreasureHunter_TrueValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.AllUnitsTreasureHunter = true;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.True(loaded.AllUnitsTreasureHunter);
    }

    [Fact]
    public void RoundTrip_AllUnitsTreasureHunter_FalseValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.AllUnitsTreasureHunter = false;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.False(loaded.AllUnitsTreasureHunter);
    }

    // ── RareTreasureOnly invariants (default OFF) ──

    [Fact]
    public void DefaultConfig_RareTreasureOnlyIsFalse()
    {
        var c = new Config();
        Assert.False(c.RareTreasureOnly);
    }

    [Fact]
    public void FromFile_MissingPath_RareTreasureOnlyDefaultFalse()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        var c = Configurable<Config>.FromFile(path, "Test");
        Assert.False(c.RareTreasureOnly);
    }

    [Fact]
    public void RoundTrip_RareTreasureOnly_TrueValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.RareTreasureOnly = true;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.True(loaded.RareTreasureOnly);
    }

    [Fact]
    public void RoundTrip_RareTreasureOnly_FalseValue()
    {
        var path = Path.Combine(TempDir(), "Config.json");

        var written = Configurable<Config>.FromFile(path, "Test");
        written.RareTreasureOnly = false;
        written.Save();

        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.False(loaded.RareTreasureOnly);
    }

    [Fact]
    public void DefaultConfig_AllFourToggles_MatchTuningDefaults()
    {
        var c = new Config();
        Assert.Equal(Tuning.TreasureEnabled, c.Enabled);
        Assert.Equal(Tuning.ClaimDetectionEnabled, c.HideClaimedTiles);
        Assert.Equal(Tuning.AllUnitsTreasureHunterEnabled, c.AllUnitsTreasureHunter);
        Assert.Equal(Tuning.RareTreasureOnlyEnabled, c.RareTreasureOnly);
    }

    // ── Tile highlight style ──────────────────────────────────────────────────

    [Fact]
    public void DefaultConfig_TileStyle_MatchesTuningDefault()
    {
        Assert.Equal(Tuning.TileStyleDefault, new Config().TileStyle);
    }

    [Fact]
    public void RoundTrip_NonDefaultTileStyle_IsSavedAsText()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        var written = Configurable<Config>.FromFile(path, "Test");
        written.TileStyle = TileStyle.Pink;
        written.Save();

        Assert.Contains("\"TileStyle\": \"Pink\"", File.ReadAllText(path));
        var loaded = Configurable<Config>.FromFile(path, "Test");
        Assert.Equal(TileStyle.Pink, loaded.TileStyle);
        Assert.Null(loaded.LoadError);
    }

    [Theory]
    [InlineData("\"Purple\"")]
    [InlineData("\"3\"")]
    [InlineData("\"Green, Pink\"")]
    [InlineData("3")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void ForgivingRead_BadStyle_GivesDefaultAndKeepsTheOtherSettings(string styleJson)
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ \"Enabled\": false, \"TileStyle\": " + styleJson +
            ", \"HideClaimedTiles\": false, \"AllUnitsTreasureHunter\": true, \"RareTreasureOnly\": true }");

        var c = Configurable<Config>.FromFile(path, "Test");

        Assert.Equal(Tuning.TileStyleDefault, c.TileStyle);
        Assert.False(c.Enabled);
        Assert.False(c.HideClaimedTiles);
        Assert.True(c.AllUnitsTreasureHunter);
        Assert.True(c.RareTreasureOnly);
        Assert.Null(c.LoadError);
    }

    [Fact]
    public void ForgivingRead_LowerCaseName_LoadsThatStyle()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ \"TileStyle\": \"green\" }");
        Assert.Equal(TileStyle.Green, Configurable<Config>.FromFile(path, "Test").TileStyle);
    }

    [Fact]
    public void OldFileWithoutTileStyle_LoadsDefaultStyleAndKeepsTheRest()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ \"Enabled\": false }");
        var c = Configurable<Config>.FromFile(path, "Test");
        Assert.Equal(Tuning.TileStyleDefault, c.TileStyle);
        Assert.False(c.Enabled);
        Assert.Null(c.LoadError);
    }

    [Fact]
    public void DescribeConfig_NamesTheTileStyle()
    {
        var cfg = new Config { TileStyle = TileStyle.Green };
        Assert.Contains("TileStyle=Green", Mod.DescribeConfig(cfg, "src.json").Line);
    }

    [Fact]
    public void ForgivingConverter_WritesTheFallbackNameForAnUndefinedValue()
    {
        var json = JsonSerializer.Serialize(new Config { TileStyle = (TileStyle)99 }, Configurable<Config>.SerializerOptions);
        Assert.Contains("\"TileStyle\": \"BlueAndYellow\"", json);
    }

    [Fact]
    public void LauncherConfigurePath_BrokenFile_StillOpensWithDefaultsAndLeavesFileAlone()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, "{ this is not valid json !!!");
        var before = File.ReadAllBytes(path);

        IUpdatableConfigurable[]? configs = null;
        var ex = Record.Exception(() => configs = new Configurator(dir).Configurations);

        Assert.Null(ex);
        var only = Assert.Single(configs!);
        var c = Assert.IsType<Config>(only);
        Assert.True(c.Enabled);
        Assert.Equal(Tuning.TileStyleDefault, c.TileStyle);
        Assert.NotNull(c.LoadError);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
