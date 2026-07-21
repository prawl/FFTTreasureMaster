using System.Collections.Generic;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// AnchorResolver: PE section walk + one AnchorScan pass + the per-entity delta cross-checks,
/// mirror-set invariant, R2/R4 derivation, and required-set/feature-flag policy. Builds a tiny
/// fake PE (header + section table) and a synthetic 7-region/10-singleton anchor table (own
/// bases/targets, NOT the real dataset -- only the shape matters) entirely in FakeSparseMemory.
/// </summary>
public class AnchorResolverTests
{
    private const long ModuleBase = 0x140000000L;
    private const long Section0VA = ModuleBase + 0x1000;
    private const int SlotSize = 64;
    private const int SlotCount = 22;

    private const uint ExecChar = 0x60000020;          // CNT_CODE | MEM_EXECUTE | MEM_READ
    private const uint CntCodeNoExecChar = 0x00000020; // CNT_CODE only -- no exec bit
    private const uint ELfanew = 0x108;
    private const int SizeOfOptionalHeader = 0xE0;

    // ── synthetic 22-slot layout: region sigs first, then the 10 singletons ──────────────────
    private const int R0P = 0, R0A = 1, R1P = 2, R1Alt = 3, R2P = 4, R3P = 5, R3Alt = 6, R4P = 7,
                       R5P = 8, R5Alt = 9, R6P = 10, R6Alt = 11,
                       Slot0Idx = 12, Slot9Idx = 13, EventIdIdx = 14, BattleModeIdx = 15,
                       PauseFlagIdx = 16, LiveMapIdIdx = 17, InvCountIdx = 18, CollectedIdx = 19,
                       UnitArrXIdx = 20, TerrainIdx = 21;

    private const long R0Base = 0x140200000L;
    private const long R0AltTarget = R0Base + 0x50;
    private const long R1Base = 0x140300000L;
    private const long R1AltTarget = R1Base + 0x50;
    private const long R2Base = 0x140400000L;
    private const long R3Base = 0x140500000L;
    private const long R3AltTarget = R3Base + 0x50;
    private const long R4Base = 0x140600000L;
    private const long R5Base = 0x140700000L;
    private const long R5AltTarget = R5Base + 0x50;
    private const long R6Base = 0x140800000L;
    private const long R6AltTarget = R6Base + 0x50;

    private const long Slot0Addr = 0x140900000L;
    private const long Slot9Addr = 0x140900010L;
    private const long EventIdAddr = 0x140900020L;
    private const long BattleModeAddr = 0x140900030L;
    private const long PauseFlagAddr = 0x140900040L;
    private const long LiveMapIdAddr = 0x140900050L;
    private const long InvCountAddr = 0x140900060L;
    private const long CollectedAddr = 0x140900070L;
    private const long UnitArrXAddr = 0x140900080L;
    private const long TerrainAddr = 0x140900090L;

    private static readonly long[] Targets =
    {
        R0Base, R0AltTarget, R1Base, R1AltTarget, R2Base, R3Base, R3AltTarget, R4Base,
        R5Base, R5AltTarget, R6Base, R6AltTarget,
        Slot0Addr, Slot9Addr, EventIdAddr, BattleModeAddr, PauseFlagAddr, LiveMapIdAddr,
        InvCountAddr, CollectedAddr, UnitArrXAddr, TerrainAddr,
    };

    // ── pattern / byte-encoding helpers ───────────────────────────────────────────────────────

    private static int?[] Pat(int idx) => new int?[]
    {
        (byte)(0xA0 + idx), (byte)(0xB0 + idx), null, null, null, null, (byte)(0xC0 + idx), (byte)(0xD0 + idx),
    };

    private static AnchorSig Sig(string name, int idx) => new(name, Pat(idx), 2, 0, Targets[idx]);

