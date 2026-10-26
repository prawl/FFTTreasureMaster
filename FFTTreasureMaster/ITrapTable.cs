namespace FFTTreasureMaster;

/// <summary>One treasure slot of a map as the loader's trap table holds it. Rare 0 means the
/// slot is unused.</summary>
internal readonly record struct TrapSlotView(ushort Rare, ushort Common);

/// <summary>
/// Seam over the mod loader's treasure table so the rare treasure algorithm runs in tests
/// without the live loader. Nothing here touches game memory, the loader owns the write.
/// </summary>
internal interface ITrapTable
{
    /// <summary>True once the loader has filled the whole table and it can be read.</summary>
    bool IsReady();

    /// <summary>The four slots of a map with other mods' edits folded in, or null when the map
    /// cannot be read.</summary>
    TrapSlotView[]? GetSlots(int mapId);

    /// <summary>Patches the common item of each non-null entry, leaving every other field and
    /// slot alone. The array is Tuning.TrapSlots long. False when the patch was not applied.</summary>
    bool TryApplyCommon(int mapId, ushort?[] commonBySlot);
}
