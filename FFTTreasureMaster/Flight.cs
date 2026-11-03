namespace FFTTreasureMaster;

/// <summary>
/// The static facade every call site uses to reach the flight recorder.
/// Every call is a silent no-op until <see cref="Init"/> builds the real
/// <see cref="FlightRecorder"/>, which Mod.cs does once right after ModLogger.Init. That
/// inertness lets every test run unmodified, since none of them call Init. The design, what is
/// captured, where files land and the retention, is in docs/LOGGING.md under "Flight recorder".
/// </summary>
internal static class Flight
{
    private static readonly object _lock = new();
    private static FlightRecorder? _core;

    /// <summary>Constructs the real recorder rooted at modDir/flight/. Called once from
    /// Mod.StartEngine, after ModLogger.Init. Every Flight.* call before this is inert.</summary>
    public static void Init(string modDir)
    {
        lock (_lock) { _core = new FlightRecorder(modDir); }
    }

    /// <summary>Append one on-change event. No-op before <see cref="Init"/>.</summary>
    public static void Record(string type, string payload) => _core?.Record(type, payload);

    /// <summary>Flag-only flush request; no I/O on the calling thread. No-op before Init.</summary>
    public static void RequestFlush(string trigger) => _core?.RequestFlush(trigger);

    /// <summary>Performs any flush <see cref="RequestFlush"/> queued. Called once per Engine tick.</summary>
    public static void DrainPending() => _core?.DrainPending();

    /// <summary>Flushes synchronously on the battle enter edge, archiving whatever the ring holds
    /// from the previous battle's tail. The enter edge is the moment that data can still be saved
    /// when a session ends in a process kill before any exit edge fires.</summary>
    public static void FlushBattleStart() => _core?.Flush("battle-start");

    /// <summary>Synchronous battle-exit flush, called from Engine's own loop thread.</summary>
    public static void FlushBattleEnd() => _core?.Flush("battle-exit");

    /// <summary>Test only. Drops the current core so a later <see cref="Init"/> starts fresh and
    /// a test that called Init does not leak a live recorder into later tests.</summary>
    internal static void Reset() { lock (_lock) { _core = null; } }
}
