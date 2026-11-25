using System;
using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Holds the Treasure Hunter bit on each deployed unit of the player's party seats, every tick.
/// </summary>
internal sealed class HunterHold
{
    private const int Seats = 5, FirstSeatSlot = 31, RecLen = (int)Offsets.UnitRecordStride, SettleTicks = 5;
    private const int XField = 0x4F, MoveField = 0x9C, HunterByte = 0x9E, NameId = 0x1FC;
    private const int ChemistJob = 75, HunterId = 509, FirstMoveId = 486;
    private const byte Bit = 0x01, TeamMask = 0x30;

    internal enum Verdict { Empty, Owns, Chemist, OtherTeam, Guest, LooksWrong, Held, Hold, TakeBack }

    private readonly IGameMemory _mem;
    private readonly AddrMap _addrs;
    private readonly HashSet<int> _setByUs = new();
    private readonly Verdict[] _last = new Verdict[Seats], _settled = new Verdict[Seats];
    private readonly int[] _streak = new int[Seats];
    private readonly HashSet<(int, Verdict)> _skipLogged = new();
    private string _summary = "";
    private bool _warned, _untrustedLogged, _entered;

    public HunterHold(IGameMemory mem, AddrMap addrs) { _mem = mem; _addrs = addrs; }

    // The anchor starts the claim scan window where seat 16 is slot 31, and points at an X field.
    internal static long SeatRecord(AddrMap a, int i) => a.UnitArrayBaseX - XField + (FirstSeatSlot + i) * Offsets.UnitRecordStride;

    private static int U16(ReadOnlySpan<byte> r, int o) => r[o] | r[o + 1] << 8;

    // Movement ids 486 to 509 map to 24 field bits, MSB first, and 509 is masked out as ours.
    private static void AddBit(Span<byte> f, int id)
    {
        if (id < FirstMoveId || id > HunterId) return;
        int i = id - FirstMoveId;
        f[i / 8] |= (byte)(0x80 >> (i % 8));
    }

    private static bool FieldMatches(ReadOnlySpan<byte> r)
    {
        int eq = U16(r, 0x18);
        if (eq != 0 && (eq < FirstMoveId || eq > HunterId)) return false;
        Span<byte> want = stackalloc byte[3];
        AddBit(want, eq);
        for (int o = 0x0A; o <= 0x10; o += 2) AddBit(want, U16(r, o));
        return r[MoveField] == want[0] && r[MoveField + 1] == want[1] && (r[HunterByte] & ~Bit) == (want[2] & ~Bit);
    }

    internal static Verdict Decide(ReadOnlySpan<byte> r, bool setByUs)
    {
        bool bitSet = (r[HunterByte] & Bit) != 0, ours = setByUs && bitSet;
        if (r[1] == 0xFF || r[0x1B5] != 1) return Verdict.Empty;
        if (U16(r, 0x18) == HunterId || U16(r, 0x0A) == HunterId || U16(r, 0x0C) == HunterId
            || U16(r, 0x0E) == HunterId || U16(r, 0x10) == HunterId) return Verdict.Owns;
        if ((r[5] & TeamMask) != 0) return ours ? Verdict.TakeBack : Verdict.OtherTeam;
        if ((r[6] & 0x09) != 0 || (r[5] & 0x04) != 0) return ours ? Verdict.TakeBack : Verdict.Guest;
        if (r[3] == ChemistJob) return Verdict.Chemist;
        if (!FieldMatches(r)) return Verdict.LooksWrong;
        return bitSet ? Verdict.Held : Verdict.Hold;
    }

    public void ResetBattle(bool entered)
    {
        _entered = entered;
        _setByUs.Clear();
        Array.Clear(_last); Array.Clear(_settled); Array.Clear(_streak);
        _skipLogged.Clear();
        _summary = "";
        _warned = _untrustedLogged = false;
    }

