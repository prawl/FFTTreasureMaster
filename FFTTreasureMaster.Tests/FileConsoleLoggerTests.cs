using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FFTTreasureMaster;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Pins the two-sink contract (docs/LOGGING.md): the FILE sink records every message, Debug
/// included, unconditionally; the CONSOLE sink is curated by LogLevel, deduped per battle on the
/// semantic (level, verb, message) key, and drops the "[verb]" bracket at Info tier only. Both
/// sinks are injected so no test touches the real console or filesystem.
/// </summary>
public class FileConsoleLoggerTests
{
    private static readonly Regex LineShape = new(
        @"^\[Treasure Master\] \[\d{2}:\d{2}:\d{2}\.\d{3}\] \[(DEBUG|INFO|WARN|ERROR)\] ",
        RegexOptions.Compiled);

    private static (FileConsoleLogger logger, List<string> console, List<string> file) Make(
        LogLevel level = LogLevel.Info)
    {
        var console = new List<string>();
        var file = new List<string>();
        var logger = new FileConsoleLogger(console.Add, file.Add) { LogLevel = level };
        return (logger, console, file);
    }

    [Fact]
    public void File_records_every_tier_even_when_the_console_threshold_hides_them()
    {
        var (logger, console, file) = Make(LogLevel.Error);
        logger.LogDebug(LogVerb.Trace, "debug evidence");
        logger.Log(LogVerb.Arm, "Info line.");
        logger.LogWarning(LogVerb.Arm, "Warning line.");
        logger.LogError(LogVerb.Engine, "Error line.");

        Assert.Equal(4, file.Count);
        Assert.Single(console);
        Assert.Contains("Error line.", console[0]);
    }

    [Fact]
    public void Console_hides_debug_at_the_default_info_level()
    {
        var (logger, console, file) = Make();
        logger.LogDebug(LogVerb.Trace, "file only");
        Assert.Empty(console);
        Assert.Single(file);
    }

    [Fact]
    public void Info_console_line_drops_the_verb_bracket_but_the_file_line_keeps_it()
    {
        var (logger, console, file) = Make();
        logger.Log(LogVerb.Arm, "Map 74 is armed.");

        Assert.Single(console);
        Assert.Single(file);
        Assert.Contains("[arm]", file[0]);
        Assert.DoesNotContain("[arm]", console[0]);
        Assert.Contains("Map 74 is armed.", console[0]);
    }

    [Fact]
    public void Warning_console_lines_keep_the_verb_bracket()
    {
        var (logger, console, _) = Make(LogLevel.Debug);
        logger.LogWarning(LogVerb.Claim, "Something degraded.");
        Assert.Single(console);
        Assert.Contains("[claim]", console[0]);
    }

    [Fact]
    public void Error_console_lines_keep_the_verb_bracket()
    {
        var (logger, console, _) = Make(LogLevel.Debug);
        logger.LogError(LogVerb.Claim, "Something broke.");
        Assert.Single(console);
        Assert.Contains("[claim]", console[0]);
    }

    [Fact]
    public void Debug_reaching_a_raised_console_keeps_the_verb_bracket()
    {
        var (logger, console, _) = Make(LogLevel.Debug);
        logger.LogDebug(LogVerb.Trace, "diagnostic line");
        Assert.Single(console);
        Assert.Contains("[trace]", console[0]);
    }

    [Fact]
    public void Every_line_on_both_sinks_carries_tag_timestamp_and_level()
    {
        var (logger, console, file) = Make(LogLevel.Debug);
        logger.Log(LogVerb.Arm, "Info line.");
        logger.LogDebug(LogVerb.Trace, "debug line");
        logger.LogWarning(LogVerb.Save, "Warning line.");
        logger.LogError(LogVerb.Engine, "Error line.");

        foreach (var line in console.Concat(file))
            Assert.Matches(LineShape, line);
    }

    [Fact]
    public void Console_dedups_a_repeated_line_but_the_file_never_does()
    {
        var (logger, console, file) = Make();
        logger.Log(LogVerb.Claim, "A treasure was claimed.");
        logger.Log(LogVerb.Claim, "A treasure was claimed.");

        Assert.Single(console);
        Assert.Equal(2, file.Count);
    }

    [Fact]
    public void Console_dedup_keys_on_the_semantic_identity_not_the_rendered_string()
    {
        // Two different verbs sharing one Info sentence render identical console text (the verb
        // is hidden at Info tier) but are distinct events: both must reach the console.
        var (logger, console, _) = Make();
        logger.Log(LogVerb.Arm, "The same sentence.");
        logger.Log(LogVerb.Treasure, "The same sentence.");
        Assert.Equal(2, console.Count);
    }

    [Fact]
    public void NoteBattleEdge_resets_the_console_dedup()
    {
        var (logger, console, file) = Make();
        logger.Log(LogVerb.Claim, "A treasure was claimed.");
        logger.NoteBattleEdge();
        logger.Log(LogVerb.Claim, "A treasure was claimed.");

        Assert.Equal(2, console.Count);
        Assert.Equal(2, file.Count);
    }

    [Fact]
    public void Error_with_exception_appends_a_detail_line_to_both_sinks()
    {
        var (logger, console, file) = Make();
        logger.LogError(LogVerb.Engine, "The engine tick failed.", new InvalidOperationException("boom"));

        Assert.Equal(2, file.Count);
        Assert.Equal(2, console.Count);
        Assert.Contains("InvalidOperationException", file[1]);
        Assert.Contains("boom", file[1]);
    }

    [Fact]
    public void A_throwing_console_sink_never_loses_the_file_line()
    {
        var file = new List<string>();
        var logger = new FileConsoleLogger(_ => throw new InvalidOperationException("console died"), file.Add);
        logger.Log(LogVerb.Arm, "Still recorded.");
        Assert.Single(file);
    }
}
