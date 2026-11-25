# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, ported from the FFTLivingWeapons system with the TM id prefix. "Now" holds what
is actively being worked for the current release (hard cap 5, each entry carries Done means +
Verify). "Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this
file only through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The
release ship gate lives in docs/RELEASE_SCOPE.md; Now is the in-flight subset, not a mirror of
that checklist.

## Now (release: 1.7.0)

- **[TM-19] Delete the dead marker path so a normal battle logs no false warning** (opened 2026-11-02) [AWAITING-LIVE]
  - Done means: a battle on default settings logs no marker line at all, and no code for the
    abandoned marker path is left in the mod or its tests. Today every battle logs a false
    warning that enhanced markers could not be written, so a player reading the log thinks the
    mod is broken when it is fine; the path behind it was abandoned and is switched off, so it
    goes rather than getting a nicer message. (Tech: MarkerWriter.cs, the EnhancedMarkingUtilityPtr
    offset in Offsets.cs, MarkerWriterTests.cs, Tuning.EnhancedMarkersEnabled, and WriteMarkers
    with _markers and _lastMarkerProbe in TreasureMaster.cs; with the gate off, Write returns 0
    and takes the warning branch. Take out the six leftover TEMP (RetryDiagnostics) blocks in
    TreasureMaster.cs and Engine.cs in the same pass if nothing still needs them. About 200 lines
    go; the pins in rules.json and the rows in docs/health/files.json shrink with them.)
  - Verify: a new test runs a default settings battle through TreasureMaster and asserts nothing is
    logged at warning level; suite green; one live battle's log shows no marker warning.

- **[TM-25] Give Treasure Hunter only to the units the player deploys, so enemies stop taking treasure** (opened 2026-11-25) [AWAITING-LIVE]
  - Done means: with All units on, every unit the player deploys picks up tile treasure,
    Chemists by the game's own rule and everyone else through the mod, and no enemy picks one
    up because of the mod. A player reported enemies grabbing treasure first, and the owner saw
    it in the game, because the setting edits the job list both sides share. Now the setting
    stops changing the job list and the mod gives the ability in each battle instead,
    recruited monsters included and Chemists skipped. A charmed player unit has no free ability
    while charmed, guests get none and the log names them, a unit that does not look right
    plays as in the unmodded game and the log names it (an unreadable read names nobody), and
    the setting follows Enable Treasure Master and no longer needs the Mod Loader. The setting's
    description says what changed, since the Treasure Hunter tag leaves the Formation screen.
    The plan is part 1 of [EQUIPPED_HUNTER_PLAN.md](EQUIPPED_HUNTER_PLAN.md). (Tech: the job
    table grant retires; each battle a new HunterHold holds bit 0x01 of +0x9E on qualifying
    records in seats 16 to 20, found from the signature anchored unit array, never the claim
    scan's window. Duet's calls F1 to F5, note 373.)
  - Verify: gate 1's set direction and gate 2's battle start and enemy turn parts passed live on
    2026-11-25; gate 1's clear direction and suspend and resume move to the owner's live check,
    since the job table grant they were written for retires here (Duet, note 373). Suite green
    with the new hold tests. It does not ship until the fact keeper's GF-19 review passes on
    the three claims it leans on: the switch alone lets a unit pick up treasure, the switch
    holds until a battle restart rebuilds the unit, and seat 20 holds staging copies marked
    0xFF (Duet, note 376). Then owner-only in the game: deployed units of several jobs pick
    treasure up, a Chemist with nothing equipped picks up treasure, an enemy of a standard job
    walked onto a lit tile leaves the treasure, a
    charmed player unit gets nothing while charmed and gets it back after, and the hold
    comes back after a Retry and after a suspend and resume.

## Backlog

- [TM-26] 2026-11-25: A normal battle can still log a warning that some treasure flag bytes are
  away from their expected value, which reads like trouble even though every tile lights. Seen
  twice on Grogh Heights on 2026-11-25, both times with all four tiles lit. Decide whether it is
  worth a warning at all, or belongs at a quieter level. (Tech: the foreign count warning in
  TreasureMaster TickArmed, logged once per battle.)

- [TM-24] 2026-11-24: Take Chemists out of the All units grant, so equipping Treasure Hunter on
  a Chemist is worth something again, then check in the game that the Chemist bonus works the
  way the research says. This is step zero of the plan in
  [EQUIPPED_HUNTER_PLAN.md](EQUIPPED_HUNTER_PLAN.md), chosen by the owner on 2026-11-24, and it
  goes before TM-23. The Chemist skip landed with TM-25, so only the check in the
  game is left here.
  - Done means: with All units on, a Chemist still picks up tile treasure with nothing
    equipped, but no longer gets the game's 25 points of rare chance for free. A Chemist who
    equips Treasure Hunter gets them, as with the mod off. Every other job keeps its grant.
    (Tech: the hold in HunterHold.cs skips job 75.)
  - Verify: suite green; then owner-only, in the game:
    at Brave 25 or lower a Chemist with Treasure Hunter equipped gets the rare on every claim,
    and at Brave 100 a Chemist with nothing equipped gets the common item on every claim. One
    miss either way means the research is wrong and TM-23 is planned again.

- [TM-23] 2026-11-24: A forum player asked that a unit who equips Treasure Hunter find the rare
  treasure more often than a unit who only has it for free, so the ability is still worth its
  slot when All units is on. The owner chose the lucky turn on 2026-11-24, to be built after
  TM-24. The plan is [EQUIPPED_HUNTER_PLAN.md](EQUIPPED_HUNTER_PLAN.md). Not started: nothing
  is built and nothing has been watched live.
  - Done means: one new number setting, 0 to 100 and 0 by default, sets how often the turn of
    a player unit with Treasure Hunter equipped is a lucky one. On a lucky turn any tile
    treasure that unit finds is the rare item. On any other turn, and for a unit that only has
    the granted ability, the odds are the game's own. It does nothing while Rare treasure only
    is on. (Tech: data writes only, no game code changed. The mod holds the rare item on both
    sides of the battle's treasure rows for the turn and holds the rows' own items otherwise.
    The game facts it leans on come from one read of the exe and are owed to the fact keeper
    before any code.)
  - Verify: the four live gates in the plan pass before any code; suite green; then owner-only
    with the setting at 100: a unit with Treasure Hunter equipped gets the rare on two claims
    running, and a unit with only the granted ability still gets its usual mix.

- [TM-22] 2026-11-12: The mod writes its mark into some memory spots that seem to light nothing,
  and nobody has checked whether they are needed. On Grogh Heights 19 of the 28 spots on file took
  the mark and kept it, the game never touched them through three and a half minutes of battle,
  and the one tile that had only such spots stayed dark. Find out what they are, then either drop
  them when the dataset is baked or write down why they stay; writing into bytes nobody can
  explain is a risk carried for nothing. (Tech: baked addresses that are neither half of a pair
  0x1B3768 apart nor tagged R6, 214 of 1604 on 37 tiles; on Grogh Heights they are the R3 and R5
  bytes resting at 0x0, 0x140F92EB9 to 0x140F93731 and their copies 0x1B39E8 above. Found
  2026-11-12 while fixing TM-21.)

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
  extra (Rare treasure only, the only loader-backed setting now) until the next launch, because
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
