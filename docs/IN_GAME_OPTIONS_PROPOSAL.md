# Proposal: Treasure Master's settings inside the game's own Options screen

STATUS: PROPOSAL (2026-11-26, Patrick asked for it after spotting Ivalice ReMixed's in-game tab;
no code, nothing run in the game)

## The short version

- **What ReMixed does.** It adds a seventh tab, "ReMixed", to the game's Settings screen. The first
  row picks a group, and the rows under it are that group's options. Each save keeps its own picks.
- **Can Treasure Master do the same?** Yes, technically. But it is a big step. It would be TM's
  first code that cuts into the game's own menu functions, it needs new table files, and it may
  fight ReMixed for the only free tab slot in the game.
- **Recommendation: not yet.** Today five of TM's six settings need a game restart. An in-game
  switch that does nothing until the next launch is a trap. Do TM-35 first, which makes settings
  apply live. Then run two short proofs, one static read and one game check of about 15 minutes.
  Pick the shape only after that: TM rows inside an existing tab, or one shared tab for all of
  Patrick's mods.
- **What this rests on.** A static read of ReMixed 0.9.40 (its decompiled DLL and its data files)
  and Patrick's screenshot. Nothing ran. Each claim below says whether it was read or is unknown.

Source: `~/Downloads/Ivalice ReMixed 0.9.40 187 0.9.40 2026-11-26T06-59Z qyIWDoUhc.zip`, mod id
`fftivc.gameplay.ivaliceremixed`, needs the Mod Loader, the shared hooks mod and Event Instruction
Upgrade.

## 1. How ReMixed does it (read from its files)

It has three parts.

### a) Table data, shipped through the Mod Loader

- **A new tab.** ReMixed edits one row of the game's ConfigCategory table (the list of Settings
  tabs). That row names the tab with a text id and lists the eleven option rows the tab shows.
- **Its own texts.** Every tab name, row name, help line and choice is a new row in the game's UI
  text table, in English only.
- **Borrowed option rows.** It ships no option rows of its own. The game has option ids 0 to 60,
  and some exist in the table that no vanilla tab shows. ReMixed borrows eleven of them. At startup
  it points each one at a row it builds in memory.
- The Mod Loader merges table edits cell by cell, so two mods that edit different rows get along.
  Two mods that edit the same row are reported as a conflict.

### b) Code: five hooks on the Settings screen

