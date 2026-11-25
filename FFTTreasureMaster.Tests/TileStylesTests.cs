using System;
using System.Linq;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// The tile style table and the byte policy built on the published style. Every Publish is undone
/// in a finally, which is safe because TestInit turns parallel tests off.
/// </summary>
public class TileStylesTests
{
    private static readonly TileStyle[] All = Enum.GetValues<TileStyle>();

    public static TheoryData<TileStyle> EveryStyle()
    {
        var d = new TheoryData<TileStyle>();
        foreach (var s in All) d.Add(s);
        return d;
    }

    [Fact]
    public void Table_has_exactly_the_six_pairs()
    {
        var expected = new (TileStyle, byte)[]
        {
            (TileStyle.BlueAndYellow, 0xCC), (TileStyle.BlueAndPink, 0xC0), (TileStyle.Green, 0xD8),
            (TileStyle.Yellow, 0x98), (TileStyle.Pink, 0xA8), (TileStyle.RedOutline, 0xF8),
        };
        Assert.Equal(expected.Length, TileStyles.Table.Count);
        foreach (var (s, b) in expected) Assert.Equal(b, TileStyles.Table[s]);
    }

    [Fact]
    public void Every_member_is_in_the_table_and_names_are_pinned_in_order()
    {
        Assert.Equal(All.Length, TileStyles.Table.Count);
        Assert.Equal(new[] { "BlueAndYellow", "BlueAndPink", "Green", "Yellow", "Pink", "RedOutline" },
            All.Select(s => s.ToString()).ToArray());
        for (int i = 0; i < All.Length; i++) Assert.Equal(i, (int)All[i]);
    }

    [Fact]
    public void Table_values_are_lit_even_unique_and_never_the_trap_marker()
    {
        var values = TileStyles.Table.Values.ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, v =>
        {
            Assert.NotEqual(0, v & 0x80);
            Assert.Equal(0, v & 0x01);
            Assert.NotEqual(0x80, v);
        });
    }

    [Fact]
    public void Default_is_BlueAndYellow_and_writes_the_old_byte()
    {
        Assert.Equal(TileStyle.BlueAndYellow, Tuning.TileStyleDefault);
        Assert.Equal(TileStyle.BlueAndYellow, default(TileStyle));
        Assert.Equal(0xCC, TileStyles.DefaultMark);
        Assert.Equal(TileStyles.DefaultMark, TileStyles.MarkOf(default));
    }

    [Fact]
    public void An_undefined_style_falls_back_to_the_default()
    {
        Assert.Equal(0xCC, TileStyles.MarkOf((TileStyle)99));
        try
        {
            TileStyles.Publish((TileStyle)99);
            Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
            Assert.Equal(0xCC, TileStyles.Mark);
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Theory]
    [MemberData(nameof(EveryStyle))]
    public void Policy_contract_holds_for_every_style(TileStyle s)
    {
        try
        {
            TileStyles.Publish(s);
            byte mark = TileStyles.MarkOf(s);
            Assert.Equal(mark, TileStyles.Mark);
            Assert.Equal(TreasureMaster.AddrState.Resting, TileStyles.Classify(0x00));
            Assert.Equal(TreasureMaster.AddrState.Resting, TileStyles.Classify(0x01));
            Assert.Equal(mark, TileStyles.WantWrite(0x00));
            Assert.Equal(mark, TileStyles.WantWrite(0x01));
            Assert.Equal(mark, TileStyles.WantWrite(0xFF));
            Assert.Equal(TreasureMaster.AddrState.Held, TileStyles.Classify(mark));
            foreach (var other in TileStyles.Table.Values.Where(v => v != mark))
                Assert.Equal(TreasureMaster.AddrState.Foreign, TileStyles.Classify(other));
            Assert.Equal(TreasureMaster.AddrState.Foreign, TileStyles.Classify(0x80));
            Assert.Equal(TreasureMaster.AddrState.Foreign, TileStyles.Classify((byte)(mark | 0x01)));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }
}
