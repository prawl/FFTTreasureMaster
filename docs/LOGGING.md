# Logging model

STATUS: CONTRACT (logging + flight recorder reference; the verb table is test-gated by
LogContractTests)

The FFTLivingWeapons logging model, adopted 2026-07-21. One page: the line format, the tier
model, the closed event-verb glossary, and the flight recorder (the black box). Log files from
before the adoption keep the old shape (see "Reading old logs" at the bottom).

## Line format

Every line opens with `[Treasure Master]`. Both sinks always carry a millisecond timestamp
(`[HH:mm:ss.fff]`) and a level bracket (`[INFO]`/`[WARN]`/`[ERROR]`/`[DEBUG]`); the console
timestamp is load-bearing (it lets a player's console paste be joined back to the matching
`treasuremaster.log` lines), never drop it. The FILE and CONSOLE shapes diverge by design:

- **FILE, every line:** `[Treasure Master] [HH:mm:ss.fff] [LEVEL] [verb] description`: five
  tokens. The `[verb]` bracket names one of the closed event verbs below.
- **CONSOLE, Info tier:** four tokens, no verb. Subject-first prose a player reads.
- **CONSOLE, Warning/Error tier:** five tokens, same as the file. A bug-report console paste
  needs the verb for triage.
- **CONSOLE, Debug tier** (only when the level is raised to Debug in code): five tokens.

**Full words on console lines, names not ids:** numeric ids, hex addresses, and raw sentinels
live in `[trace]` Debug companions on FILE lines only, via the two-line id pattern
(`ModLogger.EventWithTrace`/`WarnWithTrace`). No " -- " separator and no em dash appear in any
log text (colon/semicolon/comma/parens instead); LogContractTests enforces this by source scan.

**Console dedup is a semantic key, not a rendered-string key:** the console suppresses a repeat
only when the SAME (level, verb, message) triple already appeared this battle (reset on both
battle edges via `ModLogger.NoteBattleEdge`). The FILE is never deduped.

**Subject-first lexical fence:** every Info/Warning console-eligible message must read as a
sentence, not a label; LogContractTests enforces the lexical floor (opens with an uppercase
letter or an interpolation hole, never a bare `word:` leader), and full subjecthood beyond that
is a review rule.

## Tier model

The runtime logs through the static `ModLogger` facade (FFTTreasureMaster/ModLogger.cs), backed
by an `ILogger`; production impl is `FileConsoleLogger`, test-only swallow is `NullLogger`. The
facade surface is TYPED: every call site uses `ModLogger.Event(verb, msg)` / `Warn` / `Error` /
`Debug`, the two-line helpers `EventWithTrace`/`WarnWithTrace`, or a `ScopedLogger` from
`ModLogger.For(verb, armed)`. The old free-form `Log.Info`/`Log.Error` shim was retired
entirely; LogContractTests fails the build if it comes back or if any file outside the facade
plumbing writes to the console.

Four tiers, `LogLevel` enum (low = more verbose): `Debug` (0), `Info` (1), `Warning` (2),
`Error` (3), `None` (4, silences the console entirely).

**The two-sink rule (the one thing worth remembering):**

- The **file** (`treasuremaster.log`, rotated per launch to `treasuremaster.prev.log`) gets
  **every** message, **Debug tier included, unconditionally**, regardless of the configured
  `LogLevel`. The evidence chain a live diagnosis needs is never thinner than the console.
- The **console** (the Reloaded window) only shows a message at or above the configured
  `LogLevel` (default `Info`), and dedups repeats per battle.

**Tier meanings:** Info = the battle report (battle bookends, the arm line, claims and refunds,
the startup header, a successful anchor re-resolve). Warning = degraded but coping (fingerprint
drift, uncapturable tiles, off-flag bytes, a failed re-resolve stand-down, an unreadable
dataset or config). Error = something broke; an Error-tier line ALSO arms the flight recorder's
FlushOnce trigger (only the FIRST error of a launch produces an archive, deliberately). Debug =
file-only evidence (state transitions, hold diagnostics, arm churn, `[trace]` id companions).

**The relevance gate:** a battle on a map with no known treasures prints only the two bookends;
everything else about that battle is Debug, file-only. Arm, claim, and marker lines only fire on
maps that carry tiles, so the gate holds by construction; `ScopedLogger` exists for any future
line that needs an explicit armed predicate.

## Event verbs (closed glossary)

The one source of truth for every log line's `[verb]` token. LogContractTests parses this table
and asserts it matches the `LogVerb` enum one-for-one; the doc and the code cannot drift. The
set is CLOSED: a new subsystem reuses one of these 11 verbs, or this table gets amended
deliberately; no ad-hoc per-module prefixes. The legacy column maps the pre-adoption prefixes so
old logs stay readable.

