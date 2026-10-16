# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger, ported from the FFTLivingWeapons system with the TM id prefix. "Now" holds what
is actively being worked for the current release (hard cap 5, each entry carries Done means +
Verify). "Backlog" captures everything else at the cheapest possible entry cost. Items EXIT this
file only through docs/CHANGELOG.md, moved there in the commit that ships or kills them. The
release ship gate lives in docs/RELEASE_SCOPE.md; Now is the in-flight subset, not a mirror of
that checklist.

## Now (release: 1.6.0)

- **[TM-8] Treasure tiles stopped lighting up after the game shed its copy protection; make the mod find the game wherever Windows puts it** (opened 2026-10-16) [AWAITING-LIVE]
  - Plain language: in September the game publisher removed Denuvo. The game's code did not move,
    but without the protection wrapper Windows now loads it at a different address every launch.
    The mod assumed one fixed address, so on the new build it reads empty memory where the game's
    header should be, treats that as "not ready yet" forever, and never lights a tile. Saves were
    never at risk: nothing is written until the build check passes.
  - The fix is the one the sibling Living Weapons mod shipped as v2.3.4 (LW-387): every address
    the mod knows stays written as its documented number and is resolved against the real image
    base the mod asks Windows for at startup. The treasure dataset keeps its honest capture key;
    the existing signature re-resolve handles the key mismatch and finds every address unmoved.
    A community fork (rexx-oss, v1.5.2) patched the same break by rebasing inside the memory
    layer and deleting the whole test suite; this fix keeps the tests and the guarded design.
    (Tech: ImageBase.At(pin), live base from GetModuleHandleW(null); Offsets pins become
    properties; ArmAudit and AnchorResolver read the PE header at the live base; tile addresses
    stay dataset-space and are rebased at the TileHolder, ArmAudit and diagnostic read sites;
    AnchorResolver computes deltas in dataset space so the ASLR shift never counts as a move;
    damage report for the build itself in FFTLivingWeapons docs/research/PORT_1.5.2_NODRM_OFFSETS.md.)
  - Done means: on the Denuvo-free build (Steam buildid 24304444) the mod logs the resolved image
    base at startup, re-resolves its dataset by signature with every region delta zero, arms on a
    treasure map, and the treasure tiles glow; every existing test still passes unchanged because
    the default base is the documented one.
  - Owner live pass 2026-10-16 PASSED on buildid 24304444: base resolved 0x7FF65E0F0000, 71 maps
    re-resolved by signature with every region delta 0, Zeklaus Desert armed with 4 tiles held
    lit, glow confirmed on screen by the owner.
  - Verify: suite green including the relocated-base hold test and the literal tripwire; owner
    live pass from a fresh deploy: the startup line names a base other than 0x140000000, the
    anchor line says the addresses were re-resolved by signature with all regions at 0, a battle
    on a treasure map shows the glowing tiles. Owner only flips this row.

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
