using System;
using System.Collections.Generic;
using System.Linq;

namespace FFTTreasureMaster;

/// <summary>
/// Keeps every treasure tile marked by holding the highlight value on its render flag bytes.
/// One config toggle gates it, and when disabled the module is permanently idle and reads no
/// game memory.
///
/// Containment. L0 compares the dataset's PE build key with the live header and disarms
/// globally on a mismatch. L1 reads the map id behind a guard and needs it valid and present in
/// the dataset. L2, the terrain fingerprint, is advisory only, logged on mismatch and never a
/// block, because per battle weather perturbs the hashed fields. L3 guards every write with
/// Writable and ClassifyAddr, so Foreign bytes are never written, and arming needs a quorum of
/// TreasureMinPlausibleAddrs ok addresses, below which the module polls rather than disarming.
///
/// Set only by construction. Release means stop writing, and the engine clears marks itself.
/// The stateless gate evaluations live in ArmAudit.cs as a separate class, and the pure policy
/// statics live in TreasureMaster.Policy.cs.
/// </summary>
internal sealed partial class TreasureMaster : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    // ── internal state ────────────────────────────────────────────────────────────

    private enum Phase { Disarmed, Arming, Armed }

    private TreasureDb _db;
    private readonly Func<TreasureDb> _load;
    private readonly Func<DateTime?> _datasetStamp;
    private readonly IGameMemory _mem;
    private readonly AddrMap _addrs;
    private readonly Func<TreasureDb, AnchorResolution?>? _resolver;
    private readonly ArmAudit _audit;
    private readonly TileHolder _holder;
    private readonly MarkerWriter _markers;   // native yellow-diamond path (dark until verified)
    private readonly ClaimAudit _claims;
    private readonly bool _claimDetection;

    private readonly bool _enabled;
    private readonly FastHold _fastHold;

    private readonly CollectAudit _collect;
    private readonly bool _collectDetection;
    private readonly Func<int, TreasureTile, bool>? _collectedOracle;
    private readonly HashSet<(int, int)> _collected = new();   // tiles collected+won in a PRIOR battle (persistent; outlives refunds)

    // Feature flags for a build re-resolved by signature (see AnchorResolver). All start true --
    // the fast path (matching build key) never touches them, so config alone decides, byte-
    // identical to before AddrMap/AnchorResolver existed.
    private bool _claimOkForBuild = true;
    private bool _collectOkForBuild = true;
    private bool _fingerprintOkForBuild = true;

    private readonly HashSet<(int, int)> _claimed = new();
    private readonly Dictionary<int, int> _lastCount = new();   // itemId -> inventory count last tick
    private readonly Dictionary<int, int> _armCount = new();   // itemId -> count at arm (immutable refund baseline)
    private readonly Dictionary<(int, int), int> _claimItem = new();   // claimed tile -> item id that triggered the claim
    private string? _lastClaimDiag = null;    // last logged scan diagnostic (log on change)
    private string? _lastStateDiag = null;    // TEMP (RetryDiagnostics): dedupe inLive/phase logs
    private string? _lastHoldDiag = null;    // TEMP (RetryDiagnostics): dedupe hold-breakdown logs

    private Phase _phase = Phase.Disarmed;
    private TreasureMap? _map = null;    // the full map (identity/audit/fingerprint)
    private TreasureMap? _activeMap = null;    // the hold/publish view (claimed tiles excluded)
    private int _stableTicks = 0;       // consecutive ticks with the same valid map id
    private byte _stableMapId = 0;
    private int _armAttempts = 0;
    private int _revalidateTick = 0;
    private int _badMapTicks = 0;       // consecutive bad-map-id ticks while ARMED
    private bool _globalIdle = false;   // permanent disarm set at first tick
    private bool _globalIdleChecked = false;
    private bool _naggedThisBattle = false; // stub/missing nag once per battle
    private bool _capLoggedThisBattle = false;
    private bool _foreignLoggedThisBattle = false; // armed off-screen log once per battle
    private bool _markersLoggedThisBattle = false; // enhanced-marker first-write log once per battle
    private string? _lastMarkerProbe = null;           // last logged marker failure-probe (dedupe)
    private bool _flapLoggedThisBattle = false;    // fingerprint-mismatch log once per battle (arm or mid-battle)

    private int _stampCheckCountdown = 0;   // ticks until the next stamp comparison
    private DateTime? _lastStamp = null;           // stamp seen at the last check (null = not yet checked)
    private bool _stampInitialized = false;   // false until the first load has run

    /// <param name="enabled">Master on/off gate; null = use Tuning.TreasureEnabled (the documented
    /// default). When false the module is permanently idle and never reads game memory.</param>
    /// <param name="claimDetection">Claim detection gate; null = use Tuning.ClaimDetectionEnabled.
    /// When true, an eligible player unit standing on a tile claims it for the battle.</param>
    public TreasureMaster(TreasureDb db, IGameMemory? mem = null, bool? enabled = null,
        bool? claimDetection = null, bool? collectDetection = null,
        Func<int, TreasureTile, bool>? collectedOracle = null,
        Func<TreasureDb, AnchorResolution?>? resolver = null, AddrMap? addrs = null)
        : this(load: () => db, datasetStamp: () => null, mem: mem, enabled: enabled,
               claimDetection: claimDetection, collectDetection: collectDetection,
               collectedOracle: collectedOracle, resolver: resolver, addrs: addrs)
    { }

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
        bool? claimDetection = null,
        bool? collectDetection = null,
        Func<int, TreasureTile, bool>? collectedOracle = null,
        Func<TreasureDb, AnchorResolution?>? resolver = null,
        AddrMap? addrs = null)
    {
        _load = load;
        _datasetStamp = datasetStamp;
        _db = TreasureDb.MakeEmpty();   // placeholder; replaced on first Tick
        _mem = mem ?? new LiveMemory();
        _addrs = addrs ?? new AddrMap();
        _resolver = resolver;
        _audit = new ArmAudit(_mem, _addrs);
        _holder = new TileHolder(_mem);
        _markers = new MarkerWriter(_mem);   // Tuning.EnhancedMarkersEnabled gates it (off)
        _claims = new ClaimAudit(_mem, _addrs);
        _collect = new CollectAudit(_mem, _addrs);
        _enabled = enabled ?? Tuning.TreasureEnabled;
        _claimDetection = claimDetection ?? Tuning.ClaimDetectionEnabled;
        _collectDetection = collectDetection ?? Tuning.CollectDetectionEnabled;
        _collectedOracle = collectedOracle
            ?? (_collectDetection ? (Func<int, TreasureTile, bool>)_collect.IsCollected : null);
        _fastHold = new FastHold(_holder, Tuning.TreasureFastHoldMs);
        // The thread is not started here, since tests must not spawn OS threads. Engine starts it.
    }

    /// <summary>Starts the fast-hold background thread. Called by Engine after construction.</summary>
    public void StartFastHold() => _fastHold.Start();

    /// <summary>Exposes the fast-hold instance so tests can drive HoldOnce without a thread.</summary>
    internal FastHold FastHold => _fastHold;

    // ── ISignature ────────────────────────────────────────────────────────────────

    public void ResetBattle()
    {
        _phase = Phase.Disarmed;
        _map = null;
        _activeMap = null;
        _stableTicks = 0;
        _stableMapId = 0;
        _armAttempts = 0;
        _revalidateTick = 0;
        _badMapTicks = 0;
        _naggedThisBattle = false;
        _capLoggedThisBattle = false;
        _foreignLoggedThisBattle = false;
        _markersLoggedThisBattle = false;
        _lastMarkerProbe = null;
        _flapLoggedThisBattle = false;
        _claimed.Clear();
        _collected.Clear();
        _lastCount.Clear();
        _armCount.Clear();
        _claimItem.Clear();
        _lastClaimDiag = null;
        _lastStateDiag = null;
        _lastHoldDiag = null;
        // _globalIdle and _globalIdleChecked persist, since the L0 check runs once per startup.
        _fastHold.Publish(null);    // stop holding right away, before Tick runs again
    }

    // ── entry point ───────────────────────────────────────────────────────────────

    public void Tick(DateTime now, bool inLive)
    {
        // Eager initial load + periodic stamp-change hot-reload (runs regardless of phase or inLive).
        if (!_stampInitialized)
        {
            _db = _load();
            _stampInitialized = true;
            _lastStamp = _datasetStamp();
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
                    _globalIdle = false;
                    _globalIdleChecked = false;
                    _claimOkForBuild = true;
                    _collectOkForBuild = true;
                    _fingerprintOkForBuild = true;
                    ResetBattle();
                    var mapCount = 0;
                    foreach (var m in _db.Maps)
                        if (m.Tiles.Count > 0) mapCount++;
                    ModLogger.Event(LogVerb.Save, $"The treasure dataset was reloaded: {mapCount} map(s) carry addresses.");
                    Flight.Record("save", $"dataset reloaded maps={mapCount}");
                }
            }
        }

        // The one time L0 global idle check (config off, dataset empty, or key mismatch). When
        // CheckGlobalIdle defers because the PE header is not readable yet, _globalIdleChecked
        // resets to false, so return now and keep the phase switch from running before L0 resolves.
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
            if (sd != _lastStateDiag) { _lastStateDiag = sd; ModLogger.Debug(LogVerb.Trace, $"state {sd}"); }
        }

        if (!inLive)
        {
            _stableTicks = 0;   // stability counter resets when not in live battle
            _fastHold.Publish(null);
            return;
        }

        switch (_phase)
        {
            case Phase.Disarmed: TickDisarmed(); break;
            case Phase.Arming: TickArming(); break;
            case Phase.Armed: TickArmed(); break;
        }

        // The single publish point. Hold exactly when the phase is Armed and inLive.
        _fastHold.Publish(_phase == Phase.Armed ? (_activeMap ?? _map) : null);
    }

    // ── L0 global-idle check (once at first tick) ─────────────────────────────────

    private void CheckGlobalIdle()
    {
        _globalIdleChecked = true;

        if (!_enabled)
        {
            _globalIdle = true;
            ModLogger.Event(LogVerb.Config, "Treasure marking is disabled in the mod configuration; enable it in the Reloaded launcher to mark treasure tiles.");
            return;
        }

        if (_db.Maps.Count == 0)
        {
            _globalIdle = true;
            return;   // no dataset, so stay silent
        }

        // If the dataset has a build key, compare it to the live PE header.
        if (_db.BuildKey is { } bk)
        {
            var live = _audit.ReadPeBuildKey();
            if (live is null)
            {
                // The header is not readable yet, so do not mark global idle and retry next tick.
                _globalIdleChecked = false;
                return;
            }
            if (!BuildKeyMatches(
                    (uint)bk.TimeDateStamp, (uint)bk.SizeOfImage,
                    live.Value.TimeDateStamp, live.Value.SizeOfImage))
            {
                string mismatchDetail =
                    $"anchor dataset build {bk.TimeDateStamp:X}/{bk.SizeOfImage:X}, " +
                    $"running build {live.Value.TimeDateStamp:X}/{live.Value.SizeOfImage:X}";

                var res = _resolver?.Invoke(_db);
                if (res is null)
                {
                    _globalIdle = true;
                    ModLogger.WarnWithTrace(LogVerb.Anchor,
                        _resolver is null
                            ? "The game build does not match the treasure dataset; treasures stay unmarked until a re-capture."
                            : "The game build does not match the treasure dataset and the signature re-resolve failed; treasures stay unmarked until a re-capture.",
                        mismatchDetail);
                    Flight.Record("anchor", "standdown " + mismatchDetail);
                    Flight.RequestFlush("standdown");
                    return;
                }

                _addrs.Apply(res);
                _db = AnchorRemap.Remap(_db, res.RegionDeltas);
                _claimOkForBuild = res.ClaimOk;
                _collectOkForBuild = res.CollectOk;
                _fingerprintOkForBuild = res.FingerprintOk;

                var mapCount = 0;
                foreach (var m in _db.Maps) if (m.Tiles.Count > 0) mapCount++;
                string regions = string.Join(",", res.RegionDeltas.Select(kv => $"{kv.Key}={kv.Value:X}"));
                string derived = string.Join(",", res.DerivedRegions);
                ModLogger.EventWithTrace(LogVerb.Anchor,
                    $"The game build changed and the addresses were re-resolved by signature: {mapCount} map(s) remapped.",
                    $"anchor regions=[{regions}] derived=[{derived}] claim={_claimOkForBuild} " +
                    $"collect={_collectOkForBuild} fingerprint={_fingerprintOkForBuild} ({mismatchDetail})");
                Flight.Record("anchor", $"resolved regions=[{regions}] derived=[{derived}]");
                // Fall through and arm against the remapped dataset.
            }
        }
        // The build key is null (a stub only dataset) or matches, so proceed.
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

        // Stable, so look up the db.
        TreasureMap? found = null;
        foreach (var m in _db.Maps)
        {
            if (m.MapId == mapId) { found = m; break; }
        }

        if (found is null) return;   // unknown map, stay silent

        if (found.Tiles.Count == 0)
        {
            // A stub with no tiles, so nag once per battle.
            if (!_naggedThisBattle)
            {
                _naggedThisBattle = true;
                ModLogger.WarnWithTrace(LogVerb.Treasure,
                    $"Map {mapId} {found.Name} hides {found.TileCount} treasure tile(s) whose addresses are not captured yet; they cannot be lit.",
                    "treasure capture tool: tools/treasure_flags.py session");
            }
            return;
        }

        // Has tiles + module enabled (checked once at startup in CheckGlobalIdle): transition to ARMING.
        _map = found;
        _armAttempts = 0;
        _phase = Phase.Arming;
    }

    // ── ARMING tick ───────────────────────────────────────────────────────────────

    private void TickArming()
    {
        var map = _map!;

        // The advisory fingerprint check is telemetry only and never blocks arming. Terrain
        // fingerprints proved unreliable as a gate, because per battle weather such as rain
        // perturbs the hashed fields, so a map captured in one weather state fails to match in
        // another, and no data says which maps can weather. Containment is carried by the build
        // key, the per tick map id match, and the per tile resting byte audit and quorum below.
        // A mismatch is logged once per battle as a drift census. Map id only maps skip this.
        if (_fingerprintOkForBuild && !map.IsMapIdOnly && !_flapLoggedThisBattle && !_audit.FingerprintMatches(map))
        {
            _flapLoggedThisBattle = true;
            var d = _audit.FingerprintDiag(map);
            ModLogger.WarnWithTrace(LogVerb.Arm,
                $"Map {map.MapId} failed its terrain fingerprint check; arming on map id and quorum anyway (weather or terrain drift).",
                $"arm fingerprint readOk={d.ReadOk} fpVer={d.FpVer} got={d.Got:X} want={d.Expected:X}");
        }

        var (verdict, _) = _audit.AuditAddrs(map, Tuning.TreasureMinPlausibleAddrs);

        switch (verdict)
        {
            case ArmVerdict.Arm:
                _phase = Phase.Armed;
                _revalidateTick = 0;
                SeedCollected(map);                          // hide tiles looted and won in a prior battle
                if (_claimDetection && _claimOkForBuild) InitClaimBaseline(map);
                RebuildActiveMap();                          // _activeMap = map minus _collected (and _claimed, empty here)
                var armMap = _activeMap ?? map;
                ModLogger.Event(LogVerb.Arm,
                    $"Map {map.MapId} {map.Name} is armed: {armMap.Tiles.Count} treasure tile(s) held lit" +
                    (_collected.Count > 0 ? $", {_collected.Count} already collected and hidden" : "") +
                    (map.IsMapIdOnly ? " (matched by map id only)" : "") + ".");
                Flight.Record("arm", $"armed map={map.MapId} tiles={armMap.Tiles.Count} collected={_collected.Count}");
                _holder.Hold(armMap);
                WriteMarkers(armMap);
                break;

            case ArmVerdict.Retry:
                _armAttempts++;
                if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
                {
                    _capLoggedThisBattle = true;
                    ModLogger.Debug(LogVerb.Arm,
                        $"map {map.MapId} is waiting to arm; the flag bytes are not in their rest state (tiles off-screen?)");
                }
                break;
        }
    }

    // ── ARMED tick ────────────────────────────────────────────────────────────────

    private void TickArmed()
    {
        var map = _map!;

        // Re-check the map id every tick with one read, since two reads can disagree mid frame.
        bool mapOk = _audit.TryReadMapId(out byte currentMapId) && currentMapId == map.MapId;

        if (!mapOk)
        {
            _badMapTicks++;
            if (_badMapTicks >= Tuning.TreasureMapIdBadTicksToReset)
            {
                // The map changed (a chained battle) or something went wrong, so reset fully.
                ModLogger.Event(LogVerb.Treasure, $"The map changed from {map.MapId} mid-battle (a chained battle); resetting for the new map.");
                ResetBattle();
            }
            return;   // suspend writes this tick
        }
        _badMapTicks = 0;

        // The mid battle fingerprint check is informational only. It is skipped for map id only
        // maps, and once the first drift has been logged this battle.
        //
        // Terrain identity was already proven at arm time. The per tick map id re-check above is
        // the live still in this battle guard, and the ClassifyAddr gate in the hold loop
        // backstops stray writes. A map whose static terrain fields drift mid battle, seen live on
        // Siedge Weald where fields 2 to 5 changed 26 seconds into the same battle, must not
        // disarm. The old behavior dropped to Arming, stopped holding, and once the new terrain
        // state persisted disarmed for the rest of the fight, killing the marks. We log the first
        // drift per battle as a free in the wild census and keep holding.
        if (_fingerprintOkForBuild && !map.IsMapIdOnly && !_flapLoggedThisBattle
                && ++_revalidateTick >= Tuning.TreasureRevalidateEveryNTicks)
        {
            _revalidateTick = 0;
            if (!_audit.FingerprintMatches(map))
            {
                _flapLoggedThisBattle = true;
                ModLogger.Warn(LogVerb.Arm,
                    $"Map {map.MapId} terrain drifted mid-battle; the marks are held through it (the fingerprint no longer matches; not a disarm).");
            }
        }

        // Claim detection. A tile is claimed when a unit stands on it and its item count has
        // risen, since the count rises only on a real claim by an eligible unit and the occupancy
        // pins the exact tile. Un-light every claimed tile every tick, which outlives a stale
        // FastHold pass.
        if (_claimDetection && _claimOkForBuild)
        {
            bool grew = DetectClaims(map);
            bool shrank = DetectRefunds(map);
            RefreshClaimCounts(map);   // refresh per-tick baselines AFTER both decisions read them
            if (grew || shrank)
            {
                ModLogger.EventWithTrace(LogVerb.Claim,
                    shrank
                        ? $"A battle reset refunded the claimed treasures; the tiles are lit again ({_claimed.Count} still claimed)."
                        : $"A treasure was claimed; {_claimed.Count} tile(s) on this map are now claimed and unlit.",
                    $"claim tiles=[{string.Join(", ", _claimed)}]");
                Flight.Record("claim", (shrank ? "refund" : "claim") + $" count={_claimed.Count}");
                RebuildActiveMap();
            }
            foreach (var tile in map.Tiles)
            {
                if (_claimed.Contains((tile.X, tile.Y))) _holder.Unlight(tile);
            }
        }

        // The hold loop writes the mark on Resting bytes only. Foreign bytes, off screen tiles
        // from a camera pan, are skipped by the ClassifyAddr check inside TileHolder.Hold.
        var holdMap = _activeMap ?? map;
        var (wrote, foreign) = _holder.Hold(holdMap);
        WriteMarkers(holdMap);
        if (foreign > 0 && !_foreignLoggedThisBattle)
        {
            _foreignLoggedThisBattle = true;
            ModLogger.Warn(LogVerb.Treasure,
                $"Map {map.MapId} has {foreign} flag byte(s) away from their expected value (tiles off-screen?); skipping those and holding the rest.");
        }
        if (Tuning.RetryDiagnostics) LogHoldDiag(holdMap, wrote, foreign);
    }

    /// <summary>Temporary retry diagnostics. Logs a per armed tick breakdown of the held map's
    /// render bytes, the write count this tick and the resting, held, foreign and unreadable
    /// totals, with each tile's first byte sampled, deduped on change. After a Retry it shows
    /// whether the bytes are Foreign, Resting or Held. All reads guarded.</summary>
    private void LogHoldDiag(TreasureMap map, int wrote, int foreign)
    {
        int rest = 0, held = 0, frgn = 0, unread = 0;
        var samples = new System.Collections.Generic.List<string>();
        foreach (var tile in map.Tiles)
        {
            foreach (var (addr, _) in tile.Addrs)
            {
                long live = ImageBase.At(addr);   // tile addrs are dataset space
                if (!_mem.Readable(live, 1)) { unread++; continue; }
                switch (ClassifyAddr(_mem.U8(live)))
                {
                    case AddrState.Resting: rest++; break;
                    case AddrState.Held: held++; break;
                    case AddrState.Foreign: frgn++; break;
                }
            }
            if (tile.Addrs.Count > 0)
            {
                long a0 = ImageBase.At(tile.Addrs[0].Addr);
                string vv = _mem.Readable(a0, 1) ? _mem.U8(a0).ToString("X2") : "??";
                samples.Add($"({tile.X},{tile.Y}):{vv}");
            }
        }
        string diag = $"hold: tiles={map.Tiles.Count} wrote={wrote} rest={rest} held={held} " +
                      $"foreign={frgn} unread={unread} [{string.Join(" ", samples)}]";
        if (diag != _lastHoldDiag) { _lastHoldDiag = diag; ModLogger.Debug(LogVerb.Trace, diag); }
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
        if (_claimed.Count == 0 && _collected.Count == 0) { _activeMap = fullMap; return; }

        var filteredTiles = new System.Collections.Generic.List<TreasureTile>(fullMap.Tiles.Count);
        foreach (var t in fullMap.Tiles)
        {
            var key = (t.X, t.Y);
            if (!_claimed.Contains(key) && !_collected.Contains(key)) filteredTiles.Add(t);
        }
        _activeMap = new TreasureMap
        {
            MapId = fullMap.MapId,
            Name = fullMap.Name,
            TileCount = fullMap.TileCount,
            FpVer = fullMap.FpVer,
            FpLen = fullMap.FpLen,
            FpHashHex = fullMap.FpHashHex,
            Tiles = filteredTiles,
        };
    }

    // ── claim detection, occupancy plus an inventory count rise ────────────────────

    /// <summary>Seeds <see cref="_collected"/> once at arm from the persistent oracle with every
    /// tile the game records as collected in a prior battle. Those tiles stay out of the lit set
    /// for the whole battle and, unlike <see cref="_claimed"/>, an in battle refund does not
    /// clear them. A no-op that reads no memory when no oracle is wired.</summary>
    private void SeedCollected(TreasureMap map)
    {
        _collected.Clear();
        if (!_collectOkForBuild) return;   // the table address did not re-resolve for this build
        if (_collectedOracle is not { } oracle) return;
        foreach (var tile in map.Tiles)
            if (oracle(map.MapId, tile)) _collected.Add((tile.X, tile.Y));
    }

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
    /// Self guards on ContainsKey, so it is cheap to call every tick. A count unreadable at arm
    /// is captured on the first tick it becomes readable, which is before any claim, since a
    /// claim also needs a readable count. Never overwritten once set.</summary>
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
    /// Latches each unclaimed tile that has a unit standing on it and had one of its item counts
    /// rise since last tick, recording which item id rose, preferring the rare one, so a later
    /// refund can be classified. The count rises only on a real claim by an eligible unit, so no
    /// eligibility byte is needed, and the occupancy pins the exact tile, so maps that reuse an
    /// item id across tiles do not over latch. Logs the occupied tile set on change only when
    /// Tuning.ClaimDiagnostics is on. Returns true if any new tile latched. The caller refreshes
    /// the tracked counts after this and DetectRefunds have read the prior values.
    /// </summary>
    private bool DetectClaims(TreasureMap map)
    {
        var occupied = new HashSet<(int, int)>();
        _claims.CollectOccupied(occupied);

        bool grew = false;
        // The occupied tile dump is gated behind Tuning.ClaimDiagnostics, off by default, because
        // it re-fires on every step onto or off a treasure tile and spams a moving battle. Null
        // when off, which also skips the per tile ReadCount game memory reads below.
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
                    ModLogger.Debug(LogVerb.Claim, $"map {map.MapId} unit(s) on treasure tile(s): {diag}");
            }
        }
        return grew;
    }

    /// <summary>
    /// Detects a battle reset from the inventory refund it produces, and clears every claim so
    /// all tiles re-light. An in battle restart produces no sustained out of live window, so the
    /// exit edge never fires and ResetBattle never runs, yet it refunds the claimed items and
    /// their counts drop back to the arm baseline. Returns true if any claim was cleared.
    ///
    /// A reset refunds all claims together, so detection keys off signals one legitimate action
    /// cannot fake. A rare claimed tile back at its baseline is definitive, since a rare count
    /// cannot drop mid battle. Two or more distinct item ids dropping to baseline in the same tick
    /// is the other signal, since one consumable use drops one count and two unrelated uses never
    /// land on one tick. Counting item ids rather than tiles matters, because maps reuse one item
    /// id across several tiles and a single use would trip every tile claimed through it. Edge
    /// triggering matters too, because a level check would let two staggered uses co-occur at
    /// baseline. Counts are keyed by global item id, so a set of only common claims whose refunds
    /// straggle across ticks is a known limitation, still better than dark after a restart.
    /// </summary>
    private bool DetectRefunds(TreasureMap map)
    {
        if (_claimed.Count == 0) return false;

        bool rareRefund = false;                 // a rare claimed tile back at baseline, a definitive reset
        var edgeDropItems = new HashSet<int>(); // DISTINCT item ids that fell to <= baseline THIS tick
        foreach (var tile in map.Tiles)
        {
            var key = (tile.X, tile.Y);
            if (!_claimed.Contains(key)) continue;
            if (!_claimItem.TryGetValue(key, out int item)) continue;
            if (!_armCount.TryGetValue(item, out int baseCount)) continue;
            int cur = _claims.ReadCount(item);
            if (cur < 0 || cur > baseCount) continue;   // not at or below the arm baseline

            // Rare slot only. When a tile's rare and common ids coincide we cannot tell which slot
            // dropped, so treat it as common, since a lone drop must not fake a reset.
            if (item == tile.RareItemId && tile.RareItemId != tile.CommonItemId) rareRefund = true;
            if (_lastCount.TryGetValue(item, out int prev) && prev > baseCount) edgeDropItems.Add(item);
        }

        if (!rareRefund && edgeDropItems.Count < 2) return false;   // not a reset, so leave claims latched

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
    /// Drives the native marker write path and logs the first successful write per battle, so a
    /// live test is not blind. The active line means the utility pointer resolved and markers were
    /// written, and its absence with no diamonds means the pointer never resolved. A silent no-op
    /// while the path is gated off.
    /// </summary>
    private void WriteMarkers(TreasureMap map)
    {
        int wrote = _markers.Write(map);
        if (wrote > 0)
        {
            if (!_markersLoggedThisBattle)
            {
                _markersLoggedThisBattle = true;
                ModLogger.Event(LogVerb.Treasure, $"Enhanced markers are active: {wrote} marker slot(s) written for map {map.MapId} {map.Name}.");
            }
            return;
        }

        // Wrote nothing, so log which guard failed, deduped so a stable state logs once.
        var (readable, basePtr, writable) = _markers.Resolve();
        string probe = $"readable={readable} base=0x{basePtr:X} writable={writable}";
        if (probe != _lastMarkerProbe)
        {
            _lastMarkerProbe = probe;
            ModLogger.WarnWithTrace(LogVerb.Treasure,
                "Enhanced markers could not be written; the marking utility pointer did not resolve.",
                $"marker ptr@0x{Offsets.EnhancedMarkingUtilityPtr:X} {probe}");
        }
    }
}
