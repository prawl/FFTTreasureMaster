using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Lists the live game image's executable sections from its PE headers, through guarded reads.
/// Both signature resolvers scan only these. A section larger than the scan cap is left out, and
/// any unreadable header gives null.
/// </summary>
internal static class PeSections
{
    public static List<(long va, long size)>? Executable(IGameMemory mem)
    {
        if (!ReadU32(mem, ImageBase.Base + 0x3C, out uint eLfanew)) return null;
        long peAddr = ImageBase.Base + eLfanew;
        if (!ReadU16(mem, peAddr + 6, out ushort numSections)) return null;
        if (!ReadU16(mem, peAddr + 20, out ushort sizeOfOptionalHeader)) return null;

        long sectionTableAddr = peAddr + 24 + sizeOfOptionalHeader;
        var sections = new List<(long, long)>();
        for (int i = 0; i < numSections; i++)
        {
            long shAddr = sectionTableAddr + i * 40;
            if (!ReadU32(mem, shAddr + 8, out uint virtualSize)) return null;
            if (!ReadU32(mem, shAddr + 12, out uint virtualAddress)) return null;
            if (!ReadU32(mem, shAddr + 36, out uint characteristics)) return null;

            const uint ImageScnMemExecute = 0x20000000;
            if ((characteristics & ImageScnMemExecute) == 0) continue;
            if (virtualSize > Tuning.AnchorScanMaxSectionBytes) continue;

            sections.Add((ImageBase.Base + virtualAddress, virtualSize));
        }
        return sections;
    }

    private static bool ReadU32(IGameMemory mem, long addr, out uint value)
    {
        value = 0;
        if (!mem.Readable(addr, 4)) return false;
        value = (uint)(mem.U8(addr) | (mem.U8(addr + 1) << 8) | (mem.U8(addr + 2) << 16) | (mem.U8(addr + 3) << 24));
        return true;
    }

    private static bool ReadU16(IGameMemory mem, long addr, out ushort value)
    {
        value = 0;
        if (!mem.Readable(addr, 2)) return false;
        value = (ushort)(mem.U8(addr) | (mem.U8(addr + 1) << 8));
        return true;
    }
}
