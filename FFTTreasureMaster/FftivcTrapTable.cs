using System;
using System.Runtime.CompilerServices;
using fftivc.utility.modloader.Interfaces.Tables;
using fftivc.utility.modloader.Interfaces.Tables.Models;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>
/// Live ITrapTable over the mod loader's treasure table controller. Untested on purpose, the
/// testable logic sits in RareTreasureGrant. Every member answers null or false instead of
/// throwing, so a missing or mismatched loader can never reach the engine.
/// </summary>
internal sealed class FftivcTrapTable : ITrapTable
{
    private readonly IFFTOMapTrapFormationDataManager _mgr;

    private FftivcTrapTable(IFFTOMapTrapFormationDataManager mgr) => _mgr = mgr;

    /// <summary>Null when the loader is absent or its interfaces assembly will not resolve.</summary>
    public static ITrapTable? TryCreate(IModLoaderV1 loader)
    {
        try { return Create(loader); }
        catch { return null; }
    }

    // TryCreate must not name the foreign types itself, so a failed assembly load lands in
    // its catch instead of making TryCreate unloadable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ITrapTable? Create(IModLoaderV1 loader)
    {
        var weak = loader.GetController<IFFTOMapTrapFormationDataManager>();
        return weak != null && weak.TryGetTarget(out var mgr) ? new FftivcTrapTable(mgr) : null;
    }

    /// <summary>The loader fills ids in ascending order, so a readable last id means the whole
    /// table is there.</summary>
    public bool IsReady()
    {
        try { return _mgr.GetMapTrapFormation(Tuning.TrapTableMapCount - 1) != null; }
        catch { return false; }
    }

    public TrapSlotView[]? GetSlots(int mapId)
    {
        try
        {
            var map = _mgr.GetMapTrapFormation(mapId);
            if (map == null) return null;
            var rareBase = new[]
            {
                map.RareItemId1 ?? 0, map.RareItemId2 ?? 0, map.RareItemId3 ?? 0, map.RareItemId4 ?? 0,
            };
            var commonBase = new[]
            {
                map.CommonItemId1 ?? 0, map.CommonItemId2 ?? 0, map.CommonItemId3 ?? 0, map.CommonItemId4 ?? 0,
            };
            var audit = _mgr.ChangedProperties;
            object? Lookup(string name) =>
                audit.TryGetValue((mapId, name), out var entry) ? entry.Difference?.NewValue : null;
            var rare = AuditOverlay.Apply(rareBase, s => Lookup(AuditOverlay.RareItemPropertyNames[s]));
            var common = AuditOverlay.Apply(commonBase, s => Lookup(AuditOverlay.CommonItemPropertyNames[s]));
            var slots = new TrapSlotView[Tuning.TrapSlots];
            for (int i = 0; i < slots.Length; i++) slots[i] = new TrapSlotView(rare[i], common[i]);
            return slots;
        }
        catch { return null; }
    }

    public bool TryApplyCommon(int mapId, ushort?[] commonBySlot)
    {
        try
        {
            var patch = new MapTrapFormation { Id = mapId };
            for (int i = 0; i < commonBySlot.Length; i++)
            {
                var value = commonBySlot[i];
                if (value == null) continue;
                switch (i)
                {
                    case 0: patch.CommonItemId1 = value; break;
                    case 1: patch.CommonItemId2 = value; break;
                    case 2: patch.CommonItemId3 = value; break;
                    case 3: patch.CommonItemId4 = value; break;
                    default: return false;
                }
            }
            _mgr.ApplyTablePatch(Tuning.OwnerModId, patch);
            return true;
        }
        catch { return false; }
    }
}