- The DLL finds eight spots in the game's code by byte pattern and hooks five of them:
  1. **Tab list built.** It adds its tab, but only while fewer than 7 tabs exist.
  2. **Row drawn.** It draws its own rows.
  3. **Item changed.** It swallows changes to its rows, so the game never stores them as its own.
  4. **Row count.** It sets how many rows its tab shows (the group's size plus one).
  5. **Restore Defaults.** It puts its own rows back.
- **The drawing trick.** The game only knows how to draw its own options. To draw one of its rows,
  ReMixed briefly disguises it as the game's Fast-forward option. It swaps in a choice list it
  built, lets the game draw, then puts Fast-forward's real values back.
- **The group picker.** Row one cycles through the groups ("Levels, Jobs & Gear" and so on).
  Picking a new group makes the game rebuild the tab with that group's rows.
- **It fails soft.** If any pattern, table row or text is missing, the tab is not added and the
  log says why.

### c) Where the values live

- **Per save.** Most options go into the game's event variables, the story values every save file
  carries, along with a marker bit that says "this save has its own". That is why the screenshot
  says "Each save keeps its own".
- **For every save.** Options marked as mod-wide (in 0.9.40 only its Fast-forward style) go to its
  Reloaded `Config.json`.
- **TM would need only the mod-wide kind.** TM already writes `Config.json` one field at a time
  (`ConfigFields`, from TM-33) and re-reads it once a second (`StyleLive`).

## 2. What it would mean for Treasure Master

| Piece | ReMixed | TM today | New for TM |
|---|---|---|---|
| Hooks into game functions | five on this screen | none, TM only polls memory every 33 ms | first hooks, and a hard dependency on `reloaded.sharedlib.hooks` (Living Weapons already uses it) |
| Memory access | raw pointers inside the hooks | every read and write goes through guarded `Mem` | a clash with the house rule (see below) |
| Table files | edits shipped as nxd files | none shipped | a source file plus a bake step, since nxd files are generated (Living Weapons has `tools/lib/nxd.py` and `nxd_patch.py`) |
| File size | the page is one 628-line file | 200-line gate per file | about five files along real seams: page model, table setup, row drawing, change and default handling, hook install |
| Tests | a test recorder run live | unit tests behind `IGameMemory` | the page model, choice cycling and the Config write can be unit tested. The hooks themselves only prove out in the game. Living Weapons' hook tests are the family template |
| After a game patch | hooks re-found by pattern, fail soft | anchors re-resolve the data addresses | the hooks need their own patterns and the same build-key guard |

**The guarded-memory rule needs Patrick's ruling.** Hook code runs on the game's own thread and
gets handed raw pointers to the Settings screen. Reading that screen through `Mem` is possible,
and slower but fine for a menu. But the drawing trick calls the game's own draw function with a
swapped list. `Mem` cannot guard that. The choice is a narrow written exception for this page
alone, or no in-game page.

## 3. Living next to ReMixed (the main risk)

- **The tab limit (unknown).** ReMixed adds its tab only while fewer than 7 exist. Vanilla has 6:
  Gameplay, Control Assignments, Keyboard, Display, Sound and Optional Subtitles. ReMixed makes 7.
  By its field layout, the screen's tab list has room for more, so a static read cannot say whether
  7 is the game's limit or only ReMixed's own. If it is the game's limit, the whole game has one
  spare tab. Whichever mod hooks first takes it, and the other loses its tab without a word.
- **Borrowed option rows.** Vanilla tabs list 41 of the ids from 0 to 60, and ReMixed takes 11
  more. That leaves nine: 15, 32, 33, 44, 45 and 57 to 60. Still unchecked: whether each one has a
  row in the table, and whether the game's code uses any of them elsewhere. ReMixed skipped 32 and
  33, and the reason is unknown.
- **Text ids.** There is no shared registry. ReMixed uses ids 4050 to 4212. TM would pick a range
  far from that, because a clash shows up as a Mod Loader conflict.
- **Shared hooks.** Both mods would hook the same five functions. Reloaded chains hooks, and each
  mod acts only on its own rows, so they should pass each other through. But two nested Fast-forward
  disguises have never been tried.
- **Fast-forward's own value.** While a mod row is being drawn, Fast-forward's stored value is
  swapped out and back. Anything that reads it in that instant, such as Patrick's custom
  fast-forward mod, could see the wrong value.

## 4. Other risks

- **A crash kills the game.** A memory fault inside a hook runs on the game's thread. .NET cannot
  catch it, so the game closes. ReMixed catches ordinary errors, but its raw reads can still fault.
- **Other languages (likely, unchecked).** The UI text table is kept per language: the Mod Loader's
  layout marks it as localized. ReMixed ships English only and refuses its tab when a text row is
  missing, so a player using another text language probably gets no tab. TM would have to ship
  texts for every language, even if they are English placeholders.
- **Settings that need a restart.** Five of the six do today. In-game rows for them must say so,
  or they mislead worse than the launcher, which now shows "*restart".
- **Two places to change one setting.** The launcher and the in-game page would both write
  `Config.json`. Field-only writes keep them from clobbering each other. The one gap: a launcher
  window left open saves its stale view if the player then changes that same field there.
- **Licence.** ReMixed has no open licence. Its Nexus permissions need the author's permission to
  modify its files, use its assets or fork it, and the author asks to be asked before generative
  AI modifies its files. The download's only licence text covers the MIT code it took from
  GenericJobs. House rule: facts only. TM would rebuild from facts (table ids, layouts, which
  functions to hook), write its own code and credit ReMixed for the discovery. No code is ported.
- **Overlap with TM itself.** ReMixed has its own Move-Find rework and a paid "reveal treasures"
  that marks tiles at Formation. A player running both mods gets two mods marking tiles. That is
  a separate compatibility item, outside this proposal.

## 5. Shapes we could build (recommendation first)

- **A. Not now. Do TM-35 first, then the proofs in section 6 (recommended).** Live settings make an
  in-game page worth having. The proofs settle the tab question before anything is built.
- **B. TM rows inside an existing tab, such as Gameplay.** No new tab, so no fight with ReMixed for
  the spare. It needs an edit to that tab's row list, which only conflicts with a mod editing the
  same tab, plus three of the five hooks (draw, change, default). Unknown: how many rows a tab can
  hold before it scrolls.
- **C. One shared tab for all of Patrick's mods.** A small host mod owns one tab with ReMixed's
  group-picker idea, and TM, Living Weapons, Battle Brain and the rest each register a group. The
  best long-term shape, but the biggest build: a new repo, and every mod depends on it. It still
  competes with ReMixed for the spare tab if 7 is a hard limit.
- **D. A TM-only tab.** The weakest shape. It spends what may be the game's only spare tab on six
  settings.
- **E. Ask ReMixed's author for a way to add groups to their tab.** The least code, but it depends
  on someone else and only helps players who run ReMixed.

## 6. Proof first (each step needs the game lock and Patrick's yes)

- **Step 0: static, no game, about one session.**
  - Confirm the eight byte patterns each match exactly once on the current game build.
  - Find whose limit the 7 is by reading the code that draws the tab strip.
  - Check which spare option ids have table rows, and which languages the UI text table has.
  - File what is learned to FFTGameFacts as UNCERTAIN.
- **Step 1: live, about 15 minutes of game time, ReMixed off.** A throwaway probe mod adds one row
  with two choices to Gameplay (shape B), then, separately, an eighth tab.
  - Pass means the row draws, cycles, writes `Config.json`, comes back on Restore Defaults, and
    Fast-forward keeps its own value.
  - The eighth tab either draws or it does not. Both answers are useful.
- **Step 2: live, about 15 minutes of game time, ReMixed on.** Both pages together, then one switch
  of the text language.

That is about 30 minutes of game time in two boxes, each under the 20-minute line.

## 7. Rough cost

- **Proofs:** half a session of static reading, plus two 15-minute game boxes.
- **Shape B:** about five production files, the table bake and tests. About two to three sessions
  through /build (plan, review, build, verify), then Patrick's own game check.
- **Shape C:** roughly double, plus a new repo and a dependency added to each mod.

## 8. Facts filed

Filed to FFTGameFacts in 8254a0c, all UNCERTAIN. ReMixed is recorded there as source `remixed`.
Its Nexus permissions allow no code and no assets, so only facts are taken, in our own words. Each
pattern was checked against the exe on disk (1.5.2 without copy protection), and each matched
exactly once.

- [settings-tabs-are-config-category-rows]
- [settings-screen-routines]
- [settings-screen-object-fields]
- [game-table-getter-and-ids]
- [fast-forward-choices-from-table-323]
- [script-variable-block-layout]
- [tile-mark-kind-setter]
- New evidence on [trap-rows-copied-per-battle], [move-find-rare-roll-routine],
  [collected-bit-read-set-mid-battle] and [script-variables-word-array].

The scan, its result, the screenshot and the full notes are kept outside the repos in
`F:/FFTRec/kept/remixed_study_20261126` (RESEARCH.md first).
