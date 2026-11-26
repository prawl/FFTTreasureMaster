# Tile highlight styles: a setting and a key that cycles them

STATUS: PLAN, APPROVED in review round 2, 2026-11-25. Round 1 went to a blind reviewer, who
answered CHANGES NEEDED with one blocker and five should-fixes; round 2 checked every fix
against the code and answered APPROVE, and its notes are folded in (see the last section).
Step 1 built 2026-11-25 (dfa2d58, adfac4d, 603d6fa), blind build review SHIP in round 2, live
check passed except Yellow, which rides step 2's check. Step 2 gained the live launcher save the
same day and waits on a plan review of that part. Two steps, worked in this order: a style setting
(TM-32), then a key that cycles the style in game (TM-33). Ledger rows are in
[TODO.md](TODO.md).

## In plain words

Today every treasure tile is drawn with a blue border and a yellow fill. The game can draw a
tile many other ways, and which way is picked by one number the mod already writes. Players
get a "Tile highlight style" setting with a short list of looks, today's look staying the
default. Then a key, pressed in battle, steps through the same list on the real map and
remembers the last pick, so nobody has to guess from a name.

## What is already proven, and what is not

Game facts in FFTGameFacts, seen live on 2026-11-25:

- `tile-mark-value-picks-style` (UNCERTAIN): the value in a tile's mark flag bytes picks its
  look. A sweep of every even value from 0x80 to 0xFE gave 16 looks worth a picture, 12 of
  them clearly different. Pictures: `F:\FFTRec\kept\tile_styles_20261125\tile_styles.png`. The
  reviewer re-pulled frames for the chosen values and they match the table below.
- `tile-mark-redraws-on-value-change` (UNCERTAIN): a lit tile changes look within half a second
  when the value changes, with no cursor move.
- `trap-marker-writes-0x80` (UNCERTAIN): the player's own trap marker (key 2) writes 0x80.
- `tile-mark-records-and-flag` (PROVEN): the flag bytes and the hold the mod does today.

Not proven, and the plan does not lean on it:

- What bit 0x01 means. Most resting bytes in the shipped dataset read 0x01 (1365 of 1604),
  three tiles at once on Grogh Heights, so it is not only the cursor, whatever one sighting
  suggested. Every style value stays even, and a byte reading a style plus 0x01 stays Foreign
  and untouched, exactly as 0xCC plus 0x01 is today.
- Every region. The sweep wrote only the three bytes of one Grogh Heights tile, in regions R2,
  R4 and R6. The mod also holds bytes in R0 (Zeklaus Desert only), R1 (37 maps), R3 and R5. No
  style but 0xCC has been held there, so the live checks below cover those maps on purpose.

## Step 0: clean previews and the key check (owner, about 10 minutes, live)

- Re-shoot each listed look alone: no trap marker nearby and the cursor a few tiles off, with
  the existing sweep probe on the listed values only. The pictures are for the Nexus page and
  the setting's description, and ship nowhere else.
- Done 2026-11-25, the owner pressing keys in battle: T opens the unit's Status page, R is View
  Battlefield, 3 puts the trap marker on the mouse pointer's tile (as 2 does on the cursor's),
  and 4 to 9 do nothing. So the key list is 4 to 9. The clean previews are still to shoot.

## Step 1 (TM-32): the style setting

The list, in this order. A member's name is saved in Config.json as text and shows in the
launcher's dropdown as written, so the names are plain words, and a name is never renamed once
shipped; new looks are appended.

| Member | Value | Look |
|---|---|---|
| BlueAndYellow (default) | 0xCC | cyan border, yellow fill, today's look |
| BlueAndPink | 0xC0 | cyan border, magenta fill |
| Green | 0xD8 | green fill |
| Yellow | 0x98 | yellow fill, no cyan border |
| Pink | 0xA8 | pink fill |
| RedOutline | 0xF8 | red dotted outline |

Left out on purpose:

- 0x80, the white dotted outline. It is the value the game writes for the player's own trap
  marker, so the mod can never own it: a marked treasure tile would read as the mod's, and
  claiming it, or cycling away from that style, would rewrite or erase the player's mark.
- Looks that copy something the game shows: 0x88 blue (the move range), 0x84 orange and 0x90
  red (ability ranges), 0x82 the red no entry ring, 0xE0 the white outline (the cursor).
- Near repeats of listed looks: 0xA0 and 0xB8 (blues), 0xB0 (yellow green), 0xE8 (a fainter
  white dotted outline).

Changes:

