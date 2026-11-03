namespace FFTTreasureMaster;

/// <summary>
/// The Treasure Master tuning knobs, kept in one place so every part of the module agrees.
/// Plain constants, since the mod ships one configuration and the only user facing lever is
/// the toggle in the Reloaded config.
/// </summary>
internal static class Tuning
{
    /// <summary>The documented default for Config.Enabled. The runtime value comes from the
    /// config loaded by Mod.cs, and this is the fallback when the file cannot be read. Default on,
    /// since marking treasure tiles is the whole point of the mod.</summary>
    public const bool TreasureEnabled = true;

    /// <summary>Consecutive same-map-id ticks required before arming begins (~1s at 33ms).</summary>
    public const int TreasureArmStableTicks = 30;

    /// <summary>Ticks between full fingerprint revalidations while ARMED.</summary>
    public const int TreasureRevalidateEveryNTicks = 30;

    /// <summary>Arming attempts before logging "waiting to arm" once per battle. Arming keeps
    /// going after the cap, so the log is informational only.</summary>
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

    /// <summary>Master gate for the experimental native marker write path in MarkerWriter. While
    /// false, MarkerWriter.Write is a guaranteed no-op that reads no game memory.
    ///
    /// Dark. The floating diamond path was abandoned because dicene's build differs from our 1.5,
    /// his utility pointer reads null here, and an idle marker array cannot be fingerprinted. We
    /// get yellow tiles from the flat MarkValue write instead. Left wired in case the pointer is
    /// ever found by disassembly.</summary>
    public const bool EnhancedMarkersEnabled = false;

    /// <summary>
    /// The documented default for Config.HideClaimedTiles. The runtime value comes from the
    /// config through Mod.cs, and this is the fallback when the ctor gets a null claimDetection.
    /// When true, a tile stops being highlighted once an eligible unit picks up its treasure.
    /// The addresses are verified on 1.5 and the read path fails safe, so the worst case is a
    /// tile that stays lit. Default on.
    /// </summary>
    public const bool ClaimDetectionEnabled = true;

    /// <summary>Investigation flag for the missing tiles after "Retry from Start of Battle". The
    /// game rebuilds its tile highlight overlay only on a full battle load, so an in battle Retry
    /// never re-consumes the held mark, see handoff.md. When true, the engine logs the battle
    /// presence sentinel transitions and the module logs its phase transitions and a per tick
    /// hold breakdown, all deduped on change. Left wired off for a future session. Reads are
    /// guarded. Static readonly rather than const so the gated blocks do not trip
    /// CS0162.</summary>
    public static readonly bool RetryDiagnostics = false;

    /// <summary>Live test instrument for claim detection. When true, DetectClaims logs the set of
    /// treasure tiles a unit stands on and each tile's item counts, deduped on change. Left wired
    /// off because it re-fires every time a unit steps onto or off a treasure tile, which spams a
    /// moving battle. The per tile ReadCount reads are skipped entirely while off. Static readonly
    /// rather than const so the gated block does not trip CS0162.</summary>
    public static readonly bool ClaimDiagnostics = false;

    /// <summary>Gate for hiding tiles whose treasure was collected and the battle won in a prior
    /// battle, see CollectAudit. The table is a 64 byte bitfield at Offsets.TreasureCollectedBase,
    /// indexed by map id times 4 plus slot, most significant bit first. CollectAudit reads it
    /// behind a Readable guard, so an unreadable byte leaves the tile lit. Static readonly rather
    /// than const so the gated wiring does not trip CS0162.</summary>
    public static readonly bool CollectDetectionEnabled = true;

    // --- innate Treasure Hunter grant (TreasureHunterGrant, via the FFTIVC modloader) ---

    /// <summary>The documented default for Config.AllUnitsTreasureHunter. The runtime value comes
    /// from the config through Mod.cs, and this is the fallback when the file cannot be read. When
    /// true, the mod asks the separately installed FFTIVC Mod Loader to add the Treasure Hunter
    /// movement ability as a built in innate on the standard player jobs, so any unit of those
    /// jobs picks up treasure by stepping on the tile. Enemy humans of those jobs gain it too.
    /// Default off, because it changes battle balance, so the player opts in.</summary>
    public const bool AllUnitsTreasureHunterEnabled = false;

    // --- rare treasure only (loader trap-table patch) ---

