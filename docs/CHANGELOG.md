# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [TM-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## 1.5.0 cycle

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
