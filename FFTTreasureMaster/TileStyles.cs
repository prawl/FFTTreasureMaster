using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// The look of a lit treasure tile, saved by name in the config. Order is frozen, and the first
/// member is the default look, so default(TileStyle) is the shipped one.
/// </summary>
public enum TileStyle { BlueAndYellow, BlueAndPink, Green, Yellow, Pink, RedOutline }

/// <summary>
/// The render flag byte each style writes, and the per byte rules built on the style chosen at
/// launch. The byte value picks the look, so a held byte is exactly the mark. Bit 0x80 alone is
/// the player's trap marker and is never a mark.
/// </summary>
internal static class TileStyles
{
    internal const byte DefaultMark = 0xCC;

    internal static readonly IReadOnlyDictionary<TileStyle, byte> Table = new Dictionary<TileStyle, byte>
    {
        [TileStyle.BlueAndYellow] = DefaultMark,
        [TileStyle.BlueAndPink] = 0xC0,
        [TileStyle.Green] = 0xD8,
        [TileStyle.Yellow] = 0x98,
        [TileStyle.Pink] = 0xA8,
        [TileStyle.RedOutline] = 0xF8,
    };

    // Style and mark travel together so a reader on another thread never sees a torn pair.
    private sealed record Snapshot(TileStyle Style, byte Mark);

    private static volatile Snapshot _current = new(default, DefaultMark);

    internal static TileStyle Current => _current.Style;
    internal static byte Mark => _current.Mark;

    internal static byte MarkOf(TileStyle style) => Table.TryGetValue(style, out var m) ? m : DefaultMark;

    internal static void Publish(TileStyle style) =>
        _current = Table.ContainsKey(style) ? new Snapshot(style, MarkOf(style)) : new Snapshot(default, DefaultMark);

    internal static TreasureMaster.AddrState Classify(byte cur)
    {
        if (cur == Mark) return TreasureMaster.AddrState.Held;
        if (cur == 0x00 || cur == 0x01) return TreasureMaster.AddrState.Resting;
        return TreasureMaster.AddrState.Foreign;
    }

    internal static byte WantWrite(byte cur) => Mark;
}
