# Release Scope: 1.5.0 (ledger adoption + live confirmation)

STATUS: DRAFT (scope not yet locked by the owner)

Current shipped version 1.4.0; proposed next **1.5.0** (owner confirms the bump). This doc is
the ship gate for the release named in docs/TODO.md's Now header; TodoContractTests keeps the
two in lockstep (the release name must appear here). The heavier per-box enforcement the
sibling FFTLivingWeapons repo runs is deliberately not ported; it comes in if this doc ever
grows a real box inventory.

**Identity: "Adopt the sibling mods' engineering QoL and close out the claim-detection arc."**

## IN (ship gate; every box green = ship)

### 1. Work-ledger system (TM-1)
- [ ] docs/TODO.md + docs/CHANGELOG.md + TodoContractTests enforce the ledger contract; suite
      green; non-vacuity sabotage check performed.

### 2. Refund re-light live confirmation (TM-2)
- [ ] The claimed-tile re-light on a battle-reset refund is seen working in a real session
      (owner-only flip), or the row exits WONTFIX/RETRACTED with the reasoning on record.

### 3. Candidates pending owner triage (not yet committed to this release)
- Nexus registration + the real NexusModId in the release filename (TM-3).

## OUT (deferred, tracked in the ledger)

- Anything not listed above; see docs/TODO.md Backlog and Walled.
