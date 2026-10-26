using System.Collections.Generic;

namespace FFTTreasureMaster.Tests;

/// <summary>Scriptable ITrapTable. Maps absent from Maps read as an unused map of four (0,0)
/// slots, so a full walk sees every id readable unless a test says otherwise.</summary>
internal sealed class FakeTrapTable : ITrapTable
{
    public readonly Dictionary<int, TrapSlotView[]> Maps = new();
    public readonly HashSet<int> UnreadableIds = new();

    public int NotReadyProbes;
    public bool NeverReady;
    public bool ApplyResult = true;
    public bool MutateOnApply = true;
    public bool ThrowOnGetSlots;

    /// <summary>The first GetSlots call after a successful apply returns null, then clears.</summary>
    public bool NullGetSlotsAfterApply;

    public int IsReadyCalls;
    public int GetSlotsCalls;
    public readonly List<int> GetSlotsIds = new();
    public readonly List<(int MapId, ushort?[] Common)> ApplyCalls = new();

    private bool _nullNext;


    public bool IsReady()
    {
        IsReadyCalls++;
        if (NeverReady) return false;
        if (NotReadyProbes > 0) { NotReadyProbes--; return false; }
        return true;
    }

    public TrapSlotView[]? GetSlots(int mapId)
    {
        GetSlotsCalls++;
        GetSlotsIds.Add(mapId);
        if (ThrowOnGetSlots) throw new System.InvalidOperationException("fake seam failure");
        if (_nullNext) { _nullNext = false; return null; }
        if (UnreadableIds.Contains(mapId)) return null;
        return Maps.TryGetValue(mapId, out var m)
            ? (TrapSlotView[])m.Clone()
            : new TrapSlotView[Tuning.TrapSlots];
    }

    public bool TryApplyCommon(int mapId, ushort?[] commonBySlot)
    {
        ApplyCalls.Add((mapId, (ushort?[])commonBySlot.Clone()));
        if (!ApplyResult) return false;
        if (MutateOnApply)
        {
            if (!Maps.TryGetValue(mapId, out var m))
                Maps[mapId] = m = new TrapSlotView[Tuning.TrapSlots];
            for (int i = 0; i < commonBySlot.Length; i++)
                if (commonBySlot[i] is ushort v) m[i] = m[i] with { Common = v };
        }
        if (NullGetSlotsAfterApply) { NullGetSlotsAfterApply = false; _nullNext = true; }
        return true;
    }
}