- New `TileStyles.cs` holds the classification the policy holds today, so the policy file
  shrinks instead of growing: the `TileStyle` enum, the table, the current style as one
  published snapshot, and `Classify` and `WantWrite`. `TreasureMaster.Policy.cs` loses
  `ClassifyAddr`, `WantWrite` and `MarkValue`; `TileHolder` and `ArmAudit` call `TileStyles`.
  Its row in docs/health (the partial TreasureMaster, 537 code lines, 228 comment lines, both
  at the limit) can only go down. Comments that stop being true are rewritten within each
  file's frozen comment budget: the policy's header and the MarkValue notes, FastHold.cs:11
  "only ever sets bits", the `see cref` at TileHolder.cs:8 and ArmAudit.cs:118, and the plain
  mentions at TreasureMaster.cs:16, :422 and :466 (no project builds XML docs, so the compiler
  will not catch a stale reference). The smaller row is then written with
  `python gates/tools/health.py floor --write` and committed.
- `Configuration/Config.cs`: a `TileStyle` property, DisplayName "Tile highlight style", the
  description naming each look in words, read at the next launch. A forgiving converter on
  this property (and on the key setting in step 2) maps a name it does not know, a number, or
  an object or array (skipped with `reader.Skip()`) to the default, so a config saved by a
  newer version never fails the whole load. An attribute converter on the property wins over
  the options' `JsonStringEnumConverter`; the reviewer checked this in a live F# run.
- `Mod.cs`: read the setting, publish the style before the engine and FastHold start, add it
  to the "Configuration loaded" line.
- Text that names the settings or the moved members: `mod/ModConfig.json` ModDescription, the
  Enabled description in `Config.cs` ("a yellow-fill mark"), `README.md` ("There is one
  setting" and its bit `0x80` sentence), the sample "Configuration loaded" line in
  `docs/LOGGING.md`, and this repo's `CLAUDE.md` ("Four user-facing settings", "the four
  toggles", ClassifyAddr in the policy statics, and the TileHolder line).
- Tests, about twice the production lines, since the repo's test ratio floor is 1.72:
  - every member mapped; every value has bit 0x80, is even, is neither 0x00 nor 0x01, and is
    unique; the default is 0xCC and equals the Tuning default, as ModConfigTests does for the
    toggles; a TileStyle round trips through the JSON options; a file with an unknown style
    name loads with the default style and keeps the other four settings;
  - the policy contract for every style: a resting byte gets that style's value, that value
    reads Held, every other listed value reads Foreign, 0x80 always reads Foreign;
  - one TileHolder hold and one claim un-light under a non default style;
  - tests that change the style restore it in a `finally`, since the style is process wide
    (safe only because TestInit turns parallel tests off).
- Size: about 40 production lines in the new file, fewer in the policy than today.

Live check (owner): with a dev deploy, three styles picked in the setting and each seen after a
relaunch on Zeklaus Desert, which alone holds bytes in all seven regions R0 to R6; a claimed
tile still going dark; the dropdown's names reading well in the launcher. The three are Yellow
(0x98), RedOutline (0xF8) and Green (0xD8), because 0x98 and 0xF8 read like the game's own
overlay values combined (0x88 with 0x90, 0xE0 with 0x18). The mod owns a byte only by its exact
value, so if the game ever draws a combined value equal to the style, the mod would take it for
its own mark and could erase it on a claimed tile. So, under each style, open a unit's Move range
and an ability range over a lit tile and cancel, and under Green open a Move range over a claimed
tile: the game's overlay must draw normally and the style must come back after. (Build review
round 1, premise 7, PLAUSIBLE and cosmetic only.)

## Step 2 (TM-33): the cycle key, and a launcher save that applies live

Added 2026-11-25 during and after the step 1 live check, at the owner's words: "If I can update
the config for the Speed Mod live, shouldn't that also be true for this?" and then "Implement the
button press to update them live first". So a style saved in the launcher applies in the running
game, not only at the next launch, through the same swap as the key. Only the style goes live;
the other settings still apply at the next launch (see Text below).

Changes:

- `Config.cs`: a `StyleCycleKey` enum setting with members Off, Key4, Key5, Key6, Key7, Key8
  and Key9 (a digit cannot start a member name, and the names show in the dropdown and are
  frozen once shipped), default Key4, the nearest free key to the game's own markers on 2 and
  3. The owner ruled out the F keys, and step 0 found T, R and 3 taken. Only the top row digit
  counts, not the number pad. The forgiving converter from step 1 sits on this property too.
