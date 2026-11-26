using System.Collections.Generic;
using Xunit;

namespace FFTTreasureMaster.Tests;

internal sealed class FakeKeys : IKeySource
{
    public readonly HashSet<int> Down = new();
    public readonly List<int> Reads = new();
    public bool Focus = true;

    public bool IsDown(int virtualKey)
    {
        Reads.Add(virtualKey);
        return Down.Contains(virtualKey);
    }

    public bool GameHasFocus() => Focus;
}

/// <summary>
/// The key edge rules. One press fires once, and a press that began while a gate was shut never
/// fires later.
/// </summary>
public class StyleHotkeyTests
{
    private const int Four = 0x34;

    private static (StyleHotkey hk, FakeKeys keys) Make(StyleCycleKey key = StyleCycleKey.Key4)
    {
        var keys = new FakeKeys();
        return (new StyleHotkey(key, keys), keys);
    }

    [Fact]
    public void A_down_edge_fires_once_and_again_after_a_release()
    {
        var (hk, keys) = Make();
        Assert.False(hk.Poll(true));
        keys.Down.Add(Four);
        Assert.True(hk.Poll(true));
        for (int i = 0; i < 20; i++) Assert.False(hk.Poll(true));
        keys.Down.Clear();
        Assert.False(hk.Poll(true));
        keys.Down.Add(Four);
        Assert.True(hk.Poll(true));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void A_held_modifier_blocks_the_press_and_carries_no_edge(int modifier)
    {
        var (hk, keys) = Make();
        keys.Down.Add(modifier);
        keys.Down.Add(Four);
        Assert.False(hk.Poll(true));
        keys.Down.Remove(modifier);
        Assert.False(hk.Poll(true));
    }

    [Fact]
    public void No_focus_blocks_the_press_and_carries_no_edge()
    {
        var (hk, keys) = Make();
        keys.Focus = false;
        keys.Down.Add(Four);
        Assert.False(hk.Poll(true));
        keys.Focus = true;
        Assert.False(hk.Poll(true));
    }

    [Fact]
    public void Not_allowed_blocks_the_press_and_carries_no_edge()
    {
        var (hk, keys) = Make();
        keys.Down.Add(Four);
        Assert.False(hk.Poll(false));
        Assert.False(hk.Poll(true));
    }

    [Fact]
    public void Off_never_fires_and_never_reads_a_key()
    {
        var (hk, keys) = Make(StyleCycleKey.Off);
        keys.Down.Add(Four);
        for (int i = 0; i < 5; i++) Assert.False(hk.Poll(true));
        Assert.Empty(keys.Reads);
    }

    [Theory]
    [InlineData(StyleCycleKey.Key4, 0x34, "4")]
    [InlineData(StyleCycleKey.Key5, 0x35, "5")]
    [InlineData(StyleCycleKey.Key6, 0x36, "6")]
    [InlineData(StyleCycleKey.Key7, 0x37, "7")]
    [InlineData(StyleCycleKey.Key8, 0x38, "8")]
    [InlineData(StyleCycleKey.Key9, 0x39, "9")]
    [InlineData(StyleCycleKey.Off, 0, "")]
    public void Keys_map_to_the_top_row_digits(StyleCycleKey key, int vk, string digit)
    {
        Assert.Equal(vk, StyleHotkey.VirtualKey(key));
        Assert.Equal(digit, StyleHotkey.Digit(key));
    }

    [Fact]
    public void The_number_pad_is_never_read()
    {
        foreach (var key in System.Enum.GetValues<StyleCycleKey>())
        {
            var (hk, keys) = Make(key);
            if (key != StyleCycleKey.Off) keys.Down.Add(StyleHotkey.VirtualKey(key));
            Assert.Equal(key != StyleCycleKey.Off, hk.Poll(true));
            Assert.DoesNotContain(keys.Reads, r => r >= 0x64 && r <= 0x69);
        }
    }
}
