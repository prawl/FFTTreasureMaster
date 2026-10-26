using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// RareTreasureGrant, the pure algorithm behind the Rare treasure only toggle. Fed by
/// FakeTrapTable, no threads, no loader. Every walk covers map ids 0..127.
///
/// The invariants it pins.
///   a slot with rare id 0 is unused and never counted or written;
///   a slot whose common already equals its rare is counted AlreadyRare and not written;
///   one patch per map, carrying only the changed slots;
///   an apply counts only when the read-back confirms the written value;
///   a missing seam logs exactly one line, a never-ready table one line, a throw one line;
///   the summary prints once after a completed walk.
/// </summary>
public class RareTreasureGrantTests
{
    private static TrapSlotView[] Slots(params (int rare, int common)[] s)
        => s.Select(x => new TrapSlotView((ushort)x.rare, (ushort)x.common)).ToArray();

    private static FakeTrapTable FixtureA()
    {
        var t = new FakeTrapTable();
        t.Maps[1] = Slots((184, 242), (198, 244), (206, 252), (214, 253));
        t.Maps[3] = Slots((245, 252), (245, 253), (245, 241), (245, 242));
        return t;
    }

    private static RareTreasureGrant Grant(FakeTrapTable? t, List<string> log,
        List<int>? sleeps = null, int intervalMs = 10, int capMs = 100)
        => new(t, log.Add, ms => sleeps?.Add(ms), intervalMs, capMs);

    [Fact]
    public void PatchesCommonToRare_OnEverySlotWhereTheyDiffer()
    {
        var t = FixtureA();
        var log = new List<string>();

        var s = Grant(t, log).Run();

        Assert.Equal(2, t.ApplyCalls.Count);
        Assert.Equal(1, t.ApplyCalls[0].MapId);
        Assert.Equal(new ushort?[] { 184, 198, 206, 214 }, t.ApplyCalls[0].Common);
        Assert.Equal(3, t.ApplyCalls[1].MapId);
        Assert.Equal(new ushort?[] { 245, 245, 245, 245 }, t.ApplyCalls[1].Common);
        Assert.Equal(8, s.Applied);
        Assert.Equal(0, s.AlreadyRare);
        Assert.Equal(0, s.Failed);
        Assert.Equal(0, s.UnreadableMaps);
    }

    [Fact]
    public void ZeroRareIsUnusedEvenWithCommonSet_ZeroCommonIsPatched()
    {
        var t = new FakeTrapTable();
        t.Maps[10] = Slots((9, 0), (0, 7), (0, 0), (300, 300));

        var s = Grant(t, new List<string>()).Run();

        Assert.Single(t.ApplyCalls);
        Assert.Equal(new ushort?[] { 9, null, null, null }, t.ApplyCalls[0].Common);
        Assert.Equal(1, s.Applied);
        Assert.Equal(1, s.AlreadyRare);
        Assert.Equal(0, s.Failed);
        Assert.Equal(0, s.UnreadableMaps);
    }

    [Fact]
    public void SeamNull_ExactlyOneLogLine()
    {
        var log = new List<string>();

        var s = Grant(null, log).Run();

        Assert.Single(log);
        Assert.Contains("not installed or not enabled", log[0]);
        Assert.Equal(0, s.Applied);
        Assert.False(s.GaveUp);
    }

    [Fact]
    public void NotReady_RetriesThenApplies()
    {
        var t = FixtureA();
        t.NotReadyProbes = 3;
        var sleeps = new List<int>();

        var s = Grant(t, new List<string>(), sleeps).Run();

        Assert.Equal(new[] { 10, 10, 10 }, sleeps);
        Assert.Equal(4, t.IsReadyCalls);
        Assert.Equal(8, s.Applied);
    }

    [Fact]
    public void NeverReady_GivesUpWithOneLine()
    {
        var t = FixtureA();
        t.NeverReady = true;
        var log = new List<string>();

        var s = Grant(t, log, new List<int>(), capMs: 30).Run();

        Assert.True(s.GaveUp);
        Assert.Single(log);
        Assert.Contains("never became ready", log[0]);
        Assert.Empty(t.ApplyCalls);
    }

    [Fact]
    public void ReadBackFailure_CountsFailedNotApplied()
    {
        var t = FixtureA();
        t.MutateOnApply = false;

        var s = Grant(t, new List<string>()).Run();

        Assert.Equal(8, s.Failed);
        Assert.Equal(0, s.Applied);
    }

    [Fact]
    public void ApplyReturnsFalse_CountsFailed_NoReadBack()
    {
        var t = FixtureA();
        t.ApplyResult = false;

        var s = Grant(t, new List<string>()).Run();

        Assert.Equal(8, s.Failed);
        Assert.Equal(0, s.Applied);
        Assert.Equal(2, t.ApplyCalls.Count);
        Assert.Equal(128, t.GetSlotsCalls);
    }

