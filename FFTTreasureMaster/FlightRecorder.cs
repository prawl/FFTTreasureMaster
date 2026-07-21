using System;
using System.Collections.Generic;
using System.IO;

namespace FFTTreasureMaster;

/// <summary>One flight-recorder entry: a monotonic elapsed-ms stamp, an event type tag, and a
/// free-form payload. Immutable value type: copying it under the lock is a plain struct copy,
/// never a torn read.</summary>
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
/// The flight recorder's INSTANCE core (the "black box"), see docs/LOGGING.md. A bounded ring
/// (<see cref="Capacity"/> records) of on-change (elapsedMs, type, payload) events; callers
/// append via <see cref="Record"/> (cheap, lock-protected, no I/O ever happens there). The
/// static <see cref="Flight"/> facade is the null-object every production call site uses; this
/// class is the testable instance behind it: clock, wall-clock, file-writer, and retention
/// lister/deleter are all injected so the whole ring/flush/retention contract is unit-testable
/// with no real disk or clock (FlightRecorderTests). The pure serialize/retention-selection
/// half lives in FlightRecorder.Policy.cs (the 200-line refactor seam).
///
/// FLUSH SAFETY: <see cref="Flush"/> swaps the ring out UNDER the lock and does the
/// serialize/write/prune OUTSIDE it, so the recording thread is never blocked on disk I/O. A
/// flush failure is swallowed, never thrown, and never routed through ModLogger (a flush
/// triggered by an error log calling back into the error log would recurse).
/// <see cref="RequestFlush"/> only raises a pending flag; the real flush runs later from
/// <see cref="DrainPending"/>, called once per Engine tick.
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
    private bool _errorFlushArmed;   // FlushOnce latch: only the FIRST "error" request per launch flushes

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

    /// <summary>Raises a pending-flush flag ONLY; no I/O happens on this call. The "error"
    /// trigger is FlushOnce: after the first-ever error request, every later one is a full
    /// no-op, so an error storm cannot prune the retention into uselessness.</summary>
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
