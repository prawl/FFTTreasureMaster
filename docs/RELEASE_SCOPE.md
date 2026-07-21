# Release Scope: 1.5.0 (ledger adoption + innate Treasure Hunter)

STATUS: LOCKED (owner ship order 2026-07-21: "commit, push and cut a new tag")

Current shipped version 1.4.0; this release is **1.5.0**. This doc is the ship gate for the
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
