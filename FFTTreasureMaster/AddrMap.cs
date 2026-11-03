namespace FFTTreasureMaster;

/// <summary>
/// Holds the ten singleton game addresses the engine and the audits read every tick.
/// They start as the baked <see cref="Offsets"/> pins, already rebased onto the live image. When
/// the build key does not match and a signature re-resolve succeeds, <see cref="Apply"/>
/// overwrites all ten at once.
///
/// Only the engine loop's Tick chain writes or reads this object. Apply may run again after a
/// reload, but always after ResetBattle has published null to FastHold, which never reads
/// AddrMap. The audits read the fields on every call and never cache them.
/// </summary>
internal sealed class AddrMap
{
    public long Slot0 { get; private set; } = Offsets.Slot0;
    public long Slot9 { get; private set; } = Offsets.Slot9;
    public long EventId { get; private set; } = Offsets.EventId;
    public long BattleMode { get; private set; } = Offsets.BattleMode;
    public long PauseFlag { get; private set; } = Offsets.PauseFlag;
    public long LiveBattleMapId { get; private set; } = Offsets.LiveBattleMapId;
    public long TerrainGrid { get; private set; } = Offsets.TerrainGrid;
    public long UnitArrayBaseX { get; private set; } = Offsets.UnitArrayBaseX;
    public long InventoryCountBase { get; private set; } = Offsets.InventoryCountBase;
    public long TreasureCollectedBase { get; private set; } = Offsets.TreasureCollectedBase;

    /// <summary>Overwrites every field from a successful signature re-resolve. An optional
    /// field whose signature did not resolve arrives still carrying its baked default, and the
    /// matching Ok flag on the resolution keeps that feature off, so the stale value is never
    /// read.</summary>
    public void Apply(AnchorResolution res)
    {
        Slot0 = res.Slot0;
        Slot9 = res.Slot9;
        EventId = res.EventId;
        BattleMode = res.BattleMode;
        PauseFlag = res.PauseFlag;
        LiveBattleMapId = res.LiveBattleMapId;
        TerrainGrid = res.TerrainGrid;
        UnitArrayBaseX = res.UnitArrayBaseX;
        InventoryCountBase = res.InventoryCountBase;
        TreasureCollectedBase = res.TreasureCollectedBase;
    }
}
