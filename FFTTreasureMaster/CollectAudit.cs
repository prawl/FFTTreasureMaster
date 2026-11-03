namespace FFTTreasureMaster;

/// <summary>
/// Reads whether a tile's treasure was already collected and the battle won in an earlier battle.
/// The game records that permanently, so such a tile must never be highlighted again.
///
/// The table is a 64 byte bitfield at Offsets.TreasureCollectedBase, 128 maps by 4 slots, most
/// significant bit first. The bit index is the map id times 4 plus the slot.
///
/// Fails safe by construction. Any unreadable address, unknown slot or out of range map id
/// returns false without hiding a tile, so the worst case is a lit tile whose treasure is
/// gone. Holds no state of its own, exactly like ArmAudit and ClaimAudit.
/// </summary>
internal sealed class CollectAudit
{
    private readonly IGameMemory _mem;
    private readonly AddrMap _addrs;

    public CollectAudit(IGameMemory mem, AddrMap? addrs = null)
    {
        _mem = mem;
        _addrs = addrs ?? new AddrMap();
    }

    /// <summary>True when the game records <paramref name="tile"/>'s treasure on map
    /// <paramref name="mapId"/> as collected in a prior battle. False on any unreadable address,
    /// unknown slot or out of range input, so a tile is never hidden on an unconfirmed
    /// read.</summary>
    public bool IsCollected(int mapId, TreasureTile tile)
    {
        if (_addrs.TreasureCollectedBase == 0) return false;   // address unknown, so stay inert
        int slot = tile.Slot;
        if (mapId < 0 || mapId > 127 || slot < 0 || slot > 3) return false;   // out of range, fail safe
        int idx = mapId * 4 + slot;
        long addr = _addrs.TreasureCollectedBase + idx / 8;
        if (!_mem.Readable(addr, 1)) return false;              // unreadable, so never hide the tile
        int bit = 7 - (idx % 8);                                 // MSB-first
        return ((_mem.U8(addr) >> bit) & 1) != 0;
    }
}
