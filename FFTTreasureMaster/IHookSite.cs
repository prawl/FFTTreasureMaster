using Reloaded.Hooks.Definitions.X64;

namespace FFTTreasureMaster;

/// <summary>A game routine as a hook sees it, four register arguments in and rax out.</summary>
/// <remarks>Both hooked routines take only integers, so no float register is lost.</remarks>
[Function(CallingConventions.Microsoft)]
internal delegate nint GameFn(nint rcx, nint rdx, nint r8, nint r9);

/// <summary>Builds a detour in front of a game routine. Tests stand in for Reloaded.</summary>
internal interface IHookSite
{
    IGameHook Create(long addr, GameFn detour);
}

/// <summary>One built hook. Original works once built, and Activate arms the jump.</summary>
internal interface IGameHook
{
    GameFn Original { get; }
    void Activate();
    void Disable();
}
