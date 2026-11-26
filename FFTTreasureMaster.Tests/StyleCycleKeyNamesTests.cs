using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FFTTreasureMaster.Configuration;
using Xunit;

namespace FFTTreasureMaster.Tests;

public class StyleCycleKeyNamesTests
{
    private static readonly TypeConverter Names = TypeDescriptor.GetConverter(typeof(StyleCycleKey));

    [Fact]
    public void The_setting_says_what_the_key_does()
    {
        var prop = typeof(Config).GetProperty(nameof(Config.StyleCycleKey))!;
        Assert.Equal("Cycle Treasure Tile Hotkey *restart required",
            prop.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName);
        Assert.EndsWith("Default: 4 - Keyboard.", prop.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    [Theory]
    [InlineData(StyleCycleKey.Off, "Off")]
    [InlineData(StyleCycleKey.Key4, "4 - Keyboard")]
    [InlineData(StyleCycleKey.Key5, "5 - Keyboard")]
    [InlineData(StyleCycleKey.Key6, "6 - Keyboard")]
    [InlineData(StyleCycleKey.Key7, "7 - Keyboard")]
    [InlineData(StyleCycleKey.Key8, "8 - Keyboard")]
    [InlineData(StyleCycleKey.Key9, "9 - Keyboard")]
    public void The_settings_window_shows_each_choice_by_its_label(StyleCycleKey key, string label)
    {
        Assert.Equal(label, Names.ConvertToString(key));
        Assert.Equal(key, Names.ConvertFromString(label));
    }

    [Fact]
    public void A_saved_member_name_still_reads_back()
    {
        Assert.Equal(StyleCycleKey.Key7, Names.ConvertFromString("Key7"));
        Assert.Equal(StyleCycleKey.Off, Names.ConvertFromString("off"));
    }

    [Fact]
    public void The_window_offers_every_choice_once()
    {
        var offered = Names.GetStandardValues()!.Cast<StyleCycleKey>().ToArray();
        Assert.Equal(System.Enum.GetValues<StyleCycleKey>(), offered);
    }

    [Fact]
    public void The_settings_file_keeps_the_member_name()
    {
        var json = JsonSerializer.Serialize(new Config { StyleCycleKey = StyleCycleKey.Key5 },
            Configurable<Config>.SerializerOptions);
        Assert.Contains("\"StyleCycleKey\": \"Key5\"", json);
        Assert.DoesNotContain("Keyboard\"", json);
    }
}
