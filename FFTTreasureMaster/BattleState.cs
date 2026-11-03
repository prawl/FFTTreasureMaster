using System;

namespace FFTTreasureMaster;

/// <summary>The edge an engine tick crossed, a fresh battle Entered, a battle Exited, or neither.</summary>
internal enum BattleEdge { None, Entered, Exited }

/// <summary>
/// The pure battle in and out state machine, unit tested with no memory access.
/// Enter is instant and exit is debounced, because battleMode is a cursor tile class and not an
/// on battlefield flag. Move browsing reads 1 for seconds every turn, pausing reads 0, and mid
/// battle dialogue reads 0 with a real eventId, so one out of live read mid combat is normal
/// and must not end the battle. The slot9 sentinel sticks on the post battle world map and can
/// drop mid battle, so it cannot mark the end either.
///
/// Enter fires the instant any enter signal appears. Exit needs sustained out of live time
/// (<see cref="ExitDebounceSeconds"/>), and pause and real event ticks suspend the timer without
/// resetting it. An in live tick zeroes the accumulator. The stuck sentinel world map counts as
/// sustained out of live, so exit fires there and the next battle gets a clean Enter. Time comes
/// only from the passed in DateTime, so tests feed synthetic clocks.
/// </summary>
internal sealed class BattleState
{
    public const double ExitDebounceSeconds = 4.0;

    public bool In { get; private set; }
    private TimeSpan _outAccum = TimeSpan.Zero;   // contiguous out-of-live time (suspended by pause/event)
    private DateTime _lastTick;                    // for the per-tick delta; valid only while In
    private bool _haveLastTick;
    private bool _pairWasArmed;                    // last observed sentinel-pair state (for the edge)

    /// <summary>Both battle sentinels armed at once. A level read of this pair cannot mean a
    /// battle is starting, because both sentinels stick on the post quit world map and a level
    /// trigger re-entered instantly after every exit. Only the disarmed to armed edge of the
    /// pair enters. Live modes stay level signals, since a real battle reads mode 2 or 4 the
    /// moment the battlefield loads.</summary>
    internal static bool PairArmed(uint slot0, uint slot9) =>
        slot0 == 0xFF && slot9 == 0xFFFFFFFF;

    /// <summary>The instant battle enter signal. Either a fresh sentinel pair arm, an edge the
    /// caller computes, or a live battleMode level.</summary>
    internal static bool EnterSignal(bool pairRisingEdge, uint slot0, int battleMode) =>
        pairRisingEdge || battleMode == 2 || battleMode == 4
        || (battleMode == 3 && slot0 == 0xFF);

    /// <summary>A genuine in battle frame, which gates every module that writes battle memory.
    /// battleMode 2, 3 or 4 covers active turn frames. The slot0 marker stays set through
    /// targeting (modes 1 and 5), where gating on 2 to 4 alone starves the beat. The marker
    /// cannot be trusted alone, because quitting a battle leaves it stuck on the world map, which
    /// once kept battles live forever. So a marker only frame counts as live only with an excuse
    /// for battleMode reading 0, which is a targeting mode, the pause flag, or a real event
    /// id.</summary>
    public static bool InLiveBattle(uint slot0, int battleMode, bool paused, int eventId) =>
        battleMode == 2 || battleMode == 3 || battleMode == 4
        || (slot0 == 0xFF && (battleMode == 1 || battleMode == 5
                              || paused || IsRealEvent(eventId)));

    /// <summary>A battle map is on screen, the broad gate the Treasure Master module uses. True
    /// when slot9 is the stuck in battle sentinel and battleMode is not zero. Covers the formation
    /// screen, your turn, enemy turns, animations and cast targeting. False on the world map and
    /// the party menu even while slot9 is still stuck from a prior battle. Preferred over
    /// InLiveBattle for the treasure gate because the mode 1 flicker on enemy turns used to reset
    /// the stability counter.</summary>
    public static bool BattleDisplayed(uint slot9, int battleMode) =>
        slot9 == 0xFFFFFFFFu && battleMode != 0;

    /// <summary>A real story event or cutscene suspends the exit timer. Any nonzero id except
    /// 0xFFFF is a real event. 0xFFFF is present on every confirmed battle exit, so it is not an
    /// event. Zero is excluded. The old 1 to 399 band was guesswork, because live evidence showed
    /// event 401 mid battle with no visible dialogue, which faked an exit and dropped a kill
    /// credit. The nameId alias only occurs during combat, which already zeroes the accumulator
    /// before this check, so any out of live nonzero id reaching here is a genuine event
    /// screen.</summary>
    internal static bool IsRealEvent(int e) => e >= 1 && e != 0xFFFF;

    /// <summary>Step once per engine tick with raw reads; returns the edge that fired this tick.</summary>
    public BattleEdge Step(uint slot0, uint slot9, int battleMode, bool paused, int eventId, DateTime now)
    {
        if (!In)
        {
            bool pairArmed = PairArmed(slot0, slot9);
            bool pairEdge = pairArmed && !_pairWasArmed;
            _pairWasArmed = pairArmed;
            if (!EnterSignal(pairEdge, slot0, battleMode)) return BattleEdge.None;
            In = true;
            _outAccum = TimeSpan.Zero;
            _lastTick = now;
            _haveLastTick = true;
            return BattleEdge.Entered;
        }

        // An in live tick zeroes the accumulator. Out of live ticks accumulate only when neither
        // paused nor in a real event, since both suspend without resetting.
        TimeSpan delta = _haveLastTick && now > _lastTick ? now - _lastTick : TimeSpan.Zero;
        _lastTick = now;
        _haveLastTick = true;

        if (InLiveBattle(slot0, battleMode, paused, eventId))
        {
            _outAccum = TimeSpan.Zero;
            return BattleEdge.None;
        }
        if (!paused && !IsRealEvent(eventId)) _outAccum += delta;
        if (_outAccum.TotalSeconds >= ExitDebounceSeconds)
        {
            In = false;
            _haveLastTick = false;
            // Snapshot the pair state at the exit. A pair that armed mid battle and is still
            // stuck must not read as a fresh edge on the next out of battle tick.
            _pairWasArmed = PairArmed(slot0, slot9);
            return BattleEdge.Exited;
        }
        return BattleEdge.None;
    }
}
