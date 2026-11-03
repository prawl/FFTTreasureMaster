using System;
using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// The result of a successful signature re-resolve, driven from TreasureMaster.CheckGlobalIdle.
/// <see cref="RegionDeltas"/> feeds <see cref="AnchorRemap.Remap"/> directly. The ten singleton
/// fields are final resolved addresses, or the baked <see cref="Offsets"/> default when an
/// optional singleton's own sig failed to resolve, in which case the matching Ok flag is false
/// and keeps that feature off.
///
/// Plain settable properties rather than a record, so tests can build a canned instance with
/// an object initializer.
/// </summary>
internal sealed class AnchorResolution
{
    public IReadOnlyDictionary<string, long> RegionDeltas { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<string> DerivedRegions { get; init; } = Array.Empty<string>();

    public long Slot0 { get; init; }
    public long Slot9 { get; init; }
    public long EventId { get; init; }
    public long BattleMode { get; init; }
    public long PauseFlag { get; init; }
    public long LiveBattleMapId { get; init; }
    public long TerrainGrid { get; init; }
    public long UnitArrayBaseX { get; init; }
    public long InventoryCountBase { get; init; }
    public long TreasureCollectedBase { get; init; }

    /// <summary>True when both InventoryCountBase and UnitArrayBaseX resolved.</summary>
    public bool ClaimOk { get; init; }
    /// <summary>True when TreasureCollectedBase resolved.</summary>
    public bool CollectOk { get; init; }
    /// <summary>True when TerrainGrid resolved.</summary>
    public bool FingerprintOk { get; init; }
}
