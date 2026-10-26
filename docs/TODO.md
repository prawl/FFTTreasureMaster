# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, ported from the FFTLivingWeapons system with the TM id prefix. "Now" holds what
is actively being worked for the current release (hard cap 5, each entry carries Done means +
Verify). "Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this
file only through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The
release ship gate lives in docs/RELEASE_SCOPE.md; Now is the in-flight subset, not a mirror of
that checklist.

## Now (release: 1.6.0)

- **[TM-2] Confirm live that a battle reset re-lights the refunded treasure tile** (opened 2026-07-21) [AWAITING-LIVE]
  - Done means: the owner has seen it happen on screen: claim a treasure, reset the battle so
    the item is taken back, and the tile's glow returns. The code path is built and unit-tested
    but has never been watched working live. (Tech: the refund re-light in
    TreasureMaster claim detection, behind Config.HideClaimedTiles; 5 refund tests proven
    non-vacuous per handoff.md.)
  - Verify: owner-only flip after seeing the re-light in a live session; note the map and item
    used.
- **[TM-9] Make the release zip unpack into a real mod folder on Linux** (opened 2026-10-19) [BUILDING]
  - Done means: a player on Linux who unzips the Nexus download gets a prawl.fft.treasuremaster
    folder with the mod inside, the same as a Windows player, and the packager refuses to
    produce a zip that would not. A Linux player reported 2026-10-19 that the 1.6.0 Nexus zip
    unpacked as eight flat files named "prawl.fft.treasuremaster\..." so Reloaded never saw the
    mod; the GitHub release zip was fine. (Tech: Windows PowerShell 5.1's
    ZipFile.CreateFromDirectory writes backslash separators into entry names, the CI zip is
    built by pwsh and gets forward slashes; Publish.ps1 now writes entries by hand with '/' and
    Verify-Package fails on any backslash, the same fix ColorCustomizer already carries.)
  - Verify: Publish.ps1 run locally produces a zip whose raw central-directory names all use
    '/'; Nexus 1.6.0 file replaced with that zip; the reporter confirms the folder appears.
- **[TM-10] Every treasure tile gives its rare item, behind an off-by-default toggle** (opened 2026-10-26) [BUILDING]
  - Done means: a player who turns on the new "Rare treasure only" setting gets the rare item
    from every Move-Find treasure tile instead of the common one, the same effect as the
    community mod Rare Treasure Only by Avarath, without installing anything extra beyond
    the FFT Ivalice Chronicles Mod Loader the Treasure Hunter grant already uses. Toggle
    off (the default) changes nothing. Side effect to note at ship: the Treasure Hunter
    grant's never-verified hot-load timer goes away, so a mod loaded into a running game
    waits for the next launch. (Tech: Config.RareTreasureOnly; one runtime ApplyTablePatch
    per map through IFFTOMapTrapFormationDataManager setting CommonItemIdN := RareItemIdN on
    the 317 of 344 slots where they differ; shared LoaderGrantHost subscribes after the
    loader so our writes follow its table merge; audit-overlay read-back; premise ledger in
    the session plan.)
  - Verify: owner-only live pass: log shows 317 tiles patched and 27 already rare on the
    vanilla table, two high-Brave claims on The Siedge Weald yield Bowgun and Escutcheon,
    toggle off yields a common drop again, tile still un-lights on claim.

## Backlog

- [TM-11] 2026-10-26: A mod loaded into an already running game cannot run its loader-backed
  extras (Rare treasure only, the Treasure Hunter grant) until the next launch, because
  Reloaded never re-fires its all-mods-loaded event and a timer cannot be made safe against
  the loader's own table merge. (Tech: needs a loader-side initialized signal or a Reloaded
  API that says initialization already happened.)
- [TM-12] 2026-10-26: Log lines written through the pure grants and the loader host are not
  checked by the logging contract test, so a future edit could slip a badly shaped player
  message past the build. (Tech: LogContractTests scans only ModLogger facade calls; extend
  its literal scan to the _log, log, warn, Info and Warn delegate call sites.)
- [TM-3] 2026-07-21: The release zip cannot be uploaded to Nexus yet because the mod has no
  Nexus id in its filename, so Vortex would show it with a warning and no version. (Tech:
  Publish.ps1's NexusModId parameter is still the placeholder 0; register the mod on Nexus and
  pass the real id before the first upload.)

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
