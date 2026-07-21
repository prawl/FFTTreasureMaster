using System;

namespace FFTTreasureMaster;

/// <summary>
/// Static logging facade (the model shared with the sibling FFT mods). Every call site in the
/// runtime logs through the typed surface here (Event/Warn/Error/Debug, the two-line WithTrace
/// helpers, or a <see cref="ScopedLogger"/> from <see cref="For"/>), never against
/// <see cref="ILogger"/> or a concrete logger directly. <see cref="Instance"/> is the test
/// seam: <see cref="Reset"/>/<see cref="UseNullLogger"/> swap it. Production defaults lazily to
/// a <see cref="FileConsoleLogger"/> rooted at the modDir last passed to <see cref="Init"/>
/// (called once from Mod.cs before anything else logs).
/// </summary>
internal static class ModLogger
{
    private static ILogger? _logger;
    private static readonly object _lock = new();
    private static string? _modDir;

    /// <summary>Called once from Mod.cs, before any other logging, so the lazily-created
    /// default logger rotates/writes treasuremaster.log in the right place.</summary>
    public static void Init(string modDir)
    {
        lock (_lock) { _modDir = modDir; _logger = null; }
    }

    /// <summary>The active logger. Lazily creates the production <see cref="FileConsoleLogger"/>
    /// on first use if nothing was injected/initialized yet.</summary>
    public static ILogger Instance
    {
        get
        {
            if (_logger == null)
                lock (_lock)
                    _logger ??= new FileConsoleLogger(_modDir ?? Environment.CurrentDirectory);
            return _logger;
        }
        set { lock (_lock) { _logger = value; } }
    }

    /// <summary>Console volume threshold passthrough -- see FileConsoleLogger's two-sink doc.</summary>
    public static LogLevel LogLevel
    {
        get => Instance.LogLevel;
        set => Instance.LogLevel = value;
    }

    // --- The typed facade. A LogVerb names the closed event-verb glossary (docs/LOGGING.md).
    // The FILE line always carries "[verb] "; the CONSOLE line drops it at Info tier only and
    // keeps it at Warning/Error tier. Debug is file-only by default. ---

    public static void Event(LogVerb verb, string message) => Instance.Log(verb, message);
    public static void Warn(LogVerb verb, string message) => Instance.LogWarning(verb, message);
    public static void Error(LogVerb verb, string message) => Instance.LogError(verb, message);
    public static void Error(LogVerb verb, string message, Exception exception) => Instance.LogError(verb, message, exception);
    public static void Debug(LogVerb verb, string message) => Instance.LogDebug(verb, message);

    /// <summary>The two-line id pattern: a clean Info console sentence, paired with a [trace]
    /// Debug companion carrying the parenthesized ids/hex/sentinels (file-only by default).</summary>
    public static void EventWithTrace(LogVerb verb, string message, string traceDetail)
    {
        Event(verb, message);
        Debug(LogVerb.Trace, traceDetail);
    }

    /// <summary>Same two-line id pattern, Warning-tier console line.</summary>
    public static void WarnWithTrace(LogVerb verb, string message, string traceDetail)
    {
        Warn(verb, message);
        Debug(LogVerb.Trace, traceDetail);
    }

    /// <summary>A logger scoped to one verb and one "is this module relevant right now"
    /// predicate: Info/Warn demote to Debug while unarmed. See <see cref="ScopedLogger"/>.</summary>
    public static ScopedLogger For(LogVerb verb, Func<bool> armed) => new(verb, armed);

    /// <summary>Console-only per-battle dedup reset: Engine calls this on both the battle-enter
    /// and battle-exit edges. The file sink is never affected.</summary>
    public static void NoteBattleEdge() => Instance.NoteBattleEdge();

    /// <summary>Test-only: drop the current logger so the next call re-creates the default.</summary>
    public static void Reset()
    {
        lock (_lock) { _logger = null; _modDir = null; }
    }

    /// <summary>Test-only: swap in the swallow-everything logger.</summary>
    public static void UseNullLogger() => Instance = NullLogger.Instance;
}
