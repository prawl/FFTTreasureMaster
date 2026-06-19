using System.Collections.Generic;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Unit tests for TileHolder.Unlight -- the clear path that reverts Held bytes back to the
/// per-addr resting value. Complements the Hold tests that live in TreasureMasterTests.cs.
///
/// Contract:
///   Held (0xCC == MarkValue) bytes: written with the tile's captured off value; counted.
///   Resting (0x00, 0x01) bytes:      already unlit; no write.
///   Foreign (any other value) bytes:  never written (may be off-screen render).
///   Unwritable address:               skipped via Writable guard.
///   Return value:                     count of addresses actually reverted.
/// </summary>
public class TileHolderTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-tile TreasureMap with a single address seeded at <paramref name="currentByte"/>
    /// in a FakeSparseMemory. The tile's resting off value is <paramref name="restOff"/>.
    /// </summary>
    private static (FakeSparseMemory mem, TreasureMap map, long addr)
        MakeScene(byte currentByte, byte restOff = 0x00, bool writable = true)
    {
        long addr = 0x140300000L;
        var mem = new FakeSparseMemory();
        mem.U8s[addr]  = currentByte;
        mem.ReadableAddrs.Add(addr);
        if (writable) mem.WritableAddrs.Add(addr);

        var tile = new TreasureTile
        {
            X = 1, Y = 2,
            Addrs = new System.Collections.Generic.List<TreasureAddrEntry>
            {
                new() { Addr = addr, Off = restOff },
            },
        };
        var map = new TreasureMap
        {
            MapId = 74, Name = "Test", TileCount = 1,
            Tiles = new System.Collections.Generic.List<TreasureTile> { tile },
        };
        return (mem, map, addr);
    }

    // ── Held -> off ──────────────────────────────────────────────────────────────

    [Fact]
    public void Unlight_Held_byte_is_written_with_off_value()
    {
        var (mem, map, addr) = MakeScene(currentByte: TreasureMaster.MarkValue, restOff: 0x00);
        var holder = new TileHolder(mem);

        int n = holder.Unlight(map.Tiles[0]);

        Assert.Equal(1, n);
        Assert.True(mem.Written.ContainsKey(addr));
        Assert.Equal(0x00, mem.Written[addr]);
    }

    [Fact]
    public void Unlight_Held_byte_written_with_nonzero_off()
    {
        var (mem, map, addr) = MakeScene(currentByte: TreasureMaster.MarkValue, restOff: 0x01);
        var holder = new TileHolder(mem);

        int n = holder.Unlight(map.Tiles[0]);

        Assert.Equal(1, n);
        Assert.Equal(0x01, mem.Written[addr]);
    }

    // ── Resting -> no write ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    public void Unlight_Resting_byte_produces_no_write(byte resting)
    {
        var (mem, map, addr) = MakeScene(currentByte: resting, restOff: 0x00);
        var holder = new TileHolder(mem);

        int n = holder.Unlight(map.Tiles[0]);

        Assert.Equal(0, n);
        Assert.False(mem.Written.ContainsKey(addr));
    }

    // ── Foreign -> skip ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x42)]
    [InlineData(0x80)]
    [InlineData(0xCD)]
    [InlineData(0xFF)]
    public void Unlight_Foreign_byte_never_written(byte foreign)
    {
        var (mem, map, addr) = MakeScene(currentByte: foreign, restOff: 0x00);
        var holder = new TileHolder(mem);

        int n = holder.Unlight(map.Tiles[0]);

        Assert.Equal(0, n);
        Assert.False(mem.Written.ContainsKey(addr));
    }

    // ── Unwritable address -> skip ────────────────────────────────────────────────

    [Fact]
    public void Unlight_Unwritable_addr_is_skipped()
    {
        var (mem, map, addr) = MakeScene(currentByte: TreasureMaster.MarkValue, writable: false);
        var holder = new TileHolder(mem);

        int n = holder.Unlight(map.Tiles[0]);

        Assert.Equal(0, n);
        Assert.False(mem.Written.ContainsKey(addr));
    }

    // ── Multi-addr tile: only Held bytes reverted ─────────────────────────────────

    [Fact]
    public void Unlight_multi_addr_reverts_only_Held_returns_correct_count()
    {
        long addrHeld    = 0x140300100L;
        long addrResting = 0x140300200L;
        long addrForeign = 0x140300300L;

        var mem = new FakeSparseMemory();
        mem.U8s[addrHeld]    = TreasureMaster.MarkValue;
        mem.U8s[addrResting] = 0x00;
        mem.U8s[addrForeign] = 0xAB;
        foreach (var a in new[] { addrHeld, addrResting, addrForeign })
        {
            mem.ReadableAddrs.Add(a);
            mem.WritableAddrs.Add(a);
        }

        var tile = new TreasureTile
        {
            X = 3, Y = 4,
            Addrs = new System.Collections.Generic.List<TreasureAddrEntry>
            {
                new() { Addr = addrHeld,    Off = 0x00 },
                new() { Addr = addrResting, Off = 0x00 },
                new() { Addr = addrForeign, Off = 0x00 },
            },
        };

        var holder = new TileHolder(mem);
        int n = holder.Unlight(tile);

        Assert.Equal(1, n);
        Assert.True(mem.Written.ContainsKey(addrHeld));
        Assert.False(mem.Written.ContainsKey(addrResting));
        Assert.False(mem.Written.ContainsKey(addrForeign));
        Assert.Equal(0x00, mem.Written[addrHeld]);
    }
}