- Text that names the settings, all of it stale once a sixth setting lands: `README.md` ("five
  settings"), `CLAUDE.md` ("Five user-facing settings" and the Configuration line),
  `mod/ModConfig.json` ModDescription, `Mod.DescribeConfig`'s value list and the sample line in
  `docs/LOGGING.md` that mirrors it.
- New `StyleHotkey.cs`, called from the engine's tick behind an `IKeySource` seam. The live
  source reads the key with `GetAsyncKeyState` and counts it only while the foreground window
  belongs to the game's own process, Alt, Ctrl, Shift and both Windows keys are all up, and a
  battle map is on screen. The battle signal is the `battleDisplayed` value `Engine.cs` already
  builds and passes to TreasureMaster and HunterHold, so no new memory read. So typing in
  another program, a unit's name in a menu, Win+4 (the taskbar's fourth app) or Shift (the
  game's Tactical View) never cycles. A press fires once on its down edge; holding
  does not repeat.
  The tick runs every 31 to 47 ms, so a tap shorter than that can be missed; a normal press
  lasts longer.
- On a press: the next listed style, wrapping. The old value joins a retired set for this run,
  and the current value always leaves it, so after a wrap the current value is checked first
  and never treated as retired. 0x80 can never be in the set, because it is never listed. One
  log line names the new style and the key.
- The save rewrites one field, not the file. At the press the config path is resolved again, since
  the launcher can create the User file mid session, and the live Config.json is read fresh as a
  JSON node tree, so no other field is rewritten from launch values. If it does not parse, nothing
  is written and the log warns once; otherwise only the `TileStyle` field is replaced and the file
  is written back. A setting the player changed in the launcher since launch survives, and a
  failed load is never made permanent. A save that throws warns once and never stops the game.
- The launcher save applies live, in a small new file called from the engine tick. A tick
  countdown (a Tuning constant, as the existing countdowns do, never the wall clock) runs the
  check about once a second. The check resolves the config path through a function Mod hands
  it (ResolveConfigPath stays in Mod with its catch, since a new file may hold no silent catch)
  and compares the pair of path and last write time with the last pair it read successfully.
  Any difference counts, older included, since a copied back file keeps its source's time; a
  User file created mid session is a new path. On a difference it reads the file through a new
  `Configurable.Read(path)`, the step 1 loader made internal, with no watcher and no save. A
  read that fails, whether a sharing error during the launcher's write or a LoadError, changes
  nothing, warns once for that pair, and leaves the pair unread, so the next check reads the
  finished file. Only a successful read records the pair. A read style equal to the current one
  changes nothing and logs nothing, which also makes the mod's own key press save a no-op. Any
  other style goes through the same swap as a key press, with one log line saying the config
  file now holds it. The check is seeded at launch with the pair Mod read, so a broken file
  warns once, at launch, not again each second. It never throws to the engine: it catches its
  own read failures and logs them, with no empty catch. It does nothing while `Enabled` is off.
  The FileSystemWatcher `FromFile` arms is not used for this, since it fires on a pool thread
  and only the engine tick may swap.
- The Configure window and the key. The launcher's ConfigureModDialog listens to
  ConfigurationUpdated, which the watcher `FromFile` arms raises with a fresh read on every
  change of the file, a failed read included (read from the launcher's compiled code in plan
  review round 3, not run). The owner sees the window not change until he clicks Save
  (2026-11-25), so the window may swap the object it saves without redrawing it. Decided
  (notes 513 and 515, the owner's call): the last change wins, a Save in the window or a key
  press. The key saves the style to the file, and the watcher stops raising updates, so the
  window saves exactly what the player sees when he clicks Save. A key press made while the
  window is open is overwritten only by a Save clicked after it, which the once a second check
  then applies. That also ends the
  case where a save mid write handed the window a default config. In the step 1 check a broken
  file was replaced with clean defaults after a visit to Configure; whether closing it saves
  without a click on Save is not settled, and the plan leans on neither.
- Text: the TileStyle description's "Changes apply the next time the game is launched" becomes
  a line saying a saved change applies in the running game and the key steps through the looks
  in battle. Enabled and Hide tiles after their treasure is claimed say nothing about when a
  change applies today, so both gain "Changes apply the next time the game is launched", as the
  other two settings and the key setting say. When the check reads a file whose other settings
  differ from the launch values, it logs once per pair that those apply at the next launch, so
  an unticked Enable that keeps the tiles lit is explained in the log.
- The swap. The safety rule never writes a value the mod does not own, so without help every
  lit tile would freeze on the old look. Classification gains `Stale`, an exact match of a
  retired value, added on the same line as the other `AddrState` members in the frozen partial:
  `TileHolder.Hold` rewrites a Stale byte to the current value and `Unlight` reverts a Stale
  byte like a Held one. A byte reading anything else, a style plus 0x01 included, stays Foreign
  and untouched. Two entry points, never mixed: `Publish(style)` is the launch time set and the
  tests' reset, and it clears the retired set; a new `SwapTo(style)` is the runtime swap the key
  and the config check call, and it adds the old value to the retired set. The retired set also
  clears at each battle enter edge, since a new battle's bytes start Resting, which keeps Stale
  from reaching game drawn bytes in later battles.
- Threads: the engine thread and FastHold's 8 ms thread both classify. The current value and
  the retired set are one immutable snapshot, swapped in one write, read fresh on each pass and
  never cached. Only the engine thread swaps, for the key and for a launcher save alike. A pass
  that read the old snapshot can write the old value once; it is Stale on the next pass and
  corrected.
- The key does nothing while `Enabled` is off and nothing unless the mod is holding marks this
  moment: a one line `FastHold.Holding`, true while FastHold has a published map, which the
  engine reaches through the existing `TreasureMaster.FastHold` member, so the frozen partial
  does not grow. Holding means armed on a battle map with tiles lit, so it covers outside a
  battle, a map with no treasure, and a stood down build, and unlike `UnitArrayTrusted` it stays
  true on a build healed by signature. The key's save runs on the engine thread; a file whose
  root is not an object is not written and warns once, and a missing TileStyle field is added.
- Tests, again about twice the production lines: the down edge fires once, a held key does not
  repeat, no focus, a held modifier or no battle on screen means no press and no edge carried
  over; the order wraps; after a full wrap the current value reads Held, not Stale; a Stale
  byte is rewritten; 0x80 and any value never listed are never written; Unlight reverts a
  Stale byte; the save changes only TileStyle in a file holding other changed settings; an
  unparsable file is not written; a throwing save warns once; disabled means no poll. For the
  live apply: a changed pair with a new style swaps once and logs once; an older write time
  counts as changed; the same style swaps and logs nothing; the mod's own save then reads as no
  change; a failed read swaps nothing, warns once for that pair and is read again on the next
  check; a User file appearing mid session is picked up; an unchanged pair reads nothing; the
  countdown runs the check at most once a second; the launch pair is not read again; other
  changed settings log once that they apply at the next launch. For the swap: Publish clears
  the retired set and SwapTo adds to it; a battle enter edge clears it; swapping back to a
  retired value makes it current and never Stale; Unlight under a swap reverts a claimed tile
  holding the old value. For the window: a change to the file after `FromFile` raises no
  ConfigurationUpdated.
- Size: about 120 to 160 production lines, in `StyleHotkey.cs`, the small new config check
  file and `TileStyles.cs`, plus small edits to `TileHolder`, `FastHold`, `Engine`, `Mod`,
  `Configuration/Configurable.cs` (Read made internal; `FromFile` arms no watcher, so the watcher
  field, `OnFileChanged` and its empty catch go), `Configuration/Config.cs`
  (the key setting and the text) and the `AddrState` line in `TreasureMaster.Policy.cs`. Engine
  and Mod are each at their two allowed silent catches, so the new code adds none. Comments that
  stop being true are rewritten within each file's frozen comment budget: TileStyles.cs:12-13
  ("chosen at launch"), FastHold.cs:11-12, TileHolder.cs:7-10, TreasureMaster.Policy.cs:10-12
  and :23-26, and TreasureMaster.cs:465.
- A key read in a release build is by design, like Color Customizer's F1 window. Living Weapons
  bans
  release key reads for its dev tricks (LW-413); the shared rulebook has no such rule.

Live check (owner): the key steps through every style on a lit map, each look matching the
sheet; a claimed tile still goes dark after a change; a relaunch keeps the last style; a
setting changed in the launcher mid session survives a press; the key does nothing while the
game is in the background, with a modifier held, or outside a battle (renaming a unit with a
a 4 in the name changes nothing). A style saved in the launcher while a battle is on screen
shows within about a second; with Configure open and another setting edited in it, a key
press then Save keeps the edited setting and puts back the style the window shows. Step 1's
leftovers ride this check: Yellow, and a Move range and an ability range over a lit tile under
Yellow and under RedOutline, on Zeklaus Desert. And with Yellow and RedOutline retired (the key
cycled past them), open the same ranges over a lit tile, and read the R0 bytes on Zeklaus after
a swap, since the game flips those bytes itself (FFTGameFacts zeklaus-r0-mark-bytes-read-0x80).

## Calls for the owner

- Decided 2026-11-25: the default key is 4, chosen from a list of 4 to 9, after the owner's
  key check.
- Whether any left out look goes in. 0x80 cannot, for the reason above.
- Decided 2026-11-25 (note 508): the six style names freeze as written.
- Decided 2026-11-25 (notes 513 and 515): the last change wins, a Configure Save or key 4, and
  an open window keeps what the player sees. The owner: "the last update call is the winner so
  config save or 4".
- Decided 2026-11-25 (note 516, the owner's "Right."): only the tile style applies live, by key
  4 or a launcher save. Enable, Hide tiles and the key setting apply at the next launch, and
  their descriptions say so.

## Out of scope

- A custom config window with picture previews. Reloaded allows one
  (`TryRunCustomConfiguration` in `Configurator.cs`), but it means owning a whole settings
  window for one picture. The previews go on the Nexus page.
- A controller button for the cycle.
- The config loader resetting every setting to its default, with no warning, when the file
  fails to parse. It is older than this plan and is TM-34 in the Backlog.

## Round 1 review, and where each finding went

1. Blocker, the whole file saved from launch values: the save now rewrites one field of a fresh
   read and refuses an unparsable file (step 2).
2. A retired 0x80 would rewrite the player's trap marker: 0x80 left out of the list.
3. The policy file is at its health row: the classification moves to `TileStyles.cs`, stale
   comments listed, test lines sized to the ratio floor.
4. Only R2, R4 and R6 were swept: the live checks cover R0, R1, R3 and R5 maps.
5. The bit 0x01 premise was wrong: no longer relied on; the fact is marked CONTRADICTED and a
   clean trap marker fact filed.
6. No modifier check: Alt, Ctrl, Shift and the Windows keys must be up, the key counts only on
   a battle map, and the F keys are dropped at the owner's word.
7. 12 versus 16 looks: wording fixed, the near repeats named.
8. Text updates: listed in step 1.
9. Test hygiene: round trips, the Tuning default, the unknown name, `finally` restores.
10. A short tap can be missed: noted, accepted.

## Round 2 review, and where each note went

APPROVE, no blockers. Its notes: name the key members and put the forgiving converter on both
enums (step 2); gate the key off while the mod has stood down for an unknown build; skip object
and array values in the converter; resolve the config path again at each press; add the Windows
keys to the modifier check and say the number pad does not count; list the stale comment
references in TileHolder, ArmAudit and TreasureMaster. All folded in above. Zeklaus Desert holds
all seven regions, so the step 1 live check needs only that map.

## Duet's verdict, and what it adds

GO (notes 482 and 483, 2026-11-25), under the owner's standing word. Four additions, none
blocking the start:

1. TM-34 is fixed inside TM-32, as its first test-first change: the loader reports a file it
   cannot read instead of quietly returning defaults, and the forgiving converter is built on
   top of that loader, since both live in the same config code.
2. The Zeklaus live check moves a fact to confirmed only for what it shows on screen. The
   CONTRADICTED cursor fact stays as it is.
3. Step 2 names one test outright: a key held down steps the style once per press, so a long
   press never skips looks.
4. The build starts in a fresh session, handed off before /build-lite begins.

The build is offline only. The live check waits for the game lock's release, and the push waits
for the review's yes and that check.

## Round 3 review (step 2's live apply part), and where each finding went

CHANGES NEEDED, 2026-11-25, one blocker and eight should-fixes, all folded into step 2 in place:

1. Blocker, the Configure window follows the file through the watcher, so a key press can throw
   away an unsaved launcher edit: the owner ruled the last change wins, Save or key 4, so the
   watcher stops raising updates, which also ends the failed read raise.
2. A failed read recorded its write time and lost the finished save: only a successful read
   records the pair now, and a failed one is read again.
3. Enabled and Hide tiles had no next launch wording: both gain it, and the log explains a
   changed setting that waits for the next launch.
4. A gate on `UnitArrayTrusted` would kill the key on a healed build: the gate is
   `FastHold.Holding` instead.
5. `Publish` and the retired set: two entry points, `Publish` clears and `SwapTo` adds, and a
   battle enter edge clears the set.
6. A newer only time check misses a copied back file: any change of path or time counts.
7. Stale comments listed, within the frozen budgets.
8. Entry points and files: `Configurable.Read`, ResolveConfigPath handed in from Mod, and the
   full file list.
9. Stale widens what the mod overwrites: the retired set clears per battle, and the live check
   opens ranges with Yellow and RedOutline retired and reads R0 on Zeklaus after a swap.
Nits taken: the check never throws to the engine, a tick countdown rather than the wall clock,
the log names the config file rather than the launcher, the launch pair seeds the check, and
the key's save handles a non object root and a missing field.
