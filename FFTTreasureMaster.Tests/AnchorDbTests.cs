using System.Collections.Generic;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// AnchorDb.Parse (fail-soft schema v2 "anchors" block parser) + AnchorRemap.Remap
/// (pure per-region delta remap, used after a successful signature re-resolve on a
/// build-key mismatch -- see the update-hardening design). Parse never throws: any
/// structural problem anywhere in the block poisons the WHOLE table to null.
/// </summary>
public class AnchorDbTests
{
    // ── pattern parsing ───────────────────────────────────────────────────────

    [Fact]
    public void ParsePattern_parses_literal_bytes_and_wildcards()
    {
        var pat = AnchorDb.ParsePattern("89 05 ?? ?? ?? ?? CC");
        Assert.NotNull(pat);
        Assert.Equal(new int?[] { 0x89, 0x05, null, null, null, null, 0xCC }, pat);
    }

    [Fact]
    public void ParsePattern_rejects_a_garbage_token()
    {
        Assert.Null(AnchorDb.ParsePattern("89 05 ZZ ?? CC"));
    }

    [Fact]
    public void ParsePattern_rejects_empty_string()
    {
        Assert.Null(AnchorDb.ParsePattern(""));
    }

    // ── well-formed block parses ──────────────────────────────────────────────

    private static AnchorRegionJson MakeRegion(string id, string baseAddr, string lo, string hi) => new()
    {
        Id = id,
        Base = baseAddr,
        Span = new List<string> { lo, hi },
        Sigs = new List<AnchorSigJson>
        {
            new() { Name = id + "-primary", Pattern = "48 8B 0D ?? ?? ?? ??", DispOff = 3,
                    EndAdjust = 0, Target = baseAddr },
        },
    };

    private static AnchorSingletonJson MakeSingleton(string name, string addr) => new()
    {
        Name = name,
        Addr = addr,
        Sigs = new List<AnchorSigJson>
        {
            new() { Name = name, Pattern = "11 05 ?? ?? ?? ??", DispOff = 2,
                    EndAdjust = 0, Target = addr },
        },
    };

    private static AnchorTableJson MakeWellFormedTable() => new()
    {
        Regions = new List<AnchorRegionJson>
        {
            MakeRegion("R1", "0x140DE2BC8", "0x140DE4837", "0x140E01967"),
            MakeRegion("R3", "0x140F96348", "0x140F92D75", "0x140FB50CF"),
        },
        Singletons = new List<AnchorSingletonJson>
        {
            MakeSingleton("Slot0", "0x140782A30"),
        },
    };

    [Fact]
    public void WellFormed_block_parses_with_correct_counts_and_targets()
    {
        var table = AnchorDb.Parse(MakeWellFormedTable());
        Assert.NotNull(table);
        Assert.Equal(2, table!.Regions.Count);
        Assert.Single(table.Singletons);

        var r1 = table.Regions[0];
        Assert.Equal("R1", r1.Id);
        Assert.Equal(0x140DE2BC8L, r1.Base);
        Assert.Equal(0x140DE4837L, r1.SpanLo);
        Assert.Equal(0x140E01967L, r1.SpanHi);
        Assert.Single(r1.Sigs);
        Assert.Equal(0x140DE2BC8L, r1.Sigs[0].Target);
        Assert.Equal(new int?[] { 0x48, 0x8B, 0x0D, null, null, null, null }, r1.Sigs[0].Pattern);

        var slot0 = table.Singletons[0];
        Assert.Equal("Slot0", slot0.Name);
        Assert.Equal(0x140782A30L, slot0.Addr);
    }

    [Fact]
    public void Null_raw_table_parses_to_null()
    {
        Assert.Null(AnchorDb.Parse(null));
    }

    // ── missing required field -> null (whole table poisoned) ────────────────

    [Fact]
    public void Missing_region_span_yields_null_table()
    {
        var raw = MakeWellFormedTable();
        raw.Regions![0].Span = null;
        Assert.Null(AnchorDb.Parse(raw));
    }

    [Fact]
    public void Missing_region_base_yields_null_table()
    {
        var raw = MakeWellFormedTable();
        raw.Regions![0].Base = null;
        Assert.Null(AnchorDb.Parse(raw));
    }

    [Fact]
    public void Unparseable_target_hex_yields_null_table()
    {
        var raw = MakeWellFormedTable();
        raw.Regions![0].Sigs![0].Target = "not-hex";
        Assert.Null(AnchorDb.Parse(raw));
    }

    [Fact]
    public void Garbage_pattern_token_yields_null_table()
    {
        var raw = MakeWellFormedTable();
        raw.Singletons![0].Sigs![0].Pattern = "ZZ ?? 05";
        Assert.Null(AnchorDb.Parse(raw));
    }

    [Fact]
    public void Missing_singleton_addr_yields_null_table()
    {
        var raw = MakeWellFormedTable();
        raw.Singletons![0].Addr = null;
        Assert.Null(AnchorDb.Parse(raw));
    }

