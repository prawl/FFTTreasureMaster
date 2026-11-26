using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FFTTreasureMaster.Configuration;
using Xunit;

namespace FFTTreasureMaster.Tests;

public class SettingLabelsTests
{
    private const string Mark = " *restart required";

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
    }

    [Theory]
    [InlineData(nameof(Config.Enabled), "Enable Treasure Master")]
    [InlineData(nameof(Config.HideClaimedTiles), "Hide tiles after their treasure is claimed")]
    [InlineData(nameof(Config.AllUnitsTreasureHunter), "All units gain Treasure Hunter")]
    [InlineData(nameof(Config.RareTreasureOnly), "Rare treasure only")]
    [InlineData(nameof(Config.StyleCycleKey), "Cycle Treasure Tile Hotkey")]
    public void Each_marked_label_keeps_its_plain_name_before_the_mark(string property, string name)
    {
        Assert.Equal(name + Mark, Label(property));
    }

    [Fact]
    public void Rare_treasure_says_a_battle_begun_before_it_keeps_its_normal_items()
    {
        Assert.Contains("A battle begun before this setting took effect keeps its normal items, even when " +
                        "resumed with Continue; new battles give the rare ones.", Help(nameof(Config.RareTreasureOnly)));
    }

    [Fact]
    public void The_style_help_names_the_hotkey_by_its_launcher_name()
    {
        Assert.Contains("Cycle Treasure Tile Hotkey", Help(nameof(Config.TileStyle)));
        Assert.DoesNotContain("Tile style key", Help(nameof(Config.TileStyle)));
    }
}
