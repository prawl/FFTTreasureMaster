using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace FFTTreasureMaster;

// ── raw JSON shapes (schema v2 "anchors" block) ───────────────────────────────

/// <summary>Raw JSON shape for one sig entry. pinConst is bake-time consistency-check
/// redundancy only (gen_treasure_db.py asserts addr == target + pinConst for the two
/// parent-anchor sigs); the runtime record (<see cref="AnchorSig"/>) never carries it.</summary>
internal sealed class AnchorSigJson
{
    [JsonProperty("name")]      public string? Name      { get; set; }
    [JsonProperty("pattern")]   public string? Pattern   { get; set; }
    [JsonProperty("dispOff")]   public int?    DispOff   { get; set; }
    [JsonProperty("endAdjust")] public int?    EndAdjust { get; set; }
    [JsonProperty("target")]   public string? Target    { get; set; }
    [JsonProperty("pinConst")] public string? PinConst   { get; set; }
}

/// <summary>Raw JSON shape for one region: id, base, inclusive [lo, hi] span, sigs.</summary>
internal sealed class AnchorRegionJson
{
    [JsonProperty("id")]   public string? Id   { get; set; }
    [JsonProperty("base")] public string? Base { get; set; }
    [JsonProperty("span")] public List<string>? Span { get; set; }
    [JsonProperty("sigs")] public List<AnchorSigJson>? Sigs { get; set; }
}

/// <summary>Raw JSON shape for one singleton address (Slot0, Slot9, ... UnitArrayBaseX,
/// TerrainGrid). One or more sigs; target == addr for a direct singleton, target + pinConst
/// == addr for a parent anchor.</summary>
internal sealed class AnchorSingletonJson
{
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("addr")] public string? Addr { get; set; }
    [JsonProperty("sigs")] public List<AnchorSigJson>? Sigs { get; set; }
}

/// <summary>Raw JSON shape for the whole "anchors" block of treasure.json (and of
/// data/anchor_sigs.json minus its _meta).</summary>
internal sealed class AnchorTableJson
{
    [JsonProperty("regions")]    public List<AnchorRegionJson>?    Regions    { get; set; }
    [JsonProperty("singletons")] public List<AnchorSingletonJson>? Singletons { get; set; }
}

// ── runtime records (parsed, hex-resolved; no pinConst -- bake-time only) ────

/// <summary>One resolved sig: a masked byte pattern (null = wildcard) plus the RIP-resolve
/// parameters and the baked target VA this sig is expected to resolve to on the build the
/// dataset was captured against.</summary>
internal sealed record AnchorSig(string Name, int?[] Pattern, int DispOff, int EndAdjust, long Target);

/// <summary>One tile-address region: its baked base VA, the inclusive [SpanLo, SpanHi]
/// dataset-addr classification window, and the sigs that can resolve it.</summary>
internal sealed record AnchorRegion(string Id, long Base, long SpanLo, long SpanHi, IReadOnlyList<AnchorSig> Sigs);

/// <summary>One singleton game address (a battle sentinel, map-id slot, etc.) and the
/// sigs that can resolve it.</summary>
internal sealed record AnchorSingleton(string Name, long Addr, IReadOnlyList<AnchorSig> Sigs);

/// <summary>The whole parsed anchor table: 7 regions (R0..R6) + 10 singletons.</summary>
internal sealed record AnchorTable(IReadOnlyList<AnchorRegion> Regions, IReadOnlyList<AnchorSingleton> Singletons);

// ── fail-soft parser ──────────────────────────────────────────────────────────

/// <summary>
/// Fail-soft parser for the schema v2 "anchors" block. ANY structural problem anywhere
/// in the block (a missing field, an unparseable hex string, a garbage pattern token)
/// yields a null <see cref="AnchorTable"/> for the WHOLE table, never a partial one and
/// never a throw -- a build-key mismatch against a null anchor table simply cannot
/// re-resolve and falls back to the existing global-disarm behavior.
/// </summary>
internal static class AnchorDb
{
    public static AnchorTable? Parse(AnchorTableJson? raw)
    {
        try
        {
            if (raw?.Regions is null || raw.Singletons is null) return null;

            var regions = new List<AnchorRegion>(raw.Regions.Count);
            foreach (var r in raw.Regions)
            {
                var region = ParseRegion(r);
                if (region is null) return null;
                regions.Add(region);
            }

            var singletons = new List<AnchorSingleton>(raw.Singletons.Count);
            foreach (var s in raw.Singletons)
            {
                var singleton = ParseSingleton(s);
                if (singleton is null) return null;
                singletons.Add(singleton);
            }

            return new AnchorTable(regions, singletons);
        }
        catch
        {
            return null;
        }
    }

    private static AnchorRegion? ParseRegion(AnchorRegionJson r)
    {
        if (r.Id is null || r.Base is null || r.Span is null || r.Span.Count != 2 || r.Sigs is null)
            return null;
        if (!TreasureDb.TryParseHex(r.Base, out long baseAddr)) return null;
        if (!TreasureDb.TryParseHex(r.Span[0], out long lo)) return null;
        if (!TreasureDb.TryParseHex(r.Span[1], out long hi)) return null;

        var sigs = ParseSigs(r.Sigs);
        return sigs is null ? null : new AnchorRegion(r.Id, baseAddr, lo, hi, sigs);
    }

    private static AnchorSingleton? ParseSingleton(AnchorSingletonJson s)
    {
        if (s.Name is null || s.Addr is null || s.Sigs is null) return null;
        if (!TreasureDb.TryParseHex(s.Addr, out long addr)) return null;

        var sigs = ParseSigs(s.Sigs);
        return sigs is null ? null : new AnchorSingleton(s.Name, addr, sigs);
    }

    private static List<AnchorSig>? ParseSigs(List<AnchorSigJson> raw)
    {
        var list = new List<AnchorSig>(raw.Count);
        foreach (var s in raw)
        {
            var sig = ParseSig(s);
            if (sig is null) return null;
            list.Add(sig);
        }
        return list;
    }

    private static AnchorSig? ParseSig(AnchorSigJson s)
    {
        if (s.Name is null || s.Pattern is null || s.DispOff is null ||
            s.EndAdjust is null || s.Target is null)
            return null;

        var pattern = ParsePattern(s.Pattern);
        if (pattern is null) return null;
        if (!TreasureDb.TryParseHex(s.Target, out long target)) return null;

        return new AnchorSig(s.Name, pattern, s.DispOff.Value, s.EndAdjust.Value, target);
    }

    /// <summary>"89 05 ?? ?? ?? ?? CC" -&gt; [0x89, 0x05, null, null, null, null, 0xCC].
    /// Any token that is neither "??" nor a valid 0..0xFF hex byte fails the WHOLE
    /// pattern (and, by propagation, the whole table).</summary>
    internal static int?[]? ParsePattern(string text)
    {
        var toks = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length == 0) return null;

        var outArr = new int?[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            if (toks[i] == "??") { outArr[i] = null; continue; }
            if (!int.TryParse(toks[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b) ||
                b < 0 || b > 0xFF)
                return null;
            outArr[i] = b;
        }
        return outArr;
    }
}
