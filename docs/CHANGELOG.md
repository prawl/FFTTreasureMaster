# Changelog (work-ledger exits)

STATUS: CONTRACT (machine-checked by TodoContractTests)

Where docs/TODO.md items land when they ship, die, or retract; newest first within a cycle.
Entry first line: `- [TM-<n>] SHIPPED <hash> YYYY-MM-DD: <summary>`, or WONTFIX / RETRACTED
with a date and no hash.

## Pre-ledger (backfilled)

- [TM-0] SHIPPED 8a2a979 2026-07-21: version 1.4.0 makes the mod survive game patches instead
  of going dark. Before this, a game update moved the memory addresses the mod depends on and
  every treasure tile stopped glowing until someone rebuilt the dataset by hand; now the mod
  notices the game changed and re-finds its addresses on its own, only standing down if it
  cannot re-find them safely. (Tech: AoB signature re-resolve of the 7 tile-region bases and
  10 singleton addresses on a PE build-key mismatch, with per-entity delta cross-checks and a
  fail-safe global disarm; commits 1bd5a6d + 53c828d, released as 8a2a979.)
