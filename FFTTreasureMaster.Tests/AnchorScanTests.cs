using System.Collections.Generic;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// AnchorScan: masked byte-pattern search over caller-supplied sections (chunking, overlap,
/// aggregation, hit cap, budget) plus the RIP-resolve formula. No knowledge of regions/
/// singletons/deltas -- that policy is AnchorResolver's job, covered separately.
/// </summary>
public class AnchorScanTests
{
    // 8-byte pattern: two literal bytes, a 4-byte wildcard run (the disp32 field), two more
    // literal bytes. Shared by every test in this file.
    private static readonly int?[] Pattern = new int?[] { 0xAA, 0xBB, null, null, null, null, 0xCC, 0xDD };

    private static AnchorSig MakeSig(string name = "S", int dispOff = 2, int endAdjust = 0, long target = 0)
        => new(name, Pattern, dispOff, endAdjust, target);

    /// <summary>Writes Pattern's literal bytes at <paramref name="offset"/>, filling the
    /// wildcard run with the little-endian bytes of <paramref name="disp32"/> (any value --
    /// wildcards must match regardless).</summary>
    private static void PlacePattern(byte[] buf, int offset, int disp32 = 0)
    {
        for (int i = 0; i < Pattern.Length; i++)
            buf[offset + i] = Pattern[i] is int v ? (byte)v : (byte)0;
        buf[offset + 2] = (byte)(disp32 & 0xFF);
        buf[offset + 3] = (byte)((disp32 >> 8) & 0xFF);
        buf[offset + 4] = (byte)((disp32 >> 16) & 0xFF);
        buf[offset + 5] = (byte)((disp32 >> 24) & 0xFF);
    }

    // ── FindAll: match position coverage ──────────────────────────────────────────

    [Fact]
    public void FindAll_matches_at_range_start()
    {
        var mem = new FakeSparseMemory();
        var buf = new byte[64];
        PlacePattern(buf, 0);
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.NotNull(hits);
        Assert.Equal(new List<long> { 0x1000L }, hits!["S"]);
    }

    [Fact]
    public void FindAll_matches_at_range_end()
    {
        var mem = new FakeSparseMemory();
        var buf = new byte[64];
        int off = buf.Length - Pattern.Length;
        PlacePattern(buf, off);
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Equal(new List<long> { 0x1000L + off }, hits!["S"]);
    }

    [Fact]
    public void FindAll_wildcard_bytes_match_regardless_of_value()
    {
        var mem = new FakeSparseMemory();
        var buf = new byte[64];
        PlacePattern(buf, 0, disp32: unchecked((int)0xDEADBEEF));
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Equal(new List<long> { 0x1000L }, hits!["S"]);
    }

    [Fact]
    public void FindAll_literal_mismatch_is_rejected()
    {
        var mem = new FakeSparseMemory();
        var buf = new byte[64];
        PlacePattern(buf, 0);
        buf[0] = 0xFF;   // corrupt the first literal byte
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Empty(hits!["S"]);
    }

    [Fact]
    public void FindAll_zero_hits_returns_empty_list_not_null()
    {
        var mem = new FakeSparseMemory();
        var buf = new byte[64];   // all zero -- pattern never appears
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.NotNull(hits);
        Assert.Empty(hits!["S"]);
    }

    // ── aggregation across chunks / sections ──────────────────────────────────────

    [Fact]
    public void FindAll_two_hits_in_the_same_chunk_both_reported()
    {
        var mem = new FakeSparseMemory();
        var buf = new byte[256];
        PlacePattern(buf, 0);
        PlacePattern(buf, 64);
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Equal(2, hits!["S"].Count);
        Assert.Contains(0x1000L, hits["S"]);
        Assert.Contains(0x1000L + 64, hits["S"]);
    }

    [Fact]
    public void FindAll_two_hits_in_different_chunks_both_reported()
    {
        // Aggregation across chunks: a per-chunk early-unique would be the bug this guards.
        int chunk = Tuning.AnchorScanChunkBytes;
        var mem = new FakeSparseMemory();
        var buf = new byte[chunk + 0x100];
        PlacePattern(buf, 0);            // chunk 0
        PlacePattern(buf, chunk + 0x10); // chunk 1
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Equal(2, hits!["S"].Count);
        Assert.Contains(0x1000L, hits["S"]);
        Assert.Contains(0x1000L + chunk + 0x10, hits["S"]);
    }

    [Fact]
    public void FindAll_hit_in_overlap_window_counted_exactly_once()
    {
        // Place a hit 1 byte into chunk 0's overlap tail (rejected there; picked up by chunk 1's
        // primary range instead). Must appear exactly once, not zero and not twice.
        int chunk = Tuning.AnchorScanChunkBytes;
        var mem = new FakeSparseMemory();
        var buf = new byte[chunk + 0x100];
        PlacePattern(buf, chunk + 1);
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Single(hits!["S"]);
        Assert.Equal(0x1000L + chunk + 1, hits["S"][0]);
    }

