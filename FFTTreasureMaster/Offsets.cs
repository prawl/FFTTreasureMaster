namespace FFTTreasureMaster;

/// <summary>
/// Verified game addresses for FFT: The Ivalice Chronicles (fft_enhanced.exe, 1.5).
/// The image base is fixed at 0x140000000 with no ASLR, and every address below lives
/// in the always-mapped main module, so they are valid in-process pointers -- but access
/// still goes through <see cref="Mem"/> (RPM/WPM), NEVER a raw deref.
///
/// This is the Treasure-Master-only subset of the addresses (battle sentinels + map
/// identity + terrain grid). Sources: FFTHandsFree/docs/BATTLE_MEMORY_MAP.md and the
/// live re-find probes noted per field.
/// </summary>
internal static class Offsets
{
    // --- in-battle sentinels (read by the engine loop; fed to BattleState) ---
    public const long Slot0 = 0x140782A30;   // 1.5 +0x6000 (was 0x14077CA30): in-battle existence marker (sticks 0xFF after a QUIT)
    public const long Slot9 = 0x140782A54;   // 1.5 +0x6000 (was 0x14077CA54): in-battle sentinel (reads 0xFFFFFFFF)
    public const long EventId = 0x140782A94; // 1.5 +0x6000 (was 0x14077CA94): u16 event/cutscene id out of live battle (suspends the exit timer)

    // --- battlefield discriminator: 0 = OUT of battle (world map / menus, even while slot9
    //     is the stuck sentinel), 2/3/4 = on the live battlefield, 1/5 = targeting/animation.
    //     Verified in FFTHandsFree (CommandWatcher.cs); BattleState.BattleDisplayed gates the
    //     Treasure module on slot9-stuck AND mode != 0. ---
    public const long BattleMode = 0x1409069A0;   // 1.5 +0x6350 (was 0x140900650): u8

    // --- pause flag: 1 while a menu / Status card is open, 0 on the free battlefield / enemy
    //     turns. Used by BattleState to suspend the exit debounce (a pause mid-battle must not
    //     accumulate out-of-live time). ---
    public const long PauseFlag = 0x140C6B1C8;   // 1.5 confirmed live 2026-06-17 (was 0x140C64A5C)

    // --- Treasure Master: map identity + terrain grid ---
    // u8 current battle's map id; valid 1..127 (FFTHandsFree LiveBattleMapId contract).
    // STALE out of battle: only read while a battle map is displayed.
    public const long LiveBattleMapId = 0x140784478;   // 1.5 re-found 2026-06-17 +0x6C3C (was 0x14077D83C)

    // Static per-map terrain records, 7 bytes/tile; used read-only as the map-identity
    // fingerprint source (FNV-1a64 over a fixed-length prefix).
    public const long TerrainGrid = 0x140C6B440;   // 1.5 re-found 2026-06-17 +0x6440 (was 0x140C65000)

    // --- Claim detection: unit positions + inventory counts (VERIFIED 1.5, 2026-06-19) ---
    // A treasure tile is claimed when an item appears in inventory WHILE a unit stands on that tile.
    // Two confirmed signals (probe: tools/probes/unit_occupancy_probe.py):
    //   POSITION -- battle unit array at a fixed 0x200 stride in the stable 0x14185xxxx segment;
    //     the LIVE grid position tracks there (X at the X-field, Y at X+1). Re-found via the
    //     move-differential; identical across battles. Used only to know WHICH tile a unit is on.
    //   INVENTORY -- a 256-entry item-count array (u8 each, capped at 99): count[itemId] =
    //     InventoryCountBase + itemId. Confirmed by claim (+1 on a Move-Find pickup) and equip
    //     (-1) both hitting count[129] (Galewall); ticks mid-battle; persistent party data.
    // Together they need no eligibility byte: the count only rises when an ELIGIBLE unit (Chemist
    // or a Treasure-Hunter unit) actually claims, and the position pins the exact tile -- so this
    // covers both cases and disambiguates the 26 maps that reuse a rare item id across tiles.
    // The (0,0) coordinate is ignored for occupancy (empty / non-tracking template copies read 0,0).

    /// <summary>Grid-X address of the lowest unit slot the occupancy scan walks. Slot k's grid X is
    /// UnitArrayBaseX + k*UnitRecordStride; grid Y is at +1.</summary>
    public const long UnitArrayBaseX = 0x141851F2FL;
    /// <summary>Number of unit slots scanned (covers the live unit block with margin).</summary>
    public const int  UnitArraySlots = 65;
    /// <summary>Bytes between consecutive unit records.</summary>
    public const long UnitRecordStride = 0x200L;
    /// <summary>Base of the inventory item-count array; count of item id is the u8 at base+id.</summary>
    public const long InventoryCountBase = 0x1411A7C00L;

    // --- Persistent collected-treasure flags (LOCATED + DECODED 2026-06-21) ---
    // 64-byte bitfield at this base holds "collected" flags for all Move-Find treasures
    // (128 maps x 4 slots = 512 bits, MSB-first within each byte).
    // Decode: idx = mapId * 4 + slot (slot = 0-based position in the map's treasure list,
    // native X1..X4 file order); byte = base + idx/8; bit = 7 - (idx % 8).
    // The Readable guard in CollectAudit keeps this fail-safe: an unreadable byte returns
    // false (tile stays lit), so the worst case is a stale lit tile, never a missing tile.
    public static readonly long TreasureCollectedBase = 0x1411A7680L;

    // --- EnhancedMarker (native yellow move-find diamonds) -- UNVERIFIED for our 1.5 build ---
    // Static slot holding a pointer to the game's EnhancedMarkingUtility heap object. The marker
    // array begins at +0x8 (stride 0x18); MarkerWriter sets Enabled=2 + grid (X,Y) per treasure
    // tile and the game renders a yellow diamond. The value below is dicene's
    // FFT-MoveFind_Markers address and is NOT yet confirmed against our build key, so the write
    // path stays gated off (Tuning.EnhancedMarkersEnabled) until a live probe matches it. The
    // build-key L0 gate (TreasureMaster) is the global safety net if the game is ever patched.
    public const long EnhancedMarkingUtilityPtr = 0x143CD3AA0;
}
