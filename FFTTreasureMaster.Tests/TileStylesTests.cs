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
            TileStyles.Publish(TileStyle.Green);
            TileStyles.Publish((TileStyle)99);
            Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
            Assert.Equal(0xCC, TileStyles.Mark);
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Theory]
    [InlineData(0x00, "Resting")]
    [InlineData(0x01, "Resting")]
    [InlineData(0xCC, "Held")]
    [InlineData(0x02, "Foreign")]
    [InlineData(0x40, "Foreign")]
    [InlineData(0x7F, "Foreign")]
    [InlineData(0x80, "Foreign")]
    [InlineData(0x81, "Foreign")]
    [InlineData(0xCB, "Foreign")]
    [InlineData(0xCD, "Foreign")]
    [InlineData(0xFF, "Foreign")]
    public void Default_style_classifies_the_full_truth_table(byte cur, string expected)
    {
        Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
        Assert.Equal(Enum.Parse<TreasureMaster.AddrState>(expected), TileStyles.Classify(cur));
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

    private static TreasureMaster.AddrState St(byte b) => TileStyles.Classify(b);

    [Fact]
    public void Publish_empties_the_retired_set()
    {
        try
        {
            TileStyles.Publish(TileStyle.BlueAndYellow);
            TileStyles.SwapTo(TileStyle.Green);
            TileStyles.Publish(TileStyle.Green);
            Assert.Equal(TreasureMaster.AddrState.Foreign, St(0xCC));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Fact]
    public void SwapTo_retires_the_old_mark()
    {
        try
        {
            TileStyles.Publish(TileStyle.BlueAndYellow);
            Assert.True(TileStyles.SwapTo(TileStyle.Green));
            Assert.Equal(TreasureMaster.AddrState.Stale, St(0xCC));
            Assert.Equal(TreasureMaster.AddrState.Held, St(0xD8));
            Assert.Equal(TileStyle.Green, TileStyles.Current);
            Assert.Equal(0xD8, TileStyles.Mark);
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Fact]
    public void SwapTo_current_or_unlisted_changes_nothing()
    {
        try
        {
            TileStyles.Publish(TileStyle.Green);
            Assert.False(TileStyles.SwapTo(TileStyle.Green));
            Assert.False(TileStyles.SwapTo((TileStyle)99));
            Assert.Equal(TileStyle.Green, TileStyles.Current);
            Assert.Equal(TreasureMaster.AddrState.Foreign, St(0xCC));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Fact]
    public void Next_walks_every_style_and_wraps()
    {
        foreach (var start in All)
        {
            var seen = new System.Collections.Generic.HashSet<TileStyle>();
            var s = start;
            for (int i = 0; i < All.Length; i++) { seen.Add(s); s = TileStyles.Next(s); }
            Assert.Equal(All.Length, seen.Count);
            Assert.Equal(start, s);
        }
        Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Next(TileStyle.RedOutline));
        Assert.Equal(Tuning.TileStyleDefault, TileStyles.Next((TileStyle)99));
    }

    [Fact]
    public void After_a_full_wrap_the_mark_is_held_and_the_rest_are_stale()
    {
        try
        {
            TileStyles.Publish(TileStyle.BlueAndYellow);
            for (int i = 0; i < All.Length; i++) TileStyles.SwapTo(TileStyles.Next(TileStyles.Current));
            Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
            foreach (var s in All)
                Assert.Equal(s == TileStyle.BlueAndYellow ? TreasureMaster.AddrState.Held
                                                          : TreasureMaster.AddrState.Stale,
                    St(TileStyles.Table[s]));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Fact]
    public void ClearRetired_keeps_the_look_and_empties_the_set()
    {
        try
        {
            TileStyles.Publish(TileStyle.BlueAndYellow);
            TileStyles.SwapTo(TileStyle.Green);
            TileStyles.ClearRetired();
            Assert.Equal(TileStyle.Green, TileStyles.Current);
            Assert.Equal(0xD8, TileStyles.Mark);
            Assert.Equal(TreasureMaster.AddrState.Foreign, St(0xCC));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Fact]
    public void Only_retired_listed_marks_are_ever_stale()
    {
        try
        {
            TileStyles.Publish(TileStyle.BlueAndYellow);
            foreach (var s in All) TileStyles.SwapTo(s);
            var marks = TileStyles.Table.Values.ToHashSet();
            for (int b = 0; b < 256; b++)
                if (!marks.Contains((byte)b))
                    Assert.NotEqual(TreasureMaster.AddrState.Stale, St((byte)b));
            Assert.NotEqual(TreasureMaster.AddrState.Stale, St(0x80));
            Assert.Equal(TreasureMaster.AddrState.Resting, St(0x00));
            Assert.Equal(TreasureMaster.AddrState.Resting, St(0x01));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }

    [Fact]
    public void A_retired_mark_plus_one_is_foreign()
    {
        try
        {
            TileStyles.Publish(TileStyle.BlueAndYellow);
            TileStyles.SwapTo(TileStyle.Green);
            Assert.Equal(TreasureMaster.AddrState.Foreign, St(0xCD));
        }
        finally { TileStyles.Publish(Tuning.TileStyleDefault); }
    }
}
