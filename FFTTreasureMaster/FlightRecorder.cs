using System;
using System.Collections.Generic;
using System.IO;

namespace FFTTreasureMaster;

/// <summary>One flight recorder entry, with a monotonic elapsed ms stamp, an event type tag and
/// a free form payload. An immutable struct, so copying it under the lock is never a torn
/// read.</summary>
internal readonly struct FlightRecord
{
    public readonly long ElapsedMs;
    public readonly string Type;
    public readonly string Payload;

    public FlightRecord(long elapsedMs, string type, string payload)
    {
        ElapsedMs = elapsedMs;
        Type = type;
        Payload = payload;
    }
}

/// <summary>
/// The flight recorder's instance core, the black box described in docs/LOGGING.md.
/// A bounded ring of on change events that callers append to through <see cref="Record"/>, which
/// is cheap, lock protected and never touches disk. The static <see cref="Flight"/> facade is
/// what production calls. The clock, the file writer and the retention lister are injected, so
/// the whole ring, flush and retention contract is unit tested with no real disk or clock. The
/// pure half lives in FlightRecorder.Policy.cs.
///
/// <see cref="Flush"/> swaps the ring out under the lock and writes outside it, so the recording
/// thread never waits on disk. A flush failure is swallowed and never routed through ModLogger,
/// because a flush triggered by an error log calling back into the error log would recurse.
/// <see cref="RequestFlush"/> only raises a flag, and <see cref="DrainPending"/> does the work
/// once per Engine tick.
/// </summary>
internal sealed partial class FlightRecorder
{
    internal const int Capacity = 4096;
    internal const int RetentionCount = 20;

    private readonly string _flightDir;
    private readonly Func<long> _clock;
    private readonly Func<DateTime> _wallClock;
    private readonly Action<string, string> _fileWriter;
    private readonly Func<string, IEnumerable<string>> _lister;
    private readonly Action<string> _deleter;

    private readonly object _lock = new();
    private readonly FlightRecord[] _ring = new FlightRecord[Capacity];
    private int _head;    // next write index
    private int _count;   // records currently held (<= Capacity)

    private bool _pendingFlush;
    private string _pendingTrigger = "";
    private bool _errorFlushArmed;   // only the first error request per launch flushes

    /// <param name="modDir">Mod deployment directory; flushes land under modDir/flight/.</param>
    /// <param name="clock">Monotonic elapsedMs source. Default Environment.TickCount64.</param>
    /// <param name="wallClock">Wall-clock provider for the per-file header anchor. Default DateTime.Now.</param>
    /// <param name="fileWriter">(path, content) whole-file write. Default real IO.</param>
    /// <param name="lister">Existing flight_*.jsonl files under a directory. Default real IO.</param>
    /// <param name="deleter">Deletes one file by path. Default real IO.</param>
    public FlightRecorder(string modDir, Func<long>? clock = null, Func<DateTime>? wallClock = null,
                           Action<string, string>? fileWriter = null,
                           Func<string, IEnumerable<string>>? lister = null, Action<string>? deleter = null)
    {
        _flightDir = Path.Combine(modDir, "flight");
        _clock = clock ?? (() => Environment.TickCount64);
        _wallClock = wallClock ?? (() => DateTime.Now);
        _fileWriter = fileWriter ?? DefaultWrite;
        _lister = lister ?? DefaultList;
        _deleter = deleter ?? DefaultDelete;
    }

    /// <summary>Records currently held in the ring (test/diagnostic convenience).</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Append one on-change event. Cheap, lock-protected, no I/O; safe from any
    /// thread. Past <see cref="Capacity"/> the oldest record is silently dropped.</summary>
    public void Record(string type, string payload)
    {
        lock (_lock)
        {
            _ring[_head] = new FlightRecord(_clock(), type ?? "", payload ?? "");
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Ring contents in insertion (oldest-first) order, without clearing. Test seam
    /// only; production code never peeks, it only flushes.</summary>
    internal FlightRecord[] Snapshot() { lock (_lock) return Linearize(); }

    /// <summary>Flush the ring to a new .jsonl file under modDir/flight/, then prune retention.
    /// Grabs a linearized copy and resets the live ring UNDER the lock, then serializes, writes,
    /// and prunes OUTSIDE it. An empty ring flushes nothing (no file written). Never throws and
    /// never calls ModLogger; every failure here is swallowed.</summary>
    public void Flush(string trigger)
    {
        FlightRecord[] snapshot;
        lock (_lock)
        {
            if (_count == 0) return;
            snapshot = Linearize();
            _head = 0;
            _count = 0;
        }
        try
        {
            DateTime wall = _wallClock();
            long flushElapsed = _clock();
            string path = Path.Combine(_flightDir, $"flight_{wall:yyyyMMdd_HHmmss}_{SafeTrigger(trigger)}.jsonl");
            _fileWriter(path, Serialize(snapshot, wall, flushElapsed));
            PruneRetention();
        }
        catch { /* swallow: a flush failure must never throw or re-enter the logger */ }
    }

    /// <summary>Raises the pending flush flag and does no I/O. The error trigger fires once per
    /// launch, so an error storm cannot prune the retention into uselessness.</summary>
    public void RequestFlush(string trigger)
    {
        lock (_lock)
        {
            if (trigger == "error")
            {
                if (_errorFlushArmed) return;
                _errorFlushArmed = true;
            }
            _pendingFlush = true;
            _pendingTrigger = trigger;
        }
    }

    /// <summary>Performs whatever flush <see cref="RequestFlush"/> queued. Called from Engine's
    /// own loop tick, never from the thread that requested it. Cheap flag check when idle.</summary>
    public void DrainPending()
    {
        bool due;
        string trigger;
        lock (_lock)
        {
            due = _pendingFlush;
            trigger = _pendingTrigger;
            _pendingFlush = false;
        }
        if (due) Flush(trigger);
    }

    // ---- internals ----

    private void PruneRetention()
    {
        try
        {
            var files = _lister(_flightDir) ?? Array.Empty<string>();
            foreach (var f in SelectForDeletion(files, RetentionCount))
            {
                try { _deleter(f); } catch { }
            }
        }
        catch { }
    }

    private static void DefaultWrite(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    private static IEnumerable<string> DefaultList(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "flight_*.jsonl") : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void DefaultDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
