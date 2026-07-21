using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace FFTTreasureMaster;

// ── record types ─────────────────────────────────────────────────────────────

/// <summary>One (addr, off) pair for a treasure tile flag byte.</summary>
internal sealed class TreasureAddrEntry
{
    // Populated by TreasureDb.Load after parsing — never by JSON directly.
    public long Addr { get; internal set; }
    public byte Off  { get; internal set; }
    /// <summary>Schema v2: the anchor region id (e.g. "R1") this addr was tagged with at
    /// bake time, or null for a v1 (2-element) entry / a regionless entry. Used by
    /// AnchorRemap to compute addr' = addr + regionDeltas[Region] on a build-key mismatch.</summary>
    public string? Region { get; internal set; }

    /// <summary>Supports foreach (var (addr, off) in tile.Addrs) deconstruction.</summary>
    public void Deconstruct(out long addr, out byte off) { addr = Addr; off = Off; }
}

/// <summary>Raw JSON shape for one tile: x, y, addrs as array-of-2-string-arrays.</summary>
internal sealed class TreasureTileJson
{
    [JsonProperty("x")]     public int X { get; set; }
    [JsonProperty("y")]     public int Y { get; set; }
    /// <summary>Each element is a 2-element string array: [addrHex, offHex].</summary>
    [JsonProperty("addrs")] public List<string[]> Addrs { get; set; } = new();
    /// <summary>ItemData id of the rare Move-Find item on this tile (0 if absent). Used by claim
    /// detection: when this item's inventory count rises while a unit is on the tile, it is claimed.</summary>
    [JsonProperty("rareItemId")]   public int RareItemId   { get; set; }
    /// <summary>ItemData id of the common Move-Find item on this tile (0 if absent).</summary>
    [JsonProperty("commonItemId")] public int CommonItemId { get; set; }
    /// <summary>0-based slot index in the map's treasure list (native X1..X4 file order). Used by
    /// collect detection to index into the persistent collected-treasure bitfield. -1 = unknown.</summary>
    [JsonProperty("slot")]         public int Slot         { get; set; } = -1;
}

/// <summary>One treasure tile with its validated flag-byte addresses.</summary>
internal sealed class TreasureTile
{
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public List<TreasureAddrEntry> Addrs { get; internal set; } = new();
    /// <summary>Rare / common Move-Find item ids on this tile (0 = none). The claim detector
    /// watches their inventory counts.</summary>
    public int RareItemId   { get; internal set; }
    public int CommonItemId { get; internal set; }
    /// <summary>0-based slot index in the map's treasure list (native X1..X4 file order). Used by
    /// collect detection to index into the persistent collected-treasure bitfield. -1 = unknown.</summary>
    public int Slot { get; internal set; } = -1;
}

/// <summary>Build key: PE TimeDateStamp + SizeOfImage from the captured game binary.</summary>
internal sealed class TreasureBuildKey
{
    [JsonProperty("timeDateStamp")] public int TimeDateStamp { get; set; }
    [JsonProperty("sizeOfImage")]   public int SizeOfImage   { get; set; }
}

/// <summary>Raw JSON shape for one map entry.</summary>
internal sealed class TreasureMapJson
{
    [JsonProperty("mapId")]     public int              MapId     { get; set; }
    [JsonProperty("name")]      public string           Name      { get; set; } = "";
    [JsonProperty("tileCount")] public int              TileCount { get; set; }
    [JsonProperty("fpVer")]     public int?             FpVer     { get; set; }
    [JsonProperty("fpLen")]     public int?             FpLen     { get; set; }
    [JsonProperty("fpHash")]    public string?          FpHashHex { get; set; }
    [JsonProperty("tiles")]     public List<TreasureTileJson> Tiles { get; set; } = new();
}

/// <summary>One map's tile set (or a stub entry with no tiles for uncaptured maps).</summary>
internal sealed class TreasureMap
{
    public int     MapId     { get; internal set; }
    public string  Name      { get; internal set; } = "";
    public int     TileCount { get; internal set; }
    public int?    FpVer     { get; internal set; }
    public int?    FpLen     { get; internal set; }
    public string? FpHashHex { get; internal set; }
    public List<TreasureTile> Tiles { get; internal set; } = new();

    /// <summary>fpHash as ulong; null when fpHashHex is absent or null.</summary>
    public ulong? FpHash =>
        FpHashHex is { } s && ulong.TryParse(s.AsSpan(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                                              NumberStyles.HexNumber, null, out ulong v)
            ? v : (ulong?)null;

    /// <summary>
    /// True when this map is in map-id-only mode: <see cref="FpHash"/> is null and
    /// <see cref="FpVer"/> is exactly 0.  The runtime arms on map-id + address quorum
    /// alone, with no terrain fingerprint gate and no periodic terrain revalidation.
    /// Used for water/lava maps whose terrain grid fields animate on every re-entry and
    /// cannot produce a stable hash across instances.
    ///
    /// Stub maps (uncaptured) have <see cref="FpVer"/> null, not 0, so they do NOT satisfy
    /// this predicate.  Dry maps keep their v2/v3 fingerprint; map-id-only is an explicit
    /// per-map decision made via the 'nofp' verb in the capture tool.
    /// </summary>
    public bool IsMapIdOnly => FpHash is null && FpVer == 0;
}

// ── raw deserialization root ──────────────────────────────────────────────────

internal sealed class TreasureDbJson
{
    [JsonProperty("schema")]   public int?                Schema   { get; set; }
    [JsonProperty("buildKey")] public TreasureBuildKey?    BuildKey { get; set; }
    [JsonProperty("anchors")]  public AnchorTableJson?     Anchors  { get; set; }
    [JsonProperty("maps")]     public List<TreasureMapJson> Maps   { get; set; } = new();
}

// ── public loader ─────────────────────────────────────────────────────────────

/// <summary>
/// Fail-soft loader for treasure.json.  Missing file or parse failure yields an empty
/// dataset (no marks painted, silent no-op in every map) rather than a crash.
/// PURE FILE LOADER — no live memory access of any kind.
/// </summary>
internal sealed class TreasureDb
{
    // Internal (not private): AnchorRemap.cs reuses these bounds to drop a remapped
    // addr that lands outside the module or inside the UI render arena.
    internal const long ModuleBase = 0x140000000L;
    internal const long ModuleEnd  = 0x143000000L;   // exclusive
    internal const long UiArenaLo  = 0x140C63000L;
    internal const long UiArenaHi  = 0x140CC5000L;   // exclusive