    /// <summary>Writes slot <paramref name="idx"/>'s pattern at <paramref name="offset"/> in
    /// <paramref name="buf"/> (whose bytes live at <paramref name="sectionVA"/>), encoding
    /// whatever disp32 makes it resolve to <c>Targets[idx] + delta</c>.</summary>
    private static void WriteAt(byte[] buf, long sectionVA, int offset, int idx, long delta)
    {
        var pattern = Pat(idx);
        for (int i = 0; i < pattern.Length; i++)
            buf[offset + i] = pattern[i] is int v ? (byte)v : (byte)0;
        long siteVA = sectionVA + offset;
        int disp = (int)((Targets[idx] + delta) - (siteVA + 2 + 4 + 0));
        buf[offset + 2] = (byte)(disp & 0xFF);
        buf[offset + 3] = (byte)((disp >> 8) & 0xFF);
        buf[offset + 4] = (byte)((disp >> 16) & 0xFF);
        buf[offset + 5] = (byte)((disp >> 24) & 0xFF);
    }

    /// <summary>Builds one section's backing bytes: every slot 0..21 planted at <paramref
    /// name="delta"/>, except entries in <paramref name="omit"/> (left unplanted -- zero hits)
    /// or overridden individually via <paramref name="deltaOverride"/>.</summary>
    private static byte[] BuildSection(
        long sectionVA, long delta = 0, HashSet<int>? omit = null, Dictionary<int, long>? deltaOverride = null)
    {
        omit ??= new HashSet<int>();
        deltaOverride ??= new Dictionary<int, long>();
        var buf = new byte[0x2000];
        for (int idx = 0; idx < SlotCount; idx++)
        {
            if (omit.Contains(idx)) continue;
            long d = deltaOverride.TryGetValue(idx, out var dv) ? dv : delta;
            WriteAt(buf, sectionVA, idx * SlotSize, idx, d);
        }
        return buf;
    }

    private static AnchorTable BuildTable() => new(
        new List<AnchorRegion>
        {
            new("R0", R0Base, R0Base - 0x1000, R0Base + 0x1000, new List<AnchorSig> { Sig("R0-primary", R0P), Sig("R0-alt", R0A) }),
            new("R1", R1Base, R1Base - 0x1000, R1Base + 0x1000, new List<AnchorSig> { Sig("R1-primary", R1P), Sig("R1-alt", R1Alt) }),
            new("R2", R2Base, R2Base - 0x1000, R2Base + 0x1000, new List<AnchorSig> { Sig("R2-primary", R2P) }),
            new("R3", R3Base, R3Base - 0x1000, R3Base + 0x1000, new List<AnchorSig> { Sig("R3-primary", R3P), Sig("R3-alt", R3Alt) }),
            new("R4", R4Base, R4Base - 0x1000, R4Base + 0x1000, new List<AnchorSig> { Sig("R4-primary", R4P) }),
            new("R5", R5Base, R5Base - 0x1000, R5Base + 0x1000, new List<AnchorSig> { Sig("R5-primary", R5P), Sig("R5-alt", R5Alt) }),
            new("R6", R6Base, R6Base - 0x1000, R6Base + 0x1000, new List<AnchorSig> { Sig("R6-primary", R6P), Sig("R6-alt", R6Alt) }),
        },
        new List<AnchorSingleton>
        {
            new("Slot0", Slot0Addr, new List<AnchorSig> { Sig("Slot0", Slot0Idx) }),
            new("Slot9", Slot9Addr, new List<AnchorSig> { Sig("Slot9", Slot9Idx) }),
            new("EventId", EventIdAddr, new List<AnchorSig> { Sig("EventId", EventIdIdx) }),
            new("BattleMode", BattleModeAddr, new List<AnchorSig> { Sig("BattleMode", BattleModeIdx) }),
            new("PauseFlag", PauseFlagAddr, new List<AnchorSig> { Sig("PauseFlag", PauseFlagIdx) }),
            new("LiveBattleMapId", LiveMapIdAddr, new List<AnchorSig> { Sig("LiveBattleMapId", LiveMapIdIdx) }),
            new("InventoryCountBase", InvCountAddr, new List<AnchorSig> { Sig("InventoryCountBase", InvCountIdx) }),
            new("TreasureCollectedBase", CollectedAddr, new List<AnchorSig> { Sig("TreasureCollectedBase", CollectedIdx) }),
            new("UnitArrayBaseX", UnitArrXAddr, new List<AnchorSig> { Sig("UnitArrayBaseX-parent", UnitArrXIdx) }),
            new("TerrainGrid", TerrainAddr, new List<AnchorSig> { Sig("TerrainGrid-parent", TerrainIdx) }),
        });

