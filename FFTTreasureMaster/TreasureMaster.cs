using System;

namespace FFTTreasureMaster;

/// <summary>
/// Treasure Master: auto-holds the highlight value (0xCC -- cyan border, yellow fill) on each
/// treasure tile's render-flag bytes, keeping treasure tiles marked on the battlefield map.
/// Gated by a single config toggle
/// (Config.Enabled, default on); when disabled the module is permanently idle and reads
/// no game memory.
///
/// Containment -- L0/L1/L3 gate writes; L2 is ADVISORY (LIVE INCIDENT #5):
///   L0 build key:  dataset PE key vs live header (global disarm on mismatch).
///   L1 map id:     guarded U8 @ LiveBattleMapId, valid 1..127, present in the dataset.
///   L2 identity:   ADVISORY ONLY -- FNV-1a64 over the terrain prefix is logged on mismatch
///                  (per-battle weather perturbs the hashed fields) but never blocks arming.
///   L3 per-write:  per-addr Writable guard + ClassifyAddr check; Foreign bytes (off-screen
///                  render bytes from camera pan / action camera) are never written. At arm
///                  time, a quorum of TreasureMinPlausibleAddrs ok addrs is required; below
///                  quorum the module polls indefinitely rather than disarming permanently.
///
/// Set-only by construction: the module has no Clear path. Release = stop writing; the
/// engine clears marks itself (LIVE_LEDGER). Writes are CharmLock.Force-idiom: Writable
/// guard -> U8 read -> write MarkValue (0xCC) only on difference.
///
/// The stateless gate evaluations (PE key, fingerprint, per-addr audit) live in ArmAudit.cs
/// (a separate class -- a partial would be same-state-machine evasion per the house rules).
/// The pure policy statics (ClassifyAddr, Fnv1a64, DecideArm, etc.) live in TreasureMaster.Policy.cs.
/// </summary>
internal sealed partial class TreasureMaster : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    // ── internal state ────────────────────────────────────────────────────────────

    private enum Phase { Disarmed, Arming, Armed }

    private TreasureDb            _db;
    private readonly Func<TreasureDb>    _load;
    private readonly Func<DateTime?>     _datasetStamp;
    private readonly IGameMemory  _mem;
    private readonly ArmAudit     _audit;
    private readonly TileHolder   _holder;
    private readonly MarkerWriter _markers;   // native yellow-diamond path (dark until verified)

    private readonly bool _enabled;
    private readonly FastHold _fastHold;

    private Phase     _phase          = Phase.Disarmed;
    private TreasureMap? _map         = null;    // the active map, null when none found
    private int       _stableTicks    = 0;       // consecutive ticks with the same valid map id
    private byte      _stableMapId    = 0;
    private int       _armAttempts    = 0;
    private int       _revalidateTick = 0;
    private int       _badMapTicks    = 0;       // consecutive bad-map-id ticks while ARMED
    private bool      _globalIdle     = false;   // permanent disarm set at first tick
    private bool      _globalIdleChecked = false;
    private bool      _naggedThisBattle  = false; // stub/missing nag once per battle
    private bool      _capLoggedThisBattle = false;
    private bool      _foreignLoggedThisBattle = false; // armed off-screen log once per battle
    private bool      _markersLoggedThisBattle = false; // enhanced-marker first-write log once per battle
    private string?   _lastMarkerProbe = null;           // last logged marker failure-probe (dedupe)
    private bool      _flapLoggedThisBattle = false;    // fingerprint-mismatch log once per battle (arm or mid-battle)

    private int       _stampCheckCountdown = 0;   // ticks until the next stamp comparison
    private DateTime? _lastStamp = null;           // stamp seen at the last check (null = not yet checked)
    private bool      _stampInitialized = false;   // false until the first load has run

    /// <param name="enabled">Master on/off gate; null = use Tuning.TreasureEnabled (the documented
    /// default). When false the module is permanently idle and never reads game memory.</param>
    public TreasureMaster(TreasureDb db, IGameMemory? mem = null, bool? enabled = null)
        : this(load: () => db, datasetStamp: () => null, mem: mem, enabled: enabled) { }

    /// <summary>
    /// Injectable seam ctor used by tests and Engine alike.
    /// <paramref name="load"/> is called eagerly on the first Tick and again whenever
    /// <paramref name="datasetStamp"/> returns a value that differs from the last seen stamp.
    /// The stamp returning null is treated as "unchanged" (no-reload).
    /// </summary>
    public TreasureMaster(
        Func<TreasureDb> load,
        Func<DateTime?> datasetStamp,
        IGameMemory? mem = null,
        bool? enabled = null)
    {
        _load         = load;
        _datasetStamp = datasetStamp;
        _db           = TreasureDb.MakeEmpty();   // placeholder; replaced on first Tick
        _mem          = mem ?? new LiveMemory();
        _audit        = new ArmAudit(_mem);
        _holder       = new TileHolder(_mem);
        _markers      = new MarkerWriter(_mem);   // Tuning.EnhancedMarkersEnabled gates it (off)
        _enabled     = enabled ?? Tuning.TreasureEnabled;
        _fastHold     = new FastHold(_holder, Tuning.TreasureFastHoldMs);
        // Thread not started here -- tests must not spawn OS threads; Engine calls StartFastHold().
    }

    /// <summary>Starts the fast-hold background thread. Called by Engine after construction.</summary>
    public void StartFastHold() => _fastHold.Start();

    /// <summary>Exposes the fast-hold instance so tests can drive HoldOnce without a thread.</summary>
    internal FastHold FastHold => _fastHold;

    // ── ISignature ────────────────────────────────────────────────────────────────

    public void ResetBattle()
    {
        _phase           = Phase.Disarmed;
        _map             = null;
        _stableTicks     = 0;
        _stableMapId     = 0;
        _armAttempts     = 0;
        _revalidateTick  = 0;
        _badMapTicks     = 0;
        _naggedThisBattle           = false;
        _capLoggedThisBattle        = false;
        _foreignLoggedThisBattle    = false;
        _markersLoggedThisBattle    = false;
        _lastMarkerProbe            = null;
        _flapLoggedThisBattle       = false;
        // _globalIdle and _globalIdleChecked persist -- the L0 check is startup-once.
        _fastHold.Publish(null);    // immediacy on battle-exit: stop holding before Tick runs again
    }

    // ── entry point ───────────────────────────────────────────────────────────────

    public void Tick(DateTime now, bool inLive)
    {
        // Eager initial load + periodic stamp-change hot-reload (runs regardless of phase or inLive).
        if (!_stampInitialized)
        {
            _db               = _load();
            _stampInitialized = true;
            _lastStamp        = _datasetStamp();
            _stampCheckCountdown = Tuning.TreasureStampCheckTicks;
        }
        else
        {
            if (--_stampCheckCountdown <= 0)
            {
                _stampCheckCountdown = Tuning.TreasureStampCheckTicks;
                var current = _datasetStamp();
                if (current.HasValue && current != _lastStamp)
                {
                    _lastStamp = current;
                    _db = _load();
                    // Full state reset so L0 re-evaluates against the new dataset.
                    _globalIdle        = false;
                    _globalIdleChecked = false;
                    ResetBattle();
                    var mapCount = 0;
                    foreach (var m in _db.Maps)
                        if (m.Tiles.Count > 0) mapCount++;
                    Log.Info($"treasure: dataset reloaded -- {mapCount} map(s) with addresses");
                }
            }
        }

        // L0: one-time global idle check (config disabled, dataset empty, or key mismatch).
        // If CheckGlobalIdle defers (PE header not yet readable), _globalIdleChecked resets
        // to false -- return immediately so the phase switch cannot run before L0 resolves.
        if (!_globalIdleChecked)
        {
            CheckGlobalIdle();
            if (!_globalIdleChecked) { _fastHold.Publish(null); return; }
        }
        if (_globalIdle) { _fastHold.Publish(null); return; }

        if (!inLive)
        {
            _stableTicks = 0;   // stability counter resets when not in live battle
            _fastHold.Publish(null);
            return;
        }

        switch (_phase)
        {
            case Phase.Disarmed:      TickDisarmed(); break;
            case Phase.Arming:        TickArming();   break;
            case Phase.Armed:         TickArmed();    break;
        }

        // Single publish point: hold exactly when phase==Armed AND inLive.
        _fastHold.Publish(_phase == Phase.Armed ? _map : null);
    }

    // ── L0 global-idle check (once at first tick) ─────────────────────────────────

    private void CheckGlobalIdle()
    {
        _globalIdleChecked = true;

        if (!_enabled)
        {
            _globalIdle = true;
            Log.Info("treasure: disabled in config -- module idle (enable it in the Reloaded mod config to mark treasure tiles)");
            return;
        }

        if (_db.Maps.Count == 0)
        {
            _globalIdle = true;
            return;   // silent: no dataset, no output
        }

        // If the dataset has a build key, compare it to the live PE header.
        if (_db.BuildKey is { } bk)
        {
            var live = _audit.ReadPeBuildKey();
            if (live is null)
            {
                // Can't read the header yet -- don't mark global idle, retry next tick.
                _globalIdleChecked = false;
                return;
            }
            if (!BuildKeyMatches(
                    (uint)bk.TimeDateStamp, (uint)bk.SizeOfImage,
                    live.Value.TimeDateStamp, live.Value.SizeOfImage))
            {
                _globalIdle = true;
                Log.Info($"treasure: dataset built for game {bk.TimeDateStamp:X}/{bk.SizeOfImage:X} " +
                         $"but running {live.Value.TimeDateStamp:X}/{live.Value.SizeOfImage:X} " +
                         $"-- disarmed, re-capture needed");
                return;
            }
        }
        // Build key null (stub-only dataset) or matches: proceed.
    }

    // ── DISARMED tick ─────────────────────────────────────────────────────────────

    private void TickDisarmed()
    {
        if (!_audit.TryReadMapId(out byte mapId)) { _stableTicks = 0; return; }

        if (mapId == _stableMapId)
            _stableTicks++;
        else
        {
            _stableMapId = mapId;
            _stableTicks = 1;
        }

        if (_stableTicks < Tuning.TreasureArmStableTicks) return;

        // Stable: look up the db.
        TreasureMap? found = null;
        foreach (var m in _db.Maps)
        {
            if (m.MapId == mapId) { found = m; break; }
        }

        if (found is null) return;   // unknown map: silent

        if (found.Tiles.Count == 0)
        {
            // Stub (no tiles) -- nag once per battle.
            if (!_naggedThisBattle)
            {
                _naggedThisBattle = true;
                Log.Info($"treasure: map {mapId} {found.Name} has {found.TileCount} treasure " +
                         $"tile(s), not captured -- run treasure_flags.py session");
            }
            return;
        }

        // Has tiles + module enabled (checked once at startup in CheckGlobalIdle): transition to ARMING.
        _map         = found;
        _armAttempts = 0;
        _phase       = Phase.Arming;
    }

    // ── ARMING tick ───────────────────────────────────────────────────────────────

    private void TickArming()
    {
        var map = _map!;

        // Advisory fingerprint check -- TELEMETRY ONLY, never blocks arming (LIVE INCIDENT #5).
        // Terrain fingerprints proved unreliable as a gate: per-battle weather (rain) perturbs
        // the hashed terrain fields, so a map captured in one weather state fails to match in
        // another -- and there is no data to know which maps can weather. Containment is carried
        // by the build key (L0), the per-tick map-id match (L1, unique per map) and the per-tile
        // resting-byte audit + quorum below (L3). A mismatch is logged once per battle as a drift
        // census but does NOT disarm. Map-id-only maps have no fingerprint to check.
        if (!map.IsMapIdOnly && !_flapLoggedThisBattle && !_audit.FingerprintMatches(map))
        {
            _flapLoggedThisBattle = true;
            var d = _audit.FingerprintDiag(map);
            Log.Info($"treasure: map {map.MapId} fingerprint mismatch -- arming on map-id + quorum anyway " +
                     $"(weather/terrain drift; readOk={d.ReadOk} fpVer={d.FpVer} got={d.Got:X} want={d.Expected:X})");
        }

        var (verdict, _) = _audit.AuditAddrs(map, Tuning.TreasureMinPlausibleAddrs);

        switch (verdict)
        {
            case ArmVerdict.Arm:
                _phase          = Phase.Armed;
                _revalidateTick = 0;
                Log.Info($"treasure: map {map.MapId} {map.Name} armed -- " +
                         $"{map.Tiles.Count} tile(s)" +
                         (map.IsMapIdOnly ? " (map-id-only)" : ""));
                _holder.Hold(map);
                WriteMarkers(map);   // no-op while dark; native yellow diamonds once enabled
                break;

            case ArmVerdict.Retry:
                _armAttempts++;
                if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
                {
                    _capLoggedThisBattle = true;
                    Log.Info($"treasure: map {map.MapId} waiting to arm -- " +
                             $"flag bytes not in rest state (tiles off-screen?)");
                }
                break;
        }
    }

    // ── ARMED tick ────────────────────────────────────────────────────────────────

    private void TickArmed()
    {
        var map = _map!;

        // Re-check map id every tick (single read -- two reads can disagree mid-frame).
        bool mapOk = _audit.TryReadMapId(out byte currentMapId) && currentMapId == map.MapId;

        if (!mapOk)
        {
            _badMapTicks++;
            if (_badMapTicks >= Tuning.TreasureMapIdBadTicksToReset)
            {
                // Map changed (chained battle) or something went wrong -- full reset.
                Log.Info($"treasure: map id changed from {map.MapId} -- resetting for new battle");
                ResetBattle();
            }
            return;   // suspend writes this tick
        }
        _badMapTicks = 0;

        // Mid-battle fingerprint check -- INFORMATIONAL ONLY (skipped for map-id-only maps,
        // and once the first drift has been logged this battle).
        //
        // Terrain identity was already proven at ARM time (TickArming); the per-tick map-id
        // re-check above is the live "still in this battle" guard, and the per-addr ClassifyAddr
        // gate in the hold loop backstops stray writes. A fingerprinted map whose "static"
        // terrain fields drift mid-battle (LIVE INCIDENT #4: Siedge Weald map 74 -- fields
        // {2,3,4,5} changed ~26 s into the SAME battle) must NOT disarm: the old behavior
        // dropped to ARMING, stopped holding, and -- when the new terrain state persisted --
        // permanently disarmed for the battle, killing the marks for the rest of the fight. We
        // log the first drift per battle (a free in-the-wild drift census) and keep holding.
        if (!map.IsMapIdOnly && !_flapLoggedThisBattle
                && ++_revalidateTick >= Tuning.TreasureRevalidateEveryNTicks)
        {
            _revalidateTick = 0;
            if (!_audit.FingerprintMatches(map))
            {
                _flapLoggedThisBattle = true;
                Log.Info($"treasure: map {map.MapId} terrain drifted mid-battle -- " +
                         $"holding marks through it (fingerprint no longer matches; not a disarm)");
            }
        }

        // Hold loop: per addr, OR in 0x80 only on a Resting byte.
        // Foreign bytes (off-screen tiles: camera pan, action camera) are skipped by the
        // per-addr ClassifyAddr check inside TileHolder.Hold -- no separate veto needed.
        var (_, foreign) = _holder.Hold(map);
        WriteMarkers(map);
        if (foreign > 0 && !_foreignLoggedThisBattle)
        {
            _foreignLoggedThisBattle = true;
            Log.Info($"treasure: map {map.MapId} {foreign} byte(s) off-flag (tiles off-screen?) " +
                     $"-- skipping those, holding the rest");
        }
    }

    // ── enhanced-marker write (native yellow diamonds) ─────────────────────────────

    /// <summary>
    /// Drives the EnhancedMarker write path and logs the first successful write per battle so a
    /// live test isn't blind: the "active" line means the utility pointer resolved and markers
    /// were written; its ABSENCE (no diamonds + no line) means the pointer never resolved
    /// (Offsets.EnhancedMarkingUtilityPtr wrong/null). No-op + silent while the path is gated off.
    /// </summary>
    private void WriteMarkers(TreasureMap map)
    {
        int wrote = _markers.Write(map);
        if (wrote > 0)
        {
            if (!_markersLoggedThisBattle)
            {
                _markersLoggedThisBattle = true;
                Log.Info($"treasure: enhanced markers active -- wrote {wrote} slot(s) for map {map.MapId} {map.Name}");
            }
            return;
        }

        // Wrote nothing: log WHY (which guard failed), de-duped so a stable state logs once.
        var (readable, basePtr, writable) = _markers.Resolve();
        string probe = $"readable={readable} base=0x{basePtr:X} writable={writable}";
        if (probe != _lastMarkerProbe)
        {
            _lastMarkerProbe = probe;
            Log.Info($"treasure: enhanced markers NOT written -- ptr@0x{Offsets.EnhancedMarkingUtilityPtr:X} {probe}");
        }
    }
}
