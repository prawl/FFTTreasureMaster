using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FFTTreasureMaster.Configuration;
using Xunit;

namespace FFTTreasureMaster.Tests;

public class SettingLabelsTests
{
    private const string Mark = " *restart";
    private const string Opener = "Needs a game restart. ";

    private static PropertyInfo[] Settings() => typeof(Config)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetCustomAttribute<DisplayNameAttribute>() != null).ToArray();

    private static string Label(string property) =>
        typeof(Config).GetProperty(property)!.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName;

    private static string Help(string property) =>
        typeof(Config).GetProperty(property)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void Exactly_the_settings_read_once_at_launch_carry_the_restart_mark()
    {
        var marked = Settings().Where(p => Label(p.Name).EndsWith(Mark)).Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(new[]
        {
            nameof(Config.AllUnitsTreasureHunter), nameof(Config.Enabled), nameof(Config.HideClaimedTiles),
            nameof(Config.RareTreasureOnly), nameof(Config.StyleCycleKey),
        }, marked);
    }

    [Fact]
    public void The_live_setting_carries_no_restart_mark()
    {
        Assert.Equal("Tile highlight style", Label(nameof(Config.TileStyle)));
        Assert.DoesNotContain("restart", Label(nameof(Config.TileStyle)));
        Assert.DoesNotContain("restart", Help(nameof(Config.TileStyle)));
    }

    [Theory]
    [InlineData(nameof(Config.Enabled), "Enable Treasure Master")]
    [InlineData(nameof(Config.HideClaimedTiles), "Hide claimed tiles")]
    [InlineData(nameof(Config.AllUnitsTreasureHunter), "All units Treasure Hunter")]
    [InlineData(nameof(Config.RareTreasureOnly), "Rare treasure only")]
    [InlineData(nameof(Config.StyleCycleKey), "Cycle Tile Hotkey")]
    public void Each_marked_setting_has_a_short_name_and_says_restart_first(string property, string name)
    {
        Assert.Equal(name + Mark, Label(property));
        Assert.StartsWith(Opener, Help(property));
        Assert.DoesNotContain("Changes apply the next time the game is launched", Help(property));
    }

    // The launcher's name column showed about 33 characters before it cut a name short.
    [Fact]
    public void Every_name_fits_the_launcher_column()
    {
        foreach (var p in Settings())
            Assert.True(Label(p.Name).Length <= 34, Label(p.Name));
    }

    [Fact]
    public void Rare_treasure_help_claims_nothing_unseen_about_saved_battles()
    {
        string help = Help(nameof(Config.RareTreasureOnly));

        Assert.DoesNotContain("saved mid fight", help);
        Assert.DoesNotContain("keeps its normal items", help);
    }

    [Fact]
    public void Rare_treasure_help_does_not_say_it_needs_the_mod_loader()
    {
        string help = Help(nameof(Config.RareTreasureOnly));

        Assert.DoesNotContain("quietly does nothing", help);
        Assert.DoesNotContain("Requires the separately installed", help);
    }

    [Fact]
    public void The_style_help_names_the_hotkey_by_its_launcher_name()
    {
        Assert.Contains("Cycle Tile Hotkey", Help(nameof(Config.TileStyle)));
        Assert.DoesNotContain("Cycle Treasure Tile Hotkey", Help(nameof(Config.TileStyle)));
    }

    [Fact]
    public void The_hotkey_choices_are_the_plain_key_names()
    {
        Assert.EndsWith("Default: Key4.", Help(nameof(Config.StyleCycleKey)));
        Assert.Equal("Key5", TypeDescriptor.GetConverter(typeof(StyleCycleKey)).ConvertToString(StyleCycleKey.Key5));
        var json = JsonSerializer.Serialize(new Config { StyleCycleKey = StyleCycleKey.Key5 },
            Configurable<Config>.SerializerOptions);
        Assert.Contains("\"StyleCycleKey\": \"Key5\"", json);
    }
}
