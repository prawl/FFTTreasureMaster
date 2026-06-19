using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFTTreasureMaster;
using Newtonsoft.Json;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Runtime state machine for the Treasure Master module.  All tests drive the typed
/// Tick(DateTime, bool) + ResetBattle() entry points through a FakeSparseMemory so
/// no live game process is needed.
///
/// House invariant matrix:
///   (1)  !inLive ticks issue ZERO writes (Written empty throughout).
///   (2)  Arms after stable map id + fingerprint + audit, then writes MarkValue (0xCC) to
///        each Resting addr.
///   (3)  Re-stamps after a simulated engine clear (reset byte to off -> next tick re-writes).
///   (4)  Pre-marked 0xCC byte (Held): never written at all.
///   (5)  Mark-value structural assert: every value in Written equals MarkValue (0xCC).
///   (6)  Fingerprint mismatch at arm: zero writes ever + once log.
///   (7)  Fingerprint drift mid-battle (mutate terrain bytes, advance past the revalidate
///        tick): writes CONTINUE -- identity is proven at ARM time, so a mid-battle drift is
///        informational only and never disarms (LIVE INCIDENT #4, Siedge Weald map 74).
///   (8)  Map-id flip mid-ARMED: no writes on that tick; full re-arm cycle against the
///        new map after the bad-tick threshold.
///   (9)  Foreign addrs at arm: foreign addrs are simply skipped; arms when >= minPlausible
///        ok addrs exist; foreign addrs are never written.
///   (10) Unwritable addr skipped while siblings still written.
///   (11) ResetBattle clears state, writes nothing, fresh battle re-arms.
///   (12) Stub map (no tiles): no writes, exactly zero tile-addr writes.
///   (13) Build-key mismatch: zero flag-address reads/writes ever.
///   (14) Foreign bytes while ARMED (e.g. camera pan): module stays ARMED, skips the
///        foreign addrs, holds the rest; resumes writing foreign addrs when they return
///        to Resting (camera-pan round-trip).
///
/// Plus one PinnedBuf fact through LiveMemory: hold a 6-byte tile against pinned
/// process memory, assert MarkValue (0xCC) lands at the target offset and neighbors are untouched.
/// </summary>
public class TreasureMasterTests
{
    // ── test-db helpers ──────────────────────────────────────────────────────────

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tm_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A tile address inside the module span but outside the UI arena, guaranteed
    /// to be unique per test invocation via a base + offset scheme.
    /// 0x140200000 is well inside 0x140000000..0x143000000 and outside the UI arena.
    /// </summary>
    private static long TileAddr(int slot = 0) => 0x140200000L + slot * 0x1000;

    // Addr layout written into treasure.json: each addr entry is [addrHex, offHex].
    private static string AddrJson(long addr, byte off = 0x00)
        => $@"[""{addr:x}"", ""{off:x02}""]";

