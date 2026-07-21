using System;
using System.Collections.Generic;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// TreasureHunterGrant: the pure innate-grant algorithm behind the AllUnitsTreasureHunter
/// toggle. Fed by FakeJobTable; no game memory, no modloader, no threads.
///
/// Invariants:
///   slot policy prefers the live-proven slot 3 (index 2), else the first zero slot;
///   509 anywhere in a row means skip (idempotent across sessions and against other mods);
///   full rows are skipped and reported, never overwritten;
///   toggle off means zero seam calls and zero log lines;
///   a missing seam (modloader absent) logs exactly one line;
///   the not-ready retry sleeps until ready or gives up at the cap with one line;
///   an apply is only counted once the read-back confirms 509 landed;
///   the delayed re-assert pass restores a clobbered grant and logs the conflict;
///   any seam exception is caught and logged once, never propagated.
/// </summary>
public class TreasureHunterGrantTests
{
    private const ushort TH = 509;

    private static TreasureHunterGrant Grant(
        FakeJobTable? table, List<string> log, int[] jobs,
        bool enabled = true, Action<int>? sleep = null, List<int>? sleeps = null,
        int intervalMs = 10, int capMs = 100, int reassertMs = 50)
    {
        sleep ??= ms => sleeps?.Add(ms);
        return new TreasureHunterGrant(enabled, table, jobs, log.Add, sleep,
                                       intervalMs, capMs, reassertMs);
    }

    // ── slot policy ───────────────────────────────────────────────────────────

