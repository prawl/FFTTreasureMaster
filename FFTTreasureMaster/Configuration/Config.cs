using System.ComponentModel;

namespace FFTTreasureMaster.Configuration;

/// <summary>
/// Mod configuration. Exposed to the Reloaded-II launcher via Configurator so the
/// player can toggle settings without editing files.
/// </summary>
public class Config : Configurable<Config>
{
    [DisplayName("Enable Treasure Master")]
    [Description("Highlight the battle tiles that hide Move-Find treasure (the 0x80 mark bit is " +
                 "held on each treasure tile's render flag so it stays lit on the battlefield). " +
                 "Turn this off to disable the mod without uninstalling it. Default: on.")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;
}
