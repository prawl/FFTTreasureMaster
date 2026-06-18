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
}
