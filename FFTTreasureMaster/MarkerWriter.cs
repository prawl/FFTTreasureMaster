using System;

namespace FFTTreasureMaster;

/// <summary>
/// Writes the game's own yellow move find diamonds onto the treasure tiles, and holds no state.
/// It parallels <see cref="TileHolder"/>, which holds the render flag, and like it never reads
/// the arm gates.
///
/// The game keeps an EnhancedMarkingUtility object whose marker array begins 8 bytes in with
/// a 0x18 byte stride. Setting a marker's Enabled field to 2 with the tile's grid X and Y lights
/// a yellow diamond there, and the game picks the colour. There are four marker slots, which is
/// the per map move find cap, so overflow only needs a defensive cap. Only the four fields we
/// set are written. The mechanism is adapted from dicene's FFT-MoveFind_Markers.
///
/// Gated dark. <see cref="Write"/> reads and writes nothing unless the writer is enabled in
/// Tuning and every runtime guard passes. The utility pointer is dicene's address and is not
/// verified against our build, so it stays off until a live probe confirms it. Every write goes
/// through the IGameMemory seam, never a raw deref.
/// </summary>
internal sealed class MarkerWriter
{
    // EnhancedMarker array layout on the utility object.
    internal const int ArrayOffset = 0x8;    // first marker = utilityBase + 0x8
    internal const int Stride = 0x18;   // bytes per marker entry
    internal const int FldEnabled = 0x0;    // int, 0 is off and 2 is on
    internal const int FldX = 0x8;    // int, grid X
    internal const int FldFloor = 0xC;    // int, floor level, 0 on single level maps
    internal const int FldY = 0x10;   // int, grid Y
    internal const int MaxSlots = 4;      // four marker slots exist
    internal const uint EnabledOn = 2;      // Enabled value that renders the yellow diamond

    private readonly IGameMemory _mem;
    private readonly bool _enabled;

    /// <param name="enabled">null = use Tuning.EnhancedMarkersEnabled (dark by default).
    /// When false, <see cref="Write"/> is a no-op that reads no game memory.</param>
    public MarkerWriter(IGameMemory mem, bool? enabled = null)
    {
        _mem = mem;
        _enabled = enabled ?? Tuning.EnhancedMarkersEnabled;
    }

    /// <summary>
    /// Resolves the marker array without writing, reporting whether the pointer slot is
    /// Readable, the dereferenced base, and whether the first entry is Writable. Powers the one
    /// shot failure diagnostic. All false and zero when gated off.
    /// </summary>
    internal (bool Readable, ulong Base, bool Writable) Resolve()
    {
        if (!_enabled) return (false, 0UL, false);
        if (!_mem.Readable(Offsets.EnhancedMarkingUtilityPtr, 8)) return (false, 0UL, false);
        ulong basePtr = _mem.U64(Offsets.EnhancedMarkingUtilityPtr);
        if (basePtr == 0) return (true, 0UL, false);
        bool writable = _mem.Writable(unchecked((long)basePtr) + ArrayOffset, Stride);
        return (true, basePtr, writable);
    }

    /// <summary>Points up to four native markers at the map's treasure tiles. Returns the number
    /// of marker slots written, 0 when gated off or any guard fails. Stateless, so safe to call
    /// every tick and from any thread.</summary>
    internal int Write(TreasureMap map)
    {
        // Deref the static utility pointer (guarded; never a raw deref).
        var (readable, basePtr, _) = Resolve();
        if (!readable || basePtr == 0) return 0;

        long arrayBase = unchecked((long)basePtr) + ArrayOffset;
        int count = Math.Min(map.Tiles.Count, MaxSlots);

        int written = 0;
        for (int i = 0; i < count; i++)
        {
            long entry = arrayBase + (long)Stride * i;
            if (!_mem.Writable(entry, Stride)) continue;   // unmapped, skip it and hold the rest

            var tile = map.Tiles[i];
            _mem.W32(entry + FldEnabled, EnabledOn);
            _mem.W32(entry + FldX, (uint)tile.X);
            _mem.W32(entry + FldFloor, 0);
            _mem.W32(entry + FldY, (uint)tile.Y);
            written++;
        }
        return written;
    }
}
