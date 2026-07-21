using System;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// InnateOverlay: the pure effective-view helper behind FftivcJobTable.GetInnates. The
/// modloader's GetJob serves a startup-time snapshot that programmatic ApplyTablePatch
/// never refreshes; the ChangedProperties audit is the only surface reflecting this
/// session's patches. The overlay lays audit values over the stale base so read-backs
/// tell the truth.
///
/// Invariants:
///   an absent audit value (null) leaves the base value;
///   a present audit value replaces the base value, whatever boxed numeric type it is;
///   a junk audit value (unconvertible object) leaves the base value;
///   the input array is never mutated;
///   the slot property names match the modloader's Job model property names exactly.
/// </summary>
public class InnateOverlayTests
{
    [Fact]
    public void AbsentAudit_LeavesBaseValues()
    {
        var baseVals = new ushort[] { 474, 0, 0, 0 };

        var result = InnateOverlay.Apply(baseVals, _ => null);

        Assert.Equal(new ushort[] { 474, 0, 0, 0 }, result);
    }

    [Fact]
    public void AuditValue_ReplacesBaseValue()
    {
        var baseVals = new ushort[] { 0, 0, 0, 0 };

        var result = InnateOverlay.Apply(baseVals, slot => slot == 2 ? (object)(ushort)509 : null);

        Assert.Equal(new ushort[] { 0, 0, 509, 0 }, result);
    }

    [Fact]
    public void BoxedInt_And_BoxedByte_Convert()
    {
        var baseVals = new ushort[] { 0, 0, 0, 0 };

        var result = InnateOverlay.Apply(baseVals, slot => slot switch
        {
            0 => (object)509,          // boxed int
            1 => (object)(byte)77,     // boxed byte
            _ => null,
        });

        Assert.Equal(new ushort[] { 509, 77, 0, 0 }, result);
    }

    [Fact]
    public void JunkAuditValue_LeavesBaseValue()
    {
        var baseVals = new ushort[] { 42, 0, 0, 0 };

        var result = InnateOverlay.Apply(baseVals, slot => slot == 0 ? (object)"not a number" : null);

        Assert.Equal(new ushort[] { 42, 0, 0, 0 }, result);
    }

    [Fact]
    public void OutOfRangeInt_LeavesBaseValue()
    {
        var baseVals = new ushort[] { 7, 0, 0, 0 };

        var result = InnateOverlay.Apply(baseVals, slot => slot == 0 ? (object)(-1) : null);

        Assert.Equal(new ushort[] { 7, 0, 0, 0 }, result);
    }

    [Fact]
    public void InputArray_IsNeverMutated()
    {
        var baseVals = new ushort[] { 0, 0, 0, 0 };

        InnateOverlay.Apply(baseVals, _ => (object)(ushort)509);

        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, baseVals);
    }

    [Fact]
    public void SlotPropertyNames_MatchTheJobModel()
    {
        Assert.Equal(
            new[] { "InnateAbilityId1", "InnateAbilityId2", "InnateAbilityId3", "InnateAbilityId4" },
            InnateOverlay.SlotPropertyNames);
    }
}