    public TreasureBuildKey? BuildKey { get; }
    public IReadOnlyList<TreasureMap> Maps { get; }
    /// <summary>Schema v2 anchor table (7 region bases + 10 singleton sig anchors), or
    /// null on a v1 dataset (no "anchors" key) or any parse/structural problem -- a null
    /// table means a build-key mismatch can never re-resolve, only disarm.</summary>
    public AnchorTable? Anchors { get; }

    // Internal (not private): AnchorRemap.cs builds a new TreasureDb from a remapped
    // map list, carrying BuildKey/Anchors through unchanged.
    internal TreasureDb(TreasureBuildKey? key, List<TreasureMap> maps, AnchorTable? anchors)
    {
        BuildKey = key;
        Maps     = maps;
        Anchors  = anchors;
    }

    /// <summary>
    /// Load treasure.json from <paramref name="modDir"/>.
    /// Returns an empty dataset (no maps) on missing file, parse failure, or any
    /// top-level exception — never throws.
    /// </summary>
    public static TreasureDb Load(string modDir)
    {
        try
        {
            var path = Path.Combine(modDir, "treasure.json");
            if (!File.Exists(path)) return Empty();

            var raw  = File.ReadAllText(path);
            var root = JsonConvert.DeserializeObject<TreasureDbJson>(raw);
            if (root is null) return Empty();

            var validMaps = new List<TreasureMap>(root.Maps.Count);
            foreach (var mj in root.Maps)
            {
                var validTiles = new List<TreasureTile>(mj.Tiles.Count);
                foreach (var tj in mj.Tiles)
                {
                    var validAddrs = new List<TreasureAddrEntry>(tj.Addrs.Count);
                    bool tileOk = true;
                    foreach (var pair in tj.Addrs)
                    {
                        if (pair.Length < 2) { tileOk = false; break; }
                        if (!TryParseHex(pair[0], out long addr) ||
                            !TryParseHexByte(pair[1], out byte off))
                        { tileOk = false; break; }

                        if (addr < ModuleBase || addr >= ModuleEnd)
                        { tileOk = false; break; }

                        if (addr >= UiArenaLo && addr < UiArenaHi)
                        { tileOk = false; break; }

                        // pair[2] (when present) is the schema v2 anchor region id --
                        // a plain string, not hex ("R1", not "0xR1").  Absent on a
                        // v1 (2-element) or regionless entry.
                        validAddrs.Add(new TreasureAddrEntry
                        {
                            Addr = addr, Off = off,
                            Region = pair.Length >= 3 ? pair[2] : null,
                        });
                    }
                    if (tileOk && validAddrs.Count > 0)
                        validTiles.Add(new TreasureTile
                        {
                            X = tj.X, Y = tj.Y, Addrs = validAddrs,
                            RareItemId = tj.RareItemId, CommonItemId = tj.CommonItemId,
                            Slot = tj.Slot,
                        });
                }
                validMaps.Add(new TreasureMap
                {
                    MapId     = mj.MapId,
                    Name      = mj.Name,
                    TileCount = mj.TileCount,
                    FpVer     = mj.FpVer,
                    FpLen     = mj.FpLen,
                    FpHashHex = mj.FpHashHex,
                    Tiles     = validTiles,
                });
            }

            var anchors = AnchorDb.Parse(root.Anchors);
            return new TreasureDb(root.BuildKey, validMaps, anchors);
        }
        catch
        {
            Log.Info("treasure: treasure.json unreadable -- marks disabled");
            return Empty();
        }
    }

    private static TreasureDb Empty() => new(null, new List<TreasureMap>(), null);

    /// <summary>Returns an empty dataset. Exposed for tests that need to inject an
    /// initially-empty db and swap it out via the reload seam.</summary>
    internal static TreasureDb MakeEmpty() => Empty();

    // Internal (not private): AnchorDb.cs mirrors this exact parse for anchor hex
    // fields (target/base/span/addr) so both loaders agree on "0x"-prefix handling.
    internal static bool TryParseHex(string? s, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var span = s.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        return long.TryParse(span, NumberStyles.HexNumber, null, out result);
    }

    private static bool TryParseHexByte(string? s, out byte result)
    {
        result = 0;
        if (!TryParseHex(s, out long v) || v < 0 || v > 255) return false;
        result = (byte)v;
        return true;
    }
}