    private static TreasureDb MakeDb(AnchorTable? table) => new(null, new List<TreasureMap>(), table);

    private static void SeedU16(FakeSparseMemory mem, long addr, ushort value)
    {
        mem.U8s[addr] = (byte)(value & 0xFF);
        mem.U8s[addr + 1] = (byte)((value >> 8) & 0xFF);
        mem.ReadableAddrs.Add(addr);
        mem.ReadableAddrs.Add(addr + 1);
    }

    /// <summary>Seeds a fake PE header (e_lfanew + file header + one section header per entry).
    /// Each tuple is (virtualSize, virtualAddress, characteristics).</summary>
    private static FakeSparseMemory SeedPeHeader(params (uint virtualSize, uint virtualAddress, uint characteristics)[] sections)
    {
        var mem = new FakeSparseMemory();
        long peAddr = ModuleBase + ELfanew;
        mem.SeedU32(ModuleBase + 0x3C, ELfanew);
        SeedU16(mem, peAddr + 6, (ushort)sections.Length);
        SeedU16(mem, peAddr + 20, (ushort)SizeOfOptionalHeader);
        long sectionTableAddr = peAddr + 24 + SizeOfOptionalHeader;
        for (int i = 0; i < sections.Length; i++)
        {
            long sh = sectionTableAddr + i * 40;
            mem.SeedU32(sh + 8, sections[i].virtualSize);
            mem.SeedU32(sh + 12, sections[i].virtualAddress);
            mem.SeedU32(sh + 36, sections[i].characteristics);
        }
        return mem;
    }

    private static readonly string[] AllRegionIds = { "R0", "R1", "R2", "R3", "R4", "R5", "R6" };

