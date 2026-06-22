namespace FFTTreasureMaster;

/// <summary>
/// Stateless read-path for PERSISTENT collected-treasure detection: "has this tile's Move-Find
/// treasure already been picked up AND the battle won in a PRIOR battle?" The game records that
/// permanently (it survives across battles, including random battles on a story map), so a tile
/// the game considers collected must never be highlighted again.
///
/// The table is a 64-byte bitfield at Offsets.TreasureCollectedBase (128 maps x 4 slots = 512
/// bits, MSB-first within each byte). For a tile: idx = mapId * 4 + slot; byte address =
/// base + idx / 8; bit = 7 - (idx % 8). Collected when that bit is 1.
///
/// Still fails safe by construction: any unreadable address, unknown slot, or out-of-range
/// mapId returns false without hiding a tile (worst case: a tile stays lit though its
/// treasure is gone, never the reverse). The Readable guard is the safety net.
///
/// Holds no state of its own (the per-battle collected set lives in TreasureMaster, exactly
/// as ArmAudit/ClaimAudit do).
/// </summary>
internal sealed class CollectAudit
{
    private readonly IGameMemory _mem;

    public CollectAudit(IGameMemory mem) => _mem = mem;

    /// <summary>True when the game records <paramref name="tile"/>'s Move-Find treasure (on map
    /// <paramref name="mapId"/>) as already collected in a prior battle. Returns false on any
    /// unreadable address, unknown slot (Slot == -1), or out-of-range input -- fail-safe, never
    /// hides a tile based on an unconfirmed read.</summary>
    public bool IsCollected(int mapId, TreasureTile tile)
    {
        if (Offsets.TreasureCollectedBase == 0) return false;   // address unknown: inert (kept for safety)
        int slot = tile.Slot;
        if (mapId < 0 || mapId > 127 || slot < 0 || slot > 3) return false;   // unknown/out-of-range: fail-safe
        int idx  = mapId * 4 + slot;
        long addr = Offsets.TreasureCollectedBase + idx / 8;
        if (!_mem.Readable(addr, 1)) return false;              // unreadable: fail-safe (never hide a tile)
        int bit = 7 - (idx % 8);                                 // MSB-first
        return ((_mem.U8(addr) >> bit) & 1) != 0;
    }
}
