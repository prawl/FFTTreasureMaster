using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Unit suite for MarkerWriter -- the native EnhancedMarker (yellow move-find diamond) write
/// path. All facts drive the writer through a FakeSparseMemory (no live game), asserting the
/// dark-by-default safety contract and the per-tile field writes.
///
/// Safety matrix:
///   (1) Default ctor    -- follows Tuning.EnhancedMarkersEnabled (flag-value-agnostic).
///   (2) Disabled        -- enabled:false writes nothing and reads no game memory.
///   (3) Enabled writes  -- Enabled=2, X, Floor=0, Y per tile at the right struct offsets.
///   (4) Null pointer    -- utility ptr derefs to 0 (not yet created) -> no-op.
///   (5) Unreadable ptr  -- ptr slot not Readable -> no-op (never derefs garbage).
///   (6) Unwritable block -- no entry Writable -> writes nothing.
///   (7) Per-entry skip  -- one entry unwritable, siblings still written (TileHolder parity).
///   (8) Slot cap        -- never writes past the 4 hardware marker slots.
///   (9) Seam fact       -- W32/U64 round-trip against real RPM/WPM via PinnedBuf.
/// </summary>
public class MarkerWriterTests
{
    private const long FakeBase = 0x142000000L;   // pretend EnhancedMarkingUtility heap base

    private static TreasureMap MapWith(params (int x, int y)[] tiles)
    {
        var m = new TreasureMap { MapId = 74, Name = "Test" };
        foreach (var (x, y) in tiles)
            m.Tiles.Add(new TreasureTile { X = x, Y = y });
        return m;
    }

    /// <summary>Fake seeded with a readable, non-null utility pointer and <paramref name="slots"/>
    /// writable marker entries.</summary>
    private static FakeSparseMemory MemWithUtility(long basePtr = FakeBase, int slots = 4)
    {
        var mem = new FakeSparseMemory();
        mem.ReadableAddrs.Add(Offsets.EnhancedMarkingUtilityPtr);
        mem.U64s[Offsets.EnhancedMarkingUtilityPtr] = (ulong)basePtr;
        long arrayBase = basePtr + MarkerWriter.ArrayOffset;
        for (int i = 0; i < slots; i++)
            mem.WritableAddrs.Add(arrayBase + (long)MarkerWriter.Stride * i);
        return mem;
    }

    // (1) the default ctor follows the Tuning flag (whatever its shipped value)
    [Fact]
    public void Default_ctor_follows_tuning_flag()
    {
        var mem = MemWithUtility();
        var w = new MarkerWriter(mem);   // no explicit enabled -> Tuning.EnhancedMarkersEnabled
        Assert.Equal(Tuning.EnhancedMarkersEnabled ? 1 : 0, w.Write(MapWith((3, 7))));
    }

    // (2) disabled: no writes, never derefs the pointer
    [Fact]
    public void Disabled_writes_nothing_and_does_not_deref()
    {
        var mem = MemWithUtility();
        var w = new MarkerWriter(mem, enabled: false);

        Assert.Equal(0, w.Write(MapWith((3, 7))));
        Assert.Empty(mem.WrittenU32);
    }

    // (3) enabled: correct fields per tile
    [Fact]
    public void Enabled_writes_enabled_x_floor_y_for_each_tile()
    {
        var mem = MemWithUtility();
        var w = new MarkerWriter(mem, enabled: true);

        int n = w.Write(MapWith((3, 7), (10, 2)));

        Assert.Equal(2, n);
        long ab = FakeBase + MarkerWriter.ArrayOffset;
        Assert.Equal(2u,  mem.WrittenU32[ab + MarkerWriter.FldEnabled]);
        Assert.Equal(3u,  mem.WrittenU32[ab + MarkerWriter.FldX]);
        Assert.Equal(0u,  mem.WrittenU32[ab + MarkerWriter.FldFloor]);
        Assert.Equal(7u,  mem.WrittenU32[ab + MarkerWriter.FldY]);

        long e1 = ab + MarkerWriter.Stride;
        Assert.Equal(2u,  mem.WrittenU32[e1 + MarkerWriter.FldEnabled]);
        Assert.Equal(10u, mem.WrittenU32[e1 + MarkerWriter.FldX]);
        Assert.Equal(0u,  mem.WrittenU32[e1 + MarkerWriter.FldFloor]);
        Assert.Equal(2u,  mem.WrittenU32[e1 + MarkerWriter.FldY]);
    }

