using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>Builds game hooks through Reloaded's shared hooks library and nothing else.</summary>
internal sealed class ReloadedHookSite : IHookSite
{
    private readonly IReloadedHooks _hooks;

    private ReloadedHookSite(IReloadedHooks hooks) => _hooks = hooks;

    /// <summary>Null when reloaded.sharedlib.hooks is not loaded.</summary>
    public static IHookSite? TryCreate(IModLoaderV1 loader)
    {
        var hooksRef = loader.GetController<IReloadedHooks>();
        return hooksRef != null && hooksRef.TryGetTarget(out var hooks) && hooks != null
            ? new ReloadedHookSite(hooks)
            : null;
    }

    public IGameHook Create(long addr, GameFn detour) => new Built(_hooks.CreateHook(detour, addr), detour);

    /// <summary>Holds the detour too, since the game jumps into it long after this.</summary>
    private sealed class Built : IGameHook
    {
        private readonly IHook<GameFn> _hook;

        public Built(IHook<GameFn> hook, GameFn detour)
        {
            _hook = hook;
            Detour = detour;
        }

        public GameFn Detour { get; }
        public GameFn Original => _hook.OriginalFunction;
        public void Activate() => _hook.Activate();
        public void Disable() { if (_hook.IsHookEnabled) _hook.Disable(); }
    }
}
