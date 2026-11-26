namespace FFTTreasureMaster;

/// <summary>
/// Makes a battle's treasure copy give rare items by putting each row's rare id in its common slot.
/// A row is a tile byte, a flags byte, the rare id and the common id. The pickup reads only this
/// copy, so the main table patch alone misses a battle whose copy came from anywhere else. A rare
/// id of 0 marks an unused slot and is never written, and each written word is read back.
/// </summary>
internal static class RareCopyPatch
{
    public const int RowBytes = 6;
    public const int CopyBytes = Tuning.TrapSlots * RowBytes;

    public static RareCopyResult Apply(IGameMemory mem, long copyAddr)
    {
        if (!mem.TryReadBytes(copyAddr, CopyBytes, out var before) || before.Length < CopyBytes)
            return new RareCopyResult(false, 0, 0, null);

        int changed = 0, failed = 0;
        for (int row = 0; row < Tuning.TrapSlots; row++)
        {
            int at = row * RowBytes;
            ushort rare = Mem.U16(before, at + 2);
            if (rare == 0 || Mem.U16(before, at + 4) == rare) continue;

            long common = copyAddr + at + 4;
            if (!mem.Writable(common, 2)) { failed++; continue; }
            mem.W8(common, (byte)rare);
            mem.W8(common + 1, (byte)(rare >> 8));
            if (mem.U16(common) == rare) changed++;
            else failed++;
        }
        return new RareCopyResult(true, changed, failed, before);
    }
}

/// <summary>What one pass did. Before holds the rows as read, for the log.</summary>
internal readonly record struct RareCopyResult(bool Read, int Changed, int Failed, byte[]? Before);
