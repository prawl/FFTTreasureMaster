using System;
using System.Collections.Generic;

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
    private readonly ClaimAudit   _claims;
    private readonly bool         _claimDetection;

    private readonly bool _enabled;
    private readonly FastHold _fastHold;

    private readonly HashSet<(int, int)> _claimed = new();
    private readonly Dictionary<int, int> _lastCount = new();   // itemId -> inventory count last tick
    private readonly Dictionary<int, int> _armCount  = new();   // itemId -> count at arm (immutable refund baseline)
    private readonly Dictionary<(int, int), int> _claimItem = new();   // claimed tile -> item id that triggered the claim
    private string?   _lastClaimDiag   = null;    // last logged scan diagnostic (log on change)
    private string?   _lastStateDiag   = null;    // TEMP (RetryDiagnostics): dedupe inLive/phase logs
    private string?   _lastHoldDiag    = null;    // TEMP (RetryDiagnostics): dedupe hold-breakdown logs

    private Phase     _phase          = Phase.Disarmed;
    private TreasureMap? _map         = null;    // the full map (identity/audit/fingerprint)
    private TreasureMap? _activeMap   = null;    // the hold/publish view (claimed tiles excluded)
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
    /// <param name="claimDetection">Claim detection gate; null = use Tuning.ClaimDetectionEnabled.
    /// When true, an eligible player unit standing on a tile claims it for the battle.</param>
    public TreasureMaster(TreasureDb db, IGameMemory? mem = null, bool? enabled = null, bool? claimDetection = null)
        : this(load: () => db, datasetStamp: () => null, mem: mem, enabled: enabled,
               claimDetection: claimDetection) { }

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
        bool? enabled = null,
        bool? claimDetection = null)
    {
        _load           = load;
        _datasetStamp   = datasetStamp;
        _db             = TreasureDb.MakeEmpty();   // placeholder; replaced on first Tick
        _mem            = mem ?? new LiveMemory();
        _audit          = new ArmAudit(_mem);
        _holder         = new TileHolder(_mem);
        _markers        = new MarkerWriter(_mem);   // Tuning.EnhancedMarkersEnabled gates it (off)
        _claims         = new ClaimAudit(_mem);
        _enabled        = enabled ?? Tuning.TreasureEnabled;
        _claimDetection = claimDetection ?? Tuning.ClaimDetectionEnabled;
        _fastHold       = new FastHold(_holder, Tuning.TreasureFastHoldMs);
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
        _activeMap       = null;
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
        _claimed.Clear();
        _lastCount.Clear();
        _armCount.Clear();
        _claimItem.Clear();
        _lastClaimDiag              = null;
        _lastStateDiag              = null;
        _lastHoldDiag               = null;
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

        // TEMP (RetryDiagnostics): log inLive/phase transitions so a Retry that drops the battle-map
        // gate (inLive=false) or fails to re-arm (phase stuck) is visible in the log.
        if (Tuning.RetryDiagnostics)
        {
            string sd = $"inLive={inLive} phase={_phase}";
            if (sd != _lastStateDiag) { _lastStateDiag = sd; Log.Info($"diag/state: {sd}"); }
        }

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
        _fastHold.Publish(_phase == Phase.Armed ? (_activeMap ?? _map) : null);
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
                _activeMap      = map;   // start with the full map; claim detection narrows it
                if (_claimDetection) InitClaimBaseline(map);
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

        // Claim detection: a tile is claimed when a unit stands on it AND its item count has risen
        // (the count rises only on a real claim by an eligible unit; the occupancy pins the exact
        // tile). Un-light every claimed tile every tick (survives a stale ~8ms FastHold pass).
        if (_claimDetection)
        {
            bool grew   = DetectClaims(map);
            bool shrank = DetectRefunds(map);
            RefreshClaimCounts(map);   // refresh per-tick baselines AFTER both decisions read them
            if (grew || shrank)
            {
                Log.Info($"claim: {_claimed.Count} tile(s) active" +
                         (shrank ? " (refund re-lit tiles)" : "") +
                         $": [{string.Join(", ", _claimed)}]");
                RebuildActiveMap();
            }
            foreach (var tile in map.Tiles)
            {
                if (_claimed.Contains((tile.X, tile.Y))) _holder.Unlight(tile);
            }
        }

        // Hold loop: per addr, OR in MarkValue only on a Resting byte.
        // Foreign bytes (off-screen tiles: camera pan, action camera) are skipped by the
        // per-addr ClassifyAddr check inside TileHolder.Hold -- no separate veto needed.
        var holdMap = _activeMap ?? map;
        var (wrote, foreign) = _holder.Hold(holdMap);
        WriteMarkers(holdMap);
        if (foreign > 0 && !_foreignLoggedThisBattle)
        {
            _foreignLoggedThisBattle = true;
            Log.Info($"treasure: map {map.MapId} {foreign} byte(s) off-flag (tiles off-screen?) " +
                     $"-- skipping those, holding the rest");
        }
        if (Tuning.RetryDiagnostics) LogHoldDiag(holdMap, wrote, foreign);
    }

    /// <summary>TEMP (RetryDiagnostics): logs a per-armed-tick breakdown of the held map's render
    /// bytes -- write count this tick, and resting/held/foreign/unreadable totals across all addrs,
    /// with each tile's first-addr byte value sampled. Deduped on change. Shows, after a Retry,
    /// whether the bytes are Foreign (skipped -> no marks) vs Resting (would paint) vs Held (already
    /// lit). All reads guarded.</summary>
    private void LogHoldDiag(TreasureMap map, int wrote, int foreign)
    {
        int rest = 0, held = 0, frgn = 0, unread = 0;
        var samples = new System.Collections.Generic.List<string>();
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                if (!_mem.Readable(addr, 1)) { unread++; continue; }
                switch (ClassifyAddr(_mem.U8(addr)))
                {
                    case AddrState.Resting: rest++; break;
                    case AddrState.Held:    held++; break;
                    case AddrState.Foreign: frgn++; break;
                }
            }
            if (tile.Addrs.Count > 0)
            {
                long a0 = tile.Addrs[0].Addr;
                string vv = _mem.Readable(a0, 1) ? _mem.U8(a0).ToString("X2") : "??";
                samples.Add($"({tile.X},{tile.Y}):{vv}");
            }
        }
        string diag = $"hold: tiles={map.Tiles.Count} wrote={wrote} rest={rest} held={held} " +
                      $"foreign={frgn} unread={unread} [{string.Join(" ", samples)}]";
        if (diag != _lastHoldDiag) { _lastHoldDiag = diag; Log.Info("diag/" + diag); }
    }

    // ── claim map rebuild ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="_activeMap"/> excluding any tiles whose (X,Y) is in
    /// <see cref="_claimed"/>. If no tiles are claimed, <see cref="_activeMap"/> equals
    /// <see cref="_map"/> (the full map). The full map is never modified.
    /// </summary>
    private void RebuildActiveMap()
    {
        if (_map is not { } fullMap) return;
        if (_claimed.Count == 0) { _activeMap = fullMap; return; }

        var filteredTiles = new System.Collections.Generic.List<TreasureTile>(fullMap.Tiles.Count);
        foreach (var t in fullMap.Tiles)
        {
            if (!_claimed.Contains((t.X, t.Y))) filteredTiles.Add(t);
        }
        _activeMap = new TreasureMap
        {
            MapId     = fullMap.MapId,
            Name      = fullMap.Name,
            TileCount = fullMap.TileCount,
            FpVer     = fullMap.FpVer,
            FpLen     = fullMap.FpLen,
            FpHashHex = fullMap.FpHashHex,
            Tiles     = filteredTiles,
        };
    }

    // ── claim detection: occupancy + inventory-count rise ──────────────────────────

    /// <summary>Snapshots the inventory counts of every tile's items at arm. The per-tick baseline
    /// (<see cref="_lastCount"/>) drives claim detection (a later rise marks a claim); the immutable
    /// arm baseline (<see cref="_armCount"/>) drives refund detection (a later return to it marks a
    /// reset refund). Called once when the map arms.</summary>
    private void InitClaimBaseline(TreasureMap map)
    {
        _lastCount.Clear();
        _armCount.Clear();
        _claimItem.Clear();
        foreach (var tile in map.Tiles)
        {
            TrackCount(tile.RareItemId);
            TrackCount(tile.CommonItemId);
            TrackArmCount(tile.RareItemId);
            TrackArmCount(tile.CommonItemId);
        }
    }

    /// <summary>Captures <paramref name="itemId"/>'s count into the immutable arm baseline, once.
    /// Self-guards on ContainsKey, so it is cheap to call every tick: if the count was unreadable at
    /// arm it is captured lazily on the first tick it becomes readable (before any claim, since a
    /// claim also needs a readable count to be detected). Never overwritten once set.</summary>
    private void TrackArmCount(int itemId)
    {
        if (itemId <= 0 || _armCount.ContainsKey(itemId)) return;
        int cur = _claims.ReadCount(itemId);
        if (cur >= 0) _armCount[itemId] = cur;
    }

    /// <summary>Refreshes the per-tick baseline (<see cref="_lastCount"/>) for every tile item and
    /// lazily fills any missing arm baseline. Called once per armed tick AFTER both DetectClaims and
    /// DetectRefunds have read the prior values, so a rise (claim) and a fall (refund) are both
    /// measured against the same prior tick.</summary>
    private void RefreshClaimCounts(TreasureMap map)
    {
        foreach (var tile in map.Tiles)
        {
            TrackCount(tile.RareItemId);
            TrackCount(tile.CommonItemId);
            TrackArmCount(tile.RareItemId);
            TrackArmCount(tile.CommonItemId);
        }
    }

    /// <summary>
    /// Latches each unclaimed tile that (a) has a unit standing on it AND (b) had one of its item
    /// counts rise since last tick, recording WHICH item id rose (preferring the rare item) so a
    /// later refund can be classified. The count rises only on a real claim by an eligible unit, so
    /// this needs no per-unit eligibility byte; occupancy pins the exact tile (so maps that reuse an
    /// item id across tiles do not over-latch). Logs the occupied-treasure-tile set on change ONLY
    /// when Tuning.ClaimDiagnostics is on (default OFF -- it spams a moving battle otherwise).
    /// Returns true if any new tile latched. The caller refreshes the tracked counts
    /// (RefreshClaimCounts) after this AND DetectRefunds have read the prior values.
    /// </summary>
    private bool DetectClaims(TreasureMap map)
    {
        var occupied = new HashSet<(int, int)>();
        _claims.CollectOccupied(occupied);

        bool grew = false;
        // Diagnostic-only: the occupied-tile dump is gated behind Tuning.ClaimDiagnostics (default
        // OFF) -- it re-fires on every step onto/off a treasure tile, so it spams a moving battle.
        // Null when off, which also short-circuits the per-tile ReadCount game-memory reads below.
        var occDiag = Tuning.ClaimDiagnostics ? new System.Collections.Generic.List<string>() : null;
        foreach (var tile in map.Tiles)
        {
            var key = (tile.X, tile.Y);
            if (!occupied.Contains(key)) continue;
            occDiag?.Add($"({tile.X},{tile.Y}) r{tile.RareItemId}={_claims.ReadCount(tile.RareItemId)} " +
                         $"c{tile.CommonItemId}={_claims.ReadCount(tile.CommonItemId)}");
            if (_claimed.Contains(key)) continue;

            bool rareRose = ItemCountRose(tile.RareItemId);
            bool commonRose = !rareRose && ItemCountRose(tile.CommonItemId);
            if (rareRose || commonRose)
            {
                _claimed.Add(key);
                _claimItem[key] = rareRose ? tile.RareItemId : tile.CommonItemId;
                grew = true;
            }
        }

        if (occDiag != null)
        {
            string diag = string.Join("  ", occDiag);
            if (diag != _lastClaimDiag)
            {
                _lastClaimDiag = diag;
                if (diag.Length > 0)
                    Log.Info($"claim: map {map.MapId} unit(s) on treasure tile(s) -- {diag}");
            }
        }
        return grew;
    }

    /// <summary>
    /// Detects a battle RESET from the inventory refund it produces, and on detection clears EVERY
    /// claim so all tiles re-light. This is the fix for the in-battle "restart from the start" path,
    /// which produces no sustained out-of-live window, so the battle-exit edge never fires and
    /// ResetBattle never runs -- yet the restart refunds claimed items (their inventory counts drop
    /// back to the arm baseline). Returns true if any claim was cleared.
    ///
    /// A reset refunds ALL claims together, so detection keys off signals a single legitimate action
    /// cannot fake:
    ///   * a RARE-claimed tile back at its arm baseline. A rare count cannot drop mid-battle (FFT
    ///     has no in-battle equip, and rare Move-Find items are not consumables), so a rare refund
    ///     is an unambiguous reset. Clearing ALL claims on it also re-lights common-claimed tiles
    ///     whose refunds straggle across ticks.
    ///   * >= 2 DISTINCT item ids dropping to baseline in the SAME tick (edge-triggered). A lone
    ///     consumable use drops one item count; two unrelated uses never land on one 33ms tick.
    /// Counting DISTINCT item ids (not tiles) is load-bearing: maps reuse a single item id across
    /// several tiles (see ClaimAudit), so a single use of a shared id edge-trips every tile claimed
    /// via it -- counting tiles would fake a reset, counting item ids correctly sees one drop.
    /// Edge-triggering (the drop happened THIS tick) is equally load-bearing: a level check would let
    /// two staggered consumable uses -- each leaving a different count sitting at baseline -- co-occur
    /// and fake a reset. Counts keyed by global item id mean we cannot do better per-tile; an
    /// ALL-common claim set whose refunds straggle across ticks (or a single common claim) is a
    /// documented limitation -- still strictly better than the pre-fix dark-after-restart.
    /// </summary>
    private bool DetectRefunds(TreasureMap map)
    {
        if (_claimed.Count == 0) return false;

        bool rareRefund = false;                 // a rare-claimed tile back at baseline: definitive reset
        var  edgeDropItems = new HashSet<int>(); // DISTINCT item ids that fell to <= baseline THIS tick
        foreach (var tile in map.Tiles)
        {
            var key = (tile.X, tile.Y);
            if (!_claimed.Contains(key)) continue;
            if (!_claimItem.TryGetValue(key, out int item)) continue;
            if (!_armCount.TryGetValue(item, out int baseCount)) continue;
            int cur = _claims.ReadCount(item);
            if (cur < 0 || cur > baseCount) continue;   // not at or below the arm baseline

            // Rare slot only: when a tile's rare and common ids coincide we cannot tell which slot
            // dropped, so treat it as common (conservative -- a lone drop must not fake a reset).
            if (item == tile.RareItemId && tile.RareItemId != tile.CommonItemId) rareRefund = true;
            if (_lastCount.TryGetValue(item, out int prev) && prev > baseCount) edgeDropItems.Add(item);
        }

        if (!rareRefund && edgeDropItems.Count < 2) return false;   // not a reset: leave claims latched

        _claimed.Clear();
        _claimItem.Clear();
        return true;
    }

    /// <summary>True when <paramref name="itemId"/>'s current inventory count exceeds the value
    /// tracked last tick (a fresh increment). False for non-items or unreadable counts.</summary>
    private bool ItemCountRose(int itemId)
    {
        if (itemId <= 0) return false;
        int cur = _claims.ReadCount(itemId);
        return cur >= 0 && _lastCount.TryGetValue(itemId, out int prev) && cur > prev;
    }

    /// <summary>Refreshes the tracked count for <paramref name="itemId"/> (no-op for non-items or
    /// unreadable reads, so a count that only becomes readable mid-battle never reads as a rise).</summary>
    private void TrackCount(int itemId)
    {
        if (itemId <= 0) return;
        int cur = _claims.ReadCount(itemId);
        if (cur >= 0) _lastCount[itemId] = cur;
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
