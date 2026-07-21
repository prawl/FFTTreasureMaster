using System.Collections.Generic;
using System.Linq;

namespace FFTTreasureMaster;

/// <summary>
/// Stateless driver for a signature re-resolve: PE section walk, one AnchorScan pass over every
/// sig, then the per-entity delta cross-checks + mirror-set invariant + R2/R4 derivation +
/// required-set/feature-flag policy described in the update-hardening design. Runs from
/// TreasureMaster.CheckGlobalIdle, at most once per build-key mismatch (the existing
/// _globalIdleChecked latch already guarantees that).
///
/// THE CORE MATH: for any sig that resolves at a unique site, delta = resolvedTarget -
/// sig.Target (its OWN baked target, never the entity's base/addr) -- an alternate sig resolves
/// a DIFFERENT VA than the region base/singleton addr it anchors, so the entity's resolved
/// value is always bakedBase-or-bakedAddr + delta, never the alternate's own resolvedTarget.
/// </summary>
internal sealed class AnchorResolver
{
    private const long ModuleBase = 0x140000000L;
    private static readonly string[] RequiredIndependentRegions = { "R0", "R5", "R6" };
    private static readonly string[] MirrorRegions = { "R1", "R2", "R3", "R4" };

    private readonly IGameMemory _mem;

    public AnchorResolver(IGameMemory mem) => _mem = mem;

    public AnchorResolution? TryResolve(TreasureDb db)
    {
        if (db.Anchors is not { } table) return null;

        var sections = WalkSections();
        if (sections is null) return null;

        var allSigs = new List<AnchorSig>();
        foreach (var r in table.Regions) allSigs.AddRange(r.Sigs);
        foreach (var s in table.Singletons) allSigs.AddRange(s.Sigs);

        var scan = new AnchorScan(_mem);
        var hits = scan.FindAll(sections, allSigs);
        if (hits is null) return null;

        var regionDelta = new Dictionary<string, long>();
        foreach (var region in table.Regions)
        {
            var (failed, delta) = ResolveEntityDelta(region.Sigs, hits, scan);
            if (failed) return null;
            if (delta is { } d) regionDelta[region.Id] = d;
        }
        if (!ApplyMirrorSetAndDerive(regionDelta, out var derived)) return null;
        foreach (var id in RequiredIndependentRegions)
            if (!regionDelta.ContainsKey(id)) return null;

        var singletonDelta = new Dictionary<string, long>();
        foreach (var s in table.Singletons)
        {
            var (failed, delta) = ResolveEntityDelta(s.Sigs, hits, scan);
            if (failed) return null;
            if (delta is { } d) singletonDelta[s.Name] = d;
        }

        long? Resolve(string name)
        {
            var singleton = table.Singletons.FirstOrDefault(s => s.Name == name);
            if (singleton is null || !singletonDelta.TryGetValue(name, out long d)) return null;
            return singleton.Addr + d;
        }

        long? slot0 = Resolve("Slot0"), slot9 = Resolve("Slot9"), eventId = Resolve("EventId");
        long? battleMode = Resolve("BattleMode"), pauseFlag = Resolve("PauseFlag");
        long? liveMapId = Resolve("LiveBattleMapId");
        if (slot0 is null || slot9 is null || eventId is null || battleMode is null ||
            pauseFlag is null || liveMapId is null)
            return null;

        long? terrainGrid = Resolve("TerrainGrid");
        long? invCount = Resolve("InventoryCountBase");
        long? unitArrayX = Resolve("UnitArrayBaseX");
        long? collectedBase = Resolve("TreasureCollectedBase");

        return new AnchorResolution
        {
            RegionDeltas = regionDelta,
            DerivedRegions = derived,
            Slot0 = slot0.Value, Slot9 = slot9.Value, EventId = eventId.Value,
            BattleMode = battleMode.Value, PauseFlag = pauseFlag.Value, LiveBattleMapId = liveMapId.Value,
            TerrainGrid = terrainGrid ?? Offsets.TerrainGrid,
            UnitArrayBaseX = unitArrayX ?? Offsets.UnitArrayBaseX,
            InventoryCountBase = invCount ?? Offsets.InventoryCountBase,
            TreasureCollectedBase = collectedBase ?? Offsets.TreasureCollectedBase,
            ClaimOk = unitArrayX is not null && invCount is not null,
            CollectOk = collectedBase is not null,
            FingerprintOk = terrainGrid is not null,
        };
    }

