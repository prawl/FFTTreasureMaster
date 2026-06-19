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
                 "being highlighted for the rest of the battle. Turn this off to keep every " +
                 "treasure tile lit for the whole battle. Default: on.")]
    [DefaultValue(true)]
    public bool HideClaimedTiles { get; set; } = true;
}
