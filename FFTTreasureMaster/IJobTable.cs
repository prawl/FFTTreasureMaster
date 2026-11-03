namespace FFTTreasureMaster;

/// <summary>
/// Seam over the modloader's job table controller, so the Treasure Hunter grant is unit testable.
/// This path never touches game memory. It talks to the managed API the modloader exposes, and
/// the modloader does the table write. FftivcJobTable is the live adapter.
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
