# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, ported from the FFTLivingWeapons system with the TM id prefix. "Now" holds what
is actively being worked for the current release (hard cap 5, each entry carries Done means +
Verify). "Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this
file only through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The
release ship gate lives in docs/RELEASE_SCOPE.md; Now is the in-flight subset, not a mirror of
that checklist.

## Now (release: 1.5.0)

- **[TM-2] Confirm live that a battle reset re-lights the refunded treasure tile** (opened 2026-07-21) [AWAITING-LIVE]
  - Done means: the owner has seen it happen on screen: claim a treasure, reset the battle so
    the item is taken back, and the tile's glow returns. The code path is built and unit-tested
    but has never been watched working live. (Tech: the refund re-light in
    TreasureMaster claim detection, behind Config.HideClaimedTiles; 5 refund tests proven
    non-vacuous per handoff.md.)
  - Verify: owner-only flip after seeing the re-light in a live session; note the map and item
    used.
## Backlog

- [TM-3] 2026-07-21: The release zip cannot be uploaded to Nexus yet because the mod has no
  Nexus id in its filename, so Vortex would show it with a warning and no version. (Tech:
  Publish.ps1's NexusModId parameter is still the placeholder 0; register the mod on Nexus and
  pass the real id before the first upload.)
- [TM-5] 2026-07-21: The dev deploy quietly erases the flight recorder's saved evidence even
  though it means to keep it, so a crash investigated after a redeploy has no black box to
  read. Proven in the ColorCustomizer sibling: PowerShell's Remove-Item -Exclude spares an
  excluded DIRECTORY itself but still recursively wipes the directory's contents. (Tech:
  BuildLinked.ps1 line 45; fix by filtering top-level entries with Where-Object before
  Remove-Item, as ColorCustomizer commit 9a16b092 does for its logs/ folder.)
- [TM-6] 2026-07-21: A ledger row accidentally pasted after the Format section escapes every
  grammar and id-uniqueness scan, because the contract tests only read entries out of the
  Now, Backlog, and changelog sections; decide whether entry-shaped lines in Walled or
  Format should fail the contract. Found in the ColorCustomizer sibling (its CC-17); every
  repo sharing the ledger system has the same blind spot.

## Walled (blocked by engine / external)

- After an in-battle "Retry from Start of Battle", the treasure glow does not come back until
  you restart from the Formation screen instead. The game only rebuilds its tile-highlight
  overlay on a full battle load, and an in-battle Retry is not one; the mod keeps holding the
  highlight bytes correctly the whole time. Root-caused live 2026-06-19 (handoff.md, "RETRY
  RENDER-GATE"); the wall is engine-side.

## Format (enforced by TodoContractTests)

- Sections, in this order and no others: Now (with the release name in the header), Backlog,
  Walled, Format.
- Now: at most 5 entries. Entry first line: `- **[TM-<n>] <title>** (opened YYYY-MM-DD) [STATUS]`
  where STATUS is QUEUED, BUILDING, AWAITING-LIVE, or BLOCKED(reason). Every entry carries a
  `- Done means:` and a `- Verify:` sub-bullet. Promote from Backlog by filling those in; if Now
  is at cap, demote something first.
- Backlog: entry first line `- [TM-<n>] YYYY-MM-DD: <one sentence>`; indented continuation lines
  are free. Capture new items here in the session they surface.
- ELI5-first prose (owner rule, 2026-07-21): the first sentence of every entry, and the opening
  of every Done means / Verify, is plain language a non-programmer follows: what is broken or
  wanted, for whom, what done looks like. Technical detail (offsets, hashes, file and memory
  names) comes AFTER that opening, in continuation lines or a "(Tech: ...)" tail, never
  instead of it.
- IDs are unique across this file and docs/CHANGELOG.md; never reuse a retired ID.
- Items exit ONLY by moving to docs/CHANGELOG.md when they ship or die: in the shipping commit
  itself, or in the immediately following commit when the exit row cites that commit's own hash.
- No em dashes and no double-dash separators anywhere in this file or the changelog.
- AWAITING-LIVE resolutions (flipping a row out of AWAITING-LIVE) are owner-only.
