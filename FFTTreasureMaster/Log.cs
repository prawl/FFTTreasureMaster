using System;
using System.IO;

namespace FFTTreasureMaster;

/// <summary>Minimal logger: Reloaded console + a file in the mod dir, rotated per launch
/// (current session in treasuremaster.log, the previous one in treasuremaster.prev.log -- the old
/// append-forever file grew without bound). Millisecond timestamps so the 33ms tick events are
/// orderable against on-screen actions.</summary>
internal static class Log
{
    private static string? _file;

    public static void Init(string modDir)
    {
        try
        {
            _file = Path.Combine(modDir, "treasuremaster.log");
            if (File.Exists(_file))
                File.Move(_file, Path.Combine(modDir, "treasuremaster.prev.log"), true);
        }
        catch { }
    }

    public static void Info(string m) => Write("[FFTTreasureMaster] " + m);
    public static void Error(string m) => Write("[FFTTreasureMaster] ERROR: " + m);

    private static void Write(string m)
    {
        try { Console.WriteLine(m); } catch { }
        try { if (_file != null) File.AppendAllText(_file, DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\n"); } catch { }
    }
}
