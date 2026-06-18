using System;

namespace FFTTreasureMaster;

/// <summary>
/// Stateless write-path for the native EnhancedMarker treasure highlight -- the yellow
/// move-find diamonds. Parallel to <see cref="TileHolder"/> (the 0x80 render-flag path):
/// ArmAudit never writes, TileHolder holds the render flag, MarkerWriter drives the game's
/// own marker objects. All three keep the read-path / write-path split.
///
/// Mechanism (adapted from dicene's FFT-MoveFind_Markers): the game keeps an
/// EnhancedMarkingUtility object whose marker array begins at +0x8 with a 0x18-byte stride.
/// Each marker is { int Enabled@0, int field_4@4, int X@8, int Floor@0xC, int Y@0x10,
/// int field_14@0x14 }. Setting Enabled=2 with the tile's grid (X,Y) lights a yellow diamond
/// on that tile -- the game renders it, so we never pick a colour. There are exactly four
/// marker slots, which is exactly the per-map move-find cap (MapTrapFormationData carries
/// <= 4 slots per map), so the only overflow handling needed is a defensive cap.
///
/// We write only the four fields we set (Enabled, X, Floor, Y) and leave field_4 / field_14
/// untouched -- no read-modify-write of the whole struct.
///
/// GATED DARK. <see cref="Write"/> is a guaranteed no-op -- it reads no game memory and writes
/// nothing -- unless BOTH (a) the writer is enabled (Tuning.EnhancedMarkersEnabled, off by
/// default) AND (b) every runtime guard passes: the utility pointer slot is Readable, the
/// dereferenced pointer is non-null, and the target marker entry is Writable. The pointer
/// (Offsets.EnhancedMarkingUtilityPtr) is dicene's address and is NOT yet verified against our
/// 1.5 build, so it stays off until a live probe confirms it; the build-key L0 gate in
/// TreasureMaster is the global safety net if the game is ever patched.
///
/// Every field write goes through the IGameMemory seam (Readable/Writable pre-filter; RPM/WPM
/// the real safety net), NEVER a raw deref -- same contract as the rest of the runtime.
/// </summary>
internal sealed class MarkerWriter
{
    // EnhancedMarker array layout on the utility object.
    internal const int  ArrayOffset = 0x8;    // first marker = utilityBase + 0x8
    internal const int  Stride      = 0x18;   // bytes per marker entry
    internal const int  FldEnabled  = 0x0;    // int: 0 = off, 2 = on (yellow diamond)
    internal const int  FldX        = 0x8;    // int: grid X
    internal const int  FldFloor    = 0xC;    // int: floor/level (0 for single-level maps)
    internal const int  FldY        = 0x10;   // int: grid Y
    internal const int  MaxSlots    = 4;      // hardware cap: four marker slots exist
    internal const uint EnabledOn   = 2;      // Enabled value that renders the yellow diamond

    private readonly IGameMemory _mem;
    private readonly bool _enabled;

    /// <param name="enabled">null = use Tuning.EnhancedMarkersEnabled (dark by default).
    /// When false, <see cref="Write"/> is a no-op that reads no game memory.</param>
    public MarkerWriter(IGameMemory mem, bool? enabled = null)
    {
        _mem     = mem;
        _enabled = enabled ?? Tuning.EnhancedMarkersEnabled;
    }

    /// <summary>
    /// Points up to four native markers at the map's treasure tiles (Enabled=2, X, Y, Floor=0).
    /// Returns the number of marker slots written; 0 when gated off or any guard fails.
    /// Stateless and idempotent -- safe to call every tick and from any thread.
    /// </summary>
    /// <summary>
    /// Resolves the marker array WITHOUT writing: (pointer-slot Readable, dereferenced base,
    /// first-entry Writable). Powers the one-shot failure diagnostic so a write that lands
    /// nothing reports which guard failed. All-false/zero when gated off.
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

    internal int Write(TreasureMap map)
    {
        // Deref the static utility pointer (guarded; never a raw deref).
        var (readable, basePtr, _) = Resolve();
        if (!readable || basePtr == 0) return 0;

        long arrayBase = unchecked((long)basePtr) + ArrayOffset;
        int  count     = Math.Min(map.Tiles.Count, MaxSlots);

        int written = 0;
        for (int i = 0; i < count; i++)
        {
            long entry = arrayBase + (long)Stride * i;
            if (!_mem.Writable(entry, Stride)) continue;   // off-screen / unmapped: skip, hold the rest

            var tile = map.Tiles[i];
            _mem.W32(entry + FldEnabled, EnabledOn);
            _mem.W32(entry + FldX,       (uint)tile.X);
            _mem.W32(entry + FldFloor,   0);
            _mem.W32(entry + FldY,       (uint)tile.Y);
            written++;
        }
        return written;
    }
}
