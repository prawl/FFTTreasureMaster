using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Enable Treasure Master *restart")]
    [Description("Needs a game restart. " +
                 "Highlight the battle tiles that hide Move-Find treasure (the mark of the chosen Tile " +
                 "highlight style is held on each treasure tile's render flag so it stays lit). " +
                 "Turn this off to disable the mod without uninstalling it. Default: on.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Tile highlight style")]
    [Description("How a treasure tile is lit. BlueAndYellow is a cyan border with a yellow fill, the " +
                 "default and the mod's look until now. BlueAndPink is a cyan border with a magenta " +
                 "fill. Green is a green fill. Yellow is a yellow fill with no border. Pink is a pink " +
                 "fill. RedOutline is a red dotted outline. A saved change shows in the running " +
                 "game within about a second, and the Cycle Tile Hotkey steps through the looks in " +
                 "battle. Default: BlueAndYellow.")]
    [DefaultValue(TileStyle.BlueAndYellow)]
    [JsonConverter(typeof(TileStyleConverter))]
    public TileStyle TileStyle { get; set; } = Tuning.TileStyleDefault;

    [DisplayName("Cycle Tile Hotkey *restart")]
    [Description("Needs a game restart. " +
                 "In battle, this top row number key steps the treasure tiles through the looks of " +
                 "Tile highlight style and saves the pick for next time. It works only while the game " +
                 "window is in front, a battle map is on screen and no Shift, Ctrl, Alt or Windows key " +
                 "is held; the number pad does not count. Off turns the key off. Default: Key4.")]
    [DefaultValue(StyleCycleKey.Key4)]
    [JsonConverter(typeof(StyleCycleKeyConverter))]
    public StyleCycleKey StyleCycleKey { get; set; } = Tuning.StyleCycleKeyDefault;

    [DisplayName("Hide claimed tiles *restart")]
    [Description("Needs a game restart. " +
                 "When a treasure is claimed from a tile -- a Chemist, or any unit with the " +
                 "Treasure Hunter movement ability, picks up the hidden item -- that tile stops " +
                 "being highlighted for the rest of the battle. Also hides any tile whose Move-Find " +
                 "treasure you already collected and won in a previous battle (it does not respawn). " +
                 "Turn this off to keep every treasure tile lit. Default: on.")]
    [DefaultValue(true)]
    public bool HideClaimedTiles { get; set; } = true;

    [DisplayName("All units Treasure Hunter *restart")]
    [Description("Needs a game restart. " +
                 "In every battle, the units you deploy can pick up Move-Find treasure just by " +
                 "stepping on the tile, recruited monsters included (no more dragging a Chemist " +
                 "everywhere). Enemies never get it from this setting, guests do not get it yet, " +
                 "and a unit whose movement abilities look changed by another mod is skipped and " +
                 "named in the mod's log. Chemists are left as they are: they find treasure " +
                 "anyway, and equipping Treasure Hunter on one still earns its bonus. Enemy " +
                 "Chemists can still pick up treasure by the game's own rule. The ability does " +
                 "not show on the Formation screen. Needs Enable Treasure Master on. Default: off.")]
    [DefaultValue(false)]
    public bool AllUnitsTreasureHunter { get; set; } = false;

    [DisplayName("Rare treasure only *restart")]
    [Description("Needs a game restart. " +
                 "Every Move-Find treasure tile gives its rare item instead of its common one, so " +
                 "you never have to reload for the good drop. The same effect as the community Rare " +
                 "Treasure Only mod, built in. Requires the separately installed FFT: The Ivalice " +
                 "Chronicles Mod Loader (by Nenkai); without it this setting quietly does nothing. " +
                 "If you also have the Rare Treasure Only mod enabled you can disable it; the two do " +
                 "the same thing. Other mods that change treasure items still load; this setting " +
                 "just takes the rare for every tile. A battle begun before this setting took effect keeps " +
                 "its normal items, even when resumed with Continue; new battles give the rare ones. " +
                 "Default: off.")]
    [DefaultValue(false)]
    public bool RareTreasureOnly { get; set; } = false;
}
