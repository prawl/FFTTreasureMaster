using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FFTTreasureMaster;
using ModGates.Tests;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// RareCopyHooks over a fake hook site, so each detour can be fired with a stand in for the game's
/// routine. The stand in writes the unpatched Beddha Sandwaste rows into a pinned copy, the way the
/// game fills it, so a patch that ran before the game's routine would be overwritten and fail.
/// </summary>
public sealed class RareCopyHooksTests : IDisposable
{
    private const long CopyFn = 0x140251584L, RestoreFn = 0x1402CE7A8L;

    private sealed class FakeHook : IGameHook
    {
        public GameFn Original { get; set; } = (a, b, c, d) => 0;
        public bool Active, ThrowOnActivate;
        public void Activate() { if (ThrowOnActivate) throw new InvalidOperationException("no protect"); Active = true; }
        public void Disable() => Active = false;
    }

    private sealed class FakeSite : IHookSite
    {
        public readonly List<(long Addr, GameFn Detour, FakeHook Hook)> Made = new();
        public int ThrowOnCreate = -1, ThrowOnActivate = -1;
        public IGameHook Create(long addr, GameFn detour)
        {
            if (Made.Count == ThrowOnCreate) throw new InvalidOperationException("no room");
            var h = new FakeHook { ThrowOnActivate = Made.Count == ThrowOnActivate };
            Made.Add((addr, detour, h));
            return h;
        }
    }

    private sealed class WrapMem : IGameMemory
    {
        private readonly LiveMemory _in = new();
        public bool FailRead, ThrowRead, RefuseWrite;
        public byte U8(long a) => _in.U8(a);
        public ushort U16(long a) => _in.U16(a);
        public bool TryReadBytes(long a, int n, out byte[] b)
        {
            if (ThrowRead) throw new InvalidOperationException("read blew up");
            return _in.TryReadBytes(a, n, out b) && !FailRead;
        }
        public void W8(long a, byte v) => _in.W8(a, v);
        public bool Readable(long a, int n) => _in.Readable(a, n);
        public bool Writable(long a, int n) => !RefuseWrite && _in.Writable(a, n);
    }

    private readonly PinnedBuf _copy = PinnedBuf.Of(RareCopyPatch.CopyBytes);
    private readonly WrapMem _mem = new();
    private readonly FakeSite _site = new();
    private readonly List<string> _events = new(), _debugs = new(), _warns = new();
    private int _finds;
    private TrapCopyFind? _find;
    private bool _throwOnEvent;

    public void Dispose() => _copy.Dispose();

    private RareCopyHooks Hooks()
    {
        var log = new CopyLog(
            m => { if (_throwOnEvent) throw new InvalidOperationException("log blew up"); _events.Add(m); },
            _debugs.Add, _warns.Add);
        return new RareCopyHooks(_mem, _ => { _finds++; return _find ?? new(_copy.Addr, "agree", CopyFn, RestoreFn); }, log);
    }

    private RareCopyHooks Armed()
    {
        var h = Hooks();
        h.Arm(_site);
        return h;
    }

    private void Row(int i, ushort rare, ushort common)
    {
        int at = i * RareCopyPatch.RowBytes;
        _copy.Bytes[at] = (byte)(0x10 + i);
        _copy.Bytes[at + 2] = (byte)rare;
        _copy.Bytes[at + 3] = (byte)(rare >> 8);
        _copy.Bytes[at + 4] = (byte)common;
        _copy.Bytes[at + 5] = (byte)(common >> 8);
    }

    private ushort Common(int i) => BitConverter.ToUInt16(_copy.Bytes, i * RareCopyPatch.RowBytes + 4);

    private GameFn GameFillsBeddha(nint result = 0) => (a, b, c, d) =>
    {
        Row(0, 124, 241);
        Row(1, 0, 0);
        return result;
    };

    private nint Fire(int which, nint rcx = 82, GameFn? game = null)
    {
        var (_, detour, hook) = _site.Made[which];
        hook.Original = game ?? GameFillsBeddha();
        return detour(rcx, 0, 0, 0);
    }

