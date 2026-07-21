namespace FFTTreasureMaster;

/// <summary>
/// Seam over the FFTIVC modloader's job-table controller (IFFTOJobDataManager) so the
/// Treasure Hunter grant algorithm is unit-testable without the live loader. This path never
/// touches game memory: it talks to a managed API that the modloader exposes, and the
/// modloader does the actual table write. Production adapter: FftivcJobTable (live-only,
/// untested by design, like LiveMemory over Mem).
/// </summary>
internal interface IJobTable
{
    /// <summary>Whether the modloader's job table is ready to be read and patched. The
    /// loader locates the table with an asynchronous signature scan at startup, so early
    /// calls can find it not ready yet; the grant polls this before doing anything.</summary>
    bool IsReady();

    /// <summary>The four innate-ability slots of JOB_DATA row <paramref name="jobId"/>
    /// (index 0 = InnateAbilityId1), merged with any other mod's edits, or null when the
    /// row cannot be read.</summary>
    ushort[]? GetInnates(int jobId);

    /// <summary>Patches exactly one innate slot (slotIndex 0..3) of the row to
    /// <paramref name="abilityId"/>, leaving every other field of the row untouched
    /// (sparse patch). Returns false when the patch could not be applied.</summary>
    bool TryApplyInnate(int jobId, int slotIndex, ushort abilityId);
}
