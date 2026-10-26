using System;

namespace FFTTreasureMaster;

/// <summary>
/// Pure effective-view helper for the loader table adapters. The loader serves reads from a
/// snapshot taken at its startup, and a programmatic patch never refreshes it, so a read
/// right after a write would show the old value. The loader's changed-properties audit does
/// record every patch, so laying its values over the stale base gives the row's real state.
/// </summary>
internal static class AuditOverlay
{
    /// <summary>Job model innate property names, in slot order. Slot 0 is suffix 1.</summary>
    public static readonly string[] InnatePropertyNames =
    {
        "InnateAbilityId1", "InnateAbilityId2", "InnateAbilityId3", "InnateAbilityId4",
    };

    /// <summary>Trap formation model common item property names, in slot order.</summary>
    public static readonly string[] CommonItemPropertyNames =
    {
        "CommonItemId1", "CommonItemId2", "CommonItemId3", "CommonItemId4",
    };

    /// <summary>Trap formation model rare item property names, in slot order.</summary>
    public static readonly string[] RareItemPropertyNames =
    {
        "RareItemId1", "RareItemId2", "RareItemId3", "RareItemId4",
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
