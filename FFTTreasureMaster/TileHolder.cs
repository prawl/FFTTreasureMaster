namespace FFTTreasureMaster;

/// <summary>
/// The write path for the Treasure Master module, with no state of its own.
/// ArmAudit reads and never writes, and TileHolder writes and never evaluates arm gates.
///
/// <see cref="Hold"/> visits each tile address in the map behind a Writable guard, classifies
/// the byte with <see cref="TreasureMaster.ClassifyAddr"/>, writes the mark on a Resting byte,
/// leaves a Held byte alone, and counts a Foreign byte without writing it, since those are off
/// screen render bytes from a camera pan that return to Resting when the tile scrolls back.
/// Returns the written and foreign counts so the caller can log the first foreign bytes
/// without re-reading memory.
/// </summary>
internal sealed class TileHolder
{
    private readonly IGameMemory _mem;

    public TileHolder(IGameMemory mem) => _mem = mem;

    /// <summary>
    /// Executes one hold pass over all tile addresses in <paramref name="map"/>.
    /// Returns the number of addresses written and the number of foreign addresses seen.
    /// Foreign addresses are never written; the caller decides what to do with the count.
    /// </summary>
    internal (int written, int foreign) Hold(TreasureMap map)
    {
        int written = 0, foreign = 0;
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                long live = ImageBase.At(addr);   // tile addrs are dataset space; write the LIVE image
                if (!_mem.Writable(live, 1)) continue;
                byte cur = _mem.U8(live);
                switch (TreasureMaster.ClassifyAddr(cur))
                {
                    case TreasureMaster.AddrState.Resting:
                        byte want = TreasureMaster.WantWrite(cur);
                        if (cur != want) { _mem.W8(live, want); written++; }
                        break;
                    case TreasureMaster.AddrState.Held:
                        break;
                    case TreasureMaster.AddrState.Foreign:
                        foreign++;
                        break;
                }
            }
        }
        return (written, foreign);
    }

    /// <summary>
    /// Reverts one tile's Held bytes to their captured resting value. The Writable guard comes
    /// first, and Foreign and Resting bytes are never written. Returns how many addresses were
    /// reverted. This is the module's only clear path, needed because the engine does not
    /// reliably re-clear the mark on static maps when claim detection removes a tile mid battle.
    /// </summary>
    internal int Unlight(TreasureTile tile)
    {
        int n = 0;
        foreach (var (addr, off) in tile.Addrs)
        {
            long live = ImageBase.At(addr);   // tile addrs are dataset space; write the LIVE image
            if (!_mem.Writable(live, 1)) continue;
            byte cur = _mem.U8(live);
            if (TreasureMaster.ClassifyAddr(cur) == TreasureMaster.AddrState.Held)
            {
                _mem.W8(live, off);
                n++;
            }
        }
        return n;
    }
}
