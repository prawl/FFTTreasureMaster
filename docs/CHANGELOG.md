# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [TM-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 1.7.0 cycle

- [TM-36] SHIPPED cff5a52 2026-11-26: The tile look key and the look it keeps are now proven in
  a real battle, not only in tests. On Zeklaus Desert key 4 stepped the lit treasure tiles
  through all six looks, each press logged once and each tile changing in place with the camera
  still; Move ranges drew normally under Yellow, under RedOutline and after both were passed;
  Shift, Ctrl, Alt and another window in front did nothing; and a relaunch kept the last look.
  Patrick pressed the key himself during the run and was happy with it. Closed as passed by
  Duet (note 610); the steps this run did not reach moved to TM-37. (Tech: driven 2026-11-26 by
  SendInput scan codes inside a 20 minute game box, 05:17Z to 05:31Z; R1 to R6 bytes read the
  new mark within 1 s of every press and R0 only the mark or 0x80; every put back matched by
  hash; evidence in F:\FFTRec\kept	m36_live_20261126; facts FFTGameFacts b876af9 stay
  UNCERTAIN.)
- [TM-33] SHIPPED a0caff3 2026-11-25: In battle, key 4 now steps the treasure tiles through the
  six looks and keeps the last pick for the next launch, and a look saved in the launcher shows
  in the running game within about a second, so nobody restarts the game to try a look. Lit
  tiles change in place and a claimed tile still goes dark. Closing the launcher's settings
  window no longer undoes a look picked with the key, because the window now writes only what
  the player changed in it. The owner called it done on 2026-11-25 before the game check, which
  moved to TM-36. (Tech: full /build; stages 2ea758a settings file, a5a5fd3 Stale swap, 9d0424a
  key and StyleLive, fix a0caff3 after blind review 1 found a repeated field name could throw
  out of the window's save; 758 tests.)
- [TM-32] SHIPPED a9c7875 2026-11-25: A Tile highlight style setting offers six looks for the
  lit treasure tiles, today's blue border with a yellow fill staying the default. Live pass
  2026-11-25, Patrick at the PC: Green, Pink, RedOutline and the default seen, overlays under
  Pink, a claimed tile going dark and the trap marker. Yellow and the overlays under Yellow and
  RedOutline were not run; the owner called it done and they ride TM-36. (Tech: TileStyles holds
  the table and the per byte rules; a forgiving converter maps an unknown name to the default;
  commits adfac4d, 603d6fa, a9c7875.)
- [TM-34] SHIPPED dfa2d58 2026-11-25: A player whose Config.json got broken no longer sees every
  setting reset without a word. The settings still fall back to their defaults and the mod
  leaves the broken file alone, but the log now names the file and the reason instead of
  claiming the settings loaded, and the launcher's Configure window still opens on it to repair
  it. Live pass 2026-11-25 on Zeklaus Desert, Patrick at the PC: the warning named the file and
  the JSON reason, the tiles showed the default look, and Configure opened; after that visit to
  Configure the file held clean defaults. (Tech:
  Configurable.LoadError carries the failure, hidden from the launcher grid and never saved;
  Mod.DescribeConfig picks the warn or loaded line; the launcher path test followed in adfac4d;
  the old swallow put back turns 7 of 40 config tests red.)
- [TM-25] SHIPPED cdd40f0 2026-11-25: With All units gain Treasure Hunter on, only the player's own
  deployed units get the free ability now, so enemies stop grabbing tile treasure first, as a
  player reported. Chemists keep the game's own bonus, guests get none, and a unit that changes
  sides, charmed or otherwise, loses the mod's bonus until it comes back. Owner live passes on
  2026-11-25 on Grogh Heights: pick-ups by several jobs, a Chemist finding treasure unaided, the
  hold back after a Retry and a resume, a real Steal Heart charm taking the bonus away and a hit
  giving it back, and a bonus restored by a save taken back on a side change. Still unseen live:
  an enemy walking onto a lit tile, kept as TM-31. (Tech: HunterHold holds bit 0x01 of +0x9E in
  party seats 16 to 20 each battle and the job table grant retires; commits 161313d, dae0b3b,
  a4618fc, 7dbaa4e and cdd40f0; game facts in FFTGameFacts 91d3bdf.)
- [TM-19] SHIPPED c265b84 2026-11-25: A normal battle no longer logs a false warning that
  enhanced markers could not be written, because the abandoned marker path behind it is gone
  from the mod and its tests. Owner live pass 2026-11-25 on Grogh Heights: three launch logs,
  none with a marker line. (Tech: MarkerWriter, its offset, its tests, the tuning gate and the
  Retry diagnostics blocks removed in c265b84.)
- [TM-28] SHIPPED 15b2d2a 2026-11-25: The All units Treasure Hunter setting no longer writes out
  its seat summary about thirty times a second only to throw it away; it writes it only when the
  summary goes into the log. Players see no difference. The unit record bytes it reads for
  presence, team and guest also got names. (Tech: HunterHold.LogSettled packs the five settled
  verdicts into an int key and builds the held count and seat text only after the key changes;
  Decide and the take back use ExistByte, Flags1, Flags2, GuestMask1 and GuestMask2, named from
  FFTGameFacts unit-guest-flags and unit-existence-byte-0x1b5; tests T32 pin today's summary text
  and T33 proves an unchanged tick allocates only its block read; 652 tests; blind review YES;
  follow-ups TM-29 and TM-30.)
- [TM-21] SHIPPED d69f209 2026-11-25: All four treasure tiles on Grogh Heights light up now. The
  one at x 9, y 8 on the east edge never lit, although its treasure could still be picked up, as
  a Nexus player reported; the mod had the wrong memory spots on file for that one tile. The
  build now refuses any tile that holds no spot the game paints from, so a bad capture like this
  cannot ship again. Owner live pass 2026-11-25 on Grogh Heights: four lit tiles on screen.
  (Tech: commit of 2026-11-12; map 81 tile 9,8 gains 0x140E894F3, 0x14103CC5B and 0x141187617;
  the guard is TreasureSchemaTests.Every_tile_owns_bytes_in_the_lists_the_game_paints_from; 620
  tests. A read of the live game in the same pass found all 15 of that tile's bytes and every
  byte of the other three holding 0xCC, on the running build reached through the signature
  re-resolve. The twelve old bytes stay for now, see TM-22.)
- [TM-18] SHIPPED b8287c9 2026-11-03: Treasure Master now builds under the shared rulebook, the
  same quality checks every FFT mod will run, and today's numbers are the floor nothing may fall
  below. Comments were cut down to plain words to pass the shared rules, and the tests now delete
  the scratch folders they used to leak. No gameplay code changed. (Tech: gates/ subtree at
  v0.1.0, which is 3da7098; rules.json; the shared logger and contract tests as linked source;
  the shared build targets and style files; docs/health floor.json, files.json and style.json;
  follow-ups 204b6b7 and 72b4791; CI run 41582500395 green.)
- [TM-10] SHIPPED 6cd5e6f 2026-10-26: A new setting, "Rare treasure only" (off by default), makes
  every Move-Find treasure tile hand out its rare item instead of the common one, the same effect
  as the community mod Rare Treasure Only by Avarath, without installing anything beyond the FFT
  Ivalice Chronicles Mod Loader the Treasure Hunter grant already uses. Toggle off changes
  nothing. Owner live pass 2026-10-26 on Zeklaus Desert: log reported 317 tiles patched and 27
  already rare, claims gave the rare items. Side effect to know: the Treasure Hunter grant's
  never-verified hot-load timer is gone (a timer could fire inside the mod loader's own table
  merge), so a mod loaded into a running game waits for the next launch (TM-11 in Backlog), and
  the grant now provably runs after that merge. (Tech: Config.RareTreasureOnly; one sparse
  ApplyTablePatch per map through IFFTOMapTrapFormationDataManager setting CommonItemIdN :=
  RareItemIdN on the 317 of 344 slots where they differ; LoaderGrantHost subscribes after
  fftivc.utility.modloader so our writes follow its merge; AuditOverlay read-back; three staged
  commits 0bb686c, 44c2365, 6cd5e6f; plan approved by an independent outside reviewer and two adversarial verifiers,
  code 9 of 10, spec 9 of 10, 420 tests.)

## 1.6.0 cycle

- [TM-8] SHIPPED cf8bc21 2026-10-16: Treasure tiles light up again on the game build that shed its
  copy protection. Plain language: the September game update removed Denuvo, and the unprotected
  game is loaded at a random address every launch. The mod assumed one fixed address, read empty
  memory where the game's header should be, and waited forever, so no tile ever lit. No save was
  at risk because nothing is written until the build check passes. Now every address the mod knows
  stays written as its documented number and is resolved against the real image base the mod asks
  Windows for at startup; the treasure dataset keeps its honest capture key and the existing
  signature re-resolve confirms nothing moved. Owner live pass 2026-10-16 on Steam buildid
  24304444: base resolved 0x7FF65E0F0000, 71 maps re-resolved with every region delta 0, Zeklaus
  Desert armed with 4 tiles held lit, glow confirmed on screen. Version 1.6.0 so it sorts above
  the community fork's 1.5.2 patch of the same break (that fork rebased inside the memory layer
  and deleted the test suite; not adopted). (Tech: ImageBase.At(pin) over GetModuleHandleW(null);
  Offsets pins as properties; AnchorResolver walks sections at the live base and removes the
  ASLR shift before its delta math; tile addresses stay dataset space and rebase at TileHolder,
  ArmAudit and the hold diagnostic; literal tripwire and no-bake ratchet tests; one adversarial
  verify round, code 9 of 10; the same mechanism shipped first in FFTLivingWeapons v2.3.4.)

