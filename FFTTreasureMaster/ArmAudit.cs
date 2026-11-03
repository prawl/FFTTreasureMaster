using System;

namespace FFTTreasureMaster;

/// <summary>
/// Evaluates the arm gates for the Treasure Master module and holds no state of its own.
/// It reads through an injected IGameMemory, so it is testable without a live process. It
/// reads the PE build key, checks the terrain fingerprint, and audits each tile address,
/// classifying the bytes and asking TreasureMaster.DecideArm for the verdict.
///
/// This is a separate class, not a TreasureMaster partial. The state machine lives whole in
/// TreasureMaster.cs and this class is pure evaluation.
/// </summary>
internal sealed class ArmAudit
{
    private const long ELfanewOff = 0x3C;
    private const long TimeDateStampOff = 8;
    private const long SizeOfImageOff = 0x50;

    private readonly IGameMemory _mem;
    private readonly AddrMap _addrs;

    public ArmAudit(IGameMemory mem, AddrMap? addrs = null)
    {
        _mem = mem;
        _addrs = addrs ?? new AddrMap();
    }

    // ── PE build key ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the build key from the mapped game image. e_lfanew sits at base plus 0x3C,
    /// TimeDateStamp 8 bytes into the PE header and SizeOfImage 0x50 bytes in. Every read is
    /// Readable guarded, and any failed read returns null.
    /// </summary>
    public (uint TimeDateStamp, uint SizeOfImage)? ReadPeBuildKey()
    {
        long elfanewAddr = ImageBase.Base + ELfanewOff;
        if (!_mem.Readable(elfanewAddr, 4)) return null;
        uint eLfanew = ReadU32(_mem, elfanewAddr);

        long tsAddr = ImageBase.Base + eLfanew + TimeDateStampOff;
        if (!_mem.Readable(tsAddr, 4)) return null;

        long szAddr = ImageBase.Base + eLfanew + SizeOfImageOff;
        if (!_mem.Readable(szAddr, 4)) return null;

        return (ReadU32(_mem, tsAddr), ReadU32(_mem, szAddr));
    }

    // ── map-id read ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the map id byte behind a Readable guard and checks MapIdValid. False on any failed
    /// guard or invalid value. Shared here because TickDisarmed and TickArmed use the same gate.
    /// </summary>
    public bool TryReadMapId(out byte id)
    {
        id = 0;
        if (!_mem.Readable(_addrs.LiveBattleMapId, 1)) return false;
        id = _mem.U8(_addrs.LiveBattleMapId);
        return TreasureMaster.MapIdValid(id);
    }

    // ── fingerprint ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <see cref="TreasureMap.FpLen"/> bytes at <see cref="Offsets.TerrainGrid"/> and
    /// compares the fingerprint to <see cref="TreasureMap.FpHash"/>. False on a failed read,
    /// which the caller retries up to TreasureArmAttemptCap. A map with no fingerprint is a stub
    /// and returns false too, since it has no tiles anyway.
    ///
    /// Version 3 hashes fields 2 to 5 of each record, which survive the water and lava
    /// animation. Version 2 hashes field 0 only. Anything else hashes the raw bytes.
    /// </summary>
    public bool FingerprintMatches(TreasureMap map)
    {
        if (map.FpLen is not { } fpLen || map.FpHash is not { } expected)
            return false;

        if (!_mem.TryReadBytes(_addrs.TerrainGrid, fpLen, out var buf))
            return false;

        ulong got = map.FpVer switch
        {
            3 => TreasureMaster.MaskedTerrainHashV3(buf),
            2 => TreasureMaster.MaskedTerrainHash(buf),
            _ => TreasureMaster.Fnv1a64(buf),
        };

        return got == expected;
    }

    /// <summary>
    /// Diagnostic snapshot of the fingerprint comparison for one-shot mismatch logging:
    /// whether the terrain read succeeded, the computed hash, the expected hash, fpVer,
    /// and how many bytes were read. Pure read, no side effects.
    /// </summary>
    public (bool ReadOk, ulong Got, ulong Expected, int? FpVer, int Len) FingerprintDiag(TreasureMap map)
    {
        ulong expected = map.FpHash ?? 0;
        int fpLen = map.FpLen ?? 0;
        if (fpLen == 0 || !_mem.TryReadBytes(_addrs.TerrainGrid, fpLen, out var buf))
            return (false, 0, expected, map.FpVer, 0);
        ulong got = map.FpVer switch
        {
            3 => TreasureMaster.MaskedTerrainHashV3(buf),
            2 => TreasureMaster.MaskedTerrainHash(buf),
            _ => TreasureMaster.Fnv1a64(buf),
        };
        return (true, got, expected, map.FpVer, buf.Length);
    }

    // ── per-address audit ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads each tile address across all tiles, classifies each byte via
    /// <see cref="TreasureMaster.ClassifyAddr"/>, tallies ok/foreign/unreadable, and
    /// calls <see cref="TreasureMaster.DecideArm"/>. Returns the verdict + the foreign
    /// count so the caller can log on first occurrence.
    /// Foreign addresses contribute to the foreign tally but do not produce a Disarm verdict;
    /// the quorum check (<paramref name="minPlausible"/>) is the sole arm gate.
    /// </summary>
    public (TreasureMaster.ArmVerdict Verdict, int ForeignCount) AuditAddrs(
        TreasureMap map, int minPlausible)
    {
        int totalAddrs = 0;
        int okCount = 0, foreignCount = 0, unreadableCount = 0;
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                totalAddrs++;
                long live = ImageBase.At(addr);   // tile addrs are dataset space; read the LIVE image
                if (!_mem.Readable(live, 1)) { unreadableCount++; continue; }
                var state = TreasureMaster.ClassifyAddr(_mem.U8(live));
                if (state == TreasureMaster.AddrState.Foreign) foreignCount++;
                else okCount++;
            }
        }
        // Clamp the quorum to the actual address count so small maps can still arm.
        int effectiveMin = Math.Min(minPlausible, totalAddrs);
        return (TreasureMaster.DecideArm(okCount, foreignCount, unreadableCount,
                                          effectiveMin), foreignCount);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Little-endian U32 from four U8 reads. Caller ensures Readable before calling.</summary>
    private static uint ReadU32(IGameMemory mem, long addr)
        => (uint)(mem.U8(addr) | (mem.U8(addr + 1) << 8) | (mem.U8(addr + 2) << 16) | (mem.U8(addr + 3) << 24));
}
