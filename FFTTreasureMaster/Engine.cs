using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FFTTreasureMaster;

/// <summary>
/// The Treasure Master runtime. One background loop reads the battle sentinels once per tick,
/// steps the <see cref="BattleState"/> machine for the battle enter/exit edges, and drives the
/// <see cref="TreasureMaster"/> module: while a battle map is on screen it holds the 0x80 mark
/// bit on each known treasure tile so the tiles stay lit. Nothing here knows about weapons,
/// kills, or growth -- the module is gated solely by the config toggle.
/// </summary>
internal sealed class Engine
{
    // Poll fast: the running-water terrain animation clears the held mark between ticks, and the
    // FastHold thread (~8ms) backs this up. 33ms keeps the map-id / phase machine responsive.
    private const int PollMs = 33;

    private readonly TreasureMaster _treasure;
    private readonly BattleState _battle = new();   // debounced in/out edges (slot9 sticks; mode flickers)
    private CancellationTokenSource? _cts;

    /// <param name="modDir">Mod deployment directory (treasure.json + Config.json live here).</param>
    /// <param name="enabled">Master on/off gate, read from Config.Enabled at startup.
    /// Null falls back to Tuning.TreasureEnabled.</param>
    public Engine(string modDir, bool? enabled = null)
    {
        var treasureJson = Path.Combine(modDir, "treasure.json");
        _treasure = new TreasureMaster(
            load:         () => TreasureDb.Load(modDir),
            datasetStamp: () => { try { return File.GetLastWriteTimeUtc(treasureJson); }
                                  catch { return null; } },
            mem:      new LiveMemory(),
            enabled:  enabled);
        _treasure.StartFastHold();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            Log.Info("runtime loop started.");
            while (!token.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("tick: " + ex.Message); }
                try { await Task.Delay(PollMs, token); } catch { }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private void Tick()
    {
        uint slot0      = Mem.U32(Offsets.Slot0);
        uint slot9      = Mem.U32(Offsets.Slot9);
        int  battleMode = Mem.U8(Offsets.BattleMode);
        bool paused     = Mem.U8(Offsets.PauseFlag) == 1;
        int  eventId    = Mem.U16(Offsets.EventId);
        var  now        = DateTime.Now;

        // Enter is instant; exit is debounced (battleMode flickers, slot9 sticks). Reset the
        // module on BOTH edges so a battle that restarts without a clean exit still starts clean.
        BattleEdge edge = _battle.Step(slot0, slot9, battleMode, paused, eventId, now);
        if (edge == BattleEdge.Entered)
            Log.Info($"battle: started (slot0={slot0:X} slot9={slot9:X} mode={battleMode})");
        if (edge == BattleEdge.Entered || edge == BattleEdge.Exited)
        {
            if (edge == BattleEdge.Exited)
                Log.Info($"battle: ended (slot0={slot0:X} slot9={slot9:X} mode={battleMode})");
            _treasure.ResetBattle();
        }

        // Treasure gates on "a battle map is on screen" (slot9 armed + mode != 0) rather than
        // strict in-live: stable through formation, enemy turns, and cast animations while still
        // excluding the world map (mode 0).
        bool battleDisplayed = BattleState.BattleDisplayed(slot9, battleMode);
        _treasure.Tick(now, battleDisplayed);
    }
}