    // ── happy path / slide ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_happy_path_all_sigs_resolve_deltas_zero()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        foreach (var id in AllRegionIds) Assert.Equal(0L, result!.RegionDeltas[id]);
        Assert.Empty(result!.DerivedRegions);
        Assert.True(result.ClaimOk);
        Assert.True(result.CollectOk);
        Assert.True(result.FingerprintOk);
        Assert.Equal(Slot0Addr, result.Slot0);
    }

    [Fact]
    public void TryResolve_slid_image_every_delta_equals_the_slide()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0x1000));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        foreach (var id in AllRegionIds) Assert.Equal(0x1000L, result!.RegionDeltas[id]);
        Assert.Equal(Slot0Addr + 0x1000, result!.Slot0);
        Assert.Equal(TerrainAddr + 0x1000, result.TerrainGrid);
    }

    // ── LOAD-BEARING #2 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_region_resolved_only_via_alternate_uses_its_own_sig_target()
    {
        // R1-primary absent -- R1 must resolve via R1-alt only. R1-alt's baked target
        // (R1AltTarget = R1Base + 0x50) is a DIFFERENT VA than the region base: a buggy
        // implementation computing delta as (resolvedTarget - region.Base) instead of
        // (resolvedTarget - sig.Target) would leak that 0x50 gap into the delta (0x1050
        // instead of 0x1000).
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        long delta = 0x1000;
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: delta, omit: new HashSet<int> { R1P }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.Equal(delta, result!.RegionDeltas["R1"]);
    }

    // ── ambiguity / disagreement / cap ────────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_primary_and_alt_delta_disagreement_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        var overrides = new Dictionary<int, long> { [R1Alt] = 0x2000 };   // R1-primary stays at 0
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, deltaOverride: overrides));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_any_sig_with_two_hits_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        var buf = BuildSection(Section0VA, delta: 0);
        WriteAt(buf, Section0VA, SlotCount * SlotSize, R0P, 0);   // duplicate hit for R0-primary
        mem.RegisterBlock(Section0VA, buf);
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_delta_cap_exceeded_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        long badDelta = Tuning.AnchorMaxDeltaBytes + 1;
        var overrides = new Dictionary<int, long> { [R0P] = badDelta, [R0A] = badDelta };
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, deltaOverride: overrides));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    // ── mirror set / derivation ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_mirror_set_disagreement_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        var overrides = new Dictionary<int, long> { [R3P] = 0x2000, [R3Alt] = 0x2000 };   // R1 stays at 0
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, deltaOverride: overrides));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_R2_absent_with_R1_and_R3_agreeing_is_derived()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        long delta = 0x1000;
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: delta, omit: new HashSet<int> { R2P }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.Equal(delta, result!.RegionDeltas["R2"]);
        Assert.Contains("R2", result.DerivedRegions);
    }

    [Fact]
    public void TryResolve_R2_and_R3_both_absent_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        var omit = new HashSet<int> { R2P, R3P, R3Alt };
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: omit));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_R0_unresolved_independent_region_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        var omit = new HashSet<int> { R0P, R0A };
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: omit));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    // ── required singletons / optional feature flags ─────────────────────────────────────────

    [Fact]
    public void TryResolve_required_singleton_absent_returns_null()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: new HashSet<int> { LiveMapIdIdx }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_TerrainGrid_absent_sets_FingerprintOk_false_but_resolves()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: new HashSet<int> { TerrainIdx }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.False(result!.FingerprintOk);
        Assert.Equal(Offsets.TerrainGrid, result.TerrainGrid);
    }

    [Fact]
    public void TryResolve_InventoryCountBase_absent_sets_ClaimOk_false()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: new HashSet<int> { InvCountIdx }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.False(result!.ClaimOk);
        Assert.Equal(Offsets.InventoryCountBase, result.InventoryCountBase);
    }

    [Fact]
    public void TryResolve_UnitArrayBaseX_absent_sets_ClaimOk_false()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: new HashSet<int> { UnitArrXIdx }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.False(result!.ClaimOk);
    }

    [Fact]
    public void TryResolve_TreasureCollectedBase_absent_sets_CollectOk_false()
    {
        var mem = SeedPeHeader((0x2000, 0x1000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0, omit: new HashSet<int> { CollectedIdx }));
        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.False(result!.CollectOk);
        Assert.Equal(Offsets.TreasureCollectedBase, result.TreasureCollectedBase);
    }

    // ── section filtering ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_CNT_CODE_without_exec_bit_section_is_not_scanned()
    {
        var mem = SeedPeHeader(
            (0x2000, 0x1000, ExecChar),
            (0x40, 0x200000, CntCodeNoExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0));
        // A duplicate LiveBattleMapId hit sits in the CNT_CODE-without-X section. If the
        // exec-bit filter were broken, LiveBattleMapId would see 2 hits and the whole
        // resolution would fail.
        long dupSectionVA = ModuleBase + 0x200000;
        var dupBuf = new byte[0x40];
        WriteAt(dupBuf, dupSectionVA, 0, LiveMapIdIdx, 0);
        mem.RegisterBlock(dupSectionVA, dupBuf);

        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
        Assert.Equal(0L, result!.RegionDeltas["R0"]);
    }

    [Fact]
    public void TryResolve_oversized_executable_section_is_skipped()
    {
        var mem = SeedPeHeader(
            (0x2000, 0x1000, ExecChar),
            ((uint)(Tuning.AnchorScanMaxSectionBytes + 1), 0x200000, ExecChar));
        mem.RegisterBlock(Section0VA, BuildSection(Section0VA, delta: 0));
        long dupSectionVA = ModuleBase + 0x200000;
        var dupBuf = new byte[0x40];
        WriteAt(dupBuf, dupSectionVA, 0, LiveMapIdIdx, 0);
        mem.RegisterBlock(dupSectionVA, dupBuf);

        var result = new AnchorResolver(mem).TryResolve(MakeDb(BuildTable()));

        Assert.NotNull(result);
    }

    // ── guard clauses ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_null_anchors_table_returns_null()
    {
        var result = new AnchorResolver(new FakeSparseMemory()).TryResolve(MakeDb(null));
        Assert.Null(result);
    }
}
