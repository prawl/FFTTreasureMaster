using System;

namespace FFTTreasureMaster;

/// <summary>Outcome of one run. Applied counts only slots whose read-back confirmed the
/// written value.</summary>
internal readonly record struct RareTreasureSummary(
    int Applied, int AlreadyRare, int UnreadableMaps, int Failed, bool GaveUp);

/// <summary>
/// Makes every treasure slot hand out its rare item by copying the rare id into the common
/// slot, through the ITrapTable seam. A rare id of 0 marks an unused slot and is never
/// touched, since writing its zero would blank a live common item. Never throws, and every
/// failure path ends in a single log line.
/// </summary>
internal sealed class RareTreasureGrant
{
    private readonly ITrapTable? _table;
    private readonly Action<string> _log;
    private readonly Action<int> _sleep;
    private readonly int _retryIntervalMs;
    private readonly int _retryCapMs;

    public RareTreasureGrant(ITrapTable? table, Action<string> log, Action<int> sleep,
                             int retryIntervalMs = Tuning.GrantRetryIntervalMs,
                             int retryCapMs = Tuning.GrantRetryCapMs)
    {
        _table = table;
        _log = log;
        _sleep = sleep;
        _retryIntervalMs = retryIntervalMs;
        _retryCapMs = retryCapMs;
    }

    public RareTreasureSummary Run()
    {
        if (_table == null)
        {
            _log("The 'Rare treasure only' setting is on, but the FFT Ivalice Chronicles Mod " +
                 "Loader is not installed or not enabled, so treasure tiles give their normal " +
                 "items. Tile highlighting still works normally. (Tech: " +
                 "IFFTOMapTrapFormationDataManager controller not found.)");
            return default;
        }

        int applied = 0, already = 0, unreadable = 0, failed = 0;
        try
        {
            if (!ReadinessWait.Until(_table.IsReady, _sleep, _retryIntervalMs, _retryCapMs))
            {
                _log("The 'Rare treasure only' setting is on, but the mod loader's treasure " +
                     "table never became ready, so treasure tiles give their normal items " +
                     $"this session. (Tech: readiness probe timed out after {_retryCapMs} ms.)");
                return new RareTreasureSummary(0, 0, 0, 0, GaveUp: true);
            }

            for (int mapId = 0; mapId < Tuning.TrapTableMapCount; mapId++)
            {
                var slots = _table.GetSlots(mapId);
                if (slots == null || slots.Length != Tuning.TrapSlots) { unreadable++; continue; }

                var wanted = new ushort?[Tuning.TrapSlots];
                int wantedCount = 0;
                for (int i = 0; i < Tuning.TrapSlots; i++)
                {
                    if (slots[i].Rare == 0) continue;
                    if (slots[i].Common == slots[i].Rare) { already++; continue; }
                    wanted[i] = slots[i].Rare;
                    wantedCount++;
                }
                if (wantedCount == 0) continue;

                if (!_table.TryApplyCommon(mapId, wanted)) { failed += wantedCount; continue; }

                var check = _table.GetSlots(mapId);
                if (check == null || check.Length != Tuning.TrapSlots) { failed += wantedCount; continue; }
                for (int i = 0; i < Tuning.TrapSlots; i++)
                {
                    if (wanted[i] is not ushort want) continue;
                    if (check[i].Common == want) applied++;
                    else failed++;
                }
            }

            _log($"Rare treasure only is in effect: {applied} treasure tile(s) now give their " +
                 $"rare item, {already} already did, {unreadable} map(s) unreadable, {failed} failed.");
        }
        catch (Exception ex)
        {
            _log($"Applying rare treasure only failed; the game runs normally without it: {ex.Message}");
        }
        return new RareTreasureSummary(applied, already, unreadable, failed, GaveUp: false);
    }
}
