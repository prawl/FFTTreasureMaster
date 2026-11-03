# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, ported from the FFTLivingWeapons system with the TM id prefix. "Now" holds what
is actively being worked for the current release (hard cap 5, each entry carries Done means +
Verify). "Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this
file only through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The
release ship gate lives in docs/RELEASE_SCOPE.md; Now is the in-flight subset, not a mirror of
that checklist.

## Now (release: 1.7.0)

- **[TM-18] Take the shared rulebook so this mod builds under the same rules as the others** (opened 2026-11-02) [QUEUED]
  - Done means: the mod carries gates/ as a subtree with its rules.json, compiles the shared logger
    and tests in, imports the shared build settings and style file, and every gate runs green
    locally and in CI with today's numbers recorded as the floor. No gameplay code changes. (Tech:
    the hookup steps in gates/README.md; rules.json names prod, tests, projects and the TM prefix;
    floor.json and the comment budget baseline come from today's files.)
  - Verify: BuildLinked.ps1 and CI green on the hookup commit; the health numbers equal what the
    repo's own copy printed before the swap; the build log names the rulebook version.

## Backlog

- [TM-19] 2026-11-02: Every battle on default settings logs a false warning that enhanced markers
  could not be written, so a player reading the log thinks the mod is broken when it is fine. The
  marker path behind the warning was abandoned and is switched off, so delete it rather than fix
  the message. (Tech: MarkerWriter.cs, the EnhancedMarkingUtilityPtr offset in Offsets.cs,
  MarkerWriterTests.cs, Tuning.EnhancedMarkersEnabled, and WriteMarkers with _markers and
  _lastMarkerProbe in TreasureMaster.cs; with the gate off, Write returns 0 and takes the warning
  branch at TreasureMaster.cs:782. Take out the six leftover TEMP (RetryDiagnostics) blocks in
  TreasureMaster.cs and Engine.cs in the same pass if nothing still needs them. About 200 lines go.)
  - Done means: a battle on default settings logs no marker line at all, and no code for the
    abandoned marker path is left in the mod or its tests.
  - Verify: a new test runs a default settings battle through TreasureMaster and asserts nothing is
    logged at warning level; suite green; one live battle's log shows no marker warning.

- [TM-20] 2026-11-02: Nothing tests how the mod reacts to a real battle from start to finish,
  because Engine builds its own live memory reader and BattleState has no tests of its own. Open
  the seam and record one plain battle as a tape, enough to drive TM-19's test and to replay
  through Engine and BattleState the way Living Weapons and Battle Brain test against tapes, so a
  change to battle detection fails the build before a player meets it. The in-battle Retry and
  quit to world map tapes wait until this mod changes again; a three tape suite for a mod that
  rarely changes pays nothing until it does. Owner 2026-11-02, narrowed from three tapes to one.
  (Tech: pass IGameMemory into Engine instead of building LiveMemory inside its constructor; a
  tape is the per tick reads of slot0, slot9, battleMode, pause and event id plus the treasure
  tile bytes.)
  - Done means: one recorded battle replays through Engine and BattleState in the suite and
    checks when the battle starts and ends and which tiles light up.
  - Verify: the replay test goes red when the battle exit debounce or the enter signal is broken on
    purpose, and green again on revert; suite green.
- [TM-2] 2026-07-21: Confirm live that a battle reset re-lights the refunded treasure tile. Parked 2026-11-02 by the owner's quality pause; it was AWAITING-LIVE in Now.
  - Done means: the owner has seen it happen on screen: claim a treasure, reset the battle so
    the item is taken back, and the tile's glow returns. The code path is built and unit-tested
    but has never been watched working live. (Tech: the refund re-light in
    TreasureMaster claim detection, behind Config.HideClaimedTiles; 5 refund tests proven
    non-vacuous per handoff.md.)
  - Verify: owner-only flip after seeing the re-light in a live session; note the map and item
    used.

- [TM-9] 2026-10-19: Make the release zip unpack into a real mod folder on Linux. Parked 2026-11-02 by the owner's quality pause; it was BUILDING in Now.
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
