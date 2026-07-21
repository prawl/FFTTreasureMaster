namespace FFTTreasureMaster;

/// <summary>
/// Static null-object facade over <see cref="FlightRecorder"/>, mirroring
/// <see cref="ModLogger"/>'s swappable-Instance idiom: every call site in the runtime calls
/// <c>Flight.Record</c>/<c>RequestFlush</c>/<c>DrainPending</c>/<c>FlushBattleStart</c>/
/// <c>FlushBattleEnd</c> directly, never a concrete <see cref="FlightRecorder"/>. Every one of
/// those calls is a silent no-op until <see cref="Init"/> constructs the real core, called once
/// from Mod.cs right after ModLogger.Init. That inertness is what lets every pre-existing test
/// run unmodified: none of them call Init, so every Flight.* call inside the production code
/// they exercise does nothing.
///
/// See docs/LOGGING.md ("Flight recorder") for the design: what gets captured, where files
/// land, retention, and the accepted loss modes.
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

    /// <summary>Synchronous battle-ENTER flush: archives whatever the ring holds from the
    /// previous battle's tail and the inter-battle stretch. The enter edge is the reliable
    /// moment that data can still be saved when a session ends in a process kill (the usual
    /// kill-and-deploy cycle) before any exit edge fires.</summary>
    public static void FlushBattleStart() => _core?.Flush("battle-start");

    /// <summary>Synchronous battle-exit flush, called from Engine's own loop thread.</summary>
    public static void FlushBattleEnd() => _core?.Flush("battle-exit");

    /// <summary>Test-only: drop the current core so a later <see cref="Init"/> starts fresh and
    /// a test that called Init does not leak a live recorder into later tests.</summary>
    internal static void Reset() { lock (_lock) { _core = null; } }
}
