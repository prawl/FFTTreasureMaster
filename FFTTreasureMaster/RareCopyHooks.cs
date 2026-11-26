using System;
using System.Text;
using System.Threading;

namespace FFTTreasureMaster;

/// <summary>
/// Puts rare items in the battle's treasure copy right after the game fills it.
/// Two routines fill it. The map load copies the main table, and the restore copies saved battle
/// data back over it. Each detour runs the game's routine first, outside any try, then patches the
/// copy once. Nothing after the game's own call may throw back into the game.
/// </summary>
internal sealed class RareCopyHooks
{
    private readonly IGameMemory _mem;
    private readonly Func<IGameMemory, TrapCopyFind> _find;
    private readonly CopyLog _log;
    private bool _armed;
    private long _copy;
    private Maker? _mapLoad, _restore;
    private long _faults;

    public RareCopyHooks(IGameMemory mem, Func<IGameMemory, TrapCopyFind> find, CopyLog log)
    {
        _mem = mem;
        _find = find;
        _log = log;
    }

    public bool Active { get; private set; }

    /// <summary>How many patch passes threw and were dropped so the game carries on.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>
    /// Finds the copy and hooks both routines, all or nothing. Only the first call does anything,
    /// since an armed hook overwrites the very bytes the find matches.
    /// </summary>
    public void Arm(IHookSite? site)
    {
        if (_armed) return;
        _armed = true;
        if (site == null)
        {
            _log.Warn("Rare treasure only cannot watch the battle treasure copy because Reloaded's "
                + "shared hooks library (reloaded.sharedlib.hooks) is not loaded; some battles may still give common items.");
            return;
        }
        var found = _find(_mem);
        if (found.Addr is not long copy)
        {
            _log.Warn($"Rare treasure only could not find the battle treasure copy in this game build ({found.Why}); "
                + "no hooks were installed and some battles may still give common items.");
            return;
        }
        _copy = copy;
        _mapLoad = new Maker("the map load", found.CopyFn, true);
        _restore = new Maker("the restore of saved battle data", found.RestoreFn, false);
        Active = Install(site, _mapLoad, _restore);
    }

    private bool Install(IHookSite site, Maker map, Maker restore)
    {
        // The detour reads its hook, so each hook is stored before its jump goes in.
        map.Detour = (a, b, c, d) => Process(map, map.Hook!.Original, a, b, c, d);
        restore.Detour = (a, b, c, d) => Process(restore, restore.Hook!.Original, a, b, c, d);
        try
        {
            map.Hook = site.Create(map.Addr, map.Detour);
            restore.Hook = site.Create(restore.Addr, restore.Detour);
            map.Hook.Activate();
            restore.Hook.Activate();
        }
        catch (Exception ex)
        {
            TurnOff(map);
            TurnOff(restore);
            _log.Warn($"Rare treasure only could not hook the battle treasure copy ({ex.Message}); "
                + "no hooks are active and some battles may still give common items.");
            return false;
        }
        _log.Event($"Rare treasure only now watches the two routines that fill each battle's treasure copy, "
            + $"the map load at 0x{map.Addr:X} and the restore of saved battle data at 0x{restore.Addr:X} (copy at 0x{_copy:X}).");
        return true;
    }

    private void TurnOff(Maker m)
    {
        try { m.Hook?.Disable(); }
        catch (Exception) { Interlocked.Increment(ref _faults); }
    }

    private nint Process(Maker m, GameFn original, nint rcx, nint rdx, nint r8, nint r9)
    {
        nint result = original(rcx, rdx, r8, r9);
        try { Patch(m, rcx); }
        catch (Exception ex) { Fault(ex); }
        return result;
    }

    private void Patch(Maker m, nint rcx)
    {
        string what = m.TakesMap ? $"{m.Name} of map {rcx & 0xFFFF}" : m.Name;
        var r = RareCopyPatch.Apply(_mem, _copy);
        if (!r.Read)
            _log.Warn($"The game filled this battle's treasure copy ({what}), but the copy at 0x{_copy:X} could not be read; "
                + "this battle may give common items.");
        else if (r.Failed > 0)
            _log.Warn($"The game filled this battle's treasure copy ({what}); {r.Changed} slot(s) now give the rare item "
                + $"but {r.Failed} could not be changed. Rows before, rare/common, {Rows(r.Before!)}.");
        else if (r.Changed > 0)
            _log.Event($"The game filled this battle's treasure copy ({what}); {r.Changed} slot(s) now give the rare item. "
                + $"Rows before, rare/common, {Rows(r.Before!)}.");
        else
            _log.Debug($"The game filled this battle's treasure copy ({what}); every slot already gives its rare item.");
    }

    private void Fault(Exception ex)
    {
        Interlocked.Increment(ref _faults);
        try { _log.Warn($"Rare treasure only skipped one treasure copy patch ({ex.Message}); the game carries on."); }
        catch (Exception) { Interlocked.Increment(ref _faults); }
    }

    private static string Rows(byte[] before)
    {
        var sb = new StringBuilder();
        for (int row = 0; row < Tuning.TrapSlots; row++)
        {
            int at = row * RareCopyPatch.RowBytes;
            if (row > 0) sb.Append(' ');
            sb.Append(Mem.U16(before, at + 2)).Append('/').Append(Mem.U16(before, at + 4));
        }
        return sb.ToString();
    }

    /// <summary>One routine that fills the copy. Its hook and detour stay rooted here.</summary>
    private sealed record Maker(string Name, long Addr, bool TakesMap)
    {
        public GameFn? Detour;
        public IGameHook? Hook;
    }
}

/// <summary>Where the hook log lines go. Tests keep them, and live they go to the log.</summary>
internal sealed record CopyLog(Action<string> Event, Action<string> Debug, Action<string> Warn)
{
    public static readonly CopyLog Live = new(
        m => ModLogger.Event(LogVerb.Treasure, m),
        m => ModLogger.Debug(LogVerb.Treasure, m),
        m => ModLogger.Warn(LogVerb.Treasure, m));
}
