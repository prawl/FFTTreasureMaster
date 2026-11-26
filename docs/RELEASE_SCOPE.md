# Release Scope: 1.5.0 (ledger adoption + innate Treasure Hunter) and the 1.6.0 compat cut

STATUS: LOCKED (owner ship order 2026-07-21: "commit, push and cut a new tag"; 1.6.0 compat cut appended 2026-10-16)

Current shipped version 1.5.0 (2026-07-21); the in-flight release is **1.6.0**, the compat cut at the bottom of this file. The 1.5.0 text below is kept as shipped. This doc is the ship gate for the
release named in docs/TODO.md's Now header; TodoContractTests keeps the two in lockstep (the
release name must appear here). The heavier per-box enforcement the sibling FFTLivingWeapons
repo runs is deliberately not ported; it comes in if this doc ever grows a real box inventory.

**Identity: "Adopt the sibling mods' engineering QoL and let the whole party hunt treasure."**

## IN (ship gate; every box green = ship)

### 1. Work-ledger system (TM-1)
- [x] docs/TODO.md + docs/CHANGELOG.md + TodoContractTests enforce the ledger contract; suite
      green; non-vacuity sabotage check performed (shipped 4731b27, 2026-07-21).

### 2. Living-weapons logging model (TM-4)
- [x] Readable console + complete log file + flight recorder; owner live-verified
      (shipped 5bd1fe2, 2026-07-21).

### 3. All units gain Treasure Hunter (TM-7)
- [x] Config toggle (default off) grants innate Treasure Hunter to the player job rows via
      the FFTIVC Mod Loader as an optional dependency; owner live-verified toggle on (two
      random units claimed), toggle off (vanilla traps), and loader-absent (highlighting
      unaffected) on 2026-07-21 (shipped 133b833).

## OUT (deferred, tracked in the ledger)

- Refund re-light live confirmation (TM-2): stays in Now for the next cycle. The code path
  ships dark and fails safe (worst case a tile stays lit); it has shipped in that state
  since 1.2.0 and does not gate this release. Owner-only flip when a live battle-reset
  refund is observed.
- Nexus registration + the real NexusModId in the release filename (TM-3).
- Anything not listed above; see docs/TODO.md Backlog and Walled.

## 1.6.0 compat cut (2026-10-16): the Denuvo-free game build, no new features

The publisher removed Denuvo from the game in September 2026. The code did not move, but the
unprotected build lets Windows load it at a random address each launch (ASLR), so the mod's
fixed addresses pointed at nothing and it never armed. This cut makes the mod find the game
wherever it lands and does nothing else. Version 1.6.0 so it sorts above the community fork's
1.5.2 patch of the same break. Owner scope call 2026-10-16.

- [x] **ASLR rebase (TM-8)**: shipped cf8bc21. Every address is resolved against the live image base; the dataset
      keeps its capture key and the signature re-resolve confirms nothing moved.
- [x] **Owner live pass** (2026-10-16): base 0x7FF65E0F0000, 71 maps re-resolved with all region
      deltas zero, Zeklaus Desert armed with 4 tiles, glow confirmed on screen.
- [x] Gates re-run at the cut: dotnet test 383 green, gen_treasure_db.py self-test green.
- [x] Package built (, 2026-10-16); ModVersion bumped to 1.6.0.
- [x] Tag v1.6.0 cut at the release commit and pushed (the tag exists on the repo).

## 1.7.0 cut (2026-10-26): Rare treasure only

One new feature, chosen by the owner 2026-10-26: the retired community mod "Rare Treasure Only"
rebuilt as a config toggle (default off) on the same loader path the Treasure Hunter grant uses.
The plan was approved by an independent outside reviewer and the in-house review panel before
code, and the finished work by the same outside reviewer and two adversarial verifiers after. The 1.6.0 text above is kept as shipped.

- [x] **Rare treasure only (TM-10)**: shipped 6cd5e6f (stages 0bb686c, 44c2365, 6cd5e6f). One
      sparse table patch per map through the mod loader, 317 of 344 slots; no re-assert pass,
      no hot-load watchdog, deterministic ordering after the loader's own merge.
- [x] **Owner live pass** (2026-10-26, Zeklaus Desert): log reported 317 patched, 27 already
      rare, 0 unreadable, 0 failed; claims gave the rare items.
- [x] Gates at the cut: dotnet test 420 green, gen_treasure_db.py self-test green, Release build
      clean.
- [x] ModVersion bumped to 1.7.0; ModDescription now names all four toggles.
- [x] Tag v1.7.0 cut at the release commit and pushed (the tag exists on the repo).

## 1.8.0 cut (2026-11-26): Tile looks, the look key and the All units hold

Six looks for the lit treasure tiles, a key that steps through them in battle, a launcher save
that shows in the running game, and an All units Treasure Hunter that holds per battle on the
deployed party only. Owner release order 2026-11-26 through Duet note 615: "cut a new release
and push to github". The 1.7.0 text above is kept as shipped.

- [x] **Tile highlight style (TM-32)** and **the look key with a live launcher save (TM-33)**:
      shipped a9c7875 and a0caff3; game check TM-36 passed 2026-11-26 on Zeklaus Desert, key 4
      pressed by script and by the owner.
- [x] **A broken settings file reports itself (TM-34)**: shipped dfa2d58.
- [x] **All units hold per battle (TM-25)**, with TM-19, TM-21, TM-28 and TM-18: shipped
      cdd40f0, c265b84, d69f209, 15b2d2a and b8287c9; owner live passes of TM-25 and
      TM-21 on 2026-11-25.
- [x] Remaining live checks closed as not needed on the owner's word (TM-37, TM-31, TM-2).
- [x] Gates at the cut: dotnet test 758 green, gen_treasure_db.py self-test green, the
      manifest, format, health and temp leftover gates green in check mode.
- [x] ModVersion bumped to 1.8.0.
- [ ] Tag v1.8.0 cut at the release commit and pushed.
