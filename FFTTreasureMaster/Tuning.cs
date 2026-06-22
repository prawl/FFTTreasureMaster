namespace FFTTreasureMaster;

/// <summary>
/// Treasure Master tuning knobs, in one place so the state machine, the arm gate, and the
/// fast-hold thread agree. Plain constants -- no build flavors (the mod ships a single
/// configuration; the only user-facing lever is the on/off toggle in the Reloaded config).
/// </summary>
internal static class Tuning
{
    /// <summary>Documented default for Config.Enabled (the runtime value flows from
    /// FFTTreasureMaster.Configuration.Config, loaded by Mod.cs at startup; this constant is the
    /// fallback when the config file can't be read). Default ON -- marking treasure tiles is the
    /// whole point of the mod; the player turns it OFF in the Reloaded config if they prefer.</summary>
    public const bool TreasureEnabled = true;

    /// <summary>Consecutive same-map-id ticks required before arming begins (~1s at 33ms).</summary>
    public const int TreasureArmStableTicks = 30;

    /// <summary>Ticks between full fingerprint revalidations while ARMED.</summary>
    public const int TreasureRevalidateEveryNTicks = 30;

    /// <summary>Maximum arming attempts before logging "waiting to arm" once per battle.
    /// Arming continues indefinitely after the cap -- the log is informational only.</summary>
    public const int TreasureArmAttemptCap = 60;

    /// <summary>Minimum number of Resting or Held ("ok") tile addresses required at arm time
    /// to proceed with arming. Below this quorum the module stays ARMING (cheap polling) until
    /// enough tiles scroll into view. Protects against a battle start where most tiles are
    /// off-screen (action camera / narrow view) without permanently disarming.</summary>
    public const int TreasureMinPlausibleAddrs = 4;

    /// <summary>Consecutive bad-map-id ticks while ARMED before a full state reset
    /// back to DISARMED. The map-id change IS the battle boundary for chained story battles
    /// (the debounced exit edge may never fire in those cases).</summary>
    public const int TreasureMapIdBadTicksToReset = 3;

    /// <summary>How many Tick() calls between dataset-stamp checks (applies regardless of
    /// phase or inLive). 30 ticks ~= 1 s at the 33 ms loop. A changed stamp triggers a full
    /// reload + state reset so the next arm cycle uses fresh data.</summary>
    public const int TreasureStampCheckTicks = 30;

    /// <summary>FastHold re-stamp interval in ms (~2x per 60 fps animation frame ~= 16 ms).
    /// Out-paces the running-water wipe that clears 0x80 between 33 ms loop re-stamps.</summary>
    public const int TreasureFastHoldMs = 8;

    /// <summary>Master gate for the experimental EnhancedMarker write path (native yellow
    /// move-find diamonds via the game's EnhancedMarkingUtility -- see MarkerWriter.cs).
    /// While false, MarkerWriter.Write is a guaranteed no-op that reads no game memory.
    ///
    /// DARK. The EnhancedMarker (floating-diamond) path was abandoned: dicene's build differs
    /// from our 1.5 (both code and the utility pointer shifted non-uniformly; his
    /// EnhancedMarkingUtilityPtr reads null here), and an idle marker array can't be fingerprinted.
    /// We get yellow tiles instead via the flat MarkValue (0xCC) write -- see TreasureMaster.Policy.
    /// Left wired (no-op) in case the utility pointer is ever found by disassembly.</summary>
    public const bool EnhancedMarkersEnabled = false;

    /// <summary>
    /// Documented default for Config.HideClaimedTiles (the runtime value flows from
    /// FFTTreasureMaster.Configuration.Config via Mod.cs; this constant is the fallback used only
    /// when the ctor is passed a null claimDetection). When true, a tile stops being highlighted
    /// once its Move-Find treasure is claimed -- an eligible unit (a Chemist, or any unit with the
    /// Treasure Hunter movement ability) picks up the hidden item. The unit-occupancy + inventory
    /// addresses are 1.5-verified and baked; the read path fails safe (any unreadable read returns
    /// without marking a claim, so the worst case is a tile that stays lit). Default ON.
    /// </summary>
    public const bool ClaimDetectionEnabled = true;

    /// <summary>Investigation flag for the "no tiles after Retry from Start of Battle" bug (root
    /// cause found: the game rebuilds its tile-highlight overlay only on a full battle load, so an
    /// in-battle Retry never re-consumes the held 0xCC -- see handoff.md "RETRY RENDER-GATE"). When
    /// true, the engine logs battle-presence sentinel transitions (slot0/slot9/mode/displayed) and
    /// the module logs its phase/inLive transitions plus a per-armed-tick hold breakdown -- all
    /// DEDUPED (logged only on change). Left wired (OFF) for a future RE session. Reads are guarded.
    /// (static readonly, not const, so the gated blocks do not trip CS0162 unreachable-code.)</summary>
    public static readonly bool RetryDiagnostics = false;

    /// <summary>Live-test instrument for claim detection: when true, DetectClaims logs the set of
    /// treasure tiles a unit is standing on plus each tile's rare/common item counts, DEDUPED
    /// (logged only on change). Used to verify the claim-latch path on a real map; left wired OFF
    /// because it re-fires every time a unit steps onto or off a treasure tile -- spam during a
    /// moving battle. The per-tile ReadCount game-memory reads are skipped entirely while off.
    /// (static readonly, not const, so the gated block does not trip CS0162 unreachable-code.)</summary>
    public static readonly bool ClaimDiagnostics = false;

    /// <summary>Gate for PERSISTENT collected-treasure detection (excluding tiles whose Move-Find
    /// treasure was collected AND the battle won in a PRIOR battle -- see CollectAudit).
    /// The table is located and decoded: 64-byte bitfield at Offsets.TreasureCollectedBase,
    /// indexed as idx = mapId*4 + slot, MSB-first within each byte. CollectAudit reads this
    /// Readable-guarded; any unreadable byte returns false (tile stays lit -- fail-safe).
    /// (static readonly, not const, so the gated wiring does not trip CS0162 unreachable-code.)</summary>
    public static readonly bool CollectDetectionEnabled = true;
}
