# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [TM-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

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