    public void Tick(bool battleDisplayed, bool trusted)
    {
        if (!battleDisplayed || !_entered) return;
        if (!trusted)
        {
            if (!_untrustedLogged)
                ModLogger.Event(LogVerb.Treasure, "The All units gain Treasure Hunter setting is on, but this game build's unit addresses are not confirmed, so no unit gets it this battle.");
            _untrustedLogged = true;
            return;
        }
        if (!_mem.TryReadBytes(SeatRecord(_addrs, 0), Seats * RecLen, out var block) || block.Length < Seats * RecLen) return;
        for (int i = 0; i < Seats; i++)
        {
            var rec = new ReadOnlySpan<byte>(block, i * RecLen, RecLen);
            var v = ApplySeat(i, rec, Decide(rec, _setByUs.Contains(i)));
            _streak[i] = v == _last[i] && _streak[i] > 0 ? _streak[i] + 1 : 1;
            _last[i] = v;
            if (_streak[i] >= SettleTicks) _settled[i] = v;
        }
        LogSettled(block);
    }

    private string Who(int i, ReadOnlySpan<byte> rec) => $"seat={16 + i} job={rec[3]} name={U16(rec, NameId)}";

    private Verdict ApplySeat(int i, ReadOnlySpan<byte> rec, Verdict v)
    {
        if (v != Verdict.Hold && v != Verdict.TakeBack) return v;
        long addr = SeatRecord(_addrs, i) + HunterByte;
        if (!_mem.Writable(addr, 1)) return v;
        byte cur = _mem.U8(addr);
        if (v == Verdict.TakeBack)
        {
            _setByUs.Remove(i);
            bool team = (rec[5] & TeamMask) != 0;
            if ((cur & Bit) == 0) return team ? Verdict.OtherTeam : Verdict.Guest;
            _mem.W8(addr, (byte)(cur & ~Bit));
            string detail = Who(i, rec) + " reason=" + (team ? "changed sides" : "guest marker");
            ModLogger.EventWithTrace(LogVerb.Treasure, $"Treasure Hunter was taken back from a deployed unit because it {(team ? "changed sides" : "shows the guest marker")}.", "hunter take " + detail);
            Flight.Record("hunter", "take " + detail);
            return team ? Verdict.OtherTeam : Verdict.Guest;
        }
        if ((cur & Bit) != 0) return Verdict.Held;
        _mem.W8(addr, (byte)(cur | Bit));
        if ((_mem.U8(addr) & Bit) == 0)
        {
            if (!_warned) ModLogger.Warn(LogVerb.Treasure, "Treasure Hunter could not be written for a deployed unit; it plays this battle as in the unmodded game.");
            _warned = true;
            return v;
        }
        _setByUs.Add(i);
        ModLogger.Debug(LogVerb.Trace, "hunter set " + Who(i, rec));
        Flight.Record("hunter", "set " + Who(i, rec));
        return Verdict.Held;
    }

    private void LogSettled(byte[] block)
    {
        int held = 0;
        string detail = "";
        for (int i = 0; i < Seats; i++)
        {
            var rec = new ReadOnlySpan<byte>(block, i * RecLen, RecLen);
            var s = _settled[i];
            if (s == Verdict.Held) held++;
            if (s != Verdict.Empty) detail += $" [{Who(i, rec)} {s}]";
            if ((s != Verdict.Guest && s != Verdict.LooksWrong) || !_skipLogged.Add((i, s))) continue;
            string why = s == Verdict.Guest ? "shows the guest marker" : "does not look right";
            string extra = s == Verdict.LooksWrong ? $" field={Convert.ToHexString(rec.Slice(MoveField, 3))} words={U16(rec, 0x18):X}/{U16(rec, 0x0A):X}/{U16(rec, 0x0C):X}/{U16(rec, 0x0E):X}/{U16(rec, 0x10):X}" : "";
            ModLogger.EventWithTrace(LogVerb.Treasure, $"A deployed unit gets no Treasure Hunter from the mod because it {why}.", "hunter skip " + Who(i, rec) + extra);
        }
        string key = string.Join(",", _settled);
        if (key == _summary || detail.Length == 0) return;
        _summary = key;
        ModLogger.EventWithTrace(LogVerb.Treasure, $"Treasure Hunter is on for {held} deployed unit(s).", "hunter summary" + detail);
    }
}
