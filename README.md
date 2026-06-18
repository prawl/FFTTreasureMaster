# FFT Treasure Master

A Reloaded-II mod for **Final Fantasy Tactics: The Ivalice Chronicles** (`fft_enhanced.exe`)
that lights up the battlefield tiles hiding **Move-Find treasure**, so you never walk a unit
past a hidden item again.

It is an in-process C# runtime (`FFTTreasureMaster.dll`). On each battle map it holds the
"lit" render-flag bit (`0x80`) on every known treasure tile, keeping those tiles highlighted
for the whole fight. There is one setting: an on/off toggle in the Reloaded mod config
(**on by default**).

## Install

1. Have [Reloaded-II](https://reloaded-project.github.io/Reloaded-II/) set up for FFT: IC.
2. Drop the `prawl.fft.treasuremaster` folder into your Reloaded `Mods` directory (or install
   the release zip through the launcher).
3. Enable it in Reloaded and launch the game. No other mods are required -- it has no
   dependency on the item modloader.

To turn it off without uninstalling, open the mod's **Configure** button in Reloaded and
untick **Enable Treasure Master**.

## How it works

- `data/treasure_addrs.json` is the hand-captured source: for each battle map, the memory
  addresses of the render-flag bytes for every treasure tile, plus a per-map terrain
  fingerprint and the game build it was captured against.
- `tools/gen_treasure_db.py` bakes that into `FFTTreasureMaster/treasure.json` (the runtime's
  dataset), self-testing the hash routines and dropping any address that fails the safety
  rules.
- At runtime the DLL identifies the current map, validates it against the dataset (game-build
  key + map id + terrain fingerprint + per-address sanity), and holds the mark bit on the
  matching tiles. Every read and write goes through `ReadProcessMemory`/`WriteProcessMemory`
  on our own process handle -- never a raw pointer deref -- so a freed page degrades to a
  no-op instead of crashing the game.

The address dataset is tied to a specific game build. After a game patch the addresses must be
re-captured / re-anchored (see `tools/treasure_rebase.py` and the probes in `tools/probes/`);
the runtime detects a build mismatch and stays idle rather than writing to the wrong place.

## Build

```powershell
python tools\gen_treasure_db.py   # data/treasure_addrs.json -> FFTTreasureMaster/treasure.json (+ self-test gate)
dotnet test FFTTreasureMaster.Tests\FFTTreasureMaster.Tests.csproj   # the unit-test gate
.\BuildLinked.ps1                 # DEV: bake + test + build the DLL + deploy into the Reloaded Mods folder
.\Publish.ps1                     # PROD: bake + test + build the DLL + package the release zip
```

Both `BuildLinked.ps1` (local deploy) and `Publish.ps1` (release zip) share their prefix
(bake -> unit tests -> DLL publish, plus the required-file manifest) via `tools/pipeline.ps1`.
Two gates are enforced by both and by CI: the bake self-test and the xUnit suite.
