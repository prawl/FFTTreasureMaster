using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Reads what the claim decision needs and holds no state, exactly like ArmAudit.
/// <see cref="CollectOccupied"/> scans the battle unit array at its fixed stride and collects
/// every live unit's grid position, which says which treasure tile a unit stands on.
/// <see cref="ReadCount"/> reads one item's inventory count.
///
/// TreasureMaster calls a tile claimed when a unit stands on it and its item count has risen.
/// The count only rises on a real claim by an eligible unit, and the position pins the exact
/// tile, so no eligibility byte is needed and maps that reuse a rare item id across tiles are
/// told apart by position.
///
/// Every read is Readable guarded and an unreadable slot is skipped. With claim detection off
/// these are never called, so no game memory is read.
/// </summary>
internal sealed class ClaimAudit
{
    private readonly IGameMemory _mem;
    private readonly AddrMap _addrs;

    public ClaimAudit(IGameMemory mem, AddrMap? addrs = null)
    {
        _mem = mem;
        _addrs = addrs ?? new AddrMap();
    }

    /// <summary>Adds the grid (x,y) of every readable, on-grid unit slot to
    /// <paramref name="occupied"/>. (0,0) is skipped (empty slots and non-tracking template copies
    /// read there); coords above 30 are skipped as off-grid garbage.</summary>
    public void CollectOccupied(ISet<(int, int)> occupied)
    {
        for (int k = 0; k < Offsets.UnitArraySlots; k++)
        {
            long xAddr = _addrs.UnitArrayBaseX + (long)k * Offsets.UnitRecordStride;
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
        long addr = _addrs.InventoryCountBase + itemId;
        if (!_mem.Readable(addr, 1)) return -1;
        return _mem.U8(addr);
    }
}