    [Fact]
    public void No_hooks_library_installs_nothing_and_says_so_once()
    {
        var h = Hooks();

        h.Arm(null);

        Assert.Equal(0, _finds);
        Assert.False(h.Active);
        Assert.Contains("reloaded.sharedlib.hooks", Assert.Single(_warns));
    }

    [Fact]
    public void No_copy_address_installs_no_hooks_and_gives_the_reason_once()
    {
        _find = new TrapCopyFind(null, "TrapLookupRead matched 0 times, not once");

        var h = Armed();

        Assert.Empty(_site.Made);
        Assert.False(h.Active);
        Assert.Contains("TrapLookupRead matched 0 times", Assert.Single(_warns));
        Assert.Empty(_events);
    }

    [Fact]
    public void A_found_copy_hooks_both_routines_and_says_where()
    {
        var h = Armed();

        Assert.Equal(new[] { CopyFn, RestoreFn }, _site.Made.ConvertAll(m => m.Addr));
        Assert.All(_site.Made, m => Assert.True(m.Hook.Active));
        Assert.True(h.Active);
        string line = Assert.Single(_events);
        Assert.Contains($"0x{CopyFn:X}", line);
        Assert.Contains($"0x{RestoreFn:X}", line);
    }

    [Fact]
    public void A_second_arm_after_a_missing_library_stays_off()
    {
        var h = Hooks();

        h.Arm(null);
        h.Arm(_site);

        Assert.Equal(0, _finds);
        Assert.Empty(_site.Made);
        Assert.False(h.Active);
        Assert.Single(_warns);
    }

    [Fact]
    public void Arming_twice_scans_once_and_hooks_once()
    {
        var h = Hooks();

        h.Arm(_site);
        h.Arm(_site);

        Assert.Equal(1, _finds);
        Assert.Equal(2, _site.Made.Count);
    }

    [Fact]
    public void A_failed_first_create_builds_and_activates_nothing()
    {
        _site.ThrowOnCreate = 0;

        var h = Armed();

        Assert.False(h.Active);
        Assert.Empty(_site.Made);
        Assert.Contains("no room", Assert.Single(_warns));
        Assert.Empty(_events);
    }

    [Fact]
    public void A_failed_second_create_leaves_no_hook_active()
    {
        _site.ThrowOnCreate = 1;

        var h = Armed();

        Assert.False(h.Active);
        Assert.False(_site.Made[0].Hook.Active);
        Assert.Contains("no room", Assert.Single(_warns));
    }

    [Fact]
    public void A_failed_second_activate_turns_the_first_back_off()
    {
        _site.ThrowOnActivate = 1;

        var h = Armed();

        Assert.False(h.Active);
        Assert.False(_site.Made[0].Hook.Active);
        Assert.Contains("no protect", Assert.Single(_warns));
    }

    [Fact]
    public void The_map_load_detour_runs_the_game_first_then_gives_the_rare_item()
    {
        Armed();
        _events.Clear();

        Fire(0);

        Assert.Equal(124, Common(0));
        Assert.Equal(0, Common(1));
        string line = Assert.Single(_events);
        Assert.Contains("map load of map 82", line);
        Assert.Contains("124/241", line);
    }

    [Fact]
    public void The_restore_detour_patches_the_copy_too()
    {
        Armed();
        _events.Clear();

        Fire(1);

        Assert.Equal(124, Common(0));
        Assert.Contains("restore of saved battle data", Assert.Single(_events));
    }

    [Fact]
    public void The_map_id_is_the_low_word_of_rcx()
    {
        Armed();
        _events.Clear();

        Fire(0, rcx: unchecked((nint)0x7FFF_ABCD_0000_0052L));

        Assert.Contains("map 82", Assert.Single(_events));
    }

    [Fact]
    public void The_detour_hands_the_game_its_arguments_and_returns_its_result()
    {
        Armed();
        var seen = new List<nint>();

        _site.Made[0].Hook.Original = (a, b, c, d) => { seen.AddRange(new[] { a, b, c, d }); return 0x5A5A; };
        nint r = _site.Made[0].Detour(11, 22, 33, 44);

        Assert.Equal(new nint[] { 11, 22, 33, 44 }, seen);
        Assert.Equal((nint)0x5A5A, r);
    }

