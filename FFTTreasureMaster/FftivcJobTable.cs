using System;
using System.Runtime.CompilerServices;
using fftivc.utility.modloader.Interfaces.Tables;
using fftivc.utility.modloader.Interfaces.Tables.Models;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>
/// The live IJobTable over the FFTIVC modloader's IFFTOJobDataManager controller.
/// Live only and untested, like LiveMemory over Mem, since every testable decision lives in
/// TreasureHunterGrant behind the seam. Every member catches its own exceptions, so a missing
/// or incompatible modloader surfaces as null or false and never reaches the engine.
/// </summary>
internal sealed class FftivcJobTable : IJobTable
{
    private readonly IFFTOJobDataManager _mgr;

    private FftivcJobTable(IFFTOJobDataManager mgr) => _mgr = mgr;

    /// <summary>Null when the FFTIVC Mod Loader is absent or its interfaces assembly cannot
    /// be resolved. Never throws.</summary>
    public static IJobTable? TryCreate(IModLoaderV1 loader)
    {
        try { return Create(loader); }
        catch { return null; }   // FileNotFound/TypeLoad when the loader mod is absent
    }

    // NoInlining keeps TryCreate JITtable when the foreign interfaces assembly is missing. Only
    // this method may mention the foreign types, so the resolution failure surfaces as an
    // exception inside TryCreate's catch.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IJobTable? Create(IModLoaderV1 loader)
    {
        var weak = loader.GetController<IFFTOJobDataManager>();
        return weak != null && weak.TryGetTarget(out var mgr) ? new FftivcJobTable(mgr) : null;
    }

    /// <summary>The loader's job table is ready once its startup signature scan has found the
    /// table; before that, reads throw a managed exception (no native touch).</summary>
    public bool IsReady()
    {
        try { _mgr.GetJob(0); return true; }
        catch { return false; }
    }

    /// <summary>The effective innate state, which is GetJob's snapshot overlaid with the audit
    /// values of this session's patches, ours and other mods'. GetJob alone is stale after any
    /// ApplyTablePatch, see AuditOverlay.</summary>
    public ushort[]? GetInnates(int jobId)
    {
        try
        {
            var job = _mgr.GetJob(jobId);
            if (job == null) return null;
            var baseValues = new[]
            {
                job.InnateAbilityId1 ?? 0,
                job.InnateAbilityId2 ?? 0,
                job.InnateAbilityId3 ?? 0,
                job.InnateAbilityId4 ?? 0,
            };
            var audit = _mgr.ChangedProperties;
            return AuditOverlay.Apply(baseValues, slot =>
                audit.TryGetValue((jobId, AuditOverlay.InnatePropertyNames[slot]), out var entry)
                    ? entry.Difference?.NewValue
                    : null);
        }
        catch { return null; }
    }

    public bool TryApplyInnate(int jobId, int slotIndex, ushort abilityId)
    {
        try
        {
            var patch = new Job { Id = jobId };
            switch (slotIndex)
            {
                case 0: patch.InnateAbilityId1 = abilityId; break;
                case 1: patch.InnateAbilityId2 = abilityId; break;
                case 2: patch.InnateAbilityId3 = abilityId; break;
                case 3: patch.InnateAbilityId4 = abilityId; break;
                default: return false;
            }
            _mgr.ApplyTablePatch(Tuning.OwnerModId, patch);
            return true;
        }
        catch { return false; }
    }
}
