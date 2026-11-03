using System.Collections.Generic;
using System.IO;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Scratch folders for tests. Every folder made through Dir is deleted by the next Sweep, which
/// each test class calls from Dispose, so nothing piles up in the temp folder.
/// </summary>
internal static class Scratch
{
    private static readonly List<string> Made = new();

    public static string Dir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        lock (Made) Made.Add(dir);
        return dir;
    }

    public static void Sweep()
    {
        List<string> dirs;
        lock (Made)
        {
            dirs = new List<string>(Made);
            Made.Clear();
        }
        foreach (string dir in dirs)
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
