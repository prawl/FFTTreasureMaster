using System;

namespace FFTTreasureMaster;

/// <summary>
/// The two job bodies the host runs. Each acquires its loader controller. A loader that is
/// present but handed out no controller gets its own warn line; a missing loader is left to
/// the grant, which reports its own outcome.
/// </summary>
internal static class LoaderJobs
{
    private const string ControllerTail =
        "(Tech: GetController returned null while fftivc.utility.modloader is active; either the " +
        "loader stopped early at startup, see the loader's own red lines in the Reloaded " +
        "console, or the interfaces versions mismatch.)";

    public static void RunRareTreasure(Func<ITrapTable?> tryCreate, Func<bool> loaderActive,
                                       Action<string> log, Action<string> warn, Action<int> sleep)
    {
        var table = tryCreate();
        if (table == null && loaderActive())
        {
            warn("The FFT Ivalice Chronicles Mod Loader is installed, but its treasure-table " +
                 "controller could not be acquired, so rare treasure only is off this session. " +
                 ControllerTail);
            return;
        }
        new RareTreasureGrant(table, log, sleep).Run();
    }

    public static void RunTreasureHunter(Func<IJobTable?> tryCreate, Func<bool> loaderActive,
                                         Action<string> log, Action<string> warn, Action<int> sleep)
    {
        var table = tryCreate();
        if (table == null && loaderActive())
        {
            warn("The FFT Ivalice Chronicles Mod Loader is installed, but its job-table " +
                 "controller could not be acquired, so the Treasure Hunter grant is off this " +
                 "session. " + ControllerTail);
            return;
        }
        new TreasureHunterGrant(enabled: true, table, Tuning.TreasureHunterGrantJobIds, log, sleep).Run();
    }
}