    [Fact]
    public void Applies509_PrefersProvenSlot3()
    {
        var t = new FakeJobTable(); t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 76 }).Run();

        var call = Assert.Single(t.ApplyCalls);
        Assert.Equal((76, 2, TH), call);
        Assert.Equal(1, s.Applied);
    }

    [Fact]
    public void FallsBackToFirstZeroSlot_WhenSlot3Occupied()
    {
        var t = new FakeJobTable();
        t.Rows[93] = new ushort[] { 469, 472, 478, 0 };   // Mime: only slot 4 free
        t.Rows[75] = new ushort[] { 474, 0, 7, 0 };       // slot 3 occupied, slot 2 free
        var log = new List<string>();

        var s = Grant(t, log, new[] { 93, 75 }).Run();

        Assert.Equal(2, s.Applied);
        Assert.Contains((93, 3, TH), t.ApplyCalls);
        Assert.Contains((75, 1, TH), t.ApplyCalls);
    }

    // ── idempotency and skips ─────────────────────────────────────────────────

    [Fact]
    public void Skips_When509PresentAnywhere()
    {
        var t = new FakeJobTable();
        t.Rows[74] = new ushort[] { TH, 0, 0, 0 };
        t.Rows[75] = new ushort[] { 0, 0, TH, 0 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 74, 75 }).Run();

        Assert.Empty(t.ApplyCalls);
        Assert.Equal(2, s.AlreadyPresent);
        Assert.Equal(0, s.Applied);
    }

    [Fact]
    public void SkipsFullRows_AndNamesThemInTheSummary()
    {
        var t = new FakeJobTable(); t.Rows[15] = new ushort[] { 473, 477, 478, 470 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 15 }).Run();

        Assert.Empty(t.ApplyCalls);
        Assert.Equal(1, s.FullRows);
        var line = Assert.Single(log);
        Assert.Contains("15", line);
    }

    [Fact]
    public void DoubleRun_SecondRunAppliesNothing()
    {
        var t = new FakeJobTable(); t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var first  = Grant(t, log, new[] { 76 }).Run();
        var second = Grant(t, log, new[] { 76 }).Run();

        Assert.Equal(1, first.Applied);
        Assert.Equal(0, second.Applied);
        Assert.Equal(1, second.AlreadyPresent);
        var apply = Assert.Single(t.ApplyCalls);
        Assert.Equal((76, 2, TH), apply);
    }

    // ── gating ────────────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_ZeroSeamCalls_ZeroLogLines()
    {
        var t = new FakeJobTable(); t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 76 }, enabled: false).Run();

        Assert.Equal(0, t.SeamCalls);
        Assert.Empty(log);
        Assert.Equal(default, s);
    }

    [Fact]
    public void SeamAbsent_LogsExactlyOnce_AppliesNothing()
    {
        var log = new List<string>();

        var s = Grant(null, log, new[] { 76 }).Run();

        var line = Assert.Single(log);
        Assert.Contains("Mod Loader", line);
        Assert.Equal(0, s.Applied);
    }

    // ── readiness retry ───────────────────────────────────────────────────────

    [Fact]
    public void NotReadyThenReady_SleepsThenAppliesOnce()
    {
        var t = new FakeJobTable { NotReadyProbes = 3 };
        t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();
        var sleeps = new List<int>();

        var s = Grant(t, log, new[] { 76 }, sleeps: sleeps, intervalMs: 10, capMs: 100).Run();

        Assert.Equal(1, s.Applied);
        Assert.Equal(3, sleeps.FindAll(ms => ms == 10).Count);
    }

    [Fact]
    public void NeverReady_GivesUpAtCap_WithOneLogLine()
    {
        var t = new FakeJobTable { NeverReady = true };
        t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();
        var sleeps = new List<int>();

        var s = Grant(t, log, new[] { 76 }, sleeps: sleeps, intervalMs: 10, capMs: 30).Run();

        Assert.True(s.GaveUp);
        Assert.Equal(0, s.Applied);
        Assert.Single(log);
        Assert.Equal(0, t.GetInnatesCalls);
        Assert.Equal(3, sleeps.Count);
    }

    // ── failure semantics ─────────────────────────────────────────────────────

    [Fact]
    public void UnreadableRow_Counted_OthersStillGranted()
    {
        var t = new FakeJobTable(); t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 5, 76 }).Run();   // row 5 absent from the fake

        Assert.Equal(1, s.Unreadable);
        Assert.Equal(1, s.Applied);
    }

    [Fact]
    public void ApplyFailure_CountsFailed_AndContinues()
    {
        var t = new FakeJobTable { ApplyResult = false };
        t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        t.Rows[77] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 76, 77 }).Run();

        Assert.Equal(2, s.Failed);
        Assert.Equal(0, s.Applied);
        Assert.Equal(2, t.ApplyCalls.Count);
    }

    [Fact]
    public void ReadbackMissing_CountsFailed_NotApplied()
    {
        var t = new FakeJobTable { MutateOnApply = false };   // apply "succeeds" but nothing lands
        t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 76 }).Run();

        Assert.Equal(1, s.Failed);
        Assert.Equal(0, s.Applied);
    }

    [Fact]
    public void SeamThrows_CaughtAndLoggedOnce_NeverPropagates()
    {
        var t = new FakeJobTable { ThrowOnGetInnates = true };
        t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();

        var ex = Record.Exception(() => Grant(t, log, new[] { 76 }).Run());

        Assert.Null(ex);
        Assert.Single(log);
    }

    // ── re-assert pass ────────────────────────────────────────────────────────

    [Fact]
    public void ClobberedAfterApply_ReassertRestores_AndLogsConflict()
    {
        var t = new FakeJobTable(); t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        var log = new List<string>();
        // Clobber the granted slot during the re-assert delay (simulates another mod's
        // last-writer-wins table edit landing after ours).
        Action<int> sleep = ms => { if (ms == 50) t.Rows[76][2] = 0; };

        var s = Grant(t, log, new[] { 76 }, sleep: sleep, reassertMs: 50).Run();

        Assert.Equal(1, s.Applied);
        Assert.Equal(1, s.Reasserted);
        Assert.Equal(TH, t.Rows[76][2]);
        Assert.Equal(2, log.Count);   // summary + conflict
    }

    [Fact]
    public void NormalRun_LogsExactlyOneSummaryLine()
    {
        var t = new FakeJobTable();
        t.Rows[76] = new ushort[] { 0, 0, 0, 0 };
        t.Rows[78] = new ushort[] { 472, 0, 0, 0 };
        t.Rows[15] = new ushort[] { 473, 477, 478, 470 };
        var log = new List<string>();

        var s = Grant(t, log, new[] { 76, 78, 15, 999 }).Run();

        Assert.Single(log);
        Assert.Equal(2, s.Applied);
        Assert.Equal(1, s.FullRows);
        Assert.Equal(1, s.Unreadable);
    }
}
