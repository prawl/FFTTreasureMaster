using System;
using System.Collections.Generic;
using System.Threading;

namespace FFTTreasureMaster;

/// <summary>
/// Runs the loader-backed jobs after the mod loader has merged its tables. Reloaded runs the
/// initialized handlers in subscription order and ignores optional dependencies when ordering
/// mods, so Fire is subscribed only once the loader is known to be active. A loader that never
/// shows up is caught by Fallback, which runs the jobs so each can report the missing loader.
/// Nothing here lets an exception escape into Reloaded.
/// </summary>
internal sealed class LoaderGrantHost
{
    private readonly IReadOnlyList<(string Name, Action Job)> _jobs;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Action<Action> _runInBackground;

    private ILoaderEvents? _events;
    private int _armed;
    private int _fireSubscribed;
    private int _fired;

    public LoaderGrantHost(IReadOnlyList<(string Name, Action Job)> jobs, Action<string> info,
                           Action<string> warn, Action<Action> runInBackground)
    {
        _jobs = jobs;
        _info = info;
        _warn = warn;
        _runInBackground = runInBackground;
    }

    public void Arm(ILoaderEvents events)
    {
        if (_jobs.Count == 0) return;
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        try
        {
            _events = events;
            if (events.IsLoaderActive())
            {
                SubscribeFire();
            }
            else
            {
                events.OnModLoaded(OnModLoadedCallback);
                events.OnInitialized(Fallback);
            }

            var names = new List<string>();
            foreach (var job in _jobs) names.Add(job.Name);
            _info($"Mod loader features armed ({string.Join(", ", names)}); they run once every " +
                  "mod has finished loading. (Tech: subscribed after fftivc.utility.modloader " +
                  "when it is present, so its table merge runs first.)");
        }
        catch (Exception ex)
        {
            Warn($"The mod loader features could not be armed: {ex.Message}");
        }
    }

    private void SubscribeFire()
    {
        if (Interlocked.Exchange(ref _fireSubscribed, 1) != 0) return;
        _events!.OnInitialized(Fire);
    }

    private void OnModLoadedCallback(string modId)
    {
        try
        {
            if (modId == Tuning.LoaderModId) SubscribeFire();
        }
        catch (Exception ex)
        {
            Warn($"The mod loader features could not be armed: {ex.Message}");
        }
    }

    private void Fallback()
    {
        try
        {
            if (!_events!.IsLoaderActive()) Fire();
        }
        catch (Exception ex)
        {
            Warn($"The mod loader features could not start; the game runs normally without them: {ex.Message}");
        }
    }

    private void Fire()
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        try
        {
            _runInBackground(RunAll);
        }
        catch (Exception ex)
        {
            Warn($"The mod loader features could not start; the game runs normally without them: {ex.Message}");
        }
    }

    private void RunAll()
    {
        foreach (var (name, job) in _jobs)
        {
            try { job(); }
            catch (Exception ex)
            {
                Warn($"The {name} job failed; the game runs normally without it: {ex.Message}");
            }
        }
    }

    private void Warn(string message)
    {
        try { _warn(message); } catch { }
    }
}
