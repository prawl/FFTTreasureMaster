using System.Collections.Generic;
using FFTTreasureMaster;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// TrapCopySites over a fake code section holding the three sites that name the battle's treasure
/// copy. Each site is its pattern with the wildcards filled, and its displacements set so it names
/// the copy, another address, or a wrong image base. The patterns themselves were cut from
/// FFT_enhanced.exe sha256 937233f7 on 2026-11-26, where each matched once and named 0x140D8DC90.
/// </summary>
public sealed class TrapCopySitesTests
{
    private const long Text = 0x140001000L;
    private const long Copy = 0x140D8DC90L;
    private const int CopyAt = 0x40, RestoreAt = 0x140, LookupAt = 0x280;

    private readonly byte[] _code = new byte[0x400];
    private readonly FakeSparseMemory _mem = new();

    private static readonly List<(long, long)> Sections = new() { (Text, 0x400) };

    private void Put(int at, AnchorSig sig)
    {
        for (int i = 0; i < sig.Pattern.Length; i++)
            _code[at + i] = (byte)(sig.Pattern[i] ?? 0);
    }

    private void Disp(int at, int dispOff, long target)
    {
        long site = Text + at;
        int d = (int)(target - (site + dispOff + 4));
        System.BitConverter.GetBytes(d).CopyTo(_code, at + dispOff);
    }

    private void Rva(int at, int dispOff, long target)
        => System.BitConverter.GetBytes((uint)(target - ImageBase.Base)).CopyTo(_code, at + dispOff);

    private void AllThree(long restoreNames = Copy, long lookupNames = Copy, long lookupBase = 0)
    {
        Put(CopyAt, TrapCopySites.CopyStore);
        Disp(CopyAt, TrapCopySites.CopyStore.DispOff, Copy);
        Put(RestoreAt, TrapCopySites.RestoreStore);
        Disp(RestoreAt, TrapCopySites.RestoreStore.DispOff, restoreNames);
        Put(LookupAt, TrapCopySites.LookupRead);
        Disp(LookupAt, TrapCopySites.LookupBaseDispOff, lookupBase == 0 ? ImageBase.Base : lookupBase);
        Rva(LookupAt, TrapCopySites.LookupRead.DispOff, lookupNames);
    }

    private TrapCopyFind Find()
    {
        _mem.RegisterBlock(Text, _code);
        return TrapCopySites.Find(_mem, Sections);
    }

    [Fact]
    public void Three_sites_that_agree_give_the_copy_address()
    {
        AllThree();

        var f = Find();

        Assert.Equal(Copy, f.Addr);
    }

    [Fact]
    public void A_site_naming_another_address_gives_nothing()
    {
        AllThree(restoreNames: Copy + 0x18);

        var f = Find();

        Assert.Null(f.Addr);
        Assert.Contains("disagree", f.Why);
    }

    [Fact]
    public void A_lookup_naming_another_address_gives_nothing()
    {
        AllThree(lookupNames: Copy - 0x30);

        Assert.Null(Find().Addr);
    }

    [Fact]
    public void A_lookup_whose_base_is_not_the_image_gives_nothing()
    {
        AllThree(lookupBase: ImageBase.Base + 0x1000);

        var f = Find();

        Assert.Null(f.Addr);
        Assert.Contains("image base", f.Why);
    }

    [Fact]
    public void A_missing_site_gives_nothing_and_names_it()
    {
        AllThree();
        for (int i = LookupAt; i < LookupAt + TrapCopySites.LookupRead.Pattern.Length; i++) _code[i] = 0xCC;

        var f = Find();

        Assert.Null(f.Addr);
        Assert.Contains("TrapLookupRead matched 0 times", f.Why);
    }

    [Fact]
    public void A_site_found_twice_gives_nothing()
    {
        AllThree();
        Put(0x340, TrapCopySites.CopyStore);
        Disp(0x340, TrapCopySites.CopyStore.DispOff, Copy);

        var f = Find();

        Assert.Null(f.Addr);
        Assert.Contains("TrapCopyStore matched 2 times", f.Why);
    }

    [Fact]
    public void An_unreadable_image_gives_nothing()
    {
        AllThree();

        var f = TrapCopySites.Find(_mem, Sections);

        Assert.Null(f.Addr);
    }

    [Fact]
    public void Each_pattern_resolves_the_instruction_it_was_cut_around()
    {
        Assert.Equal(new int?[] { 0x0F, 0x29, 0x05 }, Slice(TrapCopySites.CopyStore, TrapCopySites.CopyStore.DispOff - 3, 3));
        Assert.Equal(new int?[] { 0x0F, 0x29, 0x05 }, Slice(TrapCopySites.RestoreStore, TrapCopySites.RestoreStore.DispOff - 3, 3));
        Assert.Equal(new int?[] { 0x41, 0x8A, 0x8C, 0x6D }, Slice(TrapCopySites.LookupRead, TrapCopySites.LookupRead.DispOff - 4, 4));
        Assert.Equal(new int?[] { 0x4C, 0x8D, 0x2D }, Slice(TrapCopySites.LookupRead, TrapCopySites.LookupBaseDispOff - 3, 3));
        Assert.Equal(TrapCopySites.CopyStore.DispOff + 4, TrapCopySites.CopyStore.Pattern.Length);
        Assert.Equal(TrapCopySites.RestoreStore.DispOff + 4, TrapCopySites.RestoreStore.Pattern.Length);
    }

    private static int?[] Slice(AnchorSig sig, int from, int len) => sig.Pattern[from..(from + len)];
}
