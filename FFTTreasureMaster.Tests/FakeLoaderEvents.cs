using System;
using System.Collections.Generic;
using System.Linq;

namespace FFTTreasureMaster.Tests;

/// <summary>Scriptable ILoaderEvents. Handlers are kept by method name so a test can assert the
/// subscription order, and the stand-in for the loader's own handler records into the same
/// order list the jobs write to.</summary>
internal sealed class FakeLoaderEvents : ILoaderEvents
{
    private readonly List<string> _order;
    private readonly List<(string Name, Action Handler)> _initialized = new();
    private readonly List<(string Name, Action<string> Handler)> _modLoaded = new();

    public FakeLoaderEvents(List<string> order) => _order = order;

    public bool LoaderActive;
    public bool ThrowOnSubscribe;
    public bool ThrowOnIsLoaderActive;

    public IReadOnlyList<string> InitializedNames => _initialized.Select(h => h.Name).ToList();
    public int ModLoadedCount => _modLoaded.Count;

    public bool IsLoaderActive()
    {
        if (ThrowOnIsLoaderActive) throw new InvalidOperationException("active check failed");
        return LoaderActive;
    }

    public void OnInitialized(Action handler)
    {
        if (ThrowOnSubscribe) throw new InvalidOperationException("subscribe failed");
        _initialized.Add((handler.Method.Name, handler));
    }

    public void OnModLoaded(Action<string> handler)
    {
        if (ThrowOnSubscribe) throw new InvalidOperationException("subscribe failed");
        _modLoaded.Add((handler.Method.Name, handler));
    }

    /// <summary>Models the loader having subscribed before the host armed.</summary>
    public void SeedLoaderHandler() => _initialized.Add((nameof(LoaderMarker), LoaderMarker));

    public void RaiseModLoaded(string id)
    {
        if (id == Tuning.LoaderModId)
        {
            SeedLoaderHandler();
            LoaderActive = true;
        }
        foreach (var h in _modLoaded.ToList()) h.Handler(id);
    }

    /// <summary>Runs the handlers present when the event fires, in subscription order.</summary>
    public void RaiseInitialized()
    {
        foreach (var h in _initialized.ToList()) h.Handler();
    }

    private void LoaderMarker() => _order.Add(nameof(LoaderMarker));
}
