# Tile highlight styles: a setting and a key that cycles them

STATUS: PLAN, round 2, 2026-11-25. Round 1 went to a blind reviewer, who answered CHANGES
NEEDED with one blocker and five should-fixes; this round folds all ten findings in (see the
last section). Nothing built. Two steps, worked in this order: a style setting (TM-32), then
a key that cycles the style in game (TM-33). Ledger rows are in [TODO.md](TODO.md).

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
- Press each candidate key, F2 to F9, bare, in battle and on the world map, and note any the
  game answers. The default comes from the ones that do nothing.

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
  file's frozen comment budget: the policy's header and the MarkValue notes, and FastHold.cs:11
  "only ever sets bits".
- `Configuration/Config.cs`: a `TileStyle` property, DisplayName "Tile highlight style", the
  description naming each look in words, read at the next launch. A forgiving converter on
  this one property maps a name it does not know to the default, so a config saved by a newer
  version never fails the whole load.
- `Mod.cs`: read the setting, publish the style before the engine and FastHold start, add it
  to the "Configuration loaded" line.
- Text that names the settings: `mod/ModConfig.json` ModDescription, the Enabled description in
  `Config.cs` ("a yellow-fill mark"), `README.md` ("There is one setting"), and this repo's
  `CLAUDE.md` ("Four user-facing settings").
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
relaunch on Grogh Heights (R2 to R6), Gollund Colliery Ridge (R1, R3, R5) and Zeklaus Desert
(R0, R5); a claimed tile still going dark; the dropdown's names reading well in the launcher.

## Step 2 (TM-33): the cycle key

Changes:

- `Config.cs`: a `StyleCycleKey` setting, Off or F2 to F9, default from the step 0 check. F1 is
  Color Customizer's; F10 and F12 are left out (the ShadowPlay and Steam screenshot keys).
- New `StyleHotkey.cs`, called from the engine's tick behind an `IKeySource` seam. The live
  source reads the key with `GetAsyncKeyState` and counts it only while the foreground window
  belongs to the game's own process and Alt, Ctrl and Shift are all up, so Alt+F4 or typing in
  another program never cycles. A press fires once on its down edge; holding does not repeat.
  The tick runs every 31 to 47 ms, so a tap shorter than that can be missed; a normal press
  lasts longer.
- On a press: the next listed style, wrapping. The old value joins a retired set for this run,
  and the current value always leaves it, so after a wrap the current value is checked first
  and never treated as retired. 0x80 can never be in the set, because it is never listed. One
  log line names the new style and the key.
- The save rewrites one field, not the file. At the press, the live Config.json is read fresh;
  if it does not parse, nothing is written and the log warns once; otherwise only `TileStyle`
  is replaced and the file is written back. A setting the player changed in the launcher since
  launch survives, and a failed load is never made permanent. If the launcher's Configure
  window is open and saved afterwards, its value wins; the plan accepts that. A save that
  throws warns once and never stops the game.
- The swap. The safety rule never writes a value the mod does not own, so without help every
  lit tile would freeze on the old look. Classification gains `Stale`, an exact match of a
  retired value: `TileHolder.Hold` rewrites a Stale byte to the current value and `Unlight`
  reverts a Stale byte like a Held one. A byte reading anything else, a style plus 0x01
  included, stays Foreign and untouched.
- Threads: the engine thread and FastHold's 8 ms thread both classify. The current value and
  the retired set are one immutable snapshot, swapped in one write, read fresh on each pass and
  never cached. A pass that read the old snapshot can write the old value once; it is Stale on
  the next pass and corrected.
- The key does nothing while `Enabled` is off. Outside a battle a press still changes and saves
  the choice.
- Tests, again about twice the production lines: the down edge fires once, a held key does not
  repeat, no focus or a held modifier means no press and no edge carried over; the order wraps;
  after a full wrap the current value reads Held, not Stale; a Stale byte is rewritten; 0x80
  and any value never listed are never written; Unlight reverts a Stale byte; the save changes
  only TileStyle in a file holding other changed settings; an unparsable file is not written;
  a throwing save warns once; disabled means no poll.
- Size: about 80 to 110 production lines, in `StyleHotkey.cs` and `TileStyles.cs`, plus small
  edits to `TileHolder`, `Engine` and `Mod`. Engine and Mod are each at their two allowed silent
  catches, so the new code adds none.
- A key read in a release build is by design, like Color Customizer's F1. Living Weapons bans
  release key reads for its dev tricks (LW-413); the shared rulebook has no such rule.

Live check (owner): the key steps through every style on a lit map, each look matching the
sheet; a claimed tile still goes dark after a change; a relaunch keeps the last style; a
setting changed in the launcher mid session survives a press; the key does nothing while the
game is in the background or with Alt held; no clash with Color Customizer's F1.

## Calls for the owner

- The default key, after the step 0 check.
- Whether any left out look goes in. 0x80 cannot, for the reason above.

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
6. No modifier check: Alt, Ctrl and Shift must be up, F10 and F12 left out.
7. 12 versus 16 looks: wording fixed, the near repeats named.
8. Text updates: listed in step 1.
9. Test hygiene: round trips, the Tuning default, the unknown name, `finally` restores.
10. A short tap can be missed: noted, accepted.