## 1.5.0 cycle

- [TM-6] SHIPPED 5569e8e 2026-07-21: a ledger row accidentally pasted into the Walled or
  Format section no longer hides from the contract checks; any entry-shaped line outside
  the real entry sections now fails the build instead of silently rotting where the grammar
  and id-uniqueness scans never look. Decision recorded: yes, they fail the contract.
  Proven non-vacuous by a planted stray going red and the revert going green. (Tech:
  TodoContractTests invariant I with EntryShapedRegex over the non-entry sections; the
  blind spot the ColorCustomizer sibling found as CC-17.)
- [TM-5] SHIPPED 99d0cea 2026-07-21: a dev deploy no longer erases the flight recorder's
  saved evidence, so a crash investigated after a redeploy still has its black box to read.
  Proven live: a sentinel file planted in the flight folder survived a full deploy together
  with every real archive. (Tech: the BuildLinked.ps1 clean now filters top-level entries
  with Where-Object; Remove-Item with Exclude and Recurse spares an excluded directory but
  wipes its contents; same fix as ColorCustomizer commit 9a16b092.)
- [TM-7] SHIPPED 133b833 2026-07-21: every unit the player fields can now pick up hidden
  Move-Find treasure without equipping Treasure Hunter, behind a new config toggle that
  ships OFF because it also hands the ability to enemy humans of the same jobs. Owner
  live-verified all three ways on 2026-07-21: toggle on, two random units claimed treasure;
  toggle off, vanilla traps returned; mod loader absent, tile highlighting still worked
  with one friendly log line expected. (Tech: innate ability 509 written into a free
  InnateAbilityId slot of 30 player job rows via fftivc.utility.modloader's
  IFFTOJobDataManager controller as an optional dependency; real StartEx entry point
  replacing a dead Start overload; readiness retry plus audit-overlay read-back after the
  loader's stale GetJob snapshot was diagnosed from its source; projects moved to
  net9.0-windows; commits b31df3a, ac70f72, 0e3575d, c06bff5, 133b833.)
