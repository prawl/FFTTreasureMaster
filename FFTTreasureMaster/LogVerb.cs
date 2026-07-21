using System;

namespace FFTTreasureMaster;

/// <summary>
/// The closed event-verb glossary for every log line the runtime emits (the model shared with
/// the sibling FFT mods). docs/LOGGING.md commits a verb table that must match this enum
/// one-for-one; LogContractTests pins the two in lockstep. The set is CLOSED: a new subsystem
/// reuses one of these verbs, or the doc gets amended deliberately; no ad-hoc per-module
/// prefixes.
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

/// <summary>Enum member -&gt; the literal kebab-case bracket token rendered in log lines and
/// committed in docs/LOGGING.md's verb table.</summary>
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
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb,
            "unmapped LogVerb: add it to both the Token() switch and docs/LOGGING.md's verb table"),
    };
}
