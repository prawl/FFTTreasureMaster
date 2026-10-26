using System;
using Reloaded.Mod.Interfaces.Internal;

namespace FFTTreasureMaster;

/// <summary>
/// The three loader facts the host needs, as a seam so the arming order is testable without
/// Reloaded. Implementations may throw, the host is the one place that reports it.
/// </summary>
internal interface ILoaderEvents
{
    /// <summary>True when the FFT Ivalice Chronicles Mod Loader is among the started mods.</summary>
    bool IsLoaderActive();

    /// <summary>Runs the handler once every mod has finished loading.</summary>
    void OnInitialized(Action handler);

    /// <summary>Runs the handler with the mod id each time Reloaded finishes starting a mod.</summary>
    void OnModLoaded(Action<string> handler);
}

/// <summary>Live adapter over the Reloaded loader handle. Untested by design.</summary>
internal sealed class ReloadedLoaderEvents : ILoaderEvents
{
    private readonly IModLoaderV1 _loader;

    public ReloadedLoaderEvents(IModLoaderV1 loader) =>
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public bool IsLoaderActive()
    {
        foreach (var mod in _loader.GetActiveMods())
            if (mod.Generic?.ModId == Tuning.LoaderModId) return true;
        return false;
    }

    public void OnInitialized(Action handler) => _loader.OnModLoaderInitialized += handler;

    public void OnModLoaded(Action<string> handler) =>
        _loader.ModLoaded += (_, cfg) => handler(cfg?.ModId ?? string.Empty);
}
