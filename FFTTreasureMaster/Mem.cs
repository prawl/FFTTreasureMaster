using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FFTTreasureMaster;

/// <summary>
/// Every read and write of game memory goes through RPM and WPM on our own handle, never a raw
/// deref. That is the single most important safety property of the runtime. RPM and WPM check
/// the range in the kernel and return false on an unmapped page. A raw deref access violates
/// instead, and .NET cannot catch an access violation, so it crashes the whole game. A page
/// freed between the guard and the access now degrades to a caught exception or a no-op.
///
/// VirtualQuery stays as a cheap pre filter so obvious misses skip the syscall, but RPM and WPM
/// are the safety net, not the query.
/// </summary>
internal static class Mem
{
    private static readonly nint Self = GetCurrentProcess();
    [ThreadStatic] private static byte[]? _scratch;

    /// <summary>Read n bytes; throws on a failed/partial read (callers that scan catch it).</summary>
    public static byte[] ReadBytes(long a, int n)
    {
        var buf = new byte[n];
        if (!ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) || (int)got != n)
            throw new InvalidOperationException("ReadProcessMemory failed");
        return buf;
    }

    public static bool TryReadBytes(long a, int n, out byte[] buf)
    {
        buf = new byte[n];
        return ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) && (int)got == n;
    }

    /// <summary>
    /// ReadProcessMemory into a caller-managed buffer. Returns n on a full read, else 0.
    /// Guards n <= buf.Length; an undersized buffer returns 0 (no allocation, no exception).
    /// </summary>
    public static int ReadInto(long a, byte[] buf, int n)
    {
        if (n > buf.Length) return 0;
        return ReadProcessMemory(Self, (nint)a, buf, (nuint)n, out var got) && (int)got == n ? n : 0;
    }

    /// <summary>Best-effort write; a failed write (freed page) is a safe no-op, never a fault.</summary>
    public static void WriteBytes(long a, byte[] data)
        => WriteProcessMemory(Self, (nint)a, data, (nuint)data.Length, out _);

    public static void W8(long a, byte v)
    {
        var s = _scratch ??= new byte[8];
        s[0] = v;
        WriteProcessMemory(Self, (nint)a, s, 1, out _);
    }

    /// <summary>Best-effort little-endian 4-byte write; a failed write (freed page) is a safe
    /// no-op, never a fault. Used by the EnhancedMarker write path (int-typed marker fields).</summary>
    public static void W32(long a, uint v)
    {
        var s = _scratch ??= new byte[8];
        s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16); s[3] = (byte)(v >> 24);
        WriteProcessMemory(Self, (nint)a, s, 4, out _);
    }

    // scalar reads reuse a per-thread buffer (the engine loop is single-threaded);
    // a failed/freed read returns 0 rather than faulting.
    private static bool ReadScalar(long a, int n)
    {
        var s = _scratch ??= new byte[8];
        return ReadProcessMemory(Self, (nint)a, s, (nuint)n, out var got) && (int)got == n;
    }

    public static byte U8(long a) => ReadScalar(a, 1) ? _scratch![0] : (byte)0;
    public static ushort U16(long a) => ReadScalar(a, 2) ? (ushort)(_scratch![0] | (_scratch[1] << 8)) : (ushort)0;
    public static uint U32(long a) => ReadScalar(a, 4)
        ? (uint)(_scratch![0] | (_scratch[1] << 8) | (_scratch[2] << 16) | (_scratch[3] << 24)) : 0u;
    public static ulong U64(long a)
    {
        if (!ReadScalar(a, 8)) return 0UL;
        var s = _scratch!;
        return (ulong)s[0] | ((ulong)s[1] << 8) | ((ulong)s[2] << 16) | ((ulong)s[3] << 24)
             | ((ulong)s[4] << 32) | ((ulong)s[5] << 40) | ((ulong)s[6] << 48) | ((ulong)s[7] << 56);
    }

    // little-endian parsers over a byte[] buffer (used by the region scan)
    public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    public static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    // ---- cheap pre-filter (skip a syscall on obviously-unmapped addresses) ----
    public static bool Readable(long addr, int len) => Probe(addr, len, false);
    public static bool Writable(long addr, int len) => Probe(addr, len, true);

    private static bool Probe(long addr, int len, bool needWrite)
    {
        if (VirtualQueryEx(Self, (nint)addr, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;
        if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return false;
        const uint writable = 0x04 | 0x08 | 0x40 | 0x80;   // RW | WriteCopy | ExecRW | ExecWriteCopy
        if ((mbi.Protect & (needWrite ? writable : READABLE)) == 0) return false;
        long b = (long)mbi.BaseAddress, e = b + (long)mbi.RegionSize;
        return addr >= b && addr + len <= e;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;   // RW | WriteCopy | ExecRW | ExecWriteCopy
    private const uint READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    /// <summary>Yields the base and size of every committed, private, writable, non guard region,
    /// which is the process heap. Module and file backed memory is skipped, since the card text
    /// is never there, which shrinks the paint scan from gigabytes to the heap.</summary>
    public static IEnumerable<(long baseAddr, long size)> Regions()
    {
        long addr = 0;
        MEMORY_BASIC_INFORMATION mbi;
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (addr < 0x7FFF_FFFF_0000)
        {
            if (VirtualQueryEx(Self, (nint)addr, out mbi, (uint)mbiSize) == 0)
                break;
            long b = (long)mbi.BaseAddress;
            long size = (long)mbi.RegionSize;
            long next = b + size;
            if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && (mbi.Protect & WRITABLE) != 0
                && (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0)
                yield return (b, size);
            addr = next > addr ? next : addr + 0x1000;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint h, nint addr, [Out] byte[] buf, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint h, nint addr, byte[] buf, nuint size, out nuint written);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
