using System.Collections.Generic;
using System.Linq;

namespace FFTTreasureMaster;

/// <summary>
/// Re-resolves the game addresses by signature when the build key does not match.
/// It walks the PE sections, runs one AnchorScan pass over every sig, then checks the deltas
/// per entity, holds the mirror regions to one shared delta, derives R2 and R4, and applies
/// the required set and feature flag policy. TreasureMaster.CheckGlobalIdle calls it at most
/// once per mismatch.
///
/// The core math. For a sig that resolves at a unique site, the delta is the resolved target
/// minus the sig's own baked target, never the entity's base. An alternate sig anchors a
/// different address than the entity it stands for, so the entity's resolved value is always
/// its own baked address plus the delta.
///
/// Address spaces. Sig targets, region bases and singleton addresses are dataset space, at
/// the preferred base. The section walk and the scan run over the live image, so a resolved
/// target is live and the ASLR shift comes off before any delta is taken. In the result the
/// addresses are live and the region deltas are dataset space.
/// </summary>
internal sealed class AnchorResolver
{
    private static readonly string[] RequiredIndependentRegions = { "R0", "R5", "R6" };

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
            return ImageBase.At(singleton.Addr + d);   // dataset addr plus RVA delta, rebased live
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
            Slot0 = slot0.Value,
            Slot9 = slot9.Value,
            EventId = eventId.Value,
            BattleMode = battleMode.Value,
            PauseFlag = pauseFlag.Value,
            LiveBattleMapId = liveMapId.Value,
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
    /// Every resolved member of the mirror set {R1,R2,R3,R4} must share one delta. R1 and R3
    /// are required directly. When R2 or R4 did not resolve, it takes the shared delta as a
    /// derived value, which is only reachable once R1 and R3 agree.
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
    /// Resolves one entity's delta from its own sigs. A sig with two or more hits is ambiguous
    /// and fails the whole resolution. A sig with no hits is ignored. Two sigs that both resolve
    /// must agree, else the whole resolution fails. Returns failed for either hard case, and a
    /// null delta when the entity simply has no resolving sig this pass.
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
            if (resolved is null) continue;   // the guarded disp32 read failed, so it adds nothing
            // The scan returns a live target while sig.Target is dataset space. Remove the ASLR
            // shift first so the delta is RVA only and AnchorMaxDeltaBytes keeps its meaning.
            deltas.Add((resolved.Value - ImageBase.Delta) - sig.Target);
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
        if (!ReadU32(ImageBase.Base + 0x3C, out uint eLfanew)) return null;
        long peAddr = ImageBase.Base + eLfanew;
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

            sections.Add((ImageBase.Base + virtualAddress, virtualSize));
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