    [Fact]
    public void Missing_regions_list_yields_null_table()
    {
        var raw = MakeWellFormedTable();
        raw.Regions = null;
        Assert.Null(AnchorDb.Parse(raw));
    }

    // ── AnchorRemap.Remap ──────────────────────────────────────────────────────

    private static TreasureAddrEntry Addr(long addr, byte off, string? region) =>
        new() { Addr = addr, Off = off, Region = region };

    private static TreasureTile Tile(int x, int y, params TreasureAddrEntry[] addrs) => new()
    {
        X = x, Y = y, Addrs = new List<TreasureAddrEntry>(addrs),
        RareItemId = 7, CommonItemId = 3, Slot = 1,
    };

    private static TreasureMap Map(int mapId, params TreasureTile[] tiles) => new()
    {
        MapId = mapId, Name = "Test Map", TileCount = tiles.Length,
        FpVer = 2, FpLen = 1456, FpHashHex = "0xdeadbeefdeadbeef",
        Tiles = new List<TreasureTile>(tiles),
    };

    private static TreasureDb Db(params TreasureMap[] maps) =>
        new(null, new List<TreasureMap>(maps), null);

    [Fact]
    public void Remap_applies_delta_per_region()
    {
        var db = Db(Map(74, Tile(0, 1,
            Addr(0x140de1ea7L, 0x01, "R1"),
            Addr(0x140f93191L, 0x00, "R3"))));

        var deltas = new Dictionary<string, long> { ["R1"] = 0x1000, ["R3"] = -0x500 };
        var remapped = AnchorRemap.Remap(db, deltas);

        var tile = Assert.Single(Assert.Single(remapped.Maps).Tiles);
        Assert.Equal(2, tile.Addrs.Count);
        Assert.Equal(0x140de1ea7L + 0x1000, tile.Addrs[0].Addr);
        Assert.Equal(0x140f93191L - 0x500, tile.Addrs[1].Addr);
    }

    [Fact]
    public void Remap_preserves_off_bytes()
    {
        var db = Db(Map(74, Tile(0, 1, Addr(0x140de1ea7L, 0x01, "R1"))));
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long> { ["R1"] = 0x10 });
        Assert.Equal((byte)0x01, remapped.Maps[0].Tiles[0].Addrs[0].Off);
    }

    [Fact]
    public void Remap_drops_a_regionless_pair()
    {
        var db = Db(Map(74, Tile(0, 1,
            Addr(0x140de1ea7L, 0x01, null),
            Addr(0x140f93191L, 0x00, "R3"))));
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long> { ["R3"] = 0 });
        var tile = Assert.Single(remapped.Maps[0].Tiles);
        var addr = Assert.Single(tile.Addrs);
        Assert.Equal(0x140f93191L, addr.Addr);
    }

    [Fact]
    public void Remap_drops_a_pair_with_an_unknown_region()
    {
        var db = Db(Map(74, Tile(0, 1,
            Addr(0x140de1ea7L, 0x01, "R1"),
            Addr(0x140f93191L, 0x00, "R3"))));
        // R1 is absent from regionDeltas -- an unresolved region on this build.
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long> { ["R3"] = 0 });
        var tile = Assert.Single(remapped.Maps[0].Tiles);
        var addr = Assert.Single(tile.Addrs);
        Assert.Equal(0x140f93191L, addr.Addr);
    }

    [Fact]
    public void Remap_drops_a_pair_that_lands_in_the_UI_arena()
    {
        // 0x140C63000 is the start of the UI arena. Pick a base addr + delta that lands there.
        var db = Db(Map(74, Tile(0, 1, Addr(0x140C00000L, 0x01, "R1"))));
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long> { ["R1"] = 0x63000 });
        Assert.Empty(Assert.Single(remapped.Maps).Tiles);
    }

    [Fact]
    public void Remap_drops_a_pair_that_lands_outside_the_module_span()
    {
        var db = Db(Map(74, Tile(0, 1, Addr(0x140000100L, 0x01, "R1"))));
        // Delta pushes it below ModuleBase (0x140000000).
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long> { ["R1"] = -0x200 });
        Assert.Empty(Assert.Single(remapped.Maps).Tiles);
    }

    [Fact]
    public void Remap_drops_a_tile_emptied_of_all_its_pairs()
    {
        var db = Db(Map(74, Tile(0, 1, Addr(0x140de1ea7L, 0x01, null))));
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long>());
        Assert.Empty(Assert.Single(remapped.Maps).Tiles);
    }

    [Fact]
    public void Remap_keeps_the_map_intact_even_when_all_tiles_drop()
    {
        var db = Db(Map(74, Tile(0, 1, Addr(0x140de1ea7L, 0x01, null))));
        var remapped = AnchorRemap.Remap(db, new Dictionary<string, long>());
        var map = Assert.Single(remapped.Maps);
        Assert.Equal(74, map.MapId);
        Assert.Equal("Test Map", map.Name);
        Assert.Empty(map.Tiles);
    }
}
