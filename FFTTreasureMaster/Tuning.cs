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
}