    [Fact]
    public void ReadBackNull_CountsFailed()
    {
        var t = FixtureA();
        t.NullGetSlotsAfterApply = true;

        var s = Grant(t, new List<string>()).Run();

        Assert.Equal(4, s.Failed);
        Assert.Equal(4, s.Applied);
        Assert.Equal(2, t.ApplyCalls.Count);
    }

    [Fact]
    public void UnreadableMap_CountedAndSkipped()
    {
        var t = FixtureA();
        t.UnreadableIds.Add(5);

        var s = Grant(t, new List<string>()).Run();

        Assert.Equal(1, s.UnreadableMaps);
        Assert.Equal(8, s.Applied);
        Assert.Equal(2, t.ApplyCalls.Count);
    }

    [Fact]
    public void WrongLengthSlots_TreatedAsUnreadable()
    {
        var t = new FakeTrapTable();
        t.Maps[20] = Slots((5, 6), (5, 6), (5, 6));

        var s = Grant(t, new List<string>()).Run();

        Assert.Equal(1, s.UnreadableMaps);
        Assert.Empty(t.ApplyCalls);
    }

    [Fact]
    public void SeamThrows_CaughtLoggedOnce_NoPropagation()
    {
        var t = new FakeTrapTable { ThrowOnGetSlots = true };
        var log = new List<string>();

        var ex = Record.Exception(() => Grant(t, log).Run());

        Assert.Null(ex);
        Assert.Single(log);
        Assert.Contains("failed", log[0]);
    }

    [Fact]
    public void WalksEveryMapIdOnce_InOrder()
    {
        var t = new FakeTrapTable();

        Grant(t, new List<string>()).Run();

        Assert.Equal(Enumerable.Range(0, 128).ToList(), t.GetSlotsIds);
    }

    [Fact]
    public void DoubleRun_SecondRunAppliesNothing()
    {
        var t = FixtureA();
        var g = Grant(t, new List<string>());

        var first = g.Run();
        int calls = t.ApplyCalls.Count;
        var second = g.Run();

        Assert.Equal(calls, t.ApplyCalls.Count);
        Assert.Equal(0, second.Applied);
        Assert.Equal(first.Applied + first.AlreadyRare, second.AlreadyRare);
    }

    [Fact]
    public void SummaryLine_ExactText()
    {
        var t = new FakeTrapTable();
        t.Maps[1] = Slots((184, 242), (198, 244), (206, 252), (214, 253));
        t.Maps[54] = Slots((37, 37), (37, 37), (37, 37), (37, 37));
        t.UnreadableIds.Add(7);
        var log = new List<string>();

        Grant(t, log).Run();

        Assert.Single(log);
        Assert.Equal("Rare treasure only is in effect: 4 treasure tile(s) now give their rare item, " +
                     "4 already did, 1 map(s) unreadable, 0 failed.", log[0]);
    }

    private static Dictionary<int, TrapSlotView[]> LoadSnapshot()
    {
        string path = Path.Combine(RepoRoot.Locate(), "data", "map_trap_formation.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var maps = new Dictionary<int, TrapSlotView[]>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var tiles = prop.Value.GetProperty("tiles").EnumerateArray().ToArray();
            if (tiles.Length == 0) continue;
            maps[int.Parse(prop.Name)] = tiles.Select(x => new TrapSlotView(
                x.GetProperty("rareItemId").GetUInt16(),
                x.GetProperty("commonItemId").GetUInt16())).ToArray();
        }
        return maps;
    }

    [Fact]
    public void VanillaSnapshot_Applies317Already27On81Maps()
    {
        var t = new FakeTrapTable();
        foreach (var kv in LoadSnapshot()) t.Maps[kv.Key] = kv.Value;

        var s = Grant(t, new List<string>()).Run();

        Assert.Equal(317, s.Applied);
        Assert.Equal(27, s.AlreadyRare);
        Assert.Equal(81, t.ApplyCalls.Select(c => c.MapId).Distinct().Count());
        Assert.Equal(344, s.Applied + s.AlreadyRare);
        Assert.Equal(0, s.UnreadableMaps);
    }

    [Fact]
    public void TuningTrapConstants_MatchTheSnapshot()
    {
        string path = Path.Combine(RepoRoot.Locate(), "data", "map_trap_formation.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var props = doc.RootElement.EnumerateObject().ToArray();

        Assert.Equal(Tuning.TrapTableMapCount, props.Length);
        Assert.All(props, p =>
        {
            int n = p.Value.GetProperty("tiles").GetArrayLength();
            Assert.True(n == 0 || n == Tuning.TrapSlots, $"map {p.Name} has {n} tiles");
        });
    }
}