    [Fact]
    public void FindAll_hit_straddling_a_chunk_boundary_is_found()
    {
        int chunk = Tuning.AnchorScanChunkBytes;
        int off = chunk - 3;   // starts inside chunk 0's primary range; its bytes cross into chunk 1
        var mem = new FakeSparseMemory();
        var buf = new byte[chunk + 0x100];
        PlacePattern(buf, off);
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Single(hits!["S"]);
        Assert.Equal(0x1000L + off, hits["S"][0]);
    }

    [Fact]
    public void FindAll_hit_list_is_capped()
    {
        var mem = new FakeSparseMemory();
        int count = Tuning.AnchorScanHitCap + 2;
        var buf = new byte[count * 64];
        for (int i = 0; i < count; i++) PlacePattern(buf, i * 64);
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)buf.Length) }, new[] { MakeSig() });

        Assert.Equal(Tuning.AnchorScanHitCap, hits!["S"].Count);
    }

    // ── failure modes ──────────────────────────────────────────────────────────────

    [Fact]
    public void FindAll_unreadable_chunk_mid_scan_returns_null()
    {
        int chunk = Tuning.AnchorScanChunkBytes;
        int overlap = Pattern.Length - 1;
        var mem = new FakeSparseMemory();
        // Backing data covers exactly chunk 0's window; chunk 1's read then fails.
        var buf = new byte[chunk + overlap];
        PlacePattern(buf, 0);   // a genuine hit exists before the failure
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)(chunk + 0x10000)) }, new[] { MakeSig() });

        Assert.Null(hits);
    }

    [Fact]
    public void FindAll_budget_exceeded_returns_null()
    {
        var mem = new FakeSparseMemory();
        int size = Tuning.AnchorScanMaxTotalBytes + 0x1000;
        var buf = new byte[size];   // large but zero-filled -- no hits needed to prove the cap
        mem.RegisterBlock(0x1000, buf);

        var hits = new AnchorScan(mem).FindAll(new[] { (0x1000L, (long)size) }, new[] { MakeSig() });

        Assert.Null(hits);
    }

    [Fact]
    public void FindAll_never_matches_across_a_section_boundary()
    {
        // The full 8-byte pattern split 4+4 across two adjacent 4-byte sections must never
        // be found -- each section is scanned independently, never concatenated.
        var mem = new FakeSparseMemory();
        var full = new byte[8];
        PlacePattern(full, 0);
        mem.RegisterBlock(0x1000, full);

        var sections = new[] { (0x1000L, 4L), (0x1004L, 4L) };
        var hits = new AnchorScan(mem).FindAll(sections, new[] { MakeSig() });

        Assert.NotNull(hits);
        Assert.Empty(hits!["S"]);
    }

    // ── ResolveTarget ──────────────────────────────────────────────────────────────

    private static void SeedDisp(FakeSparseMemory mem, long dispAddr, int disp32)
    {
        var bytes = new byte[]
        {
            (byte)(disp32 & 0xFF), (byte)((disp32 >> 8) & 0xFF),
            (byte)((disp32 >> 16) & 0xFF), (byte)((disp32 >> 24) & 0xFF),
        };
        mem.RegisterBlock(dispAddr, bytes);
    }

    [Fact]
    public void ResolveTarget_endAdjust_zero()
    {
        var mem = new FakeSparseMemory();
        long site = 0x140100000L;
        int disp = 0x2000;
        SeedDisp(mem, site + 2, disp);
        var sig = new AnchorSig("S", Pattern, DispOff: 2, EndAdjust: 0, Target: 0);

        long? result = new AnchorScan(mem).ResolveTarget(site, sig);

        Assert.Equal(site + 2 + 4 + 0 + disp, result);
    }

    [Fact]
    public void ResolveTarget_endAdjust_one()
    {
        var mem = new FakeSparseMemory();
        long site = 0x140100000L;
        int disp = 0x2000;
        SeedDisp(mem, site + 2, disp);
        var sig = new AnchorSig("S", Pattern, DispOff: 2, EndAdjust: 1, Target: 0);

        long? result = new AnchorScan(mem).ResolveTarget(site, sig);

        Assert.Equal(site + 2 + 4 + 1 + disp, result);
    }

    [Fact]
    public void ResolveTarget_negative_disp32_is_sign_extended()
    {
        var mem = new FakeSparseMemory();
        long site = 0x140100000L;
        int disp = -0x100;
        SeedDisp(mem, site + 2, disp);
        var sig = new AnchorSig("S", Pattern, DispOff: 2, EndAdjust: 0, Target: 0);

        long? result = new AnchorScan(mem).ResolveTarget(site, sig);

        Assert.Equal(site + 2 + 4 + 0 + disp, result);
        Assert.True(result < site, "a negative disp32 must resolve to an address BEFORE the site");
    }
}
