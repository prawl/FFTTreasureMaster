namespace FFTTreasureMaster;

/// <summary>
/// The verified game addresses this mod reads, for fft_enhanced.exe 1.5.
/// Every pin is a preferred base absolute, the only base the Denuvo wrapped builds had. The
/// Denuvo free build loads at a random base, so each pin is a property that rebases it through
/// <see cref="ImageBase.At"/> and hands out a live address. Access still goes through
/// <see cref="Mem"/>, never a raw deref.
///
/// Only the subset Treasure Master needs lives here, the battle sentinels, the map identity and
/// the terrain grid. The sources are FFTHandsFree's battle memory map and the live re-find
/// probes noted per field. AddrMap surfaces these to the runtime and nothing else reads them
/// directly. On a build key mismatch AnchorResolver re-resolves the live addresses by signature
/// and AddrMap.Apply replaces every field, so these pins are only the starting point.
/// </summary>
internal static class Offsets
{
    // --- in-battle sentinels (read by the engine loop; fed to BattleState) ---
    public static long Slot0 => ImageBase.At(0x140782A30);   // 1.5 +0x6000 (was 0x14077CA30): in-battle existence marker (sticks 0xFF after a QUIT)
    public static long Slot9 => ImageBase.At(0x140782A54);   // 1.5 +0x6000 (was 0x14077CA54): in-battle sentinel (reads 0xFFFFFFFF)
    public static long EventId => ImageBase.At(0x140782A94); // 1.5 +0x6000 (was 0x14077CA94): u16 event/cutscene id out of live battle (suspends the exit timer)

    // The battlefield state byte. 0 is out of battle (world map and menus, even while slot9 is
    // the stuck sentinel), 2, 3 and 4 are the live battlefield, 1 and 5 are targeting and
    // animation. BattleState.BattleDisplayed gates the module on slot9 stuck and mode not zero.
    public static long BattleMode => ImageBase.At(0x1409069A0);   // 1.5 +0x6350 (was 0x140900650): u8

    // The pause flag, 1 while a menu or Status card is open and 0 on the free battlefield.
    // BattleState uses it to suspend the exit debounce, so a pause mid battle never counts as
    // out of live time.
    public static long PauseFlag => ImageBase.At(0x140C6B1C8);   // 1.5 confirmed live 2026-06-17 (was 0x140C64A5C)

    // Map identity and terrain grid.
    // The current battle's map id as a u8, valid 1 to 127. It is stale out of battle, so only
    // read it while a battle map is displayed.
    public static long LiveBattleMapId => ImageBase.At(0x140784478);   // 1.5 re-found 2026-06-17 +0x6C3C (was 0x14077D83C)

    // Static per-map terrain records, 7 bytes/tile; used read-only as the map-identity
    // fingerprint source (FNV-1a64 over a fixed-length prefix).
    public static long TerrainGrid => ImageBase.At(0x140C6B440);   // 1.5 re-found 2026-06-17 +0x6440 (was 0x140C65000)

    // Claim detection, verified on 1.5 in June 2026. A treasure tile is claimed when an item
    // appears in the inventory while a unit stands on that tile. The battle unit array sits at a
    // fixed 0x200 stride and tracks each unit's live grid position, X at the X field and Y one
    // byte after. The inventory is a 256 entry item count array of u8, capped at 99, indexed by
    // item id, and it ticks mid battle. Together they need no eligibility byte, because the count
    // only rises when an eligible unit claims, and the position pins the exact tile, which tells
    // apart the 26 maps that reuse a rare item id across tiles. A unit at (0,0) is ignored, since
    // empty template copies read 0,0.

    /// <summary>Grid-X address of the lowest unit slot the occupancy scan walks. Slot k's grid X is
    /// UnitArrayBaseX + k*UnitRecordStride; grid Y is at +1.</summary>
    public static long UnitArrayBaseX => ImageBase.At(0x141851F2FL);
    /// <summary>Number of unit slots scanned (covers the live unit block with margin).</summary>
    public const int UnitArraySlots = 65;
    /// <summary>Bytes between consecutive unit records.</summary>
    public const long UnitRecordStride = 0x200L;
    /// <summary>Base of the inventory item-count array; count of item id is the u8 at base+id.</summary>
    public static long InventoryCountBase => ImageBase.At(0x1411A7C00L);

    // The persistent collected treasure flags, located and decoded in June 2026. A 64 byte
    // bitfield holds a collected flag for all 512 move find treasures, 128 maps by 4 slots, most
    // significant bit first. The bit index is the map id times 4 plus the slot, where the slot is
    // the tile's position in the map's native X1 to X4 order. CollectAudit reads it behind a
    // Readable guard, so an unreadable byte leaves the tile lit rather than hiding it.
    public static long TreasureCollectedBase => ImageBase.At(0x1411A7680L);

    // The native yellow move find diamonds, unverified on our 1.5 build. A static slot holds a
    // pointer to the game's EnhancedMarkingUtility object, whose marker array begins 8 bytes in
    // with a 0x18 stride. MarkerWriter sets Enabled to 2 and the grid X and Y per treasure tile,
    // and the game renders a yellow diamond. The value is dicene's FFT-MoveFind_Markers address
    // and is not confirmed against our build key, so the write path stays off in Tuning until a
    // live probe matches it.
    public static long EnhancedMarkingUtilityPtr => ImageBase.At(0x143CD3AA0);
}
