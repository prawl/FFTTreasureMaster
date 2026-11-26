using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using FFTTreasureMaster.Configuration;
using Reloaded.Mod.Interfaces;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// The settings file rules. Read loads without side effects, the Configure window keeps the values it
/// opened with, and Save writes only the fields that changed so a close never undoes a key write.
/// </summary>
public class ConfigSaveTests : IDisposable
{
    private static string TempDir() => Scratch.Dir("tm_save_");

    public void Dispose() => Scratch.Sweep();

    private static Config Open(string dir) =>
        Assert.IsType<Config>(Assert.Single(new Configurator(dir).Configurations));

    private static string StyleOf(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject()["TileStyle"]!.GetValue<string>();

    private const string Stock = "{ \"Enabled\": true, \"HideClaimedTiles\": true, " +
        "\"AllUnitsTreasureHunter\": false, \"RareTreasureOnly\": false, " +
        "\"TileStyle\": \"BlueAndYellow\", \"Extra\": 7 }";

    [Fact]
    public void Read_GoodFile_LoadsAndSetsNoFilePath()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ \"Enabled\": false }");
        var c = Configurable<Config>.Read(path);
        Assert.False(c.Enabled);
        Assert.Null(c.LoadError);
        Assert.Null(c.FilePath);
    }

    [Fact]
    public void Read_BrokenFile_ReturnsDefaultsWithLoadError()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ nope");
        var c = Configurable<Config>.Read(path);
        Assert.True(c.Enabled);
        Assert.NotNull(c.LoadError);
    }

    [Fact]
    public void OpenedConfig_KeepsItsValuesAfterTheFileIsRewritten()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, Stock);
        var window = new Configurator(dir);
        Assert.True(Assert.IsType<Config>(window.Configurations[0]).Enabled);
        File.WriteAllText(path, Stock.Replace("\"Enabled\": true", "\"Enabled\": false"));
        Thread.Sleep(500);
        Assert.True(Assert.IsType<Config>(window.Configurations[0]).Enabled);
    }

    [Fact]
    public void WindowSave_NoEdit_LeavesTheFileBytesAndKeyStyle()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, Stock);
        var c = Open(dir);
        File.WriteAllText(path, Stock.Replace("BlueAndYellow", "Green"));
        var before = File.ReadAllBytes(path);
        c.Save();
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("Green", StyleOf(path));
    }

    [Fact]
    public void WindowSave_EditingAnotherSetting_WritesItAndKeepsKeyStyle()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, Stock);
        var c = Open(dir);
        File.WriteAllText(path, Stock.Replace("BlueAndYellow", "Green"));
        c.RareTreasureOnly = true;
        c.Save();
        var read = Configurable<Config>.Read(path);
        Assert.True(read.RareTreasureOnly);
        Assert.Equal(TileStyle.Green, read.TileStyle);
    }

    [Fact]
    public void WindowSave_StylePickedInTheWindow_IsWritten()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, Stock);
        var c = Open(dir);
        c.TileStyle = TileStyle.Pink;
        c.Save();
        Assert.Equal("Pink", StyleOf(path));
    }

    [Fact]
    public void SaveThenKeyWriteThenSave_KeepsTheKeyStyle()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, Stock);
        var c = Open(dir);
        c.RareTreasureOnly = true;
        c.Save();
        Assert.Equal(FieldWrite.Written,
            ConfigFields.Replace(path, new JsonObject { ["TileStyle"] = "Green" }));
        c.Save();
        Assert.Equal("Green", StyleOf(path));
        Assert.True(Configurable<Config>.Read(path).RareTreasureOnly);
    }

    [Fact]
    public void MissingFile_IsWrittenWhole()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        var c = Open(dir);
        Assert.True(File.Exists(path));
        c.TileStyle = TileStyle.Green;
        c.Save();
        Assert.Equal("Green", StyleOf(path));
    }

    [Fact]
    public void UnparsableFile_WithAnEditedField_IsWrittenWhole()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, "{ broken");
        var c = Open(dir);
        c.RareTreasureOnly = true;
        c.Save();
        var read = Configurable<Config>.Read(path);
        Assert.Null(read.LoadError);
        Assert.True(read.RareTreasureOnly);
    }

    [Fact]
    public void BrokenFile_OpenedAndSavedWithNoEdit_KeepsItsBytes()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, "{ broken");
        var before = File.ReadAllBytes(path);
        Open(dir).Save();
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void WindowSave_KeepsAnUnknownExtraField()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        File.WriteAllText(path, Stock);
        var c = Open(dir);
        c.Enabled = false;
        c.Save();
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(7, root["Extra"]!.GetValue<int>());
        Assert.False(root["Enabled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("[]", "NotObject")]
    [InlineData("null", "NotObject")]
    [InlineData("{ broken", "Unparsable")]
    [InlineData("", "Unparsable")]
    public void Replace_RefusesAFileThatIsNotAnObject(string text, string expected)
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, text);
        Assert.Equal(Enum.Parse<FieldWrite>(expected),
            ConfigFields.Replace(path, new JsonObject { ["A"] = 1 }));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void Replace_MissingFile_WritesNothing()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        Assert.Equal(FieldWrite.Missing, ConfigFields.Replace(path, new JsonObject { ["A"] = 1 }));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Replace_AddsAMissingFieldAndKeepsTheRest()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ \"Extra\": 7 }");
        Assert.Equal(FieldWrite.Written,
            ConfigFields.Replace(path, new JsonObject { ["TileStyle"] = "Pink" }));
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("Pink", root["TileStyle"]!.GetValue<string>());
        Assert.Equal(7, root["Extra"]!.GetValue<int>());
    }

    [Fact]
    public void Replace_LockedFile_ThrowsIoException()
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{}");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => ConfigFields.Replace(path, new JsonObject { ["A"] = 1 }));
    }

    [Fact]
    public void StyleCycleKey_RoundTripsByName()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Config.json");
        var c = Open(dir);
        c.StyleCycleKey = StyleCycleKey.Key7;
        c.Save();
        Assert.Contains("\"StyleCycleKey\": \"Key7\"", File.ReadAllText(path));
        Assert.Equal(StyleCycleKey.Key7, Configurable<Config>.Read(path).StyleCycleKey);
    }

    [Theory]
    [InlineData("\"Key99\"")]
    [InlineData("5")]
    [InlineData("{ \"a\": 1 }")]
    public void StyleCycleKey_UnreadableValueReadsAsKey4(string json)
    {
        var path = Path.Combine(TempDir(), "Config.json");
        File.WriteAllText(path, "{ \"Enabled\": false, \"StyleCycleKey\": " + json + " }");
        var c = Configurable<Config>.Read(path);
        Assert.Equal(StyleCycleKey.Key4, c.StyleCycleKey);
        Assert.False(c.Enabled);
        Assert.Null(c.LoadError);
    }

    [Fact]
    public void StyleCycleKey_DefaultsToKey4AndIsDescribed()
    {
        Assert.Equal(StyleCycleKey.Key4, new Config().StyleCycleKey);
        Assert.Equal(Tuning.StyleCycleKeyDefault, new Config().StyleCycleKey);
        Assert.EndsWith("StyleCycleKey=Key4.", Mod.DescribeConfig(new Config(), "src.json").Line);
    }
}