    /// <summary>
    /// Mirror-set invariant: every RESOLVED member of {R1,R2,R3,R4} must share one delta. R1 and
    /// R3 are required directly (no derivation); if R2 and/or R4 are unresolved, they take the
    /// shared delta as a DERIVED value (only reachable here because R1/R3 already agree).
    /// </summary>
    private static bool ApplyMirrorSetAndDerive(Dictionary<string, long> regionDelta, out List<string> derived)
    {
        derived = new List<string>();
        if (!regionDelta.TryGetValue("R1", out long dR1)) return false;
        if (!regionDelta.TryGetValue("R3", out long dR3)) return false;
        if (dR1 != dR3) return false;
        long shared = dR1;

        foreach (var id in new[] { "R2", "R4" })
        {
            if (regionDelta.TryGetValue(id, out long d))
            {
                if (d != shared) return false;
            }
            else
            {
                regionDelta[id] = shared;
                derived.Add(id);
            }
        }
        return true;
    }

    /// <summary>
    /// Resolves one entity's (region or singleton) delta from its own sig(s). A sig with 2+ hits
    /// is ambiguous and fails the WHOLE resolution (never ignorable); sigs with 0 hits are
    /// simply ignored. Two or more sigs that DO resolve must agree, else the whole resolution
    /// fails. Returns (failed: true) for either hard-fail case; (failed: false, delta: null)
    /// when the entity legitimately has no resolving sig this pass.
    /// </summary>
    private static (bool failed, long? delta) ResolveEntityDelta(
        IReadOnlyList<AnchorSig> sigs, Dictionary<string, List<long>> hits, AnchorScan scan)
    {
        var deltas = new List<long>();
        foreach (var sig in sigs)
        {
            if (!hits.TryGetValue(sig.Name, out var list) || list.Count == 0) continue;
            if (list.Count >= 2) return (true, null);

            long? resolved = scan.ResolveTarget(list[0], sig);
            if (resolved is null) continue;   // guarded disp32 read failed -- contributes nothing
            deltas.Add(resolved.Value - sig.Target);
        }
        if (deltas.Count == 0) return (false, null);

        long first = deltas[0];
        foreach (var d in deltas)
            if (d != first) return (true, null);
        if (System.Math.Abs(first) > Tuning.AnchorMaxDeltaBytes) return (true, null);
        return (false, first);
    }

    // ── PE section walk (guarded reads, ArmAudit.ReadPeBuildKey style) ────────────────────────

    private List<(long va, long size)>? WalkSections()
    {
        if (!ReadU32(ModuleBase + 0x3C, out uint eLfanew)) return null;
        long peAddr = ModuleBase + eLfanew;
        if (!ReadU16(peAddr + 6, out ushort numSections)) return null;
        if (!ReadU16(peAddr + 20, out ushort sizeOfOptionalHeader)) return null;

        long sectionTableAddr = peAddr + 24 + sizeOfOptionalHeader;
        var sections = new List<(long, long)>();
        for (int i = 0; i < numSections; i++)
        {
            long shAddr = sectionTableAddr + i * 40;
            if (!ReadU32(shAddr + 8, out uint virtualSize)) return null;
            if (!ReadU32(shAddr + 12, out uint virtualAddress)) return null;
            if (!ReadU32(shAddr + 36, out uint characteristics)) return null;

            const uint ImageScnMemExecute = 0x20000000;
            if ((characteristics & ImageScnMemExecute) == 0) continue;
            if (virtualSize > Tuning.AnchorScanMaxSectionBytes) continue;

            sections.Add((ModuleBase + virtualAddress, virtualSize));
        }
        return sections;
    }

    private bool ReadU32(long addr, out uint value)
    {
        value = 0;
        if (!_mem.Readable(addr, 4)) return false;
        value = (uint)(_mem.U8(addr) | (_mem.U8(addr + 1) << 8) | (_mem.U8(addr + 2) << 16) | (_mem.U8(addr + 3) << 24));
        return true;
    }

    private bool ReadU16(long addr, out ushort value)
    {
        value = 0;
        if (!_mem.Readable(addr, 2)) return false;
        value = (ushort)(_mem.U8(addr) | (_mem.U8(addr + 1) << 8));
        return true;
    }
}
