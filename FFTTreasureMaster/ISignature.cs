using System;

namespace FFTTreasureMaster;

/// <summary>
/// The frame facts a module may key on, read once per tick by the engine and shared by all.
/// </summary>
internal readonly struct TickContext
{
    /// <summary>The engine tick's wall-clock instant (timeouts, grace windows).</summary>
    public DateTime Now { get; }
    /// <summary>On the live battlefield.</summary>
    public bool OnField { get; }
    /// <summary>A genuine in battle frame (<see cref="BattleState.InLiveBattle"/>), the gate
    /// for every module that writes battle memory.</summary>
    public bool InLive { get; }

    public TickContext(DateTime now, bool onField, bool inLive)
    {
        Now = now;
        OnField = onField;
        InLive = inLive;
    }
}

/// <summary>
/// One signature module. The engine drives every module the same way, Tick on each in battle
/// frame in a fixed order and ResetBattle on the debounced battle edges, so adding one is a
/// constructor line and an array entry. Modules pick the context facts they need, and each keeps
/// its richer typed Tick for tests and implements this by delegation.
/// </summary>
internal interface ISignature
{
    /// <summary>Clear per-battle state. Called on the debounced battle exit edge.</summary>
    void ResetBattle();

    /// <summary>One engine tick (~33ms) while a battle is live.</summary>
    void Tick(in TickContext ctx);
}
