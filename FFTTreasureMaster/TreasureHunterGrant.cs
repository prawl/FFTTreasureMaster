using System;
using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>Outcome of one grant run. Applied counts only rows where the read-back confirmed
/// the ability landed; Reasserted counts rows the delayed second pass had to re-apply after
/// another mod's edit clobbered them; GaveUp means the job table never became ready.</summary>
internal readonly record struct GrantSummary(
    int Applied, int AlreadyPresent, int FullRows, int Unreadable, int Failed,
    int Reasserted, bool GaveUp);

/// <summary>
/// The "All units gain Treasure Hunter" grant: writes ability 509 (Treasure Hunter, the
/// live-proven Move-Find innate) into one free innate slot of each covered JOB_DATA row,
/// through the IJobTable seam. Pure algorithm: no game memory, no threads, no clocks -- the
/// caller supplies the sleep so tests run instantly. Never throws; every failure path ends
/// in at most one log line and an inert feature, because the core tile-highlight mod must
/// never be destabilized by this optional extra.
/// </summary>
internal sealed class TreasureHunterGrant
{
    /// <summary>Treasure Hunter (Move-Find Item) in the game's unified ability numbering.</summary>
    public const ushort TreasureHunterAbilityId = 509;

    /// <summary>Innate slots per JOB_DATA row (InnateAbilityId1..4).</summary>
    public const int InnateSlots = 4;

    /// <summary>Preferred slot index (InnateAbilityId3): the exact slot the live premise
    /// probe proved working on 2026-07-21. Used whenever it is free; otherwise the first
    /// free slot is taken (vanilla monster rows prove other slots work for movement innates,
    /// but the proven slot gets priority on the 19 of 20 generic rows where it is open).</summary>
    public const int PreferredSlotIndex = 2;

    private readonly bool _enabled;
    private readonly IJobTable? _table;
    private readonly IReadOnlyList<int> _jobIds;
    private readonly Action<string> _log;
    private readonly Action<int> _sleep;
    private readonly int _retryIntervalMs;
    private readonly int _retryCapMs;
    private readonly int _reassertDelayMs;

    public TreasureHunterGrant(bool enabled, IJobTable? table, IReadOnlyList<int> jobIds,
                               Action<string> log, Action<int> sleep,
                               int retryIntervalMs = Tuning.GrantRetryIntervalMs,
                               int retryCapMs = Tuning.GrantRetryCapMs,
                               int reassertDelayMs = Tuning.GrantReassertDelayMs)
    {
        _enabled = enabled;
        _table = table;
        _jobIds = jobIds;
        _log = log;
        _sleep = sleep;
        _retryIntervalMs = retryIntervalMs;
        _retryCapMs = retryCapMs;
        _reassertDelayMs = reassertDelayMs;
    }

    /// <summary>One-shot grant (wait for readiness, apply, confirm, one delayed re-assert).
    /// Setting off: zero seam calls, zero log lines. Seam absent: exactly one log line.</summary>
    public GrantSummary Run()
    {
        if (!_enabled) return default;
        if (_table == null)
        {
            _log("The 'All units gain Treasure Hunter' setting is on, but the FFT Ivalice " +
                 "Chronicles Mod Loader mod is not installed, so no jobs were changed. Tile " +
                 "highlighting still works normally. (Tech: IFFTOJobDataManager controller " +
                 "not found.)");
            return default;
        }

        int already = 0, unreadable = 0, failed = 0, reasserted = 0;
        var fullRows = new List<int>();
        var applied  = new List<int>();
        try
        {
            if (!WaitForReady()) return new GrantSummary(0, 0, 0, 0, 0, 0, GaveUp: true);

            foreach (var jobId in _jobIds)
            {
                var innates = _table.GetInnates(jobId);
                if (innates == null || innates.Length != InnateSlots) { unreadable++; continue; }
                if (Has509(innates)) { already++; continue; }

                int slot = PickSlot(innates);
                if (slot < 0) { fullRows.Add(jobId); continue; }

                if (!_table.TryApplyInnate(jobId, slot, TreasureHunterAbilityId)) { failed++; continue; }

                var check = _table.GetInnates(jobId);
                if (check != null && Has509(check)) applied.Add(jobId);
                else failed++;
            }

            _log(Summary(applied.Count, already, fullRows, unreadable, failed));

            reasserted = ReassertPass(applied);
            if (reasserted > 0)
                _log($"Another mod overwrote the granted Treasure Hunter ability; it was " +
                     $"re-applied to {reasserted} job(s). (Tech: last-writer-wins table " +
                     $"merge; delayed re-assert pass.)");
        }
        catch (Exception ex)
        {
            _log($"Granting Treasure Hunter failed; the game runs normally without it: {ex.Message}");
        }
        return new GrantSummary(applied.Count, already, fullRows.Count, unreadable, failed,
                                reasserted, GaveUp: false);
    }

    /// <summary>Polls readiness on the injected sleep until ready or the cap elapses.</summary>
    private bool WaitForReady()
    {
        int elapsed = 0;
        while (!_table!.IsReady())
        {
            if (elapsed >= _retryCapMs)
            {
                _log("The 'All units gain Treasure Hunter' setting is on, but the mod " +
                     "loader's job table never became ready, so no jobs were changed this " +
                     $"session. (Tech: readiness probe timed out after {_retryCapMs} ms.)");
                return false;
            }
            _sleep(_retryIntervalMs);
            elapsed += _retryIntervalMs;
        }
        return true;
    }

    /// <summary>Re-reads every granted row after a delay and re-applies the ability where
    /// another mod's edit removed it. Returns the number of rows re-applied.</summary>
    private int ReassertPass(List<int> applied)
    {
        if (applied.Count == 0) return 0;
        _sleep(_reassertDelayMs);
        int reasserted = 0;
        foreach (var jobId in applied)
        {
            var now = _table!.GetInnates(jobId);
            if (now != null && Has509(now)) continue;
            int slot = now == null ? PreferredSlotIndex : Math.Max(PickSlot(now), 0);
            if (_table.TryApplyInnate(jobId, slot, TreasureHunterAbilityId)) reasserted++;
        }
        return reasserted;
    }

    private static bool Has509(ushort[] innates)
        => Array.IndexOf(innates, TreasureHunterAbilityId) >= 0;

    /// <summary>The proven slot when free, else the first free slot, else -1 (full row).</summary>
    private static int PickSlot(ushort[] innates)
        => innates[PreferredSlotIndex] == 0 ? PreferredSlotIndex
                                            : Array.IndexOf(innates, (ushort)0);

    private static string Summary(int applied, int already, List<int> fullRows, int unreadable, int failed)
    {
        var full = fullRows.Count == 0 ? "0"
                                       : $"{fullRows.Count} (jobs: {string.Join(", ", fullRows)})";
        return "Treasure Hunter granted as an innate ability: " +
               $"{applied} job(s) updated, {already} already had it, {full} with no free " +
               $"slot, {unreadable} unreadable, {failed} failed.";
    }
}
