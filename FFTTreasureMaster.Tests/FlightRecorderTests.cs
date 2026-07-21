using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFTTreasureMaster;
using Newtonsoft.Json.Linq;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Pins the flight-recorder core (docs/LOGGING.md "Flight recorder"): the bounded ring, the
/// flush/serialize/retention contract (all IO injected, no real disk or clock), the FlushOnce
/// error latch, and the null-object Flight facade's inert-before-Init guarantee.
/// </summary>
public class FlightRecorderTests
{
    private sealed class FakeIo
    {
        public readonly List<(string Path, string Content)> Written = new();
        public readonly List<string> Deleted = new();
        public List<string> Existing = new();
        public long Clock = 1000;
        public DateTime Wall = new(2026, 7, 21, 12, 0, 0);

        public FlightRecorder Make(string modDir = @"C:\fake\mod") => new(
            modDir,
            clock: () => Clock,
            wallClock: () => Wall,
            fileWriter: (p, c) => Written.Add((p, c)),
            lister: _ => Existing,
            deleter: Deleted.Add);
    }

    [Fact]
    public void Record_then_snapshot_preserves_insertion_order_and_stamps_the_clock()
    {
        var io = new FakeIo();
        var rec = io.Make();
        io.Clock = 5;
        rec.Record("battle", "enter");
        io.Clock = 9;
        rec.Record("claim", "count=1");

        var snap = rec.Snapshot();
        Assert.Equal(2, snap.Length);
        Assert.Equal(("battle", "enter", 5L), (snap[0].Type, snap[0].Payload, snap[0].ElapsedMs));
        Assert.Equal(("claim", "count=1", 9L), (snap[1].Type, snap[1].Payload, snap[1].ElapsedMs));
    }

    [Fact]
    public void The_ring_drops_the_oldest_record_past_capacity()
    {
        var io = new FakeIo();
        var rec = io.Make();
        for (int i = 0; i < FlightRecorder.Capacity + 3; i++)
            rec.Record("n", i.ToString());

        var snap = rec.Snapshot();
        Assert.Equal(FlightRecorder.Capacity, snap.Length);
        Assert.Equal("3", snap[0].Payload);
        Assert.Equal((FlightRecorder.Capacity + 2).ToString(), snap[^1].Payload);
    }

    [Fact]
    public void Flush_writes_a_header_plus_one_json_line_per_record_then_resets_the_ring()
    {
        var io = new FakeIo();
        var rec = io.Make();
        rec.Record("battle", "enter");
        rec.Record("claim", "a \"quoted\" payload\nwith a newline");
        rec.Flush("battle-exit");

        var (path, content) = Assert.Single(io.Written);
        Assert.EndsWith("flight_20260721_120000_battle-exit.jsonl", path);
        Assert.Contains(Path.Combine("mod", "flight"), path);

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var hdr = JObject.Parse(lines[0]);
        Assert.True((bool)hdr["hdr"]!);
        var second = JObject.Parse(lines[2]);
        Assert.Equal("claim", (string)second["e"]!);
        Assert.Equal("a \"quoted\" payload\nwith a newline", (string)second["d"]!);

        Assert.Equal(0, rec.Count);
    }

    [Fact]
    public void An_empty_ring_flushes_nothing()
    {
        var io = new FakeIo();
        var rec = io.Make();
        rec.Flush("battle-exit");
        Assert.Empty(io.Written);
    }

    [Fact]
    public void The_error_trigger_is_flush_once_per_launch()
    {
        var io = new FakeIo();
        var rec = io.Make();
        rec.Record("n", "1");
        rec.RequestFlush("error");
        rec.DrainPending();
        Assert.Single(io.Written);

        rec.Record("n", "2");
        rec.RequestFlush("error");   // second error of the launch: full no-op
        rec.DrainPending();
        Assert.Single(io.Written);
    }

    [Fact]
    public void A_non_error_trigger_bypasses_the_error_latch()
    {
        var io = new FakeIo();
        var rec = io.Make();
        rec.Record("n", "1");
        rec.RequestFlush("error");
        rec.DrainPending();
        rec.Record("n", "2");
        rec.RequestFlush("standdown");
        rec.DrainPending();
        Assert.Equal(2, io.Written.Count);
        Assert.EndsWith("_standdown.jsonl", io.Written[1].Path);
    }

    [Fact]
    public void RequestFlush_alone_performs_no_io_until_DrainPending()
    {
        var io = new FakeIo();
        var rec = io.Make();
        rec.Record("n", "1");
        rec.RequestFlush("error");
        Assert.Empty(io.Written);
        rec.DrainPending();
        Assert.Single(io.Written);
    }

    [Fact]
    public void Retention_deletes_the_oldest_archives_beyond_the_cap()
    {
        var io = new FakeIo();
        io.Existing = Enumerable.Range(0, FlightRecorder.RetentionCount + 5)
            .Select(i => $@"C:\fake\mod\flight\flight_2026010{i / 10}_{i % 10:D2}0000_x.jsonl")
            .ToList();
        var rec = io.Make();
        rec.Record("n", "1");
        rec.Flush("battle-exit");

        Assert.Equal(5, io.Deleted.Count);
        var sorted = io.Existing.OrderBy(f => f, StringComparer.Ordinal).Take(5).ToList();
        Assert.Equal(sorted, io.Deleted);
    }

    [Fact]
    public void The_Flight_facade_is_inert_before_Init()
    {
        Flight.Reset();
        Flight.Record("battle", "enter");
        Flight.RequestFlush("error");
        Flight.DrainPending();
        Flight.FlushBattleStart();
        Flight.FlushBattleEnd();   // none of these may throw or touch disk
    }

    [Fact]
    public void A_logger_error_arms_the_flight_error_flush_end_to_end()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tm_flight_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Flight.Reset();
            Flight.Init(dir);
            Flight.Record("battle", "enter");
            var logger = new FileConsoleLogger(_ => { }, _ => { });
            logger.LogError(LogVerb.Engine, "The engine tick failed.");
            Flight.DrainPending();

            string flightDir = Path.Combine(dir, "flight");
            Assert.True(Directory.Exists(flightDir));
            var files = Directory.GetFiles(flightDir, "flight_*_error.jsonl");
            Assert.Single(files);
        }
        finally
        {
            Flight.Reset();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