    [Fact]
    public void A_copy_already_rare_logs_only_a_debug_line()
    {
        Armed();
        _events.Clear();

        Fire(0, game: (a, b, c, d) => { Row(0, 124, 124); return 0; });

        Assert.Empty(_events);
        Assert.Single(_debugs);
        Assert.Empty(_warns);
    }

    [Fact]
    public void A_throwing_game_routine_reaches_its_caller_and_nothing_is_patched()
    {
        Armed();
        Row(0, 124, 241);

        Assert.Throws<InvalidOperationException>(() => Fire(0, game: (a, b, c, d) => throw new InvalidOperationException("game")));

        Assert.Equal(241, Common(0));
    }

    [Fact]
    public void A_throwing_memory_read_still_returns_the_game_result()
    {
        var h = Armed();
        _mem.ThrowRead = true;

        nint r = Fire(0, game: GameFillsBeddha(0x77));

        Assert.Equal((nint)0x77, r);
        Assert.Equal(1, h.Faults);
        Assert.Contains("read blew up", Assert.Single(_warns));
    }

    [Fact]
    public void A_throwing_logger_still_returns_the_game_result()
    {
        var h = Armed();
        _throwOnEvent = true;

        nint r = Fire(0, game: GameFillsBeddha(0x78));

        Assert.Equal((nint)0x78, r);
        Assert.Equal(124, Common(0));
        Assert.Equal(1, h.Faults);
    }

    [Fact]
    public void A_refused_write_warns_with_the_count_and_the_game_carries_on()
    {
        Armed();
        _events.Clear();
        _mem.RefuseWrite = true;

        nint r = Fire(0, game: GameFillsBeddha(0x7A));

        Assert.Equal((nint)0x7A, r);
        Assert.Equal(241, Common(0));
        string line = Assert.Single(_warns);
        Assert.Contains("0 slot(s) now give the rare item but 1 could not be changed", line);
        Assert.Contains("124/241", line);
        Assert.Empty(_events);
    }

    [Fact]
    public void Each_fill_is_patched_again_and_every_row_is_listed_in_order()
    {
        Armed();
        _events.Clear();

        Fire(0);
        Fire(1, game: (a, b, c, d) =>
        {
            Row(0, 124, 241);
            Row(2, 199, 12);
            Row(3, 7, 7);
            return 0;
        });

        Assert.Equal(2, _events.Count);
        Assert.Contains("124/241 0/0 0/0 0/0", _events[0]);
        Assert.Contains("124/241 0/0 199/12 7/7", _events[1]);
        Assert.Equal(199, Common(2));
    }

    [Fact]
    public void An_unreadable_copy_warns_and_the_game_carries_on()
    {
        Armed();
        _mem.FailRead = true;

        nint r = Fire(1, game: GameFillsBeddha(0x79));

        Assert.Equal((nint)0x79, r);
        Assert.Contains("could not be read", Assert.Single(_warns));
        Assert.Equal(241, Common(0));
    }

    [Fact]
    public void The_manifest_loads_the_hooks_library_first_without_requiring_it()
    {
        string path = Path.Combine(RepoRoot.Locate(), "mod", "ModConfig.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var optional = doc.RootElement.GetProperty("OptionalDependencies").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("reloaded.sharedlib.hooks", optional);
        Assert.Contains("fftivc.utility.modloader", optional);
        Assert.Equal(0, doc.RootElement.GetProperty("ModDependencies").GetArrayLength());
    }

    [Fact]
    public void The_package_refuses_to_ship_without_the_hooks_interface_dll()
    {
        string pipeline = File.ReadAllText(Path.Combine(RepoRoot.Locate(), "tools", "pipeline.ps1"));

        Assert.Contains("\"Reloaded.Hooks.Definitions.dll\"", pipeline);
    }
}
