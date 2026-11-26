using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Finds the battle's treasure copy from three code sites and trusts it only when all agree.
/// The copy routine's store and the restore's store name it RIP relative. The pickup lookup adds
/// an offset to the image base it loads into r13 just before, so that lea must land on the image
/// base for its offset to count. Each pattern must match exactly once, or nothing comes back and
/// Why names the reason. The patterns were cut from build 6A5E4A1B with every moving
/// displacement wildcarded.
/// </summary>
internal static class TrapCopySites
{
    internal static readonly AnchorSig CopyStore = Sig("TrapCopyStore",
        "48 89 5C 24 08 57 48 83 EC 20 0F B7 D9 33 D2 48 8D 0D ?? ?? ?? ?? 41 B8 B8 0B 00 00 " +
        "48 8D 04 5B 0F 10 04 C1 F2 0F 10 4C C1 10 48 8D 0D ?? ?? ?? ?? 0F 29 05 ?? ?? ?? ??", 52);

    internal static readonly AnchorSig RestoreStore = Sig("TrapRestoreStore",
        "0F 28 05 ?? ?? ?? ?? 8B D6 F2 0F 10 0D ?? ?? ?? ?? 88 05 ?? ?? ?? ?? 0F B7 05 ?? ?? ?? ?? " +
        "66 89 05 ?? ?? ?? ?? 8A 05 ?? ?? ?? ?? 88 05 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 89 05 ?? ?? ?? ?? " +
        "8A 05 ?? ?? ?? ?? 88 05 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 89 05 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? " +
        "0F 29 05 ?? ?? ?? ??", 94);

    internal static readonly AnchorSig LookupRead = Sig("TrapLookupRead",
        "88 05 ?? ?? ?? ?? 88 1D ?? ?? ?? ?? 8B F7 4C 8D 2D ?? ?? ?? ?? 48 63 C6 48 8D 2C 40 " +
        "41 8A 8C 6D ?? ?? ?? ?? 8A C1 C0 E8 04 41 3A C4 75 18 80 E1 0F 41 3A CF", 32);

    internal const int LookupBaseDispOff = 17;

    private static readonly AnchorSig[] All = { CopyStore, RestoreStore, LookupRead };

    public static TrapCopyFind Find(IGameMemory mem)
    {
        var sections = PeSections.Executable(mem);
        return sections is null ? new(null, "the game image's headers could not be read") : Find(mem, sections);
    }

    public static TrapCopyFind Find(IGameMemory mem, IReadOnlyList<(long va, long size)> sections)
    {
        var scan = new AnchorScan(mem);
        var hits = scan.FindAll(sections, All);
        if (hits is null) return new(null, "the code scan could not read the game image");
        foreach (var sig in All)
            if (hits[sig.Name].Count != 1) return new(null, $"{sig.Name} matched {hits[sig.Name].Count} times, not once");

        long lookup = hits[LookupRead.Name][0];
        if (scan.ResolveTarget(lookup, LookupRead with { DispOff = LookupBaseDispOff }) != ImageBase.Base)
            return new(null, "the lookup's base is not the image base");

        long? copy = scan.ResolveTarget(hits[CopyStore.Name][0], CopyStore);
        long? restore = scan.ResolveTarget(hits[RestoreStore.Name][0], RestoreStore);
        long? read = mem.TryReadBytes(lookup + LookupRead.DispOff, 4, out var b) ? ImageBase.Base + Mem.U32(b, 0) : null;
        if (copy is null || restore is null || read is null) return new(null, "a code site could not be read");
        if (copy != restore || copy != read)
            return new(null, $"the sites disagree, {copy:X} {restore:X} {read:X}");
        return new(copy, "three code sites agree");
    }

    private static AnchorSig Sig(string name, string pattern, int dispOff)
        => new(name, AnchorDb.ParsePattern(pattern)!, dispOff, 0, 0);
}

/// <summary>The copy's live address when the sites agree, and why not when they do not.</summary>
internal readonly record struct TrapCopyFind(long? Addr, string Why);
