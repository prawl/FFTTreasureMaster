using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// HunterHold over a pinned buffer of seats 15 to 21, driven through the real LiveMemory
/// read and write path behind a counting wrapper that can drop writes or fire a hook.
/// </summary>
public sealed class HunterHoldTests : IDisposable
{
    private sealed class CountingMem : IGameMemory
    {
        private readonly LiveMemory _in = new();
        public int Reads, U8Reads, Writes;
        public bool WritableAll = true, DropWrites, FailRead;
        public long HookAddr;
        public Action? Hook;
        public byte U8(long a)
        {
            U8Reads++;
            if (a == HookAddr && Hook is { } h) { Hook = null; h(); }
            return _in.U8(a);
        }
        public ushort U16(long a) => _in.U16(a);
        public bool TryReadBytes(long a, int n, out byte[] b) { Reads++; bool ok = _in.TryReadBytes(a, n, out b); return ok && !FailRead; }
        public void W8(long a, byte v) { Writes++; if (!DropWrites) _in.W8(a, v); }
        public bool Readable(long a, int n) => _in.Readable(a, n);
        public bool Writable(long a, int n) => WritableAll && _in.Writable(a, n);
    }

    private sealed class Capture : ModGates.Logging.ILogger
    {
        public readonly List<(string Level, string Msg)> Lines = new();
        public ModGates.Logging.LogLevel LogLevel { get; set; }
        public void Log(ModGates.Logging.LogVerb v, string m) => Lines.Add(("info", m));
        public void LogWarning(ModGates.Logging.LogVerb v, string m) => Lines.Add(("warn", m));
        public void LogError(ModGates.Logging.LogVerb v, string m) => Lines.Add(("error", m));
        public void LogError(ModGates.Logging.LogVerb v, string m, Exception e) => Lines.Add(("error", m));
        public void LogDebug(ModGates.Logging.LogVerb v, string m) => Lines.Add(("debug", m));
        public void NoteBattleEdge() { }
        public int Count(string level, string startsWith) => Lines.Count(l => l.Level == level && l.Msg.StartsWith(startsWith));
        public bool Has(string contains) => Lines.Any(l => l.Msg.Contains(contains));
    }

    private readonly PinnedBuf _pin = PinnedBuf.Of(7 * 0x200);
    private readonly CountingMem _mem = new();
    private readonly AddrMap _addrs = new();
    private readonly Capture _log = new();
    private readonly HunterHold _hold;

    public HunterHoldTests()
    {
        _addrs.Apply(new AnchorResolution { UnitArrayBaseX = _pin.Addr + 0x200 + 0x4F - 31 * 0x200 });
        _hold = new HunterHold(_mem, _addrs);
        _hold.ResetBattle(entered: true);
        ModLogger.Instance = _log;
    }

    public void Dispose()
    {
        ModLogger.UseNullLogger();
        _pin.Dispose();
    }

    private static int Off(int seat) => (seat - 15) * 0x200;
    private byte HunterByte(int seat) => _pin.Bytes[Off(seat) + 0x9E];

    // A party unit that must get the hold, so every negative test changes one field of it.
    private void Qualifying(int seat, int job = 74, int equipped = 488, byte field0 = 0x20, int name = 900)
    {
        int o = Off(seat);
        _pin.Bytes[o + 1] = 0;
        _pin.Bytes[o + 3] = (byte)job;
        _pin.Bytes[o + 5] = 0x0B;
        _pin.Bytes[o + 0x18] = (byte)equipped;
        _pin.Bytes[o + 0x19] = (byte)(equipped >> 8);
        _pin.Bytes[o + 0x9C] = field0;
        _pin.Bytes[o + 0x1B5] = 1;
        _pin.Bytes[o + 0x1FC] = (byte)name;
        _pin.Bytes[o + 0x1FD] = (byte)(name >> 8);
    }

    private void Ticks(int n) { for (int i = 0; i < n; i++) _hold.Tick(true, true); }

    [Fact]
    public void T1_SeatRecord_lands_on_the_proven_party_addresses()
    {
        ImageBase.ResetForTests();
        var a = new AddrMap();
        Assert.Equal(ImageBase.At(0x141855D7EL), HunterHold.SeatRecord(a, 0) + 0x9E);
        Assert.Equal(ImageBase.At(0x141855D7EL) + 0x800, HunterHold.SeatRecord(a, 4) + 0x9E);
    }

