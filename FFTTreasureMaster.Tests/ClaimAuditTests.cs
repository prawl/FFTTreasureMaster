using System.Collections.Generic;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Unit tests for ClaimAudit -- the stateless read-path for claim detection:
///   CollectOccupied -- the grid (x,y) of every readable, on-grid, non-(0,0) unit slot.
///   ReadCount       -- an item's inventory count (u8 @ InventoryCountBase + itemId), or -1.
/// The claim DECISION (occupied + count rose) lives in TreasureMaster and is covered there.
/// </summary>
public class ClaimAuditTests
{
    private static void SeedUnit(FakeSparseMemory mem, int k, int x, int y, bool readable = true)
    {
        long xAddr = Offsets.UnitArrayBaseX + (long)k * Offsets.UnitRecordStride;
        mem.U8s[xAddr]     = (byte)x;
        mem.U8s[xAddr + 1] = (byte)y;
        if (readable) mem.ReadableAddrs.Add(xAddr);
    }

    private static void SeedCount(FakeSparseMemory mem, int itemId, byte count, bool readable = true)
    {
        long addr = Offsets.InventoryCountBase + itemId;
        mem.U8s[addr] = count;
        if (readable) mem.ReadableAddrs.Add(addr);
    }

    // ── CollectOccupied ─────────────────────────────────────────────────────────

    [Fact]
    public void CollectOccupied_includes_readable_on_grid_units()
    {
        var mem = new FakeSparseMemory();
        SeedUnit(mem, k: 3, x: 5, y: 7);
        SeedUnit(mem, k: 8, x: 10, y: 2);
        var occ = new HashSet<(int, int)>();

        new ClaimAudit(mem).CollectOccupied(occ);

        Assert.Contains((5, 7), occ);
        Assert.Contains((10, 2), occ);
    }

    [Fact]
    public void CollectOccupied_skips_origin_and_off_grid_and_unreadable()
    {
        var mem = new FakeSparseMemory();
        SeedUnit(mem, k: 1, x: 0,  y: 0);                 // origin -> skipped
        SeedUnit(mem, k: 2, x: 40, y: 5);                 // off-grid x -> skipped
        SeedUnit(mem, k: 3, x: 5,  y: 99);                // off-grid y -> skipped
        SeedUnit(mem, k: 4, x: 6,  y: 6, readable: false);// unreadable -> skipped
        var occ = new HashSet<(int, int)>();

        new ClaimAudit(mem).CollectOccupied(occ);

        Assert.Empty(occ);
    }

    // ── ReadCount ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadCount_returns_count_for_readable_item()
    {
        var mem = new FakeSparseMemory();
        SeedCount(mem, itemId: 129, count: 3);
        Assert.Equal(3, new ClaimAudit(mem).ReadCount(129));
    }

    [Fact]
    public void ReadCount_returns_minus1_for_nonitem_or_unreadable()
    {
        var mem = new FakeSparseMemory();
        SeedCount(mem, itemId: 50, count: 4, readable: false);
        var audit = new ClaimAudit(mem);
        Assert.Equal(-1, audit.ReadCount(0));    // not a real item
        Assert.Equal(-1, audit.ReadCount(-5));   // not a real item
        Assert.Equal(-1, audit.ReadCount(50));   // unreadable
    }
}
