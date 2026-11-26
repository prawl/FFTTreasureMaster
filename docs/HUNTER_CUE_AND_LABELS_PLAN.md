# Proposal: a visible Treasure Hunter, and setting names that say when they apply

STATUS: PROPOSAL (2026-11-26, owner asked for it after the 1.8.0 release; no code yet)

Three things from Patrick's 2026-11-26 play session:

1. "All units gain Treasure Hunter" works, but players can no longer see it. Patrick: "Yes try to
   get it back."
2. The launcher should say which settings apply live and which need a game restart, and the
   style key needs a clearer name. Patrick: "Agreed."
3. Rare treasure only gave a common High Potion on Beddha Sandwaste twice. The first time the Mod
   Loader was unticked (setup). The second time the patch had landed, but the battle was resumed
   from before it. See section 3.

## 1. Bring back a visible Treasure Hunter

### What players lost, and why

Up to 1.7.0 the setting wrote Treasure Hunter into every job's list of built in abilities, so the
job screen showed a "Treasure Hunter" tag on every job. The catch: an enemy with the same job got
it too, and enemies started grabbing tile treasure first (a player report). 1.8.0 (TM-25)
switched to a hidden bit set only on your own deployed units during battle. Pick ups still work,
as Ramza's High Potion showed, but nothing on screen says so.

### Options

**A. Put it back on the job screen, and take it away from enemies each battle (recommended).**
- The mod adds Treasure Hunter to every player job's built in abilities again (all but Chemist,
  who finds treasure on the game's own rule). The job screen looks like your "before" shot.
- In every battle, the mod removes the bonus from any unit not on your side that only got it from
  the mod: enemies and guests. A unit that equips Treasure Hunter, or whose job really has it,
  keeps it.
- Your own units keep today's per battle hold, so pick ups work even if the job grant fails.
- Two ways to write the job list:
  - A1, through the FFT Mod Loader, as 1.5.0 did. Proven in the game on 2026-07-21. Works only
    when the Mod Loader is ticked; without it, the job screen tag is missing but pick ups still
    work (today's behavior), and the log says why.
  - A2, a direct write to the game's job table when the game starts. No Mod Loader needed. The
    table's layout is known but marked UNCERTAIN, and writing it this way has never been tried,
    so a short probe comes first.
- Risks: an enemy holds the bonus for a moment at battle start, before the mod takes it back (the
  mod checks 30 times a second, and nobody moves before the first turn; a Retry rebuilds the
  battle and the mod takes it back again). Other mods that edit job tables could clash with A2.

**B. Show it in battle only.** The mod also writes Treasure Hunter into a spare built in ability
slot of your deployed units' battle records. If the in battle status screen reads that slot, the
tag shows there, with no enemy side effect and no Mod Loader. The party menu's job screen would
still not show it. Unknown whether the screen reads the slot, so it needs a one battle probe.

**C. Keep it hidden and say so** in the setting's description. No risk, but no tag.

### Recommendation

A, by way of a short probe first (one launch, one battle, about 10 minutes on the game):

1. Write Treasure Hunter into one job row directly (A2) at startup; check the job screen shows the
   tag (frame) and the row reads back (memory read).
2. Enter a battle with an enemy of that job; read the enemy's built in slots and bit before and
   after the take back (memory reads), and film it.

If step 1 works, build A2. If it does not, build A1. Either way the enemy take back is the new
part, so it gets its own tests and a live check that an enemy never holds the bonus past its
first turn.

Size: medium. The take back adds writes to enemy units, a new kind of write for this mod, so it
goes through the full build pipeline with a review.

## 2. Setting names that say when they apply

Only Tile highlight style changes while the game runs. The rest are read once at launch.

| Setting today | Proposed name in the launcher |
|---|---|
| Enable Treasure Master | Enable Treasure Master *restart required |
| Hide tiles after their treasure is claimed | same name, plus *restart required |
| All units gain Treasure Hunter | All units gain Treasure Hunter *restart required |
| Rare treasure only | Rare treasure only *restart required |
| Tile highlight style | Tile highlight style (unchanged, it applies live) |
| Tile style key | Cycle Treasure Tile Hotkey *restart required |

- The hotkey's choices read "Off", "4 - Keyboard" through "9 - Keyboard" instead of Key4 to Key9.
- Only the names on screen change. The settings file keeps the same field names and values, so
  nobody's saved settings break.
- If a setting later becomes live (TM-35), it drops its mark.
- Named "Cycle Treasure Tile Hotkey" (Duet note 673, the coordinator's call; Patrick can veto it
  when he sees the launcher). Rare treasure only gains one sentence on resumed battles.

### Better still: make most of them live (TM-35)

Patrick hit this on 2026-11-26: he unticked Hide tiles mid battle, saved, and the next claimed
tile still went dark, because the setting waits for a restart (the log said so at 05:04:24).
The mod already re-reads the settings file once a second, so most settings can follow a Save:

| Setting | Live on Save? | What a live change does |
|---|---|---|
| Cycle Treasure Tile Hotkey | yes, easy | the new key works within a second |
| Enable Treasure Master | yes | off unlights every tile and stops holding; on arms again |
| Hide tiles after their treasure is claimed | yes | off relights claimed tiles, on hides them |
| All units gain Treasure Hunter | yes | off takes the bonus back, on holds it again |
| Rare treasure only | no | the Mod Loader patch runs once at startup; keeps its mark |

Recommended order: ship the restart marks now (small), then TM-35 makes the first four live
one at a time, each dropping its mark as it ships. Only Rare treasure only keeps
"*restart required" in the end.

Already drafted and not committed: the hotkey's new name and its "4 - Keyboard" choices, with 11
new tests (769 green). Still to do: the five restart marks, the gates, then a deploy so Patrick
can see the launcher. Whether Reloaded's settings window shows the "4 - Keyboard" labels is
unproven until he looks; if it shows Key4 instead, the fallback keeps Key4.

Size: small. Release as 1.8.1 once Patrick has looked.

## 3. Rare treasure in a resumed battle (optional, small)

What happened: the game keeps the treasure list in a main table, which the mod patches at
launch, and copies one map's rows into a battle copy when the battle begins. The battle copy is
saved with the battle. A read on 2026-11-26 at about 05:05 showed Beddha Sandwaste's main rows
patched (rare 124, common 124) but the running battle's copy unpatched (rare 124, common 241,
the High Potion). That battle had started at 04:57 with the Mod Loader off, so a Continue brought
back its old copy. A fresh battle should copy the patched rows; Patrick's next fresh battle
settles it.

Proposal: when a battle shows and Rare treasure only is in effect, the mod also writes each rare
id into that battle copy's common slot (four rows, guarded writes, only where they differ), so a
resumed battle gives rare items too. Fact basis: the copy address and the rows are in
FFTGameFacts (trap-rows-copied-per-battle, trap-table-rows-name-rare-and-common-item), both
UNCERTAIN, so the build starts with a read probe like the one above. Without it, a battle begun
before the setting was on keeps its common items until it ends, which the setting's description
could simply say.

## Order

1. Section 2 first (small, mostly done).
2. Then the section 1 probe, under a game lock, about 10 minutes on the game.
3. Then section 1's build.
4. Section 3 whenever Patrick wants it, or just the description line.

Each goes on the ledger as its own Backlog row once Patrick says go.