    // (4) utility pointer is null (battle not yet built) -> no-op
    [Fact]
    public void Null_utility_pointer_is_noop()
    {
        var mem = new FakeSparseMemory();
        mem.ReadableAddrs.Add(Offsets.EnhancedMarkingUtilityPtr);
        mem.U64s[Offsets.EnhancedMarkingUtilityPtr] = 0UL;
        var w = new MarkerWriter(mem, enabled: true);

        Assert.Equal(0, w.Write(MapWith((1, 1))));
        Assert.Empty(mem.WrittenU32);
    }

    // (5) pointer slot not readable -> no-op (never reads garbage)
    [Fact]
    public void Unreadable_pointer_is_noop()
    {
        var mem = new FakeSparseMemory();
        mem.U64s[Offsets.EnhancedMarkingUtilityPtr] = (ulong)FakeBase;   // value present but slot not Readable
        var w = new MarkerWriter(mem, enabled: true);

        Assert.Equal(0, w.Write(MapWith((1, 1))));
        Assert.Empty(mem.WrittenU32);
    }

    // (6) no entry writable -> writes nothing
    [Fact]
    public void Unwritable_block_writes_nothing()
    {
        var mem = new FakeSparseMemory();
        mem.ReadableAddrs.Add(Offsets.EnhancedMarkingUtilityPtr);
        mem.U64s[Offsets.EnhancedMarkingUtilityPtr] = (ulong)FakeBase;   // no WritableAddrs seeded
        var w = new MarkerWriter(mem, enabled: true);

        Assert.Equal(0, w.Write(MapWith((1, 1), (2, 2))));
        Assert.Empty(mem.WrittenU32);
    }

    // (7) one entry unwritable, the sibling is still written
    [Fact]
    public void Unwritable_single_entry_skipped_siblings_written()
    {
        var mem = MemWithUtility(slots: 2);
        long ab = FakeBase + MarkerWriter.ArrayOffset;
        mem.WritableAddrs.Remove(ab);   // slot 0 not writable; slot 1 stays
        var w = new MarkerWriter(mem, enabled: true);

        int n = w.Write(MapWith((3, 3), (4, 4)));

        Assert.Equal(1, n);
        Assert.False(mem.WrittenU32.ContainsKey(ab + MarkerWriter.FldEnabled));   // slot 0 skipped
        long e1 = ab + MarkerWriter.Stride;
        Assert.Equal(4u, mem.WrittenU32[e1 + MarkerWriter.FldX]);                 // slot 1 written
    }

    // (8) never writes past the 4 hardware slots, even with more tiles + more writable entries
    [Fact]
    public void Caps_at_four_slots()
    {
        var mem = MemWithUtility(slots: 8);
        var w = new MarkerWriter(mem, enabled: true);

        int n = w.Write(MapWith((0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5)));

        Assert.Equal(MarkerWriter.MaxSlots, n);
        long ab = FakeBase + MarkerWriter.ArrayOffset;
        long e4 = ab + (long)MarkerWriter.Stride * 4;   // 5th tile must not be written
        Assert.False(mem.WrittenU32.ContainsKey(e4 + MarkerWriter.FldEnabled));
    }

    // (9) seam round-trip against real process memory
    [Fact]
    public void PinnedBuf_W32_lands_and_U64_reads_back()
    {
        using var pin = PinnedBuf.Of(16);
        var live = new LiveMemory();

        Assert.True(live.Writable(pin.Addr, 4));
        live.W32(pin.Addr, 0x11223344u);
        Assert.Equal(0x44, pin.Bytes[0]);
        Assert.Equal(0x33, pin.Bytes[1]);
        Assert.Equal(0x22, pin.Bytes[2]);
        Assert.Equal(0x11, pin.Bytes[3]);

        for (int i = 0; i < 8; i++) pin.Bytes[i] = (byte)(i + 1);
        Assert.Equal(0x0807060504030201UL, live.U64(pin.Addr));
    }
}
