using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FFTTreasureMaster;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// The logging contract's enforcement gate (docs/LOGGING.md), the same walk-up-from-the-test-bin
/// idiom as TodoContractTests. Four source-scan checks over FFTTreasureMaster/*.cs:
///
/// 1. The LogVerb enum matches docs/LOGGING.md's committed verb table one-for-one: the doc and
///    the code cannot drift.
/// 2. No production file bypasses the typed facade: the retired Log shim (Log.Info/Log.Error/
///    Log.Init) must never come back, and Console.* writes live only inside FileConsoleLogger.
/// 3. No string literal passed to a facade call contains a double-dash separator or an em dash.
/// 4. Console-eligible messages (Event/Warn/EventWithTrace/WarnWithTrace and ScopedLogger
///    Info/Warn) pass the subject-first lexical fence: open with an uppercase letter or an
///    interpolation hole, never a bare "Word:" leader.
/// </summary>
public class LogContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "LOGGING.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "FFTTreasureMaster")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/LOGGING.md + FFTTreasureMaster/) not found above the test bin dir");
    }

    private static IEnumerable<string> SourceFiles(string repoRoot)
    {
        string root = Path.Combine(repoRoot, "FFTTreasureMaster");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")));
    }

    /// <summary>The facade's own plumbing: the only files allowed to touch raw sinks.</summary>
    private static readonly HashSet<string> PermanentAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModLogger.cs", "FileConsoleLogger.cs", "NullLogger.cs",
    };

    // --- 1. LogVerb <-> docs/LOGGING.md lockstep ---

    private static readonly Regex VerbTableRowRegex = new(@"^\|\s*`([a-z][a-z-]*)`\s*\|", RegexOptions.Compiled);

    private static List<string> ParseVerbTokensFromLoggingMd(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "docs", "LOGGING.md");
        var verbs = new List<string>();
        bool inTable = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            if (!inTable)
            {
                if (raw.StartsWith("| Verb |")) inTable = true;
                continue;
            }
            if (raw.StartsWith("|---")) continue;
            var m = VerbTableRowRegex.Match(raw);
            if (m.Success) verbs.Add(m.Groups[1].Value);
            else if (!raw.StartsWith("|")) break;
        }
        return verbs;
    }

    [Fact]
    public void LogVerb_enum_matches_the_committed_LOGGING_md_verb_table_one_for_one()
    {
        var docVerbs = ParseVerbTokensFromLoggingMd(RepoRoot());
        Assert.NotEmpty(docVerbs);
        var enumVerbs = Enum.GetValues<LogVerb>().Select(v => v.Token()).ToList();

        Assert.Equal(docVerbs.Distinct().Count(), docVerbs.Count);
        Assert.Equal(enumVerbs.Distinct().Count(), enumVerbs.Count);

        var docSet = new HashSet<string>(docVerbs);
        var enumSet = new HashSet<string>(enumVerbs);
        Assert.True(docSet.SetEquals(enumSet),
            $"docs/LOGGING.md verb table and LogVerb are out of lockstep. " +
            $"In doc but not enum: [{string.Join(", ", docSet.Except(enumSet))}]. " +
            $"In enum but not doc: [{string.Join(", ", enumSet.Except(docSet))}].");
    }

    // --- 2. No raw sink outside the facade ---

    private static readonly Regex RetiredLogShimRegex = new(@"\bLog\.(Info|Error|Init)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ConsoleWriteRegex = new(@"\bConsole\.(WriteLine|Write|Error)\b", RegexOptions.Compiled);

    [Fact]
    public void The_retired_Log_shim_has_no_call_sites_and_never_comes_back()
    {
        var offenders = ScanProduction(RetiredLogShimRegex, exempt: null);
        Assert.True(offenders.Count == 0,
            "Files calling the retired Log shim (route through ModLogger.Event/Warn/Error/Debug instead):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Console_writes_live_only_inside_the_file_console_logger()
    {
        var offenders = ScanProduction(ConsoleWriteRegex, exempt: PermanentAllowList);
        Assert.True(offenders.Count == 0,
            "Files writing to the console outside the facade plumbing:\n" + string.Join("\n", offenders));
    }

    private static List<string> ScanProduction(Regex pattern, HashSet<string>? exempt)
    {
        var offenders = new List<string>();
        foreach (var path in SourceFiles(RepoRoot()))
        {
            string name = Path.GetFileName(path);
            if (exempt != null && exempt.Contains(name)) continue;
            if (pattern.IsMatch(File.ReadAllText(path))) offenders.Add(name);
        }
        return offenders;
    }

    // --- 3. No double-dash / em dash inside a facade call's string literals ---

    private static readonly Regex StringLiteralRegex = new(@"\$?@?""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);
    private const char EmDash = '\u2014';  // the em dash by escape: the repo bans the literal character

    /// <summary>Finds every facade call (ModLogger.Event/Warn/Error/Debug/EventWithTrace/
    /// WarnWithTrace, or a ScopedLogger's Info/Warn/Debug) and returns the string-literal
    /// contents inside each call's argument list. Balances parens and skips string bodies so
    /// interpolation holes cannot desync the scan.</summary>
    internal static List<string> FacadeCallStringLiterals(string source)
    {
        var results = new List<string>();
        var callStart = new Regex(@"\bModLogger\.(Event|Warn|Error|Debug|EventWithTrace|WarnWithTrace)\s*\(|\b(?<recv>\w+)\.(Info|Warn|Debug)\s*\(");
        foreach (Match m in callStart.Matches(source))
        {
            string? args = ExtractBalancedArgs(source, m.Index);
            if (args == null) continue;
            foreach (Match lit in StringLiteralRegex.Matches(args))
                results.Add(lit.Value);
        }
        return results;
    }

    [Fact]
    public void FacadeCallStringLiterals_detects_a_double_dash_separator()
    {
        var literals = FacadeCallStringLiterals("ModLogger.Event(LogVerb.Claim, \"claimed -- at (7,6)\");");
        Assert.Contains(literals, l => l.Contains(" -- "));
    }

    [Fact]
    public void FacadeCallStringLiterals_detects_an_em_dash()
    {
        var literals = FacadeCallStringLiterals($"ModLogger.Warn(LogVerb.Save, \"corrupt{EmDash}falling back\");");
        Assert.Contains(literals, l => l.Contains(EmDash));
    }

    [Fact]
    public void FacadeCallStringLiterals_passes_a_clean_call()
    {
        var literals = FacadeCallStringLiterals("ModLogger.Event(LogVerb.Claim, \"A treasure was claimed at (7,6).\");");
        Assert.DoesNotContain(literals, l => l.Contains(" -- ") || l.Contains(EmDash));
    }

    [Fact]
    public void No_facade_call_in_the_repo_passes_a_string_literal_with_a_double_dash_or_em_dash()
    {
        var violations = new List<string>();
        foreach (var path in SourceFiles(RepoRoot()))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name)) continue;
            foreach (var lit in FacadeCallStringLiterals(File.ReadAllText(path)))
                if (lit.Contains(" -- ") || lit.Contains(EmDash))
                    violations.Add($"{name}: {lit}");
        }
        Assert.True(violations.Count == 0, "Facade calls with a disallowed separator:\n" + string.Join("\n", violations));
    }

    // --- 4. Subject-first lexical fence (console-eligible facade literals only) ---

    private static readonly Regex LeaderPrefixRegex = new(@"^[A-Za-z][A-Za-z-]*:", RegexOptions.Compiled);

    /// <summary>Extracts the raw MESSAGE argument text of every console-eligible facade call:
    /// ModLogger.Event/Warn/EventWithTrace/WarnWithTrace carry the verb first (message is arg 1);
    /// a ScopedLogger's Info/Warn take only the message (arg 0). Only literals that visibly open
    /// with a quote are returned; a variable argument cannot be lexically assessed.</summary>
    internal static List<string> ConsoleEligibleMessageLiterals(string source)
    {
        var results = new List<string>();
        CollectMessageLiterals(source, new Regex(@"\bModLogger\.(Event|Warn|EventWithTrace|WarnWithTrace)\s*\("), 1, results);
        CollectMessageLiterals(source, new Regex(@"\b(?<recv>\w+)\.(Info|Warn)\s*\("), 0, results, excludeReceiver: "ModLogger");
        return results;
    }

    private static void CollectMessageLiterals(string source, Regex callStart, int argIndex,
        List<string> results, string? excludeReceiver = null)
    {
        foreach (Match m in callStart.Matches(source))
        {
            if (excludeReceiver != null && m.Groups["recv"].Success && m.Groups["recv"].Value == excludeReceiver) continue;
            string? args = ExtractBalancedArgs(source, m.Index);
            if (args == null) continue;
            var parts = SplitTopLevelArgs(args);
            if (argIndex >= parts.Count) continue;
            string arg = parts[argIndex].Trim();
            if (arg.StartsWith("$\"") || arg.StartsWith("\""))
                results.Add(arg);
        }
    }

    private static string? ExtractBalancedArgs(string source, int matchIndex)
    {
        int openParen = source.IndexOf('(', matchIndex);
        if (openParen < 0) return null;
        int depth = 1;
        int i = openParen + 1;
        int argsStart = i;
        for (; i < source.Length && depth > 0; i++)
        {
            char c = source[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
            }
        }
        if (depth != 0) return null;
        return source.Substring(argsStart, i - argsStart - 1);
    }

    private static List<string> SplitTopLevelArgs(string args)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '(' || c == '{' || c == '[') depth++;
            else if (c == ')' || c == '}' || c == ']') depth--;
            else if (c == '"')
            {
                i++;
                while (i < args.Length && args[i] != '"')
                {
                    if (args[i] == '\\') i++;
                    i++;
                }
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(args.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(args.Substring(start));
        return parts;
    }

    /// <summary>The lexical fence: after stripping the literal markers the message must open with
    /// an uppercase letter or an interpolation hole, and must not open with a bare "Word:" leader
    /// (the old prefix style: "treasure:", "claim:", "diag/...").</summary>
    internal static bool PassesSubjectFirstFence(string literal)
    {
        string body = literal.StartsWith("$\"") ? literal.Substring(2)
            : literal.StartsWith("\"") ? literal.Substring(1)
            : literal;
        if (body.Length == 0) return false;
        char first = body[0];
        if (first == '{') return true;
        if (!char.IsUpper(first)) return false;
        return !LeaderPrefixRegex.IsMatch(body);
    }

    [Theory]
    [InlineData("\"Map 74 is armed: 4 treasure tiles held lit.\"", true)]
    [InlineData("$\"{count} tiles are claimed.\"", true)]
    [InlineData("\"treasure: map 74 armed\"", false)]
    [InlineData("\"Armed: map 74\"", false)]
    [InlineData("\"waiting to arm\"", false)]
    public void PassesSubjectFirstFence_lexical_cases(string literal, bool expected)
        => Assert.Equal(expected, PassesSubjectFirstFence(literal));

    [Fact]
    public void No_console_eligible_facade_call_in_the_repo_opens_with_a_bare_leader_word()
    {
        var violations = new List<string>();
        foreach (var path in SourceFiles(RepoRoot()))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name)) continue;
            foreach (var lit in ConsoleEligibleMessageLiterals(File.ReadAllText(path)))
                if (!PassesSubjectFirstFence(lit))
                    violations.Add($"{name}: {lit}");
        }
        Assert.True(violations.Count == 0,
            "Console-eligible facade calls failing the subject-first lexical fence:\n" + string.Join("\n", violations));
    }
}
