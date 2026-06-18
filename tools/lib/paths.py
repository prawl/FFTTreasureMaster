"""Machine + repo paths shared by the treasure tools.

ROOT is the repo checkout (lib sits one level below tools/, hence parents[2]). The Steam path is
this box's install; CI (the Windows runner) never touches it -- the bake only reads files under
ROOT, and the live-push helper only uses RELOADED_MODS when a deploy already exists.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Steam install: the live Reloaded mods folder + the modloader's table templates (the treasure
# snapshot parser reads MapTrapFormationData.xml from there when re-capturing after a patch).
STEAM_FFT = Path(r"C:\Program Files (x86)\Steam\steamapps\common"
                 r"\FINAL FANTASY TACTICS - The Ivalice Chronicles")
RELOADED_MODS = STEAM_FFT / "Reloaded" / "Mods"
TABLE_DATA = RELOADED_MODS / "FFTIVC_Mod_Loader" / "TableData"
