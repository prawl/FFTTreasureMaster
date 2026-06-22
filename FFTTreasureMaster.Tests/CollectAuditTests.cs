using Xunit;
namespace FFTTreasureMaster.Tests;
public class CollectAuditTests
{
    // Worked example (spec): map 74, slot 0 -> idx = 296, addr = 0x1411A7680 + 37 = 0x1411A76A5, bit = 7.
    private const long Base    = 0x1411A7680L;
    private const int  Map74   = 74;

    private static (long addr, int bit) Decode(int mapId, int slot)
    {
        int idx  = mapId * 4 + slot;
        long addr = Base + idx / 8;
        int  bit  = 7 - (idx % 8);
        return (addr, bit);
    }

    [Fact]
    public void Decode_true_map74_slot0_bit7()
    {
        // idx = 74*4+0 = 296; addr = 0x1411A7680 + 37 = 0x1411A76A5; bit = 7.
        var (addr, bit) = Decode(Map74, 0);
        Assert.Equal(0x1411A76A5L, addr);
        Assert.Equal(7, bit);

        var mem = new FakeSparseMemory();
        mem.U8s[addr]         = 0x80;   // bit 7 set
        mem.ReadableAddrs.Add(addr);

        var audit = new CollectAudit(mem);
        var tile  = new TreasureTile { X = 0, Y = 1, Slot = 0 };
        Assert.True(audit.IsCollected(Map74, tile));
    }

    [Fact]
    public void Decode_false_when_bit_clear()
    {
        var (addr, _) = Decode(Map74, 0);
        var mem = new FakeSparseMemory();
        mem.U8s[addr]         = 0x00;   // bit 7 clear
        mem.ReadableAddrs.Add(addr);

        var audit = new CollectAudit(mem);
        var tile  = new TreasureTile { X = 0, Y = 1, Slot = 0 };
        Assert.False(audit.IsCollected(Map74, tile));
    }

    [Fact]
    public void Decode_slot1_bit6_same_byte_as_slot0()
    {
        // map 74, slot 1 -> idx = 297; addr = 0x1411A7680 + 37 = 0x1411A76A5; bit = 6.
        var (addr0, _) = Decode(Map74, 0);
        var (addr1, bit1) = Decode(Map74, 1);
        Assert.Equal(addr0, addr1);   // same byte
        Assert.Equal(6, bit1);

        var mem = new FakeSparseMemory();
        mem.U8s[addr1]         = 0x40;   // bit 6 set, bit 7 clear
        mem.ReadableAddrs.Add(addr1);

        var audit = new CollectAudit(mem);
        Assert.True(audit.IsCollected(Map74,  new TreasureTile { Slot = 1 }));   // bit 6 set -> collected
        Assert.False(audit.IsCollected(Map74, new TreasureTile { Slot = 0 }));   // bit 7 clear -> not collected
    }

    [Fact]
    public void Fail_safe_when_addr_not_readable()
    {
        var (addr, _) = Decode(Map74, 0);
        var mem = new FakeSparseMemory();
        mem.U8s[addr] = 0xFF;   // byte present in U8s but NOT in ReadableAddrs
        // ReadableAddrs intentionally NOT populated

        var audit = new CollectAudit(mem);
        var tile  = new TreasureTile { X = 0, Y = 1, Slot = 0 };
        Assert.False(audit.IsCollected(Map74, tile));
    }

    [Fact]
    public void Fail_safe_unknown_slot_reads_nothing()
    {
        var mem   = new FakeSparseMemory();
        var audit = new CollectAudit(mem);

        // Slot -1 (default / unknown): must return false and not touch game memory.
        var tileNeg = new TreasureTile { X = 3, Y = 4, Slot = -1 };
        Assert.False(audit.IsCollected(Map74, tileNeg));

        // Slot 4 (out-of-range): same guarantee.
        var (wouldBeAddr, _) = Decode(Map74, 4);
        var tile4 = new TreasureTile { X = 3, Y = 4, Slot = 4 };
        Assert.False(audit.IsCollected(Map74, tile4));

        // Confirm no reads touched the would-be address for slot 4.
        Assert.False(mem.ReadCount.ContainsKey(wouldBeAddr));
    }
}
