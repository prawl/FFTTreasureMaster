namespace FFTTreasureMaster;

/// <summary>
/// Mutable holder of the 10 singleton game addresses the engine sentinels and the read-path
/// audits (ArmAudit/ClaimAudit/CollectAudit) use every tick. Fields initialize to the baked
/// <see cref="Offsets"/> defaults -- today's fast path is untouched: a matching build key never
/// calls <see cref="Apply"/>, so every field stays exactly the compiled-in constant. On a build-
/// key mismatch that a signature re-resolve fixes, <see cref="Apply"/> overwrites all 10 fields
/// wholesale from the resolution.
///
/// CONCURRENCY CONTRACT (verbatim -- do not paraphrase when touching this file): AddrMap is
/// written and read ONLY within the engine-loop Tick call chain (one logical thread);
/// repopulation can recur (a stamp-reload re-resolve), but always AFTER ResetBattle has
/// published null to FastHold. FastHold consumes only the published TreasureMap and must stay
/// that way -- it never reads AddrMap. Audit classes read AddrMap fields PER CALL, never cache
/// them at construction time. Aligned-long field writes are atomic on x64 by the platform, but
/// that is defense-in-depth only -- the real safety property is the single-logical-thread rule
/// above, not the atomicity.
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

    /// <summary>Overwrites every field from a successful signature re-resolve. Optional
    /// (feature-gated) fields arrive already carrying the baked default when their own sig(s)
    /// did not resolve -- the corresponding *Ok flag on the resolution is what actually gates
    /// the feature, so a stale/unresolved optional address is harmless even though it is copied
    /// in here unchanged.</summary>
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
