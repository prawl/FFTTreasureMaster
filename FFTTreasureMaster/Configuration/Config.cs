using System.ComponentModel;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Enable Treasure Master")]
    [Description("Highlight the battle tiles that hide Move-Find treasure (a yellow-fill mark is " +
                 "held on each treasure tile's render flag so it stays lit on the battlefield). " +
                 "Turn this off to disable the mod without uninstalling it. Default: on.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Hide tiles after their treasure is claimed")]
    [Description("When a treasure is claimed from a tile -- a Chemist, or any unit with the " +
                 "Treasure Hunter movement ability, picks up the hidden item -- that tile stops " +
                 "being highlighted for the rest of the battle. Also hides any tile whose Move-Find " +
                 "treasure you already collected and won in a previous battle (it does not respawn). " +
                 "Turn this off to keep every treasure tile lit. Default: on.")]
    [DefaultValue(true)]
    public bool HideClaimedTiles { get; set; } = true;

    [DisplayName("All units gain Treasure Hunter")]
    [Description("In every battle, each unit you deploy can pick up Move-Find treasure just by " +
                 "stepping on the tile, recruited monsters included (no more dragging a Chemist " +
                 "everywhere). Enemies never get it from this setting, and guests do not get it " +
                 "yet. Chemists are left as they are: they find treasure anyway, and equipping " +
                 "Treasure Hunter on one still earns its bonus. The ability does not show on the " +
                 "Formation screen. Needs Enable Treasure Master on. Changes apply the next time " +
                 "the game is launched. Default: off.")]
    [DefaultValue(false)]
    public bool AllUnitsTreasureHunter { get; set; } = false;

    [DisplayName("Rare treasure only")]
    [Description("Every Move-Find treasure tile gives its rare item instead of its common one, so " +
                 "you never have to reload for the good drop. The same effect as the community Rare " +
                 "Treasure Only mod, built in. Requires the separately installed FFT: The Ivalice " +
                 "Chronicles Mod Loader (by Nenkai); without it this setting quietly does nothing. " +
                 "If you also have the Rare Treasure Only mod enabled you can disable it; the two do " +
                 "the same thing. Other mods that change treasure items still load; this setting " +
                 "just takes the rare for every tile. Changes apply the next time the game is " +
                 "launched. Default: off.")]
    [DefaultValue(false)]
    public bool RareTreasureOnly { get; set; } = false;
}
