using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Stateless read-path for claim detection. No state of its own (the per-battle state lives in
/// TreasureMaster, exactly as ArmAudit). Provides the two reads the claim decision needs:
///
///   <see cref="CollectOccupied"/> -- scans the battle unit array (fixed 0x200 stride in the stable
///     0x14185xxxx segment; the LIVE grid position tracks there) and collects every live unit's
///     grid (x,y). Used to know WHICH treasure tile a unit is standing on.
///   <see cref="ReadCount"/> -- reads an item's inventory count (u8 @ InventoryCountBase + itemId).
///
/// TreasureMaster decides a tile is claimed when a unit stands on it AND its item count has risen:
/// the count only rises on an actual claim by an eligible unit (a Chemist, or a unit with Treasure
/// Hunter equipped), and the occupancy pins the exact tile -- so no per-unit eligibility byte is
/// needed, and maps that reuse a rare item id across tiles are disambiguated by position.
///
/// Every read is Readable-guarded; an unreadable slot/address is skipped (fail-safe). When the
/// caller leaves claim detection gated off, these are never called, so no game memory is read.
/// </summary>
internal sealed class ClaimAudit
{
    private readonly IGameMemory _mem;

    public ClaimAudit(IGameMemory mem) => _mem = mem;

    /// <summary>Adds the grid (x,y) of every readable, on-grid unit slot to
    /// <paramref name="occupied"/>. (0,0) is skipped (empty slots and non-tracking template copies
    /// read there); coords above 30 are skipped as off-grid garbage.</summary>
    public void CollectOccupied(ISet<(int, int)> occupied)
    {
        for (int k = 0; k < Offsets.UnitArraySlots; k++)
        {
            long xAddr = Offsets.UnitArrayBaseX + (long)k * Offsets.UnitRecordStride;
            if (!_mem.Readable(xAddr, 2)) continue;
            int x = _mem.U8(xAddr);
            int y = _mem.U8(xAddr + 1);
            if (x == 0 && y == 0) continue;   // empty slot / non-tracking template copy
            if (x > 30 || y > 30) continue;   // off-grid garbage
            occupied.Add((x, y));
        }
    }

    /// <summary>Reads the inventory count (0..99) for <paramref name="itemId"/>, or -1 when the id
    /// is not a real item (&lt;= 0) or the address is unreadable.</summary>
    public int ReadCount(int itemId)
    {
        if (itemId <= 0) return -1;
        long addr = Offsets.InventoryCountBase + itemId;
        if (!_mem.Readable(addr, 1)) return -1;
        return _mem.U8(addr);
    }
}
