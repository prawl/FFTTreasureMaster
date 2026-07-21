using System;
using System.Collections.Generic;
using System.IO;

namespace FFTTreasureMaster;

/// <summary>
/// Production <see cref="ILogger"/>. TWO-SINK semantics: the FILE sink (treasuremaster.log,
/// rotated per launch to treasuremaster.prev.log exactly as the retired Log class did) writes
/// EVERY message, Debug tier included, UNCONDITIONALLY, each line timestamped to the
/// millisecond. The CONSOLE sink only writes messages at or above <see cref="LogLevel"/>, and
/// additionally suppresses a line whose (level, verb, message) identity already appeared once
/// this battle; <see cref="NoteBattleEdge"/> resets that seen-set on both battle edges. The
/// FILE sink is never deduped: the evidence chain a live diagnosis needs is never thinner than
/// the console.
///
/// RENDERING SPLIT: the FILE line always carries the verb:
/// <c>[Treasure Master] [HH:mm:ss.fff] [LEVEL] [verb] description</c>. The CONSOLE line drops
/// the "[verb] " segment at Info tier only (subject-first prose a player reads). Warning and
/// Error console lines keep the verb (a bug-report console paste needs it for triage), and so
/// does a Debug line reaching a console raised to Debug (a diagnostic tier, not curated
/// narrative).
///
/// The console dedup key is the SEMANTIC identity (level, verb, message) computed before any
/// rendering: two different verbs sharing one Info sentence render identical console text but
/// are distinct events, and both reach the console.
/// </summary>
internal sealed class FileConsoleLogger : ILogger
{
    private const string Prefix = "[Treasure Master]";

    private readonly Action<string> _consoleSink;
    private readonly Action<string> _fileSink;
    private readonly HashSet<(LogLevel level, LogVerb verb, string message)> _consoleSeenThisBattle = new();

    /// <summary>Guards the whole body of <see cref="Write"/> and <see cref="NoteBattleEdge"/>.
    /// Two threads log here: the Engine loop's thread (all routine logging plus the battle-edge
    /// NoteBattleEdge) and the FastHold re-stamp thread (should it ever gain a log call). The
    /// seen-set is a plain HashSet, so Add and Clear racing unlocked would corrupt it; the lock
    /// also keeps one line's file and console halves from interleaving with another thread's,
    /// and keeps two concurrent File.AppendAllText calls from colliding on the file handle
    /// (a sharing-violation IOException would be swallowed and silently drop the line).</summary>
    private readonly object _gate = new();

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Production ctor: rotates modDir/treasuremaster.log to treasuremaster.prev.log
    /// once, then wires the real console + file sinks.</summary>
    public FileConsoleLogger(string modDir) : this(SafeConsoleWrite, MakeFileSink(modDir)) { }

    /// <summary>Test seam: inject fake sinks so tests never touch the real console or
    /// filesystem. Internal: FFTTreasureMaster.Tests drives this directly.</summary>
    internal FileConsoleLogger(Action<string> consoleSink, Action<string> fileSink)
    {
        _consoleSink = consoleSink;
        _fileSink = fileSink;
    }

    private static void SafeConsoleWrite(string m) { try { Console.WriteLine(m); } catch { } }

    /// <summary>Rotate any prior session's log out of the way, then return a closure appending
    /// one line per call. Rotation failures (locked file, read-only mod dir) degrade to "no
    /// file sink" rather than throwing: console logging must survive a broken deploy folder.</summary>
    private static Action<string> MakeFileSink(string modDir)
    {
        string file = Path.Combine(modDir, "treasuremaster.log");
        try
        {
            if (File.Exists(file))
                File.Move(file, Path.Combine(modDir, "treasuremaster.prev.log"), true);
        }
        catch { }
        return line => { try { File.AppendAllText(file, line + "\n"); } catch { } };
    }

    public void Log(LogVerb verb, string message) => Write(LogLevel.Info, verb, message);
    public void LogWarning(LogVerb verb, string message) => Write(LogLevel.Warning, verb, message);

    /// <summary>Also arms the flight recorder's FlushOnce error trigger: a flag-only request,
    /// no I/O on this call or thread. The Engine loop drains it on its next tick
    /// (Flight.DrainPending), so an error logged from any thread never stalls on disk I/O.</summary>
    public void LogError(LogVerb verb, string message)
    {
        Write(LogLevel.Error, verb, message);
        Flight.RequestFlush("error");
    }

    public void LogError(LogVerb verb, string message, Exception exception)
    {
        LogError(verb, message);
        if (exception != null)
            Write(LogLevel.Error, verb, $"  {exception.GetType().Name}: {exception.Message}");
    }

    public void LogDebug(LogVerb verb, string message) => Write(LogLevel.Debug, verb, message);

    /// <inheritdoc/>
    public void NoteBattleEdge()
    {
        lock (_gate) { _consoleSeenThisBattle.Clear(); }
    }

    /// <summary>The two-sink core. FILE gets every call unconditionally, always with the verb
    /// bracket. CONSOLE gets a line only when <paramref name="level"/> clears the threshold AND
    /// the (level, verb, message) identity has not already shown this battle; the console line
    /// drops the verb bracket at Info tier only.</summary>
    private void Write(LogLevel level, LogVerb verb, string message)
    {
        lock (_gate)
        {
            string levelToken = LevelToken(level);
            string body = message ?? string.Empty;
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string verbBracket = $"[{verb.Token()}] ";

            try { _fileSink($"{Prefix} [{timestamp}] [{levelToken}] {verbBracket}{body}"); } catch { }

            if (level >= LogLevel && _consoleSeenThisBattle.Add((level, verb, body)))
            {
                bool showVerbOnConsole = level != LogLevel.Info;
                string consoleLine = $"{Prefix} [{timestamp}] [{levelToken}] {(showVerbOnConsole ? verbBracket : "")}{body}";
                try { _consoleSink(consoleLine); } catch { }
            }
        }
    }

    private static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => level.ToString().ToUpperInvariant(),
    };
}
