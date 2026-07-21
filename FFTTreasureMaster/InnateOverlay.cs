using System;

namespace FFTTreasureMaster;

/// <summary>
/// Pure effective-view helper for FftivcJobTable. The modloader's GetJob serves a managed
/// snapshot cloned once at its startup scan; a programmatic ApplyTablePatch writes game
/// memory and the ChangedProperties audit but never refreshes that snapshot, so GetJob is
/// permanently stale for this session's patches (loader source verified 2026-07-21,
/// FFTOJobDataManager.cs: Init clones at lines 53-60, ApplyTablePatch lines 99-191 never
/// touch _moddedTable). The audit is the only truthful surface: laying its values over the
/// stale base yields the row's real innate state.
/// </summary>
internal static class InnateOverlay
{
    /// <summary>The Job model's innate property names, index-aligned with the slot order
    /// used across the grant (0 = InnateAbilityId1). These are the audit dictionary's
    /// PropertyName keys.</summary>
    public static readonly string[] SlotPropertyNames =
    {
        "InnateAbilityId1", "InnateAbilityId2", "InnateAbilityId3", "InnateAbilityId4",
    };

    /// <summary>Returns a copy of <paramref name="baseValues"/> with each slot replaced by
    /// its audit value where one exists and converts cleanly; the base value survives an
    /// absent (null) or unconvertible audit value. Never mutates the input.</summary>
    public static ushort[] Apply(ushort[] baseValues, Func<int, object?> auditValue)
    {
        var result = (ushort[])baseValues.Clone();
        for (int slot = 0; slot < result.Length; slot++)
        {
            var boxed = auditValue(slot);
            if (boxed != null && TryToUShort(boxed, out var value)) result[slot] = value;
        }
        return result;
    }

    /// <summary>Defensive unboxing: the audit's ModelDiff.NewValue is a plain object whose
    /// boxed type the loader does not contract. Accept any numeric that fits a ushort.</summary>
    internal static bool TryToUShort(object boxed, out ushort value)
    {
        switch (boxed)
        {
            case ushort us: value = us; return true;
            case byte b: value = b; return true;
            case short s when s >= 0: value = (ushort)s; return true;
            case int i when i is >= 0 and <= ushort.MaxValue: value = (ushort)i; return true;
            case uint u when u <= ushort.MaxValue: value = (ushort)u; return true;
            case long l when l is >= 0 and <= ushort.MaxValue: value = (ushort)l; return true;
        }
        value = 0;
        return false;
    }
}