    /// <summary>Build a treasure.json with one map, one tile, given addresses.</summary>
    private static TreasureDb BuildDb(
        string dir, int mapId = 74, string name = "Test Map",
        int tileX = 0, int tileY = 1,
        IEnumerable<(long addr, byte off)>? addrs = null,
        int? fpLen = null, string? fpHash = null,
        TreasureBuildKey? buildKey = null,
        bool stub = false,
        int? fpVer = null)
    {
        var addrList = addrs?.ToList() ?? new List<(long, byte)>
            { (TileAddr(0), 0x00), (TileAddr(1), 0x00) };

        string tilesJson = stub ? "[]" : $@"[{{
            ""x"": {tileX}, ""y"": {tileY},
            ""addrs"": [{string.Join(", ", addrList.Select(a => AddrJson(a.addr, a.off)))}]
        }}]";

        string fpVerStr   = fpVer  is {} ver ? $@"""fpVer"": {ver},"   : "";
        string fpLenStr   = fpLen  is {} l ? $@"""fpLen"": {l},"   : "";
        string fpHashStr  = fpHash is {} h ? $@"""fpHash"": ""{h}"","  : "";
        string bkStr      = buildKey is null ? "null" :
            $@"{{""timeDateStamp"": {buildKey.TimeDateStamp}, ""sizeOfImage"": {buildKey.SizeOfImage}}}";

        string json = $@"{{
            ""buildKey"": {bkStr},
            ""maps"": [{{
                ""mapId"": {mapId}, ""name"": ""{name}"", ""tileCount"": {(stub ? 2 : addrList.Count)},
                {fpVerStr}
                {fpLenStr}
                {fpHashStr}
                ""tiles"": {tilesJson}
            }}]
        }}";
        File.WriteAllText(Path.Combine(dir, "treasure.json"), json);
        return TreasureDb.Load(dir);
    }

    // ── terrain-fingerprint helpers ──────────────────────────────────────────────

    /// <summary>Compute the v1 FNV-1a64 hash of <paramref name="terrain"/> to seed fpHash.</summary>
    private static string TerrainFpHash(byte[] terrain)
        => TreasureMaster.Fnv1a64(terrain).ToString("x");

    /// <summary>Compute the v2 masked hash of <paramref name="terrain"/> to seed fpHash.</summary>
    private static string TerrainFpHashV2(byte[] terrain)
        => TreasureMaster.MaskedTerrainHash(terrain).ToString("x");

    // ── fake-memory builder ──────────────────────────────────────────────────────

    /// <summary>
    /// A FakeSparseMemory pre-seeded for a one-tile, two-addr test scenario:
    ///   - mapId at Offsets.LiveBattleMapId
    ///   - terrain bytes at Offsets.TerrainGrid (fpLen bytes)
    ///   - tile addrs Resting (0x00), marked Writable
    /// </summary>
    private static FakeSparseMemory BuildMem(
        byte mapId,
        byte[] terrain,
        IList<long> tileAddrs,
        byte initialByte = 0x00,
        bool addrsWritable = true)
    {
        var mem = new FakeSparseMemory();
        // map id
        mem.U8s[Offsets.LiveBattleMapId] = mapId;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        // terrain block (TryReadBytes path -- added as a region)
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        // tile addrs
        foreach (long a in tileAddrs)
        {
            mem.U8s[a] = initialByte;
            mem.ReadableAddrs.Add(a);
            if (addrsWritable) mem.WritableAddrs.Add(a);
        }
        return mem;
    }

    // PE header read helper: seed the fake with what ArmAudit.ReadPeBuildKey reads.
    // ArmAudit reads U32 as 4 little-endian U8 reads.
    private static void SeedPeHeader(FakeSparseMemory mem, uint timeDateStamp, uint sizeOfImage)
    {
        // e_lfanew = U32 @ 0x140000000+0x3C
        long elfanewAddr = 0x140000000L + 0x3C;
        uint eLfanew = 0x100; // a plausible e_lfanew offset
        mem.SeedU32(elfanewAddr, eLfanew);
        // timeDateStamp = U32 @ base+e_lfanew+8
        long tsAddr = 0x140000000L + eLfanew + 8;
        mem.SeedU32(tsAddr, timeDateStamp);
        // sizeOfImage = U32 @ base+e_lfanew+0x50
        long szAddr = 0x140000000L + eLfanew + 0x50;
        mem.SeedU32(szAddr, sizeOfImage);
    }

    // ── tick helpers ─────────────────────────────────────────────────────────────

    private static void TickN(TreasureMaster tm, int n, bool inLive = true, DateTime? t = null)
    {
        var now = t ?? DateTime.Now;
        for (int i = 0; i < n; i++)
            tm.Tick(now + TimeSpan.FromMilliseconds(i * 33), inLive);
    }

    // Advance past the stability window (TreasureArmStableTicks) + a few extra for arming
    private static void StabilizeAndArm(TreasureMaster tm, int extra = 5)
        => TickN(tm, Tuning.TreasureArmStableTicks + extra);

    /// <summary>Create a TreasureMaster with enabled=true (the module's master toggle) so the
    /// arm path runs in tests regardless of the shipped default.</summary>
    private static TreasureMaster Make(TreasureDb db, IGameMemory mem)
        => new(db, mem, enabled: true);

    // ── Config.Enabled = false: permanently idle, reads no game memory ────────────

    /// <summary>The central new behavior of the standalone mod: a disabled module never arms,
    /// never writes a mark, and never even reads a game address (not the flag bytes, not the
    /// map id). Mirrors BuildKey_mismatch_zero_writes_and_no_flag_addr_reads.</summary>
    [Fact]
    public void Disabled_zero_writes_and_no_reads()
    {
        var dir     = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr    = TileAddr(7);
        var db  = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
                          addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = new TreasureMaster(db, mem, enabled: false);
        TickN(tm, Tuning.TreasureArmStableTicks + 20, inLive: true);

        Assert.Empty(mem.Written);                                               // never marks a tile
        Assert.False(mem.ReadCount.TryGetValue(addr, out _));                     // never reads a flag byte
        Assert.False(mem.ReadCount.TryGetValue(Offsets.LiveBattleMapId, out _));  // never reads the map id
    }

    // ── (1) !inLive ticks issue ZERO writes ──────────────────────────────────────

    [Fact]
    public void InLiveFalse_zero_writes_throughout()
    {
        var dir = TempDir();
        var terrain = new byte[7];
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain));
        var addrs = new[] { TileAddr(0), TileAddr(1) };
        var mem = BuildMem(74, terrain, addrs);

        var tm = Make(db, mem);
        TickN(tm, Tuning.TreasureArmStableTicks + 20, inLive: false);

        Assert.Empty(mem.Written);
    }

    // ── (2) Arms and writes MarkValue (0xCC) to each Resting addr ─────────────────

    [Fact]
    public void Armed_writes_mark_value_to_each_resting_addr()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(0), TileAddr(1) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addrs[0]));
        Assert.True(mem.Written.ContainsKey(addrs[1]));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrs[0]]);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrs[1]]);
    }

    // addr with low bit set (0x01): flat mark, NOT 0x80|0x01 -- the byte value is the colour,
    // so a low-bit-resting tile gets exactly MarkValue (0xCC), same as a 0x00-resting tile.
    [Fact]
    public void Armed_writes_flat_mark_value_on_low_bit_resting_addr()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var addr = TileAddr(2);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: 0x01);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── (3) Re-stamps after a simulated engine clear ──────────────────────────────

    [Fact]
    public void Armed_restamps_after_engine_clears_mark()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70 };
        var addr = TileAddr(3);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.True(mem.Written.ContainsKey(addr));

        // Simulate engine clearing the mark
        mem.U8s[addr] = 0x00;
        mem.Written.Clear();

        // Next tick should re-write
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── (4) Pre-marked 0x81 byte (Held): never written ───────────────────────────

    [Fact]
    public void Held_addr_never_written()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0xDE, 0xAD, 0x01 };
        var addr = TileAddr(4);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        // MarkValue (0xCC) = Held -- the flat mark already present on the tile
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: TreasureMaster.MarkValue);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        // Advance many more ticks past revalidate period
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 10);

        Assert.False(mem.Written.ContainsKey(addr));
    }

    // ── (5) mark-value structural assert: every write is exactly MarkValue ────────

    [Fact]
    public void Every_Written_value_equals_mark_value()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(0), TileAddr(1) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 20);

        Assert.NotEmpty(mem.Written);
        foreach (var kv in mem.Written)
            Assert.Equal(TreasureMaster.MarkValue, kv.Value);
    }

    // ── (6) Fingerprint mismatch at arm: ARMS ANYWAY (advisory fingerprint) ────────
    // LIVE INCIDENT #5: terrain fingerprints are advisory only. Per-battle weather (rain)
    // perturbs the hashed terrain fields, so a captured hash won't match a differently-
    // weathered instance -- and there is no data to know which maps can weather. Arming is
    // gated by the per-tile resting-byte quorum (L3), not the fingerprint, so a mismatch
    // still arms; the mismatch is logged once per battle as telemetry.

    [Fact]
    public void Fingerprint_mismatch_at_arm_arms_anyway()
    {
        var dir  = TempDir();
        var realTerrain  = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
        var wrongTerrain = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00 };
        var addr = TileAddr(5);
        // DB stores hash for realTerrain, but memory has wrongTerrain (e.g. it's raining).
        var db = BuildDb(dir, fpLen: realTerrain.Length, fpHash: TerrainFpHash(realTerrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, wrongTerrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // The resting tile addr meets quorum, so the module arms and holds despite the
        // fingerprint mismatch.
        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── (7) Fingerprint drift mid-battle: KEEP HOLDING (no disarm) ────────────────
    // LIVE INCIDENT #4 (Siedge Weald, map 74): a fingerprinted map's "static" terrain
    // fields drifted ~26 s into the same battle, and the old periodic-revalidation path
    // permanently disarmed -- killing the marks for the rest of the fight. The mid-battle
    // fingerprint check is now informational only: identity is proven at ARM time and the
    // per-tick map-id check guards chained battles, so a mid-battle drift must NOT stop
    // holding.

    [Fact]
    public void Fingerprint_drift_mid_battle_keeps_holding()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(6);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Drift the terrain bytes in memory (static fields no longer match the captured hash).
        for (int i = 0; i < terrain.Length; i++)
            terrain[i] ^= 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;   // engine cleared the mark

        // Let the old revalidation path fully run its course (revalidate boundary + arm
        // cap): under the OLD contract the module would now be permanently BattleDisarmed.
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + Tuning.TreasureArmAttemptCap + 10);

        // Clear and prove the module is STILL holding (old behavior: zero writes here).
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "mid-battle fingerprint drift must keep holding, not disarm");
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── (8) Map-id flip mid-ARMED ─────────────────────────────────────────────────

    [Fact]
    public void MapId_flip_mid_armed_suspends_writes_and_triggers_full_reset()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(7);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        // Flip to an unknown map id (not in db)
        mem.U8s[Offsets.LiveBattleMapId] = 99;

        // Run bad-tick threshold ticks -- should NOT write the tile
        TickN(tm, Tuning.TreasureMapIdBadTicksToReset + 2);

        Assert.Empty(mem.Written);
    }

    // ── (9) Foreign addrs at arm: foreign addrs never written; arms when quorum met ──

    /// <summary>
    /// A foreign addr at arm time (e.g. tile off-screen when the battle starts) is simply
    /// never written -- the module arms once TreasureMinPlausibleAddrs ok addrs are visible.
    /// Key assertion: the foreign addr is never written; the resting siblings ARE written.
    /// </summary>
    [Fact]
    public void Foreign_addr_at_arm_is_skipped_and_resting_sibling_is_written()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0x44 };
        var addrForeign = TileAddr(8);
        // Build a map with enough ok addrs to meet quorum (minPlausible=4 default, but we
        // use a multi-addr layout -- provide TreasureMinPlausibleAddrs ok addrs + 1 foreign).
        var okAddrs = Enumerable.Range(0, Tuning.TreasureMinPlausibleAddrs)
            .Select(i => TileAddr(40 + i))
            .ToArray();
        var allAddrs = okAddrs.Concat(new[] { addrForeign }).ToArray();
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: allAddrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, allAddrs, initialByte: 0x00);
        // Flip the foreign addr to 0x42 AFTER seeding (BuildMem seeds all as 0x00)
        mem.U8s[addrForeign] = 0x42;

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Foreign addr is never written
        Assert.False(mem.Written.ContainsKey(addrForeign));
        // At least one ok addr IS written
        Assert.True(okAddrs.Any(a => mem.Written.ContainsKey(a)));
    }

    /// <summary>
    /// Below quorum: all addrs foreign at arm time -> module stays ARMING, zero writes.
    /// It will eventually arm once tiles scroll back into view.
    /// </summary>
    [Fact]
    public void Foreign_all_addrs_at_arm_stays_arming_zero_writes()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0x44 };
        var addr = TileAddr(8);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        // 0x42 is Foreign (not in {0x00, 0x01, 0xCC})
        var mem = BuildMem(74, terrain, new[] { addr }, initialByte: 0x42);

        var tm = Make(db, mem);
        // Arm stable ticks + many extra -- still ARMING (quorum not met)
        TickN(tm, Tuning.TreasureArmStableTicks + Tuning.TreasureArmAttemptCap + 10);

        Assert.Empty(mem.Written);
    }

    // ── (10) Unwritable addr skipped while siblings still written ─────────────────

    [Fact]
    public void Unwritable_addr_skipped_siblings_still_written()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrA = TileAddr(9);
        var addrB = TileAddr(10);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addrA, (byte)0x00), (addrB, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addrA, addrB });
        // Mark addrA as NOT writable
        mem.WritableAddrs.Remove(addrA);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.False(mem.Written.ContainsKey(addrA));
        Assert.True(mem.Written.ContainsKey(addrB));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrB]);
    }

    // ── (11) ResetBattle clears state, writes nothing, fresh battle re-arms ───────

    [Fact]
    public void ResetBattle_clears_state_and_writes_nothing_on_first_tick()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(11);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        tm.ResetBattle();

        // Immediately after ResetBattle, zero writes
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void ResetBattle_fresh_battle_after_reset_rearms_and_writes()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(15);  // unique slot to avoid cross-test contamination
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        // First battle
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Reset (simulates battle end + engine clearing marks)
        tm.ResetBattle();
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;  // engine cleared the mark

        // Second battle: fresh stability + arm cycle
        TickN(tm, Tuning.TreasureArmStableTicks + 5);

        Assert.NotEmpty(mem.Written);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── (12) Stub map: no writes, zero tile-addr writes ───────────────────────────

    [Fact]
    public void Stub_map_no_writes()
    {
        var dir  = TempDir();
        // stub=true: no tiles, no fpHash
        var db = BuildDb(dir, stub: true);
        var addr = TileAddr(12);
        var mem  = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 74;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.U8s[addr] = 0x00;
        mem.ReadableAddrs.Add(addr);
        mem.WritableAddrs.Add(addr);

        var tm = Make(db, mem);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
    }

    // ── (13) Build-key mismatch: zero flag-address reads/writes ever ──────────────

    [Fact]
    public void BuildKey_mismatch_zero_writes_and_no_flag_addr_reads()
    {
        var dir  = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(13);
        // DB has buildKey {ts=0x1234, soi=0x5678}
        var db = BuildDb(dir,
            fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: new TreasureBuildKey { TimeDateStamp = 0x1234, SizeOfImage = 0x5678 });

        var mem = BuildMem(74, terrain, new[] { addr });
        // Seed PE header with DIFFERENT values -> mismatch
        SeedPeHeader(mem, timeDateStamp: 0xAAAA, sizeOfImage: 0xBBBB);

        var tm = Make(db, mem);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
        // The flag addr should never have been touched
        Assert.False(mem.ReadCount.TryGetValue(addr, out _));
    }

    // ── FIX 1: L0 gate bypass -- PE header unreadable defers and blocks phase entry ──

    /// <summary>
    /// When the dataset has a BuildKey but the PE header bytes are NOT yet readable,
    /// CheckGlobalIdle resets _globalIdleChecked=false and returns. The tick must return
    /// immediately without entering the phase switch -- no arming, no writes, no flag reads.
    /// Once the header becomes readable with a matching key, the module proceeds normally.
    /// </summary>
    [Fact]
    public void L0_PE_header_unreadable_blocks_arming_and_writes()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(20);
        var bk = new TreasureBuildKey { TimeDateStamp = 0xAB12, SizeOfImage = 0xCD34 };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: bk);

        var mem = BuildMem(74, terrain, new[] { addr });
        // PE header NOT seeded -- Readable returns false for all PE offsets.

        var tm = Make(db, mem);
        // Many ticks: header stays unreadable -- must never write, never arm.
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        Assert.Empty(mem.Written);
        Assert.False(mem.ReadCount.TryGetValue(addr, out _));
    }

    [Fact]
    public void L0_PE_header_becomes_readable_matching_key_module_then_arms()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(21);
        var bk = new TreasureBuildKey { TimeDateStamp = 0xAB12, SizeOfImage = 0xCD34 };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: bk);

        var mem = BuildMem(74, terrain, new[] { addr });
        // PE header absent initially.
        var tm = Make(db, mem);
        TickN(tm, 5);
        Assert.Empty(mem.Written);

        // Now seed the matching key and run enough ticks to stabilize + arm.
        SeedPeHeader(mem, timeDateStamp: 0xAB12, sizeOfImage: 0xCD34);
        TickN(tm, Tuning.TreasureArmStableTicks + 10);

        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── (14) Foreign bytes while ARMED: stay armed, skip foreign, resume on return ──

    /// <summary>
    /// After arming cleanly, if tile addresses return Foreign bytes (camera pan, action camera),
    /// the module must stay ARMED, skip those foreign addrs on that tick, and continue
    /// writing the non-foreign addrs.  No disarm on any number of foreign bytes.
    /// </summary>
    [Fact]
    public void Armed_foreign_bytes_stays_armed_and_skips_foreign_addrs()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(30), TileAddr(31), TileAddr(32) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Mutate 2 of 3 addrs to Foreign (camera panned away), reset addrs[2] to Resting
        mem.U8s[addrs[0]] = 0x42;
        mem.U8s[addrs[1]] = 0x42;
        mem.U8s[addrs[2]] = 0x00;
        mem.Written.Clear();

        // One tick: foreign addrs skipped, non-foreign Resting addr IS written -- still ARMED
        tm.Tick(DateTime.Now, inLive: true);

        Assert.False(mem.Written.ContainsKey(addrs[0]));
        Assert.False(mem.Written.ContainsKey(addrs[1]));
        Assert.True(mem.Written.ContainsKey(addrs[2]));

        // Subsequent ticks: stays armed, continues writing addrs[2]
        mem.U8s[addrs[2]] = 0x00;
        mem.Written.Clear();
        TickN(tm, 3);
        Assert.True(mem.Written.ContainsKey(addrs[2]));
    }

    /// <summary>
    /// Camera-pan round-trip: bytes go Foreign (0x42) while off-screen -> skipped, stays ARMED;
    /// then return to Resting (0x00) when camera pans back -> written again.
    /// Proves no permanent state is set by the off-screen interval.
    /// </summary>
    [Fact]
    public void Armed_camera_pan_roundtrip_bytes_return_resting_are_written_again()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(33), TileAddr(34), TileAddr(35) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Phase 1: all addrs go Foreign (off-screen) -- module stays armed, zero writes
        foreach (var a in addrs) mem.U8s[a] = 0x42;
        mem.Written.Clear();
        TickN(tm, 5);
        Assert.Empty(mem.Written);  // all foreign, all skipped; module still ARMED

        // Phase 2: camera pans back, bytes return to Resting (engine cleared the mark too)
        foreach (var a in addrs) mem.U8s[a] = 0x00;
        mem.Written.Clear();
        tm.Tick(DateTime.Now, inLive: true);

        // All addrs now Resting -> all written again
        foreach (var a in addrs)
            Assert.True(mem.Written.ContainsKey(a), $"addr {a:x} should be written after camera pan back");
    }

    // ── (15) Non-field-0 terrain mutation: hash unchanged -> stays armed ────────────
    // This is the structural fix for LIVE INCIDENT #2.
    // v2 MaskedTerrainHash ignores fields 1-6; mutating only those bytes must not
    // trigger any disarm or re-arm cycle -- the module stays ARMED with no gap in writes.

    /// <summary>
    /// Non-field-0 bytes mutate (field 1 and field 6, the incident pattern) while
    /// field-0 bytes hold still.  The v2 masked hash is unchanged -> module stays
    /// ARMED, writes continue without interruption.
    /// </summary>
    [Fact]
    public void Armed_non_field0_terrain_mutation_hash_unchanged_stays_armed()
    {
        var dir = TempDir();
        // 7-byte record: field 0 = 0x05; fields 1-6 = some initial values
        var terrain = new byte[]
        {
            0x05, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x00,  // record 0
        };
        var addr = TileAddr(50);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Mutate field 1 and field 6 (the incident pattern) -- field 0 unchanged
        terrain[1] = 0x11;  // field 1 changed
        terrain[6] = 0xFF;  // field 6 changed
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;  // engine cleared mark

        // Advance past revalidate period: hash must still match -> stays ARMED, writes resume
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

        // Still writing -- module did NOT disarm or re-arm
        Assert.True(mem.Written.ContainsKey(addr),
            "non-field-0 mutation should not change masked hash; module should stay armed");
    }

    /// <summary>
    /// v2 map: the hashed field (field-0) drifts mid-battle.  Under the informational-only
    /// mid-battle check the module KEEPS HOLDING -- it does not suspend or disarm, even past
    /// the arm attempt cap (the old behavior re-proved, suspended, and eventually disarmed).
    /// </summary>
    [Fact]
    public void Armed_v2_field0_drift_mid_battle_keeps_holding()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // record 0: field-0=0x05 (v2-hashed)
        };
        var addr = TileAddr(51);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Drift field-0 (the v2-hashed field) -> hash no longer matches.
        terrain[0] = 0x09;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;

        // Let the old revalidation path fully run (boundary + arm cap -> old: BattleDisarmed).
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + Tuning.TreasureArmAttemptCap + 10);

        // Clear and prove it is STILL holding.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "v2 field-0 drift must keep holding (informational check, no disarm)");
    }

    /// <summary>
    /// A mid-battle drift on the hashed field must not even briefly suspend holding: the
    /// ticks straight across the revalidate boundary still write.  The old behavior dropped
    /// to ARMING and published null while it "re-proved", which is what made the marks
    /// visibly vanish (LIVE INCIDENT #4).
    /// </summary>
    [Fact]
    public void Armed_drift_does_not_suspend_holding_across_revalidate_boundary()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        var addr = TileAddr(52);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 2);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Drift field-0, then tick just past the first revalidate boundary -- the exact
        // point where the old code dropped to ARMING and published null.
        terrain[0] = 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 3);

        // Clear what happened during the crossing; the NEXT ticks must still hold (old code
        // would be re-proving in ARMING here and write nothing).
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 3);

        Assert.True(mem.Written.ContainsKey(addr),
            "holding must not suspend across the mid-battle drift boundary");
    }

    // ── (17) v3 fingerprint: water-map regression ────────────────────────────────
    // LIVE INCIDENT #3 (Zeirchele Falls, map 83): fields {0,1,6} animate on water maps;
    // a v2 fingerprint (field-0 only) cycles with the animation and triggers spurious
    // disarm/re-arm.  A v3 fingerprint (fields {2,3,4,5}) is immune because those
    // fields are static geometry on all map types.

    /// <summary>Compute the v3 masked hash of terrain to seed fpHash for a v3 map.</summary>
    private static string TerrainFpHashV3(byte[] terrain)
        => TreasureMaster.MaskedTerrainHashV3(terrain).ToString("x");

    /// <summary>
    /// A v3 map whose field-0 (height) bytes mutate every tick (water animation) while
    /// fields {2,3,4,5} hold still MUST STAY ARMED -- this is the exact water-map regression.
    /// </summary>
    [Fact]
    public void V3_field0_animates_fields2345_static_stays_armed()
    {
        var dir = TempDir();
        // 7-byte record: field-0 will animate; fields 2-5 are static geometry.
        var terrain = new byte[]
        {
            0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x00,  // record 0: field-0=0x10 (will change)
        };
        var addr = TileAddr(70);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Animate field-0 repeatedly (water height cycle) -- fields 2-5 are untouched
        for (int cycle = 0; cycle < 5; cycle++)
        {
            terrain[0] = (byte)(0x10 + cycle);  // field-0 changes every frame
            mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
            mem.U8s[addr] = 0x00;   // engine cleared mark
            mem.Written.Clear();

            // Advance past a revalidate period -- v3 hash must still match
            TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

            Assert.True(mem.Written.ContainsKey(addr),
                $"cycle {cycle}: v3 should stay armed when only field-0 animates");
        }
    }

    /// <summary>
    /// A v3 map whose field-6 (flow) bytes animate stays armed (same root cause as field-0).
    /// </summary>
    [Fact]
    public void V3_field6_animates_fields2345_static_stays_armed()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x01,  // record 0: field-6=0x01 (will animate)
        };
        var addr = TileAddr(71);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Animate field-6 (flow) -- stays armed
        terrain[6] = 0xFF;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        mem.U8s[addr] = 0x00;
        mem.Written.Clear();
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "v3 should stay armed when only field-6 (flow) animates");
    }

    /// <summary>
    /// A v3 map whose field-3 (a hashed "static geometry" field) drifts mid-battle KEEPS
    /// HOLDING.  LIVE INCIDENT #4 proved these fields are not always battle-invariant
    /// (Siedge Weald, map 74: fields {2,3,4,5} changed ~26 s into the same battle); a
    /// mid-battle drift is no longer treated as a different map.  The arm-time fingerprint
    /// gate still proves identity before the first hold; only the mid-battle re-check is
    /// downgraded to informational.
    /// </summary>
    [Fact]
    public void V3_static_field_drift_mid_battle_keeps_holding()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x00,  // record 0: field-3=0x06 (v3-hashed)
        };
        var addr = TileAddr(72);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Drift field-3 (a v3-hashed field) -- the exact live-incident pattern.
        terrain[3] = 0xAA;
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;

        // Let the old revalidation path fully run (boundary + arm cap -> old: BattleDisarmed).
        TickN(tm, Tuning.TreasureRevalidateEveryNTicks + Tuning.TreasureArmAttemptCap + 10);

        // Clear and prove it is STILL holding.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;
        TickN(tm, 5);

        Assert.True(mem.Written.ContainsKey(addr),
            "v3 static-field drift mid-battle must keep holding (LIVE INCIDENT #4)");
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    /// <summary>
    /// FingerprintMatches dispatches on fpVer: a v2 map uses v2 hash (MaskedTerrainHash),
    /// a v3 map uses v3 hash (MaskedTerrainHashV3).  A v3 map with a v2-computed fpHash
    /// does NOT match, and vice versa (the two hashes are different for the same raw bytes).
    /// </summary>
    [Fact]
    public void FingerprintMatches_dispatch_v2_vs_v3()
    {
        var dir = TempDir();
        // Same terrain; v2 and v3 hashes will differ for this buffer
        var terrain = new byte[]
        {
            0x10, 0xAA, 0x05, 0x06, 0x07, 0x08, 0xBB,
        };

        var addrV2 = TileAddr(73);
        var addrV3 = TileAddr(74);

        // v2 map: fpVer=2, fpHash from v2 formula
        var dbV2 = BuildDb(dir, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHashV2(terrain),
            addrs: new[] { (addrV2, (byte)0x00) }, fpVer: 2);

        var memV2 = BuildMem(74, terrain, new[] { addrV2 });
        var tmV2 = Make(dbV2, memV2);
        StabilizeAndArm(tmV2);
        Assert.True(memV2.Written.ContainsKey(addrV2),
            "v2 map with v2 hash should arm");

        var dir3 = TempDir();
        // v3 map: fpVer=3, fpHash from v3 formula
        var dbV3 = BuildDb(dir3, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHashV3(terrain),
            addrs: new[] { (addrV3, (byte)0x00) }, fpVer: 3);

        var memV3 = BuildMem(74, terrain, new[] { addrV3 });
        var tmV3 = Make(dbV3, memV3);
        StabilizeAndArm(tmV3);
        Assert.True(memV3.Written.ContainsKey(addrV3),
            "v3 map with v3 hash should arm");
    }

    /// <summary>
    /// A v3 map with a v2-computed fpHash (wrong version's hash) ARMS ANYWAY -- the
    /// fingerprint is advisory, so a hash mismatch (wrong version or weather drift) no longer
    /// blocks arming; the per-tile quorum is the gate.
    /// </summary>
    [Fact]
    public void FingerprintMatches_v3_map_with_v2_hash_arms_anyway()
    {
        var dir = TempDir();
        var terrain = new byte[]
        {
            0x10, 0xAA, 0x05, 0x06, 0x07, 0x08, 0xBB,
        };
        var addr = TileAddr(75);
        // fpVer=3 but fpHash is computed with v2 formula -> mismatch (advisory only now).
        var db = BuildDb(dir, fpLen: terrain.Length,
            fpHash: TerrainFpHashV2(terrain),   // wrong hash for v3
            addrs: new[] { (addr, (byte)0x00) }, fpVer: 3);
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── PinnedBuf fact: MarkValue lands at target, neighbors untouched ────────────

    /// <summary>
    /// Drive a 6-byte pinned buffer through LiveMemory (RPM/WPM on our own process),
    /// asserting the mark value lands at offset 0 and bytes 1-5 are untouched.
    /// Mirrors MemBitsTests and BarrageTests pattern.
    /// </summary>
    [Fact]
    public void PinnedBuf_hold_mark_value_lands_and_neighbors_untouched()
    {
        using var pin = PinnedBuf.Of(6);
        pin.Bytes[0] = 0x00;   // target -- should become MarkValue (0xCC)
        pin.Bytes[1] = 0x42;   // neighbor
        pin.Bytes[2] = 0x11;
        pin.Bytes[3] = 0xFE;
        pin.Bytes[4] = 0x00;
        pin.Bytes[5] = 0x55;

        var live = new LiveMemory();
        // Guard is satisfied: our own process memory is always Writable + Readable
        Assert.True(live.Writable(pin.Addr, 1));
        int cur = live.U8(pin.Addr);
        byte want = TreasureMaster.WantWrite((byte)cur);
        live.W8(pin.Addr, want);

        Assert.Equal(TreasureMaster.MarkValue, pin.Bytes[0]);
        Assert.Equal(0x42, pin.Bytes[1]);
        Assert.Equal(0x11, pin.Bytes[2]);
        Assert.Equal(0xFE, pin.Bytes[3]);
        Assert.Equal(0x00, pin.Bytes[4]);
        Assert.Equal(0x55, pin.Bytes[5]);
    }

    // ── (16) Hot-reload seam tests ────────────────────────────────────────────

    /// <summary>Stamp unchanged across TreasureStampCheckTicks ticks -> load never re-invoked.</summary>
    [Fact]
    public void Reload_stamp_unchanged_load_not_reinvoked()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(60);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        int loadCount = 0;
        DateTime? stamp = DateTime.UtcNow;
        var tm = new TreasureMaster(
            load: () => { loadCount++; return db; },
            datasetStamp: () => stamp,
            mem: mem,
            enabled: true);

        // Run well past the check interval; stamp never changes.
        TickN(tm, Tuning.TreasureStampCheckTicks * 3);

        // Initial load counts as 1 (eager at first tick).
        Assert.Equal(1, loadCount);
    }

    /// <summary>Stamp changes -> load re-invoked, state cleared, new map arms.</summary>
    [Fact]
    public void Reload_stamp_change_triggers_reload_and_state_cleared()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(61);
        var db = BuildDb(dir, mapId: 74, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        int loadCount = 0;
        DateTime? stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tm = new TreasureMaster(
            load: () => { loadCount++; return db; },
            datasetStamp: () => stamp,
            mem: mem,
            enabled: true);

        // Stabilize and arm on the initial dataset.
        StabilizeAndArm(tm);
        Assert.Equal(1, loadCount);
        Assert.NotEmpty(mem.Written);

        // Advance stamp -> triggers reload.
        stamp = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        // Clear written log so we can observe the re-arm writes separately.
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;

        // Run enough ticks to cross the check interval AND re-stabilize + arm.
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);

        Assert.Equal(2, loadCount);
        // After reload + re-arm, writes resume.
        Assert.NotEmpty(mem.Written);
    }

    /// <summary>
    /// Empty dataset at boot (load returns empty db), then stamp changes to a populated
    /// dataset -> module un-idles and arms on the same map id.
    /// </summary>
    [Fact]
    public void Reload_empty_at_boot_then_populated_dataset_un_idles_and_arms()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(62);

        // Two distinct db instances: empty, then populated.
        var emptyDb = TreasureDb.MakeEmpty();
        var populatedDb = BuildDb(dir, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHash(terrain), addrs: new[] { (addr, (byte)0x00) });

        var mem = BuildMem(74, terrain, new[] { addr });

        int loadCount = 0;
        DateTime? stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bool returnPopulated = false;

        var tm = new TreasureMaster(
            load: () => { loadCount++; return returnPopulated ? populatedDb : emptyDb; },
            datasetStamp: () => stamp,
            mem: mem,
            enabled: true);

        // Many ticks with empty dataset: module stays idle (globally), no writes.
        TickN(tm, Tuning.TreasureStampCheckTicks + 10);
        Assert.Empty(mem.Written);

        // Now switch to populated and bump stamp.
        returnPopulated = true;
        stamp = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);

        // Run enough ticks to detect stamp change, reload, un-idle, stabilize + arm.
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);

        Assert.True(loadCount >= 2, $"Expected at least 2 loads, got {loadCount}");
        Assert.NotEmpty(mem.Written);
    }

    /// <summary>
    /// Reload with a mismatched build key -> global disarm re-evaluated (module stays/re-idles).
    /// </summary>
    [Fact]
    public void Reload_mismatched_build_key_re_idles_module()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(63);

        // Dataset whose build key does NOT match the PE header in mem.
        var mismatchDb = BuildDb(dir, mapId: 74, fpLen: terrain.Length,
            fpHash: TerrainFpHash(terrain), addrs: new[] { (addr, (byte)0x00) },
            buildKey: new TreasureBuildKey { TimeDateStamp = 0x1111, SizeOfImage = 0x2222 });

        var mem = BuildMem(74, terrain, new[] { addr });
        // Seed PE header with DIFFERENT values -> mismatch.
        SeedPeHeader(mem, timeDateStamp: 0xAAAA, sizeOfImage: 0xBBBB);

        int loadCount = 0;
        DateTime? stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var tm = new TreasureMaster(
            load: () => { loadCount++; return mismatchDb; },
            datasetStamp: () => stamp,
            mem: mem,
            enabled: true);

        // Run enough to detect stamp changes and reload multiple times; key always mismatches.
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);
        stamp = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        TickN(tm, Tuning.TreasureStampCheckTicks + Tuning.TreasureArmStableTicks + 10);

        // Key always mismatches -> zero flag-address writes ever.
        Assert.Empty(mem.Written);
        Assert.False(mem.ReadCount.TryGetValue(addr, out _),
            "flag address should never be read when build key mismatches");
    }

    // ── map-id-only mode (water/lava maps) ────────────────────────────────────
    // A map whose stored fpHash is null and fpVer is 0 is a map-id-only map.
    // The terrain fingerprint gate is entirely absent: it arms on map-id + address
    // quorum alone, and never disarms on terrain change.
    //
    // Invariant matrix:
    //   (MIO-1) Arms without fingerprint match, even when the live terrain hash is garbage.
    //   (MIO-2) Does NOT disarm on terrain change mid-battle (the water regression).
    //   (MIO-3) A map-id flip still disarms a map-id-only map (live wrong-map guard stays).
    //   (MIO-4) A fingerprinted map ARMS ANYWAY on a fingerprint mismatch (advisory only).
    //   (MIO-5) Bake: a map-id-only map (verified tiles, null fpHash) ships alongside a
    //            fingerprinted map; both appear in the output.

    /// <summary>Build a map-id-only db (fpVer=0, fpHash=null, no fpLen).</summary>
    private static TreasureDb BuildMapIdOnlyDb(
        string dir, int mapId = 83, string name = "Zeirchele Falls",
        int tileX = 2, int tileY = 3,
        IEnumerable<(long addr, byte off)>? addrs = null)
    {
        var addrList = addrs?.ToList() ?? new List<(long, byte)>
            { (TileAddr(100), 0x00), (TileAddr(101), 0x00), (TileAddr(102), 0x00) };
        string tilesJson = $@"[{{
            ""x"": {tileX}, ""y"": {tileY},
            ""addrs"": [{string.Join(", ", addrList.Select(a => AddrJson(a.addr, a.off)))}]
        }}]";
        string json = $@"{{
            ""buildKey"": null,
            ""maps"": [{{
                ""mapId"": {mapId}, ""name"": ""{name}"", ""tileCount"": 1,
                ""fpVer"": 0,
                ""tiles"": {tilesJson}
            }}]
        }}";
        File.WriteAllText(Path.Combine(dir, "treasure.json"), json);
        return TreasureDb.Load(dir);
    }

    /// <summary>
    /// (MIO-1) A map-id-only map arms WITHOUT a fingerprint match, even when the live
    /// terrain is garbage/changing (simulating Zeirchele Falls water animation).
    /// The module must reach ARMED and write its tile addresses.
    /// </summary>
    [Fact]
    public void MapIdOnly_arms_without_fingerprint_match_even_with_garbage_terrain()
    {
        var dir  = TempDir();
        var addrs = new[] { TileAddr(100), TileAddr(101), TileAddr(102) };
        var db = BuildMapIdOnlyDb(dir,
            addrs: addrs.Select(a => (a, (byte)0x00)));

        // Terrain is random garbage -- no valid fingerprint can ever match.
        var garbageTerrain = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56 };
        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 83;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.TerrainBlocks[Offsets.TerrainGrid] = garbageTerrain;
        foreach (var a in addrs)
        {
            mem.U8s[a] = 0x00;
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Must have written to all three addresses despite garbage terrain.
        foreach (var a in addrs)
        {
            Assert.True(mem.Written.ContainsKey(a),
                $"map-id-only: addr {a:x} should be written even with garbage terrain");
            Assert.Equal(TreasureMaster.MarkValue, mem.Written[a]);
        }
    }

    /// <summary>
    /// (MIO-2) A map-id-only map that is already ARMED must NOT disarm on terrain change
    /// mid-battle.  This is the exact water regression: fields {0,1,6} animate every frame,
    /// but the map-id-only path has no terrain revalidation loop at all.
    /// </summary>
    [Fact]
    public void MapIdOnly_does_not_disarm_on_terrain_change_mid_battle()
    {
        var dir  = TempDir();
        var addrs = new[] { TileAddr(103), TileAddr(104), TileAddr(105) };
        var db = BuildMapIdOnlyDb(dir, mapId: 83, tileX: 2, tileY: 3,
            addrs: addrs.Select(a => (a, (byte)0x00)));

        var terrain = new byte[] { 0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x01 };
        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 83;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        foreach (var a in addrs)
        {
            mem.U8s[a] = 0x00;
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Continuously mutate the terrain (water animation cycling all fields).
        for (int cycle = 0; cycle < 5; cycle++)
        {
            terrain[0] = (byte)(0x10 + cycle);
            terrain[1] = (byte)(0xAA + cycle);
            terrain[6] = (byte)(0xFF - cycle);
            mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
            // Engine clears the mark between cycles.
            foreach (var a in addrs) mem.U8s[a] = 0x00;
            mem.Written.Clear();

            TickN(tm, Tuning.TreasureRevalidateEveryNTicks + 5);

            foreach (var a in addrs)
                Assert.True(mem.Written.ContainsKey(a),
                    $"cycle {cycle}: map-id-only map should stay armed despite terrain animation");
        }
    }

    /// <summary>
    /// (MIO-3) Even though there is no terrain gate, the live wrong-map guard (map-id
    /// check every tick) still disarms a map-id-only map when the map id changes.
    /// </summary>
    [Fact]
    public void MapIdOnly_map_id_flip_still_disarms()
    {
        var dir  = TempDir();
        var addrs = new[] { TileAddr(106), TileAddr(107), TileAddr(108) };
        var db = BuildMapIdOnlyDb(dir, mapId: 83,
            addrs: addrs.Select(a => (a, (byte)0x00)));

        var terrain = new byte[] { 0x10, 0x00, 0x05, 0x06, 0x07, 0x08, 0x01 };
        var mem = new FakeSparseMemory();
        mem.U8s[Offsets.LiveBattleMapId] = 83;
        mem.ReadableAddrs.Add(Offsets.LiveBattleMapId);
        mem.TerrainBlocks[Offsets.TerrainGrid] = terrain;
        foreach (var a in addrs)
        {
            mem.U8s[a] = 0x00;
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tm = Make(db, mem);
        StabilizeAndArm(tm);
        mem.Written.Clear();

        // Flip to an unknown map id.
        mem.U8s[Offsets.LiveBattleMapId] = 77;

        // Run through the bad-tick threshold -- must stop writing.
        TickN(tm, Tuning.TreasureMapIdBadTicksToReset + 2);

        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// (MIO-4) A fingerprinted map (fpHash not null) ARMS ANYWAY on a fingerprint mismatch,
    /// exactly like a map-id-only map: the fingerprint is advisory, so the only gate is the
    /// per-tile resting quorum. A fingerprinted map and a map-id-only map now behave the same
    /// at arm time when their terrain doesn't match the captured hash (weather drift).
    /// </summary>
    [Fact]
    public void Fingerprinted_map_arms_despite_fingerprint_mismatch_like_mapidonly()
    {
        var dir  = TempDir();
        var realTerrain  = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
        var wrongTerrain = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00 };
        var addr = TileAddr(109);
        // Explicitly a fingerprinted (non-map-id-only) map.
        var db = BuildDb(dir, mapId: 74, fpLen: realTerrain.Length,
            fpHash: TerrainFpHash(realTerrain),
            addrs: new[] { (addr, (byte)0x00) });

        // Memory has the wrong terrain -- fingerprint mismatch (e.g. it's raining).
        var mem = BuildMem(74, wrongTerrain, new[] { addr });

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── FastHold tests ────────────────────────────────────────────────────────────
    // No real threads are spawned (Start/StartFastHold never called).
    // All tests drive HoldOnce() directly -- the thread-safe property is argued by
    // construction: TileHolder is stateless and set-only (writes the same MarkValue), so
    // concurrent callers are idempotent and safe.

    /// <summary>
    /// FastHold.HoldOnce with a published map writes MarkValue to the tile addresses via
    /// the underlying TileHolder (same fake-memory path as the normal tick).
    /// </summary>
    [Fact]
    public void FastHold_HoldOnce_with_published_map_writes_tile_addresses()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(200), TileAddr(201) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var holder = new TileHolder(mem);
        var fh = new FastHold(holder, intervalMs: 8);

        // Null published: HoldOnce writes nothing.
        fh.HoldOnce();
        Assert.Empty(mem.Written);

        // Publish a map: HoldOnce writes MarkValue to each resting addr.
        var map = db.Maps[0];
        fh.Publish(map);
        fh.HoldOnce();

        Assert.True(mem.Written.ContainsKey(addrs[0]));
        Assert.True(mem.Written.ContainsKey(addrs[1]));
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrs[0]]);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrs[1]]);
    }

    /// <summary>
    /// FastHold.HoldOnce with null published writes nothing even after a map was
    /// previously published (Publish(null) clears the held reference).
    /// </summary>
    [Fact]
    public void FastHold_HoldOnce_with_null_published_writes_nothing()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(202), TileAddr(203) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var holder = new TileHolder(mem);
        var fh = new FastHold(holder, intervalMs: 8);

        // Publish then clear.
        fh.Publish(db.Maps[0]);
        fh.Publish(null);

        fh.HoldOnce();
        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// Once ARMED, FastHold.HoldOnce writes the tile addresses (the map was published
    /// by TreasureMaster's Tick path on transition to Phase.Armed).
    /// </summary>
    [Fact]
    public void FastHold_armed_state_HoldOnce_writes_tile_addresses()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(210), TileAddr(211) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        // Clear written log so only HoldOnce writes are counted.
        mem.Written.Clear();
        // Reset addrs so they look Resting again (engine cleared the mark).
        foreach (var a in addrs) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();

        Assert.True(mem.Written.ContainsKey(addrs[0]),
            "FastHold.HoldOnce should write addr[0] when phase is Armed");
        Assert.True(mem.Written.ContainsKey(addrs[1]),
            "FastHold.HoldOnce should write addr[1] when phase is Armed");
    }

    /// <summary>
    /// After ResetBattle(), FastHold.HoldOnce writes nothing (null was published
    /// by ResetBattle on the battle-exit edge).
    /// </summary>
    [Fact]
    public void FastHold_after_ResetBattle_HoldOnce_writes_nothing()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(212), TileAddr(213) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        StabilizeAndArm(tm);

        tm.ResetBattle();
        mem.Written.Clear();
        foreach (var a in addrs) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();

        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// After a !inLive tick, FastHold.HoldOnce writes nothing (null was published
    /// by the !inLive early return in Tick).
    /// </summary>
    [Fact]
    public void FastHold_after_inLive_false_tick_HoldOnce_writes_nothing()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addrs = new[] { TileAddr(214), TileAddr(215) };
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: addrs.Select(a => (a, (byte)0x00)));
        var mem = BuildMem(74, terrain, addrs, initialByte: 0x00);

        var tm = Make(db, mem);
        // Arm the module in live battle.
        StabilizeAndArm(tm);

        // Now tick with inLive=false.
        tm.Tick(DateTime.Now, inLive: false);
        mem.Written.Clear();
        foreach (var a in addrs) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();

        Assert.Empty(mem.Written);
    }

    // ── (18) battleDisplayed gate: formation / enemy-turn coverage ────────────────
    // The Treasure Master module gates on a single bool from the Engine. Prior to this
    // fix the Engine passed InLiveBattle, which flickered false during enemy turns and
    // animations (battleMode==1 without slot0==0xFF). This kept resetting _stableTicks
    // and prevented arming mid-battle. The Engine now passes BattleDisplayed instead:
    // slot9==0xFFFFFFFF && battleMode!=0. Formation and enemy turns both satisfy that,
    // so the module receives a stable true throughout the battle.
    //
    // The module's Tick(DateTime, bool) interface is unchanged; these tests drive it
    // directly with the semantically correct gate value.

    /// <summary>
    /// Continuous gate=true (formation or any battle mode) arms in exactly
    /// TreasureArmStableTicks ticks -- no flicker resets the counter.
    /// </summary>
    [Fact]
    public void BattleDisplayed_continuous_true_arms_in_stable_ticks()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(220);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // Exactly TreasureArmStableTicks - 1 ticks: not yet armed.
        TickN(tm, Tuning.TreasureArmStableTicks - 1, inLive: true);
        Assert.Empty(mem.Written);

        // One more tick tips past the threshold -- now armed + write.
        TickN(tm, 6, inLive: true);
        Assert.NotEmpty(mem.Written);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    /// <summary>
    /// Gate=false (world map) resets stability and publishes null -- no writes and
    /// FastHold stops holding.
    /// </summary>
    [Fact]
    public void BattleDisplayed_false_resets_stability_and_publishes_null()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(221);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // Arm the module.
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // Transition to world map (battleDisplayed=false).
        tm.Tick(DateTime.Now, inLive: false);

        // Clear and verify: no further writes; HoldOnce produces nothing (null published).
        mem.Written.Clear();
        foreach (var a in new[] { addr }) mem.U8s[a] = 0x00;

        tm.FastHold.HoldOnce();
        Assert.Empty(mem.Written);
    }

    /// <summary>
    /// After gate goes false (world map) then true again (new battle / formation),
    /// the module re-arms from scratch -- the stability counter was reset.
    /// </summary>
    [Fact]
    public void BattleDisplayed_false_then_true_rearms_from_scratch()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(222);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // First battle: arm.
        StabilizeAndArm(tm);
        Assert.NotEmpty(mem.Written);

        // World map: gate=false resets state.
        tm.Tick(DateTime.Now, inLive: false);
        tm.ResetBattle();
        mem.Written.Clear();
        mem.U8s[addr] = 0x00;

        // Not yet armed on the very next true tick (stability counter was zeroed).
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Empty(mem.Written);

        // Full stability window passes -> arms and writes again.
        TickN(tm, Tuning.TreasureArmStableTicks + 5, inLive: true);
        Assert.NotEmpty(mem.Written);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    /// <summary>
    /// Simulates the pre-fix flicker: the gate alternates false/true repeatedly
    /// (battleMode==1 without excuse each odd tick, like an enemy turn without slot0==0xFF).
    /// With the old InLiveBattle gate this reset _stableTicks on every odd tick.
    /// With the new battleDisplayed gate (always true while the map is displayed),
    /// the module receives continuous true and arms normally.
    /// This test drives Tick with continuous true -- asserting it arms in <= stableTicks + overhead.
    /// </summary>
    [Fact]
    public void BattleDisplayed_stable_gate_arms_despite_what_inLive_would_have_been()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var addr = TileAddr(223);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });

        var tm = Make(db, mem);

        // Drive with continuous gate=true (what battleDisplayed produces during a battle).
        // With inLive this would alternately be false on enemy turns, resetting _stableTicks.
        // With battleDisplayed the counter accumulates uninterrupted.
        TickN(tm, Tuning.TreasureArmStableTicks + 10, inLive: true);

        Assert.NotEmpty(mem.Written);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addr]);
    }

    // ── Claim detection integration tests ────────────────────────────────────────

    /// <summary>Grid-X address of claim-scan unit slot <paramref name="slot"/>.</summary>
    private static long ClaimSlotX(int slot)
        => Offsets.UnitArrayBaseX + (long)slot * Offsets.UnitRecordStride;

    /// <summary>Seeds one battle-unit slot's grid (x,y) in the occupancy-scan array.</summary>
    private static void SeedClaimUnit(FakeSparseMemory mem, int x, int y, int slot = 0)
    {
        long xAddr = ClaimSlotX(slot);
        mem.U8s[xAddr]     = (byte)x;
        mem.U8s[xAddr + 1] = (byte)y;
        mem.ReadableAddrs.Add(xAddr);
    }

    /// <summary>Moves a previously-seeded unit to a new tile (updates grid x,y).</summary>
    private static void MoveClaimUnit(FakeSparseMemory mem, int x, int y, int slot = 0)
    {
        long xAddr = ClaimSlotX(slot);
        mem.U8s[xAddr]     = (byte)x;
        mem.U8s[xAddr + 1] = (byte)y;
    }

    /// <summary>Seeds an item's inventory count at InventoryCountBase + itemId.</summary>
    private static void SeedItemCount(FakeSparseMemory mem, int itemId, byte count)
    {
        long addr = Offsets.InventoryCountBase + itemId;
        mem.U8s[addr] = count;
        mem.ReadableAddrs.Add(addr);
    }

    /// <summary>Increments an item's inventory count by one (simulates a claim adding the item).</summary>
    private static void BumpItemCount(FakeSparseMemory mem, int itemId)
    {
        long addr = Offsets.InventoryCountBase + itemId;
        mem.U8s[addr] = (byte)(mem.U8s.GetValueOrDefault(addr) + 1);
    }

    /// <summary>
    /// LOAD-BEARING: Two tiles C (claimed) and S (sibling, unclaimed), each with a rare item id.
    /// A unit stands on C; C's item count ticks up (the claim). After the detect tick:
    ///   - addrC byte is reverted (mem.U8s[addrC] != MarkValue) AND a write is recorded.
    ///   - After FastHold.HoldOnce + 5 more ticks, addrC stays unlit; addrS stays held.
    /// Non-vacuous: byte-state assertion (not just Written log) + HoldOnce exercised.
    /// Red-trigger: publishing _map instead of _activeMap would re-stamp addrC -> MarkValue.
    /// </summary>
    [Fact]
    public void ClaimDetection_claimed_tile_unlit_and_stays_unlit_through_FastHold()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

        long addrC = TileAddr(300);
        long addrS = TileAddr(301);
        byte offC  = 0x00;
        byte offS  = 0x00;
        int tileX  = 5, tileY  = 7;   // C tile coords ; rare item 200
        int sibX   = 6, sibY   = 8;   // S tile coords ; rare item 201

        string json = $@"{{
            ""buildKey"": null,
            ""maps"": [{{
                ""mapId"": 74, ""name"": ""Test"", ""tileCount"": 2,
                ""fpVer"": 1,
                ""fpLen"": {terrain.Length},
                ""fpHash"": ""{TreasureMaster.Fnv1a64(terrain):x}"",
                ""tiles"": [
                    {{ ""x"": {tileX}, ""y"": {tileY}, ""rareItemId"": 200, ""addrs"": [[""{addrC:x}"", ""{offC:x2}""]] }},
                    {{ ""x"": {sibX},  ""y"": {sibY},  ""rareItemId"": 201, ""addrs"": [[""{addrS:x}"", ""{offS:x2}""]] }}
                ]
            }}]
        }}";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "treasure.json"), json);
        var db = TreasureDb.Load(dir);

        var mem = BuildMem(74, terrain, new[] { addrC, addrS }, initialByte: 0x00);

        // A unit stands on C the whole time; counts are stable through arm (so no claim fires while
        // stabilizing -- the baseline is captured at arm). The claim is the count tick below.
        SeedClaimUnit(mem, x: tileX, y: tileY);
        SeedItemCount(mem, 200, 1);   // C's rare item
        SeedItemCount(mem, 201, 1);   // S's rare item

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);

        StabilizeAndArm(tm);
        Assert.True(mem.Written.ContainsKey(addrC), "C should be marked after arm");
        Assert.True(mem.Written.ContainsKey(addrS), "S should be marked after arm");

        // Claim C: its rare item count ticks up while the unit stands on C.
        mem.U8s[addrC] = TreasureMaster.MarkValue;   // Held so Unlight can revert it
        BumpItemCount(mem, 200);
        mem.Written.Clear();
        tm.Tick(DateTime.Now, inLive: true);

        // Assert: C reverted (byte state AND write log)
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);
        Assert.True(mem.Written.ContainsKey(addrC), "Unlight must record a write for addrC");
        Assert.Equal(offC, mem.Written[addrC]);

        // Now clear write log, HoldOnce, then 5 more armed ticks -- C must NOT come back
        mem.Written.Clear();
        tm.FastHold.HoldOnce();
        TickN(tm, 5);

        Assert.False(mem.Written.ContainsKey(addrC),
            "FastHold must not re-stamp claimed addrC");
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);
        // Sibling S is still held (no unit on it, its count never rose)
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrS]);
    }

    /// <summary>
    /// Idempotency / stale-FastHold-pass guard: the un-light runs EVERY armed tick over the
    /// latched claimed set, not only the tick a claim is first detected. Simulates the ~8ms race
    /// the filtered publish alone cannot close -- a stale FastHold pass re-stamps a claimed tile
    /// after the unit has already moved off -- and proves a later armed tick re-reverts it.
    /// Red-trigger: gating the Unlight loop behind "_claimed grew" (one-shot) leaves the
    /// re-stamped byte at MarkValue, since this tick adds no new claim.
    /// </summary>
    [Fact]
    public void ClaimDetection_unlight_reasserted_every_tick_after_stale_restamp()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(330);
        byte offC  = 0x00;
        int tileX = 5, tileY = 7;

        string json = $@"{{
            ""buildKey"": null,
            ""maps"": [{{
                ""mapId"": 74, ""name"": ""Test"", ""tileCount"": 1,
                ""fpVer"": 1,
                ""fpLen"": {terrain.Length},
                ""fpHash"": ""{TreasureMaster.Fnv1a64(terrain):x}"",
                ""tiles"": [
                    {{ ""x"": {tileX}, ""y"": {tileY}, ""rareItemId"": 200, ""addrs"": [[""{addrC:x}"", ""{offC:x2}""]] }}
                ]
            }}]
        }}";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "treasure.json"), json);
        var db = TreasureDb.Load(dir);

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);   // unit on C
        SeedItemCount(mem, 200, 1);

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim C: its item count ticks up while the unit stands on C.
        mem.U8s[addrC] = TreasureMaster.MarkValue;   // Held so Unlight can revert it
        BumpItemCount(mem, 200);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);   // claimed + unlit

        // Move the unit OFF C (latch must hold, no new claim this tick) and simulate a stale
        // FastHold pass re-stamping C to MarkValue.
        MoveClaimUnit(mem, 1, 1);
        mem.U8s[addrC] = TreasureMaster.MarkValue;   // stale re-stamp
        mem.Written.Clear();

        // One more armed tick: the every-tick un-light must re-revert the latched claimed tile,
        // even though _claimed did not grow this tick.
        tm.Tick(DateTime.Now, inLive: true);

        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);
        Assert.True(mem.Written.ContainsKey(addrC),
            "every-tick un-light must re-revert a stale re-stamp of a latched claimed tile");
        Assert.Equal(offC, mem.Written[addrC]);
    }

    /// <summary>
    /// After ResetBattle, _claimed is cleared so the fresh battle re-lights the previously
    /// claimed tile (C is armed and marked again after reset + re-arm).
    /// </summary>
    [Fact]
    public void ClaimDetection_ResetBattle_clears_claimed_fresh_battle_relights()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(310);
        byte offC  = 0x00;
        int tileX = 5, tileY = 7;

        string json = $@"{{
            ""buildKey"": null,
            ""maps"": [{{
                ""mapId"": 74, ""name"": ""Test"", ""tileCount"": 1,
                ""fpVer"": 1,
                ""fpLen"": {terrain.Length},
                ""fpHash"": ""{TreasureMaster.Fnv1a64(terrain):x}"",
                ""tiles"": [
                    {{ ""x"": {tileX}, ""y"": {tileY}, ""rareItemId"": 200, ""addrs"": [[""{addrC:x}"", ""{offC:x2}""]] }}
                ]
            }}]
        }}";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "treasure.json"), json);
        var db = TreasureDb.Load(dir);

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);   // unit on C
        SeedItemCount(mem, 200, 1);

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);

        StabilizeAndArm(tm);
        Assert.True(mem.Written.ContainsKey(addrC), "C should be marked after arm");

        // Claim C: count ticks up while the unit stands on it.
        mem.U8s[addrC] = TreasureMaster.MarkValue;   // ensure Held so Unlight fires
        BumpItemCount(mem, 200);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);

        // Reset battle: _claimed and the count baseline must be cleared.
        tm.ResetBattle();
        mem.Written.Clear();
        mem.U8s[addrC] = 0x00;   // engine cleared the mark
        // Unit still on C, but the count is stable now (no fresh claim), so re-arm just re-lights.

        // Fresh battle: re-arm should light addrC again (claim cleared by reset)
        TickN(tm, Tuning.TreasureArmStableTicks + 5);

        Assert.True(mem.Written.ContainsKey(addrC), "Fresh battle must re-light C after reset");
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrC]);
    }

    /// <summary>
    /// Zero claim reads when claimDetection:false (the default). The unit-array scan must never
    /// touch the move-ability byte (slot 0) when claim detection is gated off.
    /// </summary>
    [Fact]
    public void ClaimDetection_disabled_zero_reads_in_unit_array()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addr = TileAddr(320);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });
        SeedClaimUnit(mem, x: 5, y: 7);   // slot-0 move byte is Readable: detection WOULD read it

        // Explicitly disable claim detection (the Tuning/Config default is now ON) to prove the
        // gated-off path reads no unit-array memory.
        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: false);
        StabilizeAndArm(tm);
        TickN(tm, 10);

        long claimMoveAddr = Offsets.UnitArrayBaseX;   // occupancy scan reads the grid-X byte here
        Assert.False(mem.ReadCount.TryGetValue(claimMoveAddr, out _),
            "unit-array position byte must never be read when claimDetection is off");
    }

    /// <summary>
    /// Zero claim reads when inLive is false -- claim detection must not run outside a battle.
    /// </summary>
    [Fact]
    public void ClaimDetection_not_inLive_zero_reads_in_unit_array()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addr = TileAddr(321);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) });
        var mem = BuildMem(74, terrain, new[] { addr });
        SeedClaimUnit(mem, x: 5, y: 7);

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        TickN(tm, Tuning.TreasureArmStableTicks + 20, inLive: false);

        long claimMoveAddr = Offsets.UnitArrayBaseX;   // occupancy scan reads the grid-X byte here
        Assert.False(mem.ReadCount.TryGetValue(claimMoveAddr, out _),
            "unit-array position byte must not be read when !inLive");
    }

    /// <summary>
    /// Zero claim reads on build-key mismatch -- _globalIdle prevents TickArmed from running.
    /// </summary>
    [Fact]
    public void ClaimDetection_build_key_mismatch_zero_reads_in_unit_array()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addr = TileAddr(322);
        var db = BuildDb(dir, fpLen: terrain.Length, fpHash: TerrainFpHash(terrain),
            addrs: new[] { (addr, (byte)0x00) },
            buildKey: new TreasureBuildKey { TimeDateStamp = 0x1111, SizeOfImage = 0x2222 });
        var mem = BuildMem(74, terrain, new[] { addr });
        SeedPeHeader(mem, timeDateStamp: 0xAAAA, sizeOfImage: 0xBBBB);
        SeedClaimUnit(mem, x: 5, y: 7);

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        TickN(tm, Tuning.TreasureArmStableTicks + 20);

        long claimMoveAddr = Offsets.UnitArrayBaseX;   // occupancy scan reads the grid-X byte here
        Assert.False(mem.ReadCount.TryGetValue(claimMoveAddr, out _),
            "unit-array position byte must not be read on build-key mismatch");
    }

    // ── Refund un-latch (OPEN-BUG fix: in-battle restart re-lights claimed tiles) ──
    //
    // An in-battle "restart from the start" produces no sustained out-of-live window, so the
    // BattleState exit debounce never fires -> ResetBattle never runs -> _claimed persists ->
    // already-claimed tiles stay excluded from the publish view ("no tiles" after a restart).
    // The restart REFUNDS claimed items (their inventory count drops back to the arm baseline),
    // which IS observable, so un-latching on a refund re-lights the tiles without a battle edge.
    //
    // A rare item's count never drops mid-battle except on a reset, so a rare-claimed tile
    // un-latches on any refund to <= the arm baseline. A common item can also drop when the
    // player USES a found consumable, so a common-claimed tile un-latches only when >= 2 claimed
    // tiles refund at once (a reset refunds all claims together; one consumable use drops one).

    /// <summary>Builds a one-tile-per-coord claim map (each tile gets an explicit rare+common id)
    /// and loads it. Tiles param: (x, y, addrHex, rareId, commonId).</summary>
    private static TreasureDb BuildClaimDb(string dir, byte[] terrain,
        params (int x, int y, long addr, int rare, int common)[] tiles)
    {
        var tileJson = string.Join(",", tiles.Select(t =>
            $@"{{ ""x"": {t.x}, ""y"": {t.y}, ""rareItemId"": {t.rare}, ""commonItemId"": {t.common}, ""addrs"": [[""{t.addr:x}"", ""00""]] }}"));
        string json = $@"{{
            ""buildKey"": null,
            ""maps"": [{{
                ""mapId"": 74, ""name"": ""Test"", ""tileCount"": {tiles.Length},
                ""fpVer"": 1, ""fpLen"": {terrain.Length}, ""fpHash"": ""{TreasureMaster.Fnv1a64(terrain):x}"",
                ""tiles"": [{tileJson}]
            }}]
        }}";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "treasure.json"), json);
        return TreasureDb.Load(dir);
    }

    /// <summary>
    /// CORE FIX: a rare-claimed tile whose rare item count refunds back to the arm baseline is
    /// un-latched and re-lit, WITHOUT any ResetBattle call (the in-battle-restart path). Red on
    /// the pre-fix code: claimed tiles never un-latch, so addrC stays at the resting off value.
    /// </summary>
    [Fact]
    public void ClaimDetection_rare_item_refund_relights_tile_without_reset()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(340);
        int tileX = 5, tileY = 7;
        var db = BuildClaimDb(dir, terrain, (tileX, tileY, addrC, rare: 200, common: 0));

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);   // unit on C
        SeedItemCount(mem, 200, 1);               // arm baseline = 1

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim C: rare count 1 -> 2 while the unit stands on it.
        mem.U8s[addrC] = TreasureMaster.MarkValue;
        BumpItemCount(mem, 200);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);   // claimed + unlit

        // In-battle restart REFUNDS the rare item: count 2 -> 1 (== arm baseline). No ResetBattle.
        SeedItemCount(mem, 200, 1);
        tm.Tick(DateTime.Now, inLive: true);

        // C is un-latched and re-lit (Hold re-stamps MarkValue the same tick).
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrC]);
    }

    /// <summary>
    /// FALSE-POSITIVE GUARD: a tile claimed via its COMMON item must NOT re-light when that
    /// single common count drops back to baseline (the player used a found consumable). Only
    /// a multi-tile simultaneous refund (a reset) re-lights common-claimed tiles.
    /// </summary>
    [Fact]
    public void ClaimDetection_single_common_use_does_not_relight()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(341);
        int tileX = 5, tileY = 7;
        // Tile has both ids; the claim will be via the COMMON item (50), rare (200) stays put.
        var db = BuildClaimDb(dir, terrain, (tileX, tileY, addrC, rare: 200, common: 50));

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);
        SeedItemCount(mem, 200, 1);   // rare baseline (never changes here)
        SeedItemCount(mem, 50, 5);    // common arm baseline = 5

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim C via the common item: count 5 -> 6.
        mem.U8s[addrC] = TreasureMaster.MarkValue;
        BumpItemCount(mem, 50);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);   // claimed + unlit

        // Player USES the found consumable: count 6 -> 5 (== baseline). Single drop, common item.
        SeedItemCount(mem, 50, 5);
        TickN(tm, 3);

        // Must STAY dark -- the treasure is still gone; a lone common-count dip is not a reset.
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);
    }

    /// <summary>
    /// RESET via simultaneous common refunds: two common-claimed tiles whose counts both refund
    /// to baseline in the same tick is a battle restart -> both re-light. Red on the pre-fix code.
    /// </summary>
    [Fact]
    public void ClaimDetection_simultaneous_common_refunds_relight_all()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC1 = TileAddr(342);
        long addrC2 = TileAddr(343);
        var db = BuildClaimDb(dir, terrain,
            (5, 7, addrC1, rare: 200, common: 50),
            (6, 8, addrC2, rare: 201, common: 51));

        var mem = BuildMem(74, terrain, new[] { addrC1, addrC2 }, initialByte: 0x00);
        SeedClaimUnit(mem, x: 5, y: 7, slot: 0);   // unit on C1
        SeedClaimUnit(mem, x: 6, y: 8, slot: 1);   // unit on C2
        SeedItemCount(mem, 50, 5);   // C1 common baseline
        SeedItemCount(mem, 51, 3);   // C2 common baseline

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim both via their common items in one tick.
        mem.U8s[addrC1] = TreasureMaster.MarkValue;
        mem.U8s[addrC2] = TreasureMaster.MarkValue;
        BumpItemCount(mem, 50);
        BumpItemCount(mem, 51);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC1]);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC2]);

        // Battle restart refunds BOTH: counts drop to baseline together. No ResetBattle.
        SeedItemCount(mem, 50, 5);
        SeedItemCount(mem, 51, 3);
        tm.Tick(DateTime.Now, inLive: true);

        // Both un-latched and re-lit.
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrC1]);
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrC2]);
    }

    /// <summary>
    /// REGRESSION GUARD for the edge-trigger: two DIFFERENT common-claimed tiles whose counts drop
    /// back to baseline on DIFFERENT ticks (the player uses two found consumables seconds apart) must
    /// NOT be read as a reset -- both stay dark (claimed). A level-triggered >=2 rule would pair the
    /// two lingering at-baseline counts on the second use and falsely re-light both.
    /// </summary>
    [Fact]
    public void ClaimDetection_staggered_common_uses_do_not_relight()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC1 = TileAddr(344);
        long addrC2 = TileAddr(345);
        var db = BuildClaimDb(dir, terrain,
            (5, 7, addrC1, rare: 200, common: 50),
            (6, 8, addrC2, rare: 201, common: 51));

        var mem = BuildMem(74, terrain, new[] { addrC1, addrC2 }, initialByte: 0x00);
        SeedClaimUnit(mem, x: 5, y: 7, slot: 0);
        SeedClaimUnit(mem, x: 6, y: 8, slot: 1);
        SeedItemCount(mem, 50, 5);
        SeedItemCount(mem, 51, 3);

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim both via their common items in one tick.
        mem.U8s[addrC1] = TreasureMaster.MarkValue;
        mem.U8s[addrC2] = TreasureMaster.MarkValue;
        BumpItemCount(mem, 50);
        BumpItemCount(mem, 51);
        tm.Tick(DateTime.Now, inLive: true);

        // Use the first found consumable (count 50: 6 -> 5). A few ticks pass.
        SeedItemCount(mem, 50, 5);
        TickN(tm, 3);
        // Use the second found consumable (count 51: 4 -> 3), on a LATER tick.
        SeedItemCount(mem, 51, 3);
        TickN(tm, 3);

        // Neither re-lights -- both treasures were genuinely consumed; this is not a reset.
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC1]);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC2]);
    }

    /// <summary>
    /// BOUNDARY + arm-baseline immutability: a rare-claimed tile whose count climbs above the claim
    /// value (another copy found elsewhere) and then PARTIALLY drops -- but stays above the arm
    /// baseline -- must STAY dark. Only a drop to &lt;= the ORIGINAL arm baseline re-lights it,
    /// proving _armCount stays pinned and is not refreshed toward the elevated count.
    /// </summary>
    [Fact]
    public void ClaimDetection_partial_rare_drop_above_baseline_stays_dark_until_full_refund()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(346);
        int tileX = 5, tileY = 7;
        var db = BuildClaimDb(dir, terrain, (tileX, tileY, addrC, rare: 200, common: 0));

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);
        SeedItemCount(mem, 200, 1);   // arm baseline = 1

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim C (1 -> 2), then a second copy of the same rare turns up over later ticks (2 -> 3).
        mem.U8s[addrC] = TreasureMaster.MarkValue;
        SeedItemCount(mem, 200, 2);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);   // claimed + unlit
        SeedItemCount(mem, 200, 3);
        TickN(tm, 2);

        // Partial drop 3 -> 2: still ABOVE the arm baseline of 1 -> must stay dark.
        SeedItemCount(mem, 200, 2);
        TickN(tm, 2);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);

        // Full refund 2 -> 1 (== arm baseline) -> re-lights, proving baseline stayed pinned at 1.
        SeedItemCount(mem, 200, 1);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrC]);
    }

    /// <summary>
    /// REPLAY loop: after a refund re-lights a tile, the player plays on and RE-CLAIMS the same
    /// treasure -- the tile must un-light again. Proves the claim/refund cycle is replayable, not
    /// one-shot (the un-latch must leave the per-tick/arm baselines in a state that allows the next
    /// rise to re-latch).
    /// </summary>
    [Fact]
    public void ClaimDetection_relit_tile_can_be_reclaimed()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(347);
        int tileX = 5, tileY = 7;
        var db = BuildClaimDb(dir, terrain, (tileX, tileY, addrC, rare: 200, common: 0));

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);
        SeedItemCount(mem, 200, 1);

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // Claim, then refund (restart) -> re-lit.
        mem.U8s[addrC] = TreasureMaster.MarkValue;
        SeedItemCount(mem, 200, 2);
        tm.Tick(DateTime.Now, inLive: true);
        SeedItemCount(mem, 200, 1);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrC]);   // re-lit

        // Re-claim the same treasure post-restart: count rises again with the unit on the tile.
        mem.U8s[addrC] = TreasureMaster.MarkValue;   // currently lit/Held
        SeedItemCount(mem, 200, 2);
        tm.Tick(DateTime.Now, inLive: true);

        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);   // re-claimed + unlit again
    }

    /// <summary>
    /// CROSS-BATTLE no-leak: ResetBattle must clear the refund-tracking state (_armCount/_claimItem),
    /// so a second battle works off its OWN fresh arm baseline. Here battle 2's baseline is higher
    /// than battle 1's; a leaked stale baseline of 1 would break refund detection at the new baseline.
    /// </summary>
    [Fact]
    public void ClaimDetection_refund_state_does_not_leak_across_reset()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC = TileAddr(348);
        int tileX = 5, tileY = 7;
        var db = BuildClaimDb(dir, terrain, (tileX, tileY, addrC, rare: 200, common: 0));

        var mem = BuildMem(74, terrain, new[] { addrC }, initialByte: 0x00);
        SeedClaimUnit(mem, x: tileX, y: tileY);
        SeedItemCount(mem, 200, 1);   // battle 1 baseline = 1

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);
        mem.U8s[addrC] = TreasureMaster.MarkValue;
        SeedItemCount(mem, 200, 2);
        tm.Tick(DateTime.Now, inLive: true);   // claim in battle 1

        // End battle 1; battle 2 starts with a DIFFERENT live count for the same item.
        tm.ResetBattle();
        mem.U8s[addrC] = 0x00;
        SeedItemCount(mem, 200, 5);   // battle 2 baseline = 5
        TickN(tm, Tuning.TreasureArmStableTicks + 5);
        Assert.Equal(TreasureMaster.MarkValue, mem.Written[addrC]);   // re-armed + lit in battle 2

        // Claim in battle 2 (5 -> 6), then refund to the FRESH baseline 5 -> must re-light.
        mem.U8s[addrC] = TreasureMaster.MarkValue;
        SeedItemCount(mem, 200, 6);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC]);   // claimed in battle 2
        SeedItemCount(mem, 200, 5);
        tm.Tick(DateTime.Now, inLive: true);
        Assert.Equal(TreasureMaster.MarkValue, mem.U8s[addrC]);      // refund at fresh baseline re-lights
    }

    /// <summary>
    /// SHARED COMMON ID: two tiles whose treasures share ONE common item id (maps reuse item ids
    /// across tiles -- see ClaimAudit) are both claimed via that id. Using a SINGLE consumable of
    /// that id drops the one shared count to baseline, which edge-trips BOTH tiles -- but that is one
    /// item being used, not a reset, so neither tile re-lights. Guards that reset detection counts
    /// DISTINCT item ids that dropped, not tiles.
    /// </summary>
    [Fact]
    public void ClaimDetection_shared_common_id_single_use_does_not_relight()
    {
        var dir = TempDir();
        var terrain = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        long addrC1 = TileAddr(349);
        long addrC2 = TileAddr(350);
        // Both tiles share common id 50 (distinct rare ids, unseeded so they never trigger).
        var db = BuildClaimDb(dir, terrain,
            (5, 7, addrC1, rare: 200, common: 50),
            (6, 8, addrC2, rare: 201, common: 50));

        var mem = BuildMem(74, terrain, new[] { addrC1, addrC2 }, initialByte: 0x00);
        SeedClaimUnit(mem, x: 5, y: 7, slot: 0);
        SeedClaimUnit(mem, x: 6, y: 8, slot: 1);
        SeedItemCount(mem, 50, 5);   // shared common baseline

        var tm = new TreasureMaster(db, mem, enabled: true, claimDetection: true);
        StabilizeAndArm(tm);

        // One pickup of the shared item latches both occupied tiles (same-tick double-latch).
        mem.U8s[addrC1] = TreasureMaster.MarkValue;
        mem.U8s[addrC2] = TreasureMaster.MarkValue;
        BumpItemCount(mem, 50);   // 5 -> 6
        tm.Tick(DateTime.Now, inLive: true);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC1]);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC2]);

        // Use ONE of the found consumables: the single shared count drops to baseline (6 -> 5).
        SeedItemCount(mem, 50, 5);
        TickN(tm, 3);

        // Not a reset (one distinct item id dropped) -- both stay dark.
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC1]);
        Assert.NotEqual(TreasureMaster.MarkValue, mem.U8s[addrC2]);
    }

}
