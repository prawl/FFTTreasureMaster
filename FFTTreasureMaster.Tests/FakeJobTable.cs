using System;
using System.Collections.Generic;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Fake IJobTable for TreasureHunterGrant tests. Rows is the fake JOB_DATA innate state
/// (length-4 arrays). Readiness, apply results, and mutation are all scriptable so tests can
/// exercise the not-ready retry, the read-back confirm, and the clobber re-assert paths.
/// </summary>
internal sealed class FakeJobTable : IJobTable
{
    public readonly Dictionary<int, ushort[]> Rows = new();

    /// <summary>IsReady returns false this many times before turning true.</summary>
    public int NotReadyProbes;

    /// <summary>IsReady never turns true (drives the give-up path).</summary>
    public bool NeverReady;

    /// <summary>Return value of TryApplyInnate.</summary>
    public bool ApplyResult = true;

    /// <summary>Whether a successful TryApplyInnate actually writes Rows (false simulates a
    /// write that reports success but does not land, so the read-back confirm fails).</summary>
    public bool MutateOnApply = true;

    /// <summary>GetInnates throws (drives the catch-and-log-once path).</summary>
    public bool ThrowOnGetInnates;

    public int IsReadyCalls;
    public int GetInnatesCalls;
    public readonly List<(int JobId, int Slot, ushort Ability)> ApplyCalls = new();

    public int SeamCalls => IsReadyCalls + GetInnatesCalls + ApplyCalls.Count;

    public bool IsReady()
    {
        IsReadyCalls++;
        if (NeverReady) return false;
        if (NotReadyProbes > 0) { NotReadyProbes--; return false; }
        return true;
    }

    public ushort[]? GetInnates(int jobId)
    {
        GetInnatesCalls++;
        if (ThrowOnGetInnates) throw new InvalidOperationException("fake seam failure");
        return Rows.TryGetValue(jobId, out var r) ? (ushort[])r.Clone() : null;
    }

    public bool TryApplyInnate(int jobId, int slotIndex, ushort abilityId)
    {
        ApplyCalls.Add((jobId, slotIndex, abilityId));
        if (!ApplyResult) return false;
        if (MutateOnApply && Rows.TryGetValue(jobId, out var r)) r[slotIndex] = abilityId;
        return true;
    }
}