    [Fact]
    public void T2_Qualifying_unit_gets_only_the_hunter_bit()
    {
        Qualifying(16);
        var before = _pin.Bytes.ToArray();
        Ticks(1);
        var after = _pin.Bytes.ToArray();
        Assert.Equal(0x01, HunterByte(16));
        for (int i = 0; i < before.Length; i++)
            if (i != Off(16) + 0x9E) Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void T3_Not_displayed_and_untrusted_touch_nothing()
    {
        Qualifying(16);
        _hold.Tick(false, true);
        _hold.Tick(true, false);
        _hold.Tick(true, false);
        Assert.Equal(0, _mem.Reads + _mem.U8Reads + _mem.Writes);
        Assert.Equal(1, _log.Count("info", "The All units Treasure Hunter setting"));
        _hold.ResetBattle(entered: true);
        _hold.Tick(true, false);
        Assert.Equal(2, _log.Count("info", "The All units Treasure Hunter setting"));
    }

    [Fact]
    public void T4_Chemist_gets_nothing()
    {
        Qualifying(16, job: 75);
        Ticks(2);
        Assert.Equal(0, HunterByte(16));
    }

    [Fact]
    public void T5_Enemy_team_gets_nothing()
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + 5] = 0x9B;
        Ticks(2);
        Assert.Equal(0, HunterByte(16));
    }

    [Theory]
    [InlineData(1, 0xFF)]
    [InlineData(0x1B5, 0)]
    public void T6_Staging_copy_or_absent_unit_gets_nothing(int field, int value)
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + field] = (byte)value;
        Ticks(2);
        Assert.Equal(0, HunterByte(16));
    }

    // An equipped 509 replaces the 488 word, so its record carries no 488 field bit, or the unit
    // reads as wrong looking and the owner check is never reached.
    [Theory]
    [InlineData(0x18, 0x00)]
    [InlineData(0x0A, 0x20)]
    [InlineData(0x0C, 0x20)]
    [InlineData(0x0E, 0x20)]
    [InlineData(0x10, 0x20)]
    public void T7_Owning_509_is_never_written_or_taken_back(int word, int field0)
    {
        Qualifying(16, field0: (byte)field0);
        _pin.Bytes[Off(16) + word] = 0xFD;
        _pin.Bytes[Off(16) + word + 1] = 0x01;
        Ticks(2);
        Assert.Equal(0, HunterByte(16));
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(2);
        Assert.Equal(0x01, HunterByte(16));
    }

    [Fact]
    public void T8_Looks_wrong_gets_nothing()
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + 0x9C] |= 0x04;
        Qualifying(17);
        _pin.Bytes[Off(17) + 0x18] = 0x58;
        _pin.Bytes[Off(17) + 0x19] = 0x02;
        Ticks(2);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(0, HunterByte(17));
    }

    [Fact]
    public void T9_Already_held_is_never_written()
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        Ticks(3);
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T10_Charm_takes_it_back_and_the_end_of_charm_restores_it()
    {
        Qualifying(16);
        Ticks(1);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(1, _log.Count("info", "Treasure Hunter was taken back"));
        _pin.Bytes[Off(16) + 5] = 0x0B;
        Ticks(1);
        Assert.Equal(0x01, HunterByte(16));
    }

    // A Retry or a resume hands the bit back from the game's own copy, as seen live, so the hold
    // finds it already set and must still own it.
    [Fact]
    public void T11_A_restored_bit_on_a_qualifying_unit_is_adopted_and_taken_back_on_a_side_change()
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        Ticks(1);
        Assert.Equal(0, _mem.Writes);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(1, _log.Count("info", "Treasure Hunter was taken back"));
        _pin.Bytes[Off(16) + 5] = 0x0B;
        Ticks(1);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(1, _log.Count("debug", "hunter set"));
    }

    [Theory]
    [InlineData(0x18, 0x00)]
    [InlineData(0x0A, 0x20)]
    public void T29_A_real_Treasure_Hunter_bit_is_never_adopted(int word, int field0)
    {
        Qualifying(16, field0: (byte)field0);
        _pin.Bytes[Off(16) + word] = 0xFD;
        _pin.Bytes[Off(16) + word + 1] = 0x01;
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        Ticks(2);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(2);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(0, _mem.Writes);
        Assert.Equal(0, _log.Count("info", "Treasure Hunter was taken back"));
    }

    [Fact]
    public void T29_An_enemy_in_a_party_seat_is_never_adopted()
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + 5] = 0x9B;
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        Ticks(2);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(2);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(0, _mem.Writes);
        Assert.Equal(0, _log.Count("info", "Treasure Hunter was taken back"));
    }

    [Theory]
    [InlineData(6, 0x01)]
    [InlineData(0x9C, 0x40)]
    [InlineData(1, 0xFF)]
    public void T30_A_guest_an_odd_record_or_an_empty_seat_is_never_adopted(int field, int value)
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + field] = (byte)value;
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        Ticks(2);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(2);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T31_An_adoption_leaves_one_trace_line_and_a_flight_record()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tm_hunter_adopt_" + Guid.NewGuid().ToString("N"));
        try
        {
            Flight.Reset();
            Flight.Init(dir);
            Qualifying(16);
            _pin.Bytes[Off(16) + 0x9E] = 0x01;
            Ticks(3);
            Flight.RequestFlush("error");
            Flight.DrainPending();

            Assert.Equal(1, _log.Count("debug", "hunter adopt seat=16 job=74 name=900"));
            string text = string.Concat(Directory.GetFiles(Path.Combine(dir, "flight"), "flight_*.jsonl").Select(File.ReadAllText));
            Assert.Contains("adopt seat=16 job=74 name=900", text);
        }
        finally
        {
            Flight.Reset();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void T29_A_Chemist_with_a_set_bit_is_never_adopted()
    {
        Qualifying(16, job: 75);
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        Ticks(2);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(2);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T12_A_rebuild_is_held_again_with_a_trace_line()
    {
        Qualifying(16);
        Ticks(1);
        _pin.Bytes[Off(16) + 0x9E] = 0;
        Ticks(1);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(2, _log.Count("debug", "hunter set"));
    }

    [Fact]
    public void T13_ResetBattle_forgets_ownership_until_a_tick_adopts_the_bit_again()
    {
        Qualifying(16);
        Ticks(1);
        _hold.ResetBattle(entered: true);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0x01, HunterByte(16));
        _pin.Bytes[Off(16) + 5] = 0x0B;
        Ticks(1);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0, HunterByte(16));
    }

    [Fact]
    public void T14_Neighbour_seats_are_never_written()
    {
        Qualifying(15);
        Qualifying(21);
        var before = _pin.Bytes.ToArray();
        Ticks(2);
        Assert.Equal(before, _pin.Bytes);
    }

    [Fact]
    public void T15_Unreadable_array_does_not_throw_or_write()
    {
        _addrs.Apply(new AnchorResolution { UnitArrayBaseX = 0x10 });
        Ticks(2);
        Assert.Equal(0, _mem.U8Reads);
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T16_A_monster_with_a_movement_innate_gets_the_bit()
    {
        Qualifying(17, job: 94, equipped: 0, field0: 0x02);
        _pin.Bytes[Off(17) + 0x0A] = 0xEC;
        _pin.Bytes[Off(17) + 0x0B] = 0x01;
        Ticks(1);
        Assert.Equal(0x01, HunterByte(17));
    }

    [Fact]
    public void T17_Summary_logs_once_after_settling_and_a_short_wrong_look_logs_no_skip()
    {
        Qualifying(16);
        Ticks(4);
        Assert.Equal(0, _log.Count("info", "Treasure Hunter is on"));
        Ticks(1);
        Assert.Equal(1, _log.Count("info", "Treasure Hunter is on for 1"));
        Ticks(2);
        Assert.Equal(1, _log.Count("info", "Treasure Hunter is on"));
        Qualifying(17);
        _pin.Bytes[Off(17) + 0x9C] |= 0x04;
        Ticks(2);
        Qualifying(17);
        Ticks(10);
        Assert.Equal(0, _log.Count("info", "A deployed unit gets no"));
    }

    [Fact]
    public void T19_Guest_marker_is_skipped_logged_once_and_taken_back_if_it_appears_later()
    {
        Qualifying(16);
        _pin.Bytes[Off(16) + 6] = 0x01;
        Qualifying(17);
        _pin.Bytes[Off(17) + 5] = 0x0F;
        Qualifying(18);
        Ticks(12);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(0, HunterByte(17));
        Assert.Equal(0x01, HunterByte(18));
        Assert.Equal(2, _log.Count("info", "A deployed unit gets no Treasure Hunter from the mod because it shows the guest marker"));
        Assert.True(_log.Has("hunter skip seat=16 job=74 name=900"));
        _pin.Bytes[Off(18) + 6] = 0x08;
        Ticks(1);
        Assert.Equal(0, HunterByte(18));
        Assert.Equal(1, _log.Count("info", "Treasure Hunter was taken back from a deployed unit because it shows the guest marker"));
    }

    [Fact]
    public void T25_A_take_back_whose_bit_is_already_clear_logs_nothing()
    {
        Qualifying(16);
        Ticks(1);
        _mem.HookAddr = HunterHold.SeatRecord(_addrs, 0) + 0x9E;
        _mem.Hook = () => _pin.Bytes[Off(16) + 0x9E] = 0;
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0, _log.Count("info", "Treasure Hunter was taken back"));
        Assert.Equal(0, _log.Lines.Count(l => l.Msg.StartsWith("hunter take")));
        Assert.Equal(1, _mem.Writes);
    }

    [Fact]
    public void T25_A_take_back_trace_names_the_unit_and_the_reason()
    {
        Qualifying(16);
        Ticks(1);
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.True(_log.Has("hunter take seat=16 job=74 name=900 reason=changed sides"));
    }

    [Fact]
    public void T26_Changing_the_name_bytes_of_a_held_unit_logs_no_new_summary()
    {
        Qualifying(16);
        Ticks(6);
        Assert.Equal(1, _log.Count("info", "Treasure Hunter is on"));
        _pin.Bytes[Off(16) + 0x1FC] = 0x77;
        Ticks(8);
        Assert.Equal(1, _log.Count("info", "Treasure Hunter is on"));
    }

    [Fact]
    public void T25_A_guest_take_back_is_a_trace_with_its_reason_and_counts_as_guest()
    {
        Qualifying(18);
        Ticks(1);
        _pin.Bytes[Off(18) + 6] = 0x08;
        Ticks(1);
        Assert.Equal(0, HunterByte(18));
        Assert.True(_log.Has("hunter take seat=18 job=74 name=900 reason=guest marker"));
        Ticks(3);
        Assert.False(_log.Has("hunter skip seat=18"));
        Ticks(1);
        Assert.True(_log.Has("hunter skip seat=18"));
    }

    [Fact]
    public void T27_A_failed_block_read_writes_nothing_even_with_a_filled_buffer()
    {
        Qualifying(16);
        _mem.FailRead = true;
        Ticks(1);
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T25_The_set_and_the_guest_take_back_reach_the_flight_record()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tm_hunter_flight_" + Guid.NewGuid().ToString("N"));
        try
        {
            Flight.Reset();
            Flight.Init(dir);
            Qualifying(16);
            Ticks(1);
            _pin.Bytes[Off(16) + 6] = 0x08;
            Ticks(1);
            Flight.RequestFlush("error");
            Flight.DrainPending();

            string text = string.Concat(Directory.GetFiles(Path.Combine(dir, "flight"), "flight_*.jsonl").Select(File.ReadAllText));
            Assert.Contains("set seat=16 job=74 name=900", text);
            Assert.Contains("take seat=16 job=74 name=900 reason=guest marker", text);
        }
        finally
        {
            Flight.Reset();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void T20_Wrong_looking_unit_logs_one_settled_skip_with_its_name()
    {
        Qualifying(16, name: 1234);
        _pin.Bytes[Off(16) + 0x9C] |= 0x04;
        Ticks(12);
        Assert.Equal(1, _log.Count("info", "A deployed unit gets no Treasure Hunter from the mod because it does not look right"));
        Assert.True(_log.Has("hunter skip seat=16 job=74 name=1234"));
    }

    [Fact]
    public void T21_A_bit_set_between_the_block_read_and_the_write_survives()
    {
        Qualifying(16);
        _mem.HookAddr = HunterHold.SeatRecord(_addrs, 0) + 0x9E;
        _mem.Hook = () => _pin.Bytes[Off(16) + 0x9E] |= 0x02;
        Ticks(1);
        Assert.Equal(0x03, HunterByte(16));
    }

    [Fact]
    public void T22_Take_back_keeps_the_other_seven_bits()
    {
        Qualifying(16);
        Ticks(1);
        _pin.Bytes[Off(16) + 0x9E] = 0xFF;
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0xFE, HunterByte(16));
    }

    [Fact]
    public void T23_Not_writable_means_no_write()
    {
        Qualifying(16);
        _mem.WritableAll = false;
        Ticks(3);
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T24_A_dropped_write_warns_once_and_is_not_owned()
    {
        Qualifying(16);
        _mem.DropWrites = true;
        Ticks(10);
        Assert.Equal(1, _log.Count("warn", "Treasure Hunter could not be written"));
        _mem.DropWrites = false;
        _pin.Bytes[Off(16) + 0x9E] = 0x01;
        _pin.Bytes[Off(16) + 5] = 0x1B;
        Ticks(1);
        Assert.Equal(0x01, HunterByte(16));
    }

    // Seen live on a Continue into a mid battle autosave. The battle drew 263 ms before the
    // engine's start edge, the hold set four bits, and the edge's reset then forgot them.
    [Fact]
    public void T28_A_Continue_writes_nothing_before_the_battle_start_edge()
    {
        var hold = new HunterHold(_mem, _addrs);
        Qualifying(16);
        hold.Tick(true, true);
        hold.Tick(true, true);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(0, _mem.Writes);
    }

    [Fact]
    public void T28_A_unit_charmed_after_a_Continue_loses_the_bit_the_mod_set()
    {
        var hold = new HunterHold(_mem, _addrs);
        Qualifying(16);
        hold.Tick(true, true);
        hold.ResetBattle(entered: true);
        hold.Tick(true, true);
        Assert.Equal(0x01, HunterByte(16));
        _pin.Bytes[Off(16) + 5] = 0x1B;
        hold.Tick(true, true);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(1, _log.Count("info", "Treasure Hunter was taken back"));
    }

    [Fact]
    public void T28_After_a_battle_exit_the_next_load_waits_for_its_own_start_edge()
    {
        Qualifying(16);
        _hold.ResetBattle(entered: false);
        Ticks(3);
        Assert.Equal(0, HunterByte(16));
        Assert.Equal(0, _mem.Writes);
        _hold.ResetBattle(entered: true);
        Ticks(1);
        Assert.Equal(0x01, HunterByte(16));
        Assert.Equal(1, _log.Count("debug", "hunter set"));
    }

    private int Summaries => _log.Count("info", "Treasure Hunter is on");

    [Fact]
    public void T32_The_summary_names_each_occupied_seat_in_order_and_skips_the_empty_one()
    {
        Qualifying(16);
        Qualifying(18, job: 75, name: 902);
        Ticks(5);
        Assert.Equal(1, Summaries);
        Assert.Contains(("info", "Treasure Hunter is on for 1 deployed unit(s)."), _log.Lines);
        Assert.Contains(("debug", "hunter summary [seat=16 job=74 name=900 Held] [seat=18 job=75 name=902 Chemist]"), _log.Lines);
    }

    [Fact]
    public void T32_A_lone_Chemist_logs_a_summary_for_none()
    {
        Qualifying(16, job: 75);
        Ticks(6);
        Assert.Equal(1, Summaries);
        Assert.Contains(("info", "Treasure Hunter is on for 0 deployed unit(s)."), _log.Lines);
        Assert.Contains(("debug", "hunter summary [seat=16 job=75 name=900 Chemist]"), _log.Lines);
    }

    [Fact]
    public void T32_Five_empty_seats_log_no_summary()
    {
        Ticks(8);
        Assert.Equal(0, Summaries);
        Assert.Equal(0, _log.Count("debug", "hunter summary"));
    }

    [Fact]
    public void T32_Coming_back_to_the_logged_seats_after_all_go_empty_logs_no_second_summary()
    {
        Qualifying(16);
        Ticks(5);
        Assert.Equal(1, Summaries);
        _pin.Bytes[Off(16) + 0x1B5] = 0;
        Ticks(6);
        _pin.Bytes[Off(16) + 0x1B5] = 1;
        Ticks(6);
        Assert.Equal(1, Summaries);
    }

    [Fact]
    public void T32_A_settled_change_logs_a_second_summary_with_the_new_count_and_seats()
    {
        Qualifying(16);
        Ticks(5);
        Qualifying(17, name: 901);
        Ticks(4);
        Assert.Equal(1, Summaries);
        Ticks(1);
        Assert.Equal(2, Summaries);
        Assert.Contains(("info", "Treasure Hunter is on for 2 deployed unit(s)."), _log.Lines);
        Assert.Contains(("debug", "hunter summary [seat=16 job=74 name=900 Held] [seat=17 job=74 name=901 Held]"), _log.Lines);
    }

    // A settled tick may allocate only the block read, so any summary text built there shows up.
    [Fact]
    public void T33_An_unchanged_settled_tick_allocates_nothing_beyond_its_block_read()
    {
        Qualifying(16);
        Qualifying(17, job: 75, name: 901);
        long addr = HunterHold.SeatRecord(_addrs, 0);
        Ticks(12);
        for (int i = 0; i < 4; i++) _mem.TryReadBytes(addr, 5 * 0x200, out _);
        long t0 = GC.GetAllocatedBytesForCurrentThread();
        Ticks(20);
        long ticks = GC.GetAllocatedBytesForCurrentThread() - t0;
        long r0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) _mem.TryReadBytes(addr, 5 * 0x200, out _);
        long reads = GC.GetAllocatedBytesForCurrentThread() - r0;
        Assert.Equal(1, Summaries);
        Assert.Equal(reads, ticks);
    }
}
