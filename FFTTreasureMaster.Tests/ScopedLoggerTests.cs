using System;
using System.Collections.Generic;
using FFTTreasureMaster;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Pins the relevance gate: a ScopedLogger's Info/Warn demote to Debug (file-only by default)
/// when the module is not armed, and a throwing armed-predicate counts as unarmed. Messages use
/// GUIDs so assertions stay immune to any other test class logging through the shared static
/// facade in parallel.
/// </summary>
public class ScopedLoggerTests
{
    private sealed class CaptureLogger : ILogger
    {
        public readonly List<(LogLevel Level, LogVerb? Verb, string Message)> Lines = new();
        public LogLevel LogLevel { get; set; } = LogLevel.Debug;
        public void Log(LogVerb verb, string message) => Lines.Add((LogLevel.Info, verb, message));
        public void LogWarning(LogVerb verb, string message) => Lines.Add((LogLevel.Warning, verb, message));
        public void LogError(LogVerb verb, string message) => Lines.Add((LogLevel.Error, verb, message));
        public void LogError(LogVerb verb, string message, Exception exception) => Lines.Add((LogLevel.Error, verb, message));
        public void LogDebug(LogVerb verb, string message) => Lines.Add((LogLevel.Debug, verb, message));
        public void NoteBattleEdge() { }
    }

    private static (CaptureLogger capture, string msg) Arrange()
    {
        var capture = new CaptureLogger();
        ModLogger.Instance = capture;
        return (capture, Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Info_logs_at_info_tier_when_armed()
    {
        var (capture, msg) = Arrange();
        try
        {
            ModLogger.For(LogVerb.Claim, () => true).Info(msg);
            Assert.Contains((LogLevel.Info, (LogVerb?)LogVerb.Claim, msg), capture.Lines);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void Info_demotes_to_debug_when_unarmed()
    {
        var (capture, msg) = Arrange();
        try
        {
            ModLogger.For(LogVerb.Claim, () => false).Info(msg);
            Assert.Contains((LogLevel.Debug, (LogVerb?)LogVerb.Claim, msg), capture.Lines);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void Warn_demotes_to_debug_when_unarmed()
    {
        var (capture, msg) = Arrange();
        try
        {
            ModLogger.For(LogVerb.Arm, () => false).Warn(msg);
            Assert.Contains((LogLevel.Debug, (LogVerb?)LogVerb.Arm, msg), capture.Lines);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void A_throwing_armed_predicate_counts_as_unarmed()
    {
        var (capture, msg) = Arrange();
        try
        {
            ModLogger.For(LogVerb.Claim, () => throw new InvalidOperationException()).Info(msg);
            Assert.Contains((LogLevel.Debug, (LogVerb?)LogVerb.Claim, msg), capture.Lines);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void EventWithTrace_emits_the_info_line_plus_a_trace_debug_companion()
    {
        var (capture, msg) = Arrange();
        try
        {
            ModLogger.EventWithTrace(LogVerb.Arm, msg, "detail " + msg);
            Assert.Contains((LogLevel.Info, (LogVerb?)LogVerb.Arm, msg), capture.Lines);
            Assert.Contains((LogLevel.Debug, (LogVerb?)LogVerb.Trace, "detail " + msg), capture.Lines);
        }
        finally { ModLogger.UseNullLogger(); }
    }
}
