using System;

namespace ModGates.Logging;

/// <summary>
/// Every verb a log line may carry. The set is closed, and the table in docs/LOGGING.md lists
/// the same verbs, so a new subsystem reuses one of these.
/// </summary>
internal enum LogVerb
{
    Startup,
    Config,
    BattleStart,
    BattleEnd,
    Arm,
    Treasure,
    Claim,
    Anchor,
    Save,
    Engine,
    Trace,
}

/// <summary>The bracket token each verb renders as.</summary>
internal static class LogVerbToken
{
    public static string Token(this LogVerb verb) => verb switch
    {
        LogVerb.Startup => "startup",
        LogVerb.Config => "config",
        LogVerb.BattleStart => "battle-start",
        LogVerb.BattleEnd => "battle-end",
        LogVerb.Arm => "arm",
        LogVerb.Treasure => "treasure",
        LogVerb.Claim => "claim",
        LogVerb.Anchor => "anchor",
        LogVerb.Save => "save",
        LogVerb.Engine => "engine",
        LogVerb.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unmapped LogVerb"),
    };
}