- [TM-4] SHIPPED 5bd1fe2 2026-07-21: the mod's messages now match the FFTLivingWeapons logging
  model: the console tells a short story a player can read (battle started, map armed, treasure
  claimed) while treasuremaster.log keeps every timestamped detail, and a black-box flight
  recorder archives each battle's events so a bug report is diagnosable after the fact. Owner
  live-verified the new launch header, battle report, and flight archive in game 2026-07-21.
  (Tech: typed ModLogger facade, FileConsoleLogger two-sink core with the [Treasure Master]
  tag, closed 11-verb glossary pinned by LogContractTests, ScopedLogger, FlightRecorder with
  battle-edge, first-error, and standdown flushes; the old Log shim retired; 91 new tests,
  suite 322 green.)
- [TM-1] SHIPPED 4731b27 2026-07-21: the repo now tracks its work the same way the sibling
  mods do, in one machine-checked ledger, so open items survive between sessions instead of
  living in scattered notes. (Tech: docs/TODO.md + docs/CHANGELOG.md + docs/RELEASE_SCOPE.md
  under the 35 TodoContractTests ported from the FFTLivingWeapons pattern; proven non-vacuous
  by a deliberate format sabotage going red; enforced by the existing test gate in
  BuildLinked, Publish, and CI.)

## Pre-ledger (backfilled)

- [TM-0] SHIPPED 8a2a979 2026-07-21: version 1.4.0 makes the mod survive game patches instead
  of going dark. Before this, a game update moved the memory addresses the mod depends on and
  every treasure tile stopped glowing until someone rebuilt the dataset by hand; now the mod
  notices the game changed and re-finds its addresses on its own, only standing down if it
  cannot re-find them safely. (Tech: AoB signature re-resolve of the 7 tile-region bases and
  10 singleton addresses on a PE build-key mismatch, with per-entity delta cross-checks and a
  fail-safe global disarm; commits 1bd5a6d + 53c828d, released as 8a2a979.)
