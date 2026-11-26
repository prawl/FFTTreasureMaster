using System;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// RareCopyPatch over a pinned stand in for the battle's treasure copy, four rows of six bytes,
/// driven through the real LiveMemory read and write path behind a wrapper that can fail a read,
/// refuse a write or drop one. The Beddha Sandwaste rows are the ones read live on 2026-11-26.
/// </summary>
public sealed class RareCopyPatchTests : IDisposable
{
    private sealed class WrapMem : IGameMemory
    {
        private readonly LiveMemory _in = new();
        public int Writes;
        public bool FailRead, RefuseWrite, DropWrites;
        public byte U8(long a) => _in.U8(a);
        public ushort U16(long a) => _in.U16(a);
        public bool TryReadBytes(long a, int n, out byte[] b) { bool ok = _in.TryReadBytes(a, n, out b); return ok && !FailRead; }
        public void W8(long a, byte v) { Writes++; if (!DropWrites) _in.W8(a, v); }
        public bool Readable(long a, int n) => _in.Readable(a, n);
        public bool Writable(long a, int n) => !RefuseWrite && _in.Writable(a, n);
    }

    private readonly PinnedBuf _copy = PinnedBuf.Of(RareCopyPatch.CopyBytes);
    private readonly WrapMem _mem = new();

    public void Dispose() => _copy.Dispose();

    private void Row(int i, byte tile, byte flags, ushort rare, ushort common)
    {
        int at = i * RareCopyPatch.RowBytes;
        _copy.Bytes[at] = tile;
        _copy.Bytes[at + 1] = flags;
        _copy.Bytes[at + 2] = (byte)rare;
        _copy.Bytes[at + 3] = (byte)(rare >> 8);
        _copy.Bytes[at + 4] = (byte)common;
        _copy.Bytes[at + 5] = (byte)(common >> 8);
    }

    private ushort Common(int i) => (ushort)(_copy.Bytes[i * 6 + 4] | (_copy.Bytes[i * 6 + 5] << 8));
    private ushort Rare(int i) => (ushort)(_copy.Bytes[i * 6 + 2] | (_copy.Bytes[i * 6 + 3] << 8));

    private void Beddha()
    {
        Row(0, 0x37, 0x10, 124, 241);
        Row(1, 0x43, 0x10, 124, 241);
        Row(2, 0x83, 0x10, 124, 241);
        Row(3, 0x88, 0x10, 124, 241);
    }

    [Fact]
    public void Beddha_rows_give_the_rare_item_from_the_common_slot()
    {
        Beddha();

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.True(r.Read);
        Assert.Equal(4, r.Changed);
        Assert.Equal(0, r.Failed);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(124, Common(i));
            Assert.Equal(124, Rare(i));
            Assert.Equal(0x10, _copy.Bytes[i * 6 + 1]);
        }
        Assert.Equal(new byte[] { 0x37, 0x43, 0x83, 0x88 },
                     new[] { _copy.Bytes[0], _copy.Bytes[6], _copy.Bytes[12], _copy.Bytes[18] });
    }

    [Fact]
    public void The_result_keeps_the_rows_as_they_were_before_the_write()
    {
        Beddha();

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.NotNull(r.Before);
        Assert.Equal(RareCopyPatch.CopyBytes, r.Before!.Length);
        Assert.Equal(241, r.Before[4] | (r.Before[5] << 8));
    }

    [Fact]
    public void Unused_and_already_rare_rows_are_never_written()
    {
        Row(0, 0x12, 0x00, 0, 50);
        Row(1, 0x34, 0x10, 7, 7);
        Row(2, 0x56, 0x10, 124, 241);
        Row(3, 0x00, 0x00, 0, 0);

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.Equal(1, r.Changed);
        Assert.Equal(2, _mem.Writes);
        Assert.Equal(50, Common(0));
        Assert.Equal(7, Common(1));
        Assert.Equal(124, Common(2));
        Assert.Equal(0, Common(3));
    }

    [Fact]
    public void An_unreadable_copy_writes_nothing()
    {
        Beddha();
        _mem.FailRead = true;

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.False(r.Read);
        Assert.Equal(0, _mem.Writes);
        Assert.Equal(241, Common(0));
    }

    [Fact]
    public void A_refused_write_counts_as_failed_and_writes_nothing()
    {
        Beddha();
        _mem.RefuseWrite = true;

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.Equal(0, r.Changed);
        Assert.Equal(4, r.Failed);
        Assert.Equal(0, _mem.Writes);
        Assert.Equal(241, Common(3));
    }

    [Fact]
    public void A_write_that_does_not_land_counts_as_failed()
    {
        Beddha();
        _mem.DropWrites = true;

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.Equal(0, r.Changed);
        Assert.Equal(4, r.Failed);
    }

    [Fact]
    public void A_second_pass_finds_nothing_to_change()
    {
        Beddha();
        RareCopyPatch.Apply(_mem, _copy.Addr);
        int writes = _mem.Writes;

        var r = RareCopyPatch.Apply(_mem, _copy.Addr);

        Assert.Equal(0, r.Changed);
        Assert.Equal(writes, _mem.Writes);
    }
}
