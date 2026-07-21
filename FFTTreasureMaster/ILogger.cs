using System;

namespace FFTTreasureMaster;

/// <summary>
/// Logging contract for the runtime (the model shared with the sibling FFT mods).
/// <see cref="ModLogger"/> is the static facade every call site uses;
/// <see cref="FileConsoleLogger"/> is the production implementation and <see cref="NullLogger"/>
/// the test-only swallow-everything one. Every entry point is verb-aware: the old free-form
/// Log.Info/Log.Error shim was retired in the same change that introduced this contract, so
/// there is no verb-less legacy surface to ratchet down.
/// </summary>
internal interface ILogger
{
    /// <summary>Console volume threshold -- see <see cref="FileConsoleLogger"/> for the two-sink
    /// semantics (the file evidence chain ignores this entirely).</summary>
    LogLevel LogLevel { get; set; }

    /// <summary>Info tier. File: "[LEVEL] [verb] message". Console: "[LEVEL] message" (the verb
    /// bracket is dropped at Info tier only; subject-first prose).</summary>
    void Log(LogVerb verb, string message);

    /// <summary>Warning tier. Both sinks carry "[verb] " (a bug-report console paste needs the
    /// verb for triage).</summary>
    void LogWarning(LogVerb verb, string message);

    /// <summary>Error tier. Both sinks carry "[verb] "; also arms the flight recorder's
    /// FlushOnce error trigger in the production implementation.</summary>
    void LogError(LogVerb verb, string message);

    /// <summary>Error tier with exception detail appended as a second line.</summary>
    void LogError(LogVerb verb, string message, Exception exception);

    /// <summary>Debug tier: file-always, console only when the threshold is raised to Debug
    /// (and then the verb bracket rides along).</summary>
    void LogDebug(LogVerb verb, string message);

    /// <summary>Resets the console-only per-battle dedup seen-set. Called from Engine on both
    /// battle edges via <see cref="ModLogger.NoteBattleEdge"/>; the file sink is never deduped.</summary>
    void NoteBattleEdge();
}

/// <summary>Verbosity tiers, low (most verbose) to high. A configured <see cref="LogLevel"/> of
/// N allows console output for any call at tier &gt;= N; None (4) silences the console
/// entirely. The file sink ignores this and records everything.</summary>
internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4,
}
