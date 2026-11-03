using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FFTTreasureMaster;

/// <summary>
/// The live image base of fft_enhanced.exe, so every documented address can be rebased onto it.
/// Every pin in this mod is stored at the preferred base 0x140000000, the only base the game had
/// while Denuvo wrapped. The Denuvo free build turned ASLR on, so the image loads at a random
/// base each launch and At(pin) rebases a pin onto it. Base defaults to Preferred, so unit tests
/// and tools keep reading the documented numbers. Mod.StartEngine resolves the live base once,
/// before the Engine exists, and nothing else writes it. Depends on nothing else in the mod.
/// </summary>
internal static class ImageBase
{
    /// <summary>The base every documented pin was captured at (all 1.5.x builds up to the ASLR build).</summary>
    public const long Preferred = 0x140000000L;

    /// <summary>The live image base. Written only before the Engine thread starts, so no locking is needed.</summary>
    public static long Base { get; private set; } = Preferred;

    public static long Delta => Base - Preferred;

    /// <summary>Rebase a documented preferred-base absolute onto the live image.</summary>
    public static long At(long preferredAbsolute) => preferredAbsolute + Delta;

    /// <summary>Accepts a plausible module base, positive and 64KB aligned the way PE allocations
    /// are. Returns false and leaves Base untouched otherwise.</summary>
    public static bool Set(long liveBase)
    {
        if (liveBase <= 0 || (liveBase & 0xFFFF) != 0) return false;
        Base = liveBase;
        return true;
    }

    // GetModuleHandleW(NULL) returns the calling process's exe base by definition (kernel32).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>Resolves the live exe base. GetModuleHandleW(null) is the exe base by definition
    /// and has no transient failure mode. The fallback is the main module's BaseAddress. False on
    /// a zero result or any exception, and liveBase is 0 then.</summary>
    public static bool TryResolveLive(out long liveBase)
    {
        liveBase = 0;
        try
        {
            IntPtr handle = GetModuleHandleW(null);
            if (handle != IntPtr.Zero)
            {
                liveBase = handle.ToInt64();
                return true;
            }
            using var process = Process.GetCurrentProcess();
            IntPtr? addr = process.MainModule?.BaseAddress;
            if (addr is null || addr.Value == IntPtr.Zero) return false;
            liveBase = addr.Value.ToInt64();
            return true;
        }
        catch (Exception)
        {
            liveBase = 0;
            return false;
        }
    }

    /// <summary>Test seam that restores Preferred.</summary>
    internal static void ResetForTests() => Base = Preferred;
}