    /// <summary>Fallback for Config.RareTreasureOnly when the config file cannot be read.
    /// Default OFF so the player opts in.</summary>
    public const bool RareTreasureOnlyEnabled = false;

    /// <summary>Map ids in the game's trap formation table, 0 through 127.</summary>
    public const int TrapTableMapCount = 128;

    /// <summary>Treasure slots per map in the trap formation table.</summary>
    public const int TrapSlots = 4;

    /// <summary>Reloaded mod id of the FFTIVC Mod Loader.</summary>
    public const string LoaderModId = "fftivc.utility.modloader";

    /// <summary>This mod's own Reloaded id, the folder name under Mods and User/Mods and the owner
    /// stamped on every table patch. Must match ModConfig.json.</summary>
    public const string OwnerModId = "prawl.fft.treasuremaster";

    /// <summary>The JOB_DATA rows the Treasure Hunter grant covers. Ramza's three story jobs (1,
    /// 2, 3), the joined special characters with a free innate slot (13 Orlandeau, 22 Mustadio,
    /// 25 Rapha, 26 Marach, 30 Agrias, 50 Cloud, 72 Reis as Holy Dragon), and the twenty generic
    /// jobs (74 to 93). Left out are 31 Beowulf, the only Templar row and shared with enemy
    /// Templars, 42 Meliadoul, whose row is not live verified, 15 Reis human, 144 Byblos and 145
    /// Construct 8, whose four innate slots are full, 21 Orran, a guest only, and every monster
    /// row (94 to 141), where wild monster spillover outweighs the benefit. Values from the
    /// vanilla JOB_DATA dump, verified against the live premise probe of July 2026.</summary>
    public static readonly int[] TreasureHunterGrantJobIds =
    {
        1, 2, 3, 13, 22, 25, 26, 30, 50, 72,
        74, 75, 76, 77, 78, 79, 80, 81, 82, 83,
        84, 85, 86, 87, 88, 89, 90, 91, 92, 93,
    };

    /// <summary>Delay between readiness probes while waiting for the modloader's job table
    /// (its signature scan runs asynchronously at startup; there is no cross-mod ordering
    /// guarantee, so the grant polls instead of assuming).</summary>
    public const int GrantRetryIntervalMs = 250;

    /// <summary>Total time the grant will wait for the job table before giving up for the
    /// session (one warn line; the core mod is unaffected).</summary>
    public const int GrantRetryCapMs = 30000;

    /// <summary>Delay before the single re-assert pass that re-reads every granted row and
    /// re-applies the ability if another mod's table edit clobbered it (last-writer-wins
    /// merge in the modloader; our late write usually wins, this makes it certain).</summary>
    public const int GrantReassertDelayMs = 2000;

    // --- the signature re-resolve scan mechanics (AnchorScan and AnchorResolver) ---

    /// <summary>Bytes read per chunk while scanning a section for a signature pattern. Chunking
    /// keeps a single TryReadBytes call small and predictable; the whole section is covered by
    /// stepping this far each iteration (the actual read length also includes the overlap below).</summary>
    public const int AnchorScanChunkBytes = 0x40000;

    /// <summary>Largest section VirtualSize eligible for scanning. Comfortably above the real
    /// game's ~0x610000 executable text section, but well under the game's 0x147AC7E0-byte
    /// RWX .trace blob (an execute-flagged section that is emphatically not code to search).</summary>
    public const int AnchorScanMaxSectionBytes = 0x2000000;

    /// <summary>Total bytes scannable across every eligible section before the whole resolve
    /// gives up and returns null. Several moderate sections could otherwise sum past a sane
    /// budget even though each passes AnchorScanMaxSectionBytes.</summary>
    public const int AnchorScanMaxTotalBytes = 0x4000000;

    /// <summary>Per-pattern hit cap. Two or more hits already means the signature is ambiguous
    /// (a hard fail for the whole resolution); this just bounds the list so a degenerate
    /// mostly-wildcard pattern can't grow unbounded.</summary>
    public const int AnchorScanHitCap = 8;

    /// <summary>Largest |delta| (resolved VA minus baked VA) accepted from a signature
    /// resolve. A sane rebase after a patch shifts code by at most a few sections' worth of
    /// bytes; anything past this cap is treated as a bad resolve (null) rather than trusted.</summary>
    public const int AnchorMaxDeltaBytes = 0x4000000;
}
