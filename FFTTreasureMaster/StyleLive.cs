using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FFTTreasureMaster.Configuration;

namespace FFTTreasureMaster;

/// <summary>
/// Changes the tile look while the game runs, from the number key or from a saved config file.
/// </summary>
internal sealed record StyleSetup(Config Launch, Func<string> ResolvePath, string LaunchPath,
                                  DateTime LaunchWriteUtc);

internal sealed class StyleLive
{
    private readonly StyleSetup _setup;
    private readonly bool _enabled;
    private readonly StyleHotkey _hotkey;
    private readonly Func<bool> _holding;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly HashSet<string> _warned = new();
    private int _countdown = Tuning.StyleCheckTicks;
    private (string Path, DateTime Time) _lastRead;
    private (string Path, DateTime Time)? _lastWarned;
    private string _lastOthers = "";

    internal StyleLive(StyleSetup setup, bool enabled, IKeySource keys, Func<bool> holding,
                       Action<string> info, Action<string> warn)
    {
        _setup = setup;
        _enabled = enabled;
        _hotkey = new StyleHotkey(setup.Launch.StyleCycleKey, keys);
        _holding = holding;
        _info = info;
        _warn = warn;
        _lastRead = (setup.LaunchPath, setup.LaunchWriteUtc);
    }

    internal void Tick(BattleEdge edge, bool battleDisplayed)
    {
        if (!_enabled) return;
        try
        {
            if (edge == BattleEdge.Entered) TileStyles.ClearRetired();
            if (_hotkey.Poll(battleDisplayed && _holding())) Step();
            if (--_countdown > 0) return;
            _countdown = Tuning.StyleCheckTicks;
            Check();
        }
        catch (Exception ex)
        {
            WarnOnce(ex.Message, "The tile style check failed: " + ex.Message);
        }
    }

    private void Step()
    {
        var next = TileStyles.Next(TileStyles.Current);
        TileStyles.SwapTo(next);
        _info($"Tile style is now {next} (key {StyleHotkey.Digit(_setup.Launch.StyleCycleKey)}).");
        Save(next);
    }

    // One warning per file and cause, since the style differs on every press.
    private void Save(TileStyle next)
    {
        string path = "";
        try
        {
            path = _setup.ResolvePath();
            var result = ConfigFields.Replace(path, new JsonObject { ["TileStyle"] = next.ToString() });
            if (result != FieldWrite.Written) WarnSave(next, path, result.ToString());
        }
        catch (Exception ex)
        {
            WarnSave(next, path, ex.Message);
        }
    }

    private void WarnSave(TileStyle next, string path, string why) =>
        WarnOnce(path + "|" + why,
            $"The tile style {next} could not be saved to {path} ({why}); it holds for this session only.");

    private void WarnOnce(string key, string text)
    {
        if (_warned.Add(key)) _warn(text);
    }

    private void Check()
    {
        string path = _setup.ResolvePath();
        var pair = (path, File.GetLastWriteTimeUtc(path));
        if (pair == _lastRead) return;
        var read = Configurable<Config>.Read(path);
        if (read.LoadError != null)
        {
            if (_lastWarned != pair)
                _warn($"The config file {path} could not be read ({read.LoadError}); the tile style stays {TileStyles.Current}.");
            _lastWarned = pair;
            return;
        }
        _lastRead = pair;
        NoteOthers(read);
        if (read.TileStyle == TileStyles.Current) return;
        if (TileStyles.SwapTo(read.TileStyle))
            _info($"Tile style is now {read.TileStyle}, read from the config file.");
    }

    private void NoteOthers(Config read)
    {
        var l = _setup.Launch;
        var parts = new List<string>();
        if (read.Enabled != l.Enabled) parts.Add($"Enabled={read.Enabled}");
        if (read.HideClaimedTiles != l.HideClaimedTiles) parts.Add($"HideClaimedTiles={read.HideClaimedTiles}");
        if (read.AllUnitsTreasureHunter != l.AllUnitsTreasureHunter)
            parts.Add($"AllUnitsTreasureHunter={read.AllUnitsTreasureHunter}");
        if (read.RareTreasureOnly != l.RareTreasureOnly) parts.Add($"RareTreasureOnly={read.RareTreasureOnly}");
        if (read.StyleCycleKey != l.StyleCycleKey) parts.Add($"StyleCycleKey={read.StyleCycleKey}");
        var text = string.Join(" ", parts);
        if (text != _lastOthers && text.Length > 0)
            _info($"The config file now holds {text}; those settings apply the next time the game is launched.");
        _lastOthers = text;
    }
}
