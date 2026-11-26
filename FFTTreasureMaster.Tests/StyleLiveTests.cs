using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFTTreasureMaster.Configuration;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// The live style rules over real temp files. Every test restores the published style.
/// </summary>
public class StyleLiveTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Scratch.Dir("tm_live_");
    private readonly FakeKeys _keys = new();
    private readonly List<string> _info = new();
    private readonly List<string> _warn = new();
    private bool _holding = true;
    private string _path;
    private readonly string _launchPath;

    public StyleLiveTests()
    {
        _launchPath = _path = Path.Combine(_dir, "Config.json");
        Write(_launchPath, T0, _ => { });
        TileStyles.Publish(TileStyle.BlueAndYellow);
    }

    public void Dispose()
    {
        TileStyles.Publish(Tuning.TileStyleDefault);
        Scratch.Sweep();
    }

    private static void Write(string path, DateTime time, Action<JsonObject> edit)
    {
        var node = JsonSerializer.SerializeToNode(new Config(), Configurable<Config>.SerializerOptions)!.AsObject();
        node["Extra"] = 7;
        edit(node);
        File.WriteAllText(path, node.ToJsonString());
        File.SetLastWriteTimeUtc(path, time);
    }

    private StyleLive Make(bool enabled = true, string? launchPath = null, Config? launch = null) =>
        new(new StyleSetup(launch ?? new Config(), () => _path, launchPath ?? _launchPath, T0),
            enabled, _keys, () => _holding, _info.Add, _warn.Add);

    private static void Ticks(StyleLive live, int n, BattleEdge edge = BattleEdge.None, bool shown = true)
    {
        for (int i = 0; i < n; i++) live.Tick(edge, shown);
    }

    private void Press(StyleLive live, bool shown = true)
    {
        _keys.Down.Add(0x34);
        live.Tick(BattleEdge.None, shown);
        _keys.Down.Clear();
        live.Tick(BattleEdge.None, shown);
    }

    private static string StyleIn(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject()["TileStyle"]!.GetValue<string>();

    [Fact]
    public void Disabled_reads_no_key_checks_nothing_and_clears_nothing()
    {
        TileStyles.SwapTo(TileStyle.Green);
        Write(_path, T0.AddMinutes(1), n => n["TileStyle"] = "Pink");
        _keys.Down.Add(0x34);
        var live = Make(enabled: false);
        Ticks(live, 100, BattleEdge.Entered);
        Assert.Empty(_keys.Reads);
        Assert.Equal(TileStyle.Green, TileStyles.Current);
        Assert.Equal(TreasureMaster.AddrState.Stale, TileStyles.Classify(0xCC));
        Assert.Empty(_info);
        Assert.Empty(_warn);
    }

    [Fact]
    public void Only_an_entered_edge_clears_the_retired_set()
    {
        var live = Make();
        TileStyles.SwapTo(TileStyle.Green);
        live.Tick(BattleEdge.Exited, true);
        live.Tick(BattleEdge.None, true);
        Assert.Equal(TreasureMaster.AddrState.Stale, TileStyles.Classify(0xCC));
        live.Tick(BattleEdge.Entered, true);
        Assert.Equal(TreasureMaster.AddrState.Foreign, TileStyles.Classify(0xCC));
    }

    [Fact]
    public void A_press_swaps_to_the_next_look_logs_and_saves_only_the_style()
    {
        var live = Make();
        Press(live);
        Assert.Equal(TileStyle.BlueAndPink, TileStyles.Current);
        Assert.Equal(new[] { "Tile style is now BlueAndPink (key 4)." }, _info);
        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal("BlueAndPink", root["TileStyle"]!.GetValue<string>());
        Assert.Equal(7, root["Extra"]!.GetValue<int>());
        Assert.True(root["Enabled"]!.GetValue<bool>());
        Assert.False(root["RareTreasureOnly"]!.GetValue<bool>());
        Assert.Empty(_warn);
    }

    [Fact]
    public void A_press_with_nothing_held_or_no_battle_does_nothing()
    {
        var live = Make();
        var before = File.ReadAllBytes(_path);
        _holding = false;
        Press(live);
        _holding = true;
        Press(live, shown: false);
        Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.Empty(_info);
    }

    [Fact]
    public void The_save_follows_the_resolver_to_a_user_file()
    {
        var live = Make();
        Press(live);
        var user = Path.Combine(_dir, "User.json");
        Write(user, T0.AddMinutes(5), _ => { });
        _path = user;
        Press(live);
        Assert.Equal("Green", StyleIn(user));
        Assert.Equal("BlueAndPink", StyleIn(_launchPath));
    }

    [Fact]
    public void An_unparsable_file_is_not_written_and_warns_once_over_two_presses()
    {
        File.WriteAllText(_path, "{ broken");
        var before = File.ReadAllBytes(_path);
        var live = Make();
        Press(live);
        Press(live);
        Assert.Equal(before, File.ReadAllBytes(_path));
        var line = Assert.Single(_warn);
        Assert.Contains("could not be saved", line);
        Assert.Contains("Unparsable", line);
        Assert.Contains("it holds for this session only.", line);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_file_that_is_not_an_object_is_not_written_and_warns(string text)
    {
        File.WriteAllText(_path, text);
        Press(Make());
        Assert.Equal(text, File.ReadAllText(_path));
        Assert.Contains("NotObject", Assert.Single(_warn));
    }

    [Fact]
    public void A_file_without_the_style_field_gains_it()
    {
        File.WriteAllText(_path, "{ \"Extra\": 7 }");
        Press(Make());
        Assert.Equal("BlueAndPink", StyleIn(_path));
        Assert.Empty(_warn);
    }

    [Fact]
    public void A_save_that_throws_warns_once_and_the_tick_does_not_throw()
    {
        var live = Make();
        var hold = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Assert.Null(Record.Exception(() => Press(live)));
            Assert.Null(Record.Exception(() => Press(live)));
        }
        finally { hold.Dispose(); }
        Assert.Single(_warn);
    }

    [Fact]
    public void A_changed_pair_swaps_once_and_logs_once()
    {
        var live = Make();
        Write(_path, T0.AddMinutes(1), n => n["TileStyle"] = "Pink");
        Ticks(live, 90);
        Assert.Equal(TileStyle.Pink, TileStyles.Current);
        Assert.Equal(new[] { "Tile style is now Pink, read from the config file." }, _info);
    }

    [Fact]
    public void An_unchanged_pair_is_not_read_and_an_older_time_counts_as_changed()
    {
        var live = Make();
        Write(_path, T0.AddMinutes(1), n => n["TileStyle"] = "Pink");
        Ticks(live, 30);
        Write(_path, T0.AddMinutes(1), n => n["TileStyle"] = "Green");
        Ticks(live, 60);
        Assert.Equal(TileStyle.Pink, TileStyles.Current);
        Assert.Single(_info);
        Write(_path, T0.AddDays(-1), n => n["TileStyle"] = "Yellow");
        Ticks(live, 30);
        Assert.Equal(TileStyle.Yellow, TileStyles.Current);
    }

    [Fact]
    public void The_launch_pair_is_not_read_again()
    {
        File.WriteAllText(_path, "{ broken");
        File.SetLastWriteTimeUtc(_path, T0);
        Ticks(Make(), 300);
        Assert.Empty(_warn);
        Assert.Empty(_info);
    }

    [Fact]
    public void Reading_the_same_style_swaps_nothing_and_logs_nothing()
    {
        var live = Make();
        Write(_path, T0.AddMinutes(1), _ => { });
        Ticks(live, 60);
        Assert.Empty(_info);
        Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
    }

    [Fact]
    public void A_failed_read_warns_once_per_pair_and_is_read_again()
    {
        var live = Make();
        File.WriteAllText(_path, "{ broken");
        File.SetLastWriteTimeUtc(_path, T0.AddMinutes(1));
        Ticks(live, 90);
        var line = Assert.Single(_warn);
        Assert.StartsWith("The config file ", line);
        Assert.EndsWith("the tile style stays BlueAndYellow.", line);
        Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
        Write(_path, T0.AddMinutes(1), n => n["TileStyle"] = "Green");
        Ticks(live, 30);
        Assert.Equal(TileStyle.Green, TileStyles.Current);
        Assert.Single(_warn);
    }

    [Fact]
    public void The_check_runs_on_the_thirtieth_tick_and_every_thirty_after()
    {
        var live = Make();
        Write(_path, T0.AddMinutes(1), n => n["TileStyle"] = "Pink");
        Ticks(live, 29);
        Assert.Equal(TileStyle.BlueAndYellow, TileStyles.Current);
        Ticks(live, 1);
        Assert.Equal(TileStyle.Pink, TileStyles.Current);
        Write(_path, T0.AddMinutes(2), n => n["TileStyle"] = "Green");
        Ticks(live, 29);
        Assert.Equal(TileStyle.Pink, TileStyles.Current);
        Ticks(live, 1);
        Assert.Equal(TileStyle.Green, TileStyles.Current);
    }

    [Fact]
    public void Other_changed_settings_log_once_per_new_set_of_values()
    {
        var live = Make();
        Write(_path, T0.AddMinutes(1), n => n["RareTreasureOnly"] = true);
        Ticks(live, 30);
        const string tail = "; those settings apply the next time the game is launched.";
        Assert.Equal(new[] { "The config file now holds RareTreasureOnly=True" + tail }, _info);
        Press(live);
        Ticks(live, 60);
        Assert.Single(_info.FindAll(i => i.StartsWith("The config file now holds")));
        Write(_path, T0.AddMinutes(3), n => { n["RareTreasureOnly"] = true; n["Enabled"] = false; });
        Ticks(live, 30);
        Assert.Contains("The config file now holds Enabled=False RareTreasureOnly=True" + tail, _info);
    }

    [Fact]
    public void A_user_file_appearing_mid_session_is_read_and_applied()
    {
        var live = Make();
        Ticks(live, 30);
        var user = Path.Combine(_dir, "User.json");
        Write(user, T0.AddMinutes(-5), n => n["TileStyle"] = "RedOutline");
        _path = user;
        Ticks(live, 30);
        Assert.Equal(TileStyle.RedOutline, TileStyles.Current);
    }
}