| Verb | Legacy prefix(es) | Level discipline |
|---|---|---|
| `startup` | bare startup lines, `runtime loop started.` | Info: the launch header; the runtime-loop line is the liveness canary. |
| `config` | `config:` | Info once per launch; Warning on read failure; the disabled-in-config notice. |
| `battle-start` | `battle: started` | Info once per battle; sentinels move to a `[trace]` companion. |
| `battle-end` | `battle: ended` | Info once per battle; sentinels in the `[trace]` companion. |
| `arm` | `treasure:` arming lines, fingerprint lines | Info for the armed line; Warning for fingerprint drift; arm churn is Debug. |
| `treasure` | `treasure:` hold and marker lines | Info for enhanced markers and chained-battle resets; Warning for uncapturable tiles, off-flag bytes, and a marker pointer that will not resolve. |
| `claim` | `claim:` | Info per claim and per refund; occupancy diagnostics are Debug. |
| `anchor` | build-key mismatch lines | Info for a successful signature re-resolve; Warning for the stand-down. |
| `save` | `treasure: dataset reloaded` | Info for dataset reload summaries; Warning for an unreadable dataset. |
| `engine` | `tick:` | Error: tick-loop internal errors, console-deduped per battle. |
| `trace` | `diag/engine:` `diag/state:` `diag/hold:` | Debug, file-only: sentinel transitions, hold breakdowns, id companions. |

## The launch header

Three Info lines at every launch (four when a battle report follows), console shape (no verb
brackets at Info tier):

```
[Treasure Master] [12:01:03.114] [INFO] Treasure Master is starting inside fft_enhanced.exe.
[Treasure Master] [12:01:03.120] [INFO] Configuration loaded: Enabled=True HideClaimedTiles=True.
[Treasure Master] [12:01:03.140] [INFO] The runtime loop has started; battles are being watched.
```

The config source path rides the `[trace]` companion in the file. A config read failure turns
the middle line into a Warning naming the defaults in force. The runtime-loop line is the
liveness canary that closes the header.

## The battle report

What a typical battle on a treasure map prints at the default LogLevel:

```
Battle started.
Map 74 Siedge Weald is armed: 4 treasure tile(s) held lit.
A treasure was claimed; 1 tile(s) on this map are now claimed and unlit.
Battle ended.
```

A battle on a map with no known treasures prints only the two bookends. Warnings that survive
to the console: fingerprint drift (armed anyway), uncapturable tiles, off-flag bytes, a marker
pointer that will not resolve, and the anchor stand-down after a game patch.

## Flight recorder (the black box)

An always-on, cheap, structured capture of on-change runtime events so the first live anomaly
of a session is diagnosable after the fact even if nobody was watching the console.

**Shape:** `FlightRecorder.cs` is the testable INSTANCE core: a bounded ring (capacity 4096,
oldest-dropped) of `(elapsedMs, type, payload)` records, every dependency (clock, wall clock,
file writer, retention lister/deleter) injected (`FlightRecorderTests`). `Flight.cs` is a
static null-object facade over it, mirroring `ModLogger`'s swappable-Instance idiom: every call
site is a silent no-op until `Flight.Init(modDir)` runs (once, from Mod.cs), which is what lets
every pre-existing test keep passing unmodified.

**What gets captured (on-change only, never per-tick):** battle enter/exit edges (Engine), arm
verdicts (TreasureMaster), claim and refund rulings, anchor re-resolve outcomes and stand-downs,
and dataset reloads. The jsonl record types (`"battle"`, `"arm"`, `"claim"`, `"anchor"`,
`"save"`) are a separate namespace from the console verbs; do not "align" them.

**Where files land:** `<modDir>/flight/flight_<yyyyMMdd_HHmmss>_<trigger>.jsonl`, one compact
JSON object per line (Newtonsoft.Json). The first line is a header object
(`{"hdr": true, "wall": "...", "t": <elapsedMs>}`) so a file's records can be cross-referenced
against `treasuremaster.log`'s `[HH:mm:ss.fff]` timestamps. Every other line is
`{"t": <elapsedMs>, "e": "<type>", "d": "<payload>"}`.

**Flush triggers:** (a) the battle-ENTER edge (`Flight.FlushBattleStart`): the previous
battle's tail is archived at the next battle's enter edge, the reliable moment when a session
usually ends in a process kill; (b) the battle-EXIT edge (`Flight.FlushBattleEnd`); both run
synchronously on Engine's own loop thread. (c) The first Error-tier line of a launch: FlushOnce,
flag-only at the log site (`Flight.RequestFlush("error")`), drained once per Engine tick
(`Flight.DrainPending`), so an error logged on any thread never stalls on disk I/O, and an
error storm cannot prune the retention into uselessness. (d) An anchor stand-down
(`Flight.RequestFlush("standdown")`): it happens before any battle edge is reachable and must
not depend on the error latch.

**Retention:** after every flush, files beyond the 20 newest are deleted (oldest-first).
`BuildLinked.ps1`'s clean step excludes the deployed `flight/` folder so a dev deploy does not
erase the evidence; `Publish.ps1` stages from the repo tree and needs no change.

**Invariants:** `FlightRecorder.Flush` never calls back into `ModLogger` (re-entrancy), an
empty ring flushes nothing, and a hard process kill loses only the records since the last
flush (`CanUnload()` is false; there is no exit hook).

## Reading old logs

`treasuremaster.prev.log` files and console pastes from before the adoption use the old shape
(console: `[FFTTreasureMaster] message`; file: `HH:mm:ss.fff [FFTTreasureMaster] message`;
module prefixes like `treasure:`/`claim:`/`diag/...`). The legacy column in the verb table
maps them onto today's verbs. Note the tag change: grep old logs for `[FFTTreasureMaster]`, new
ones for `[Treasure Master]`.
