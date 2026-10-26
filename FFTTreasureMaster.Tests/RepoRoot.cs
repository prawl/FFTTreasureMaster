using System;
using System.IO;

namespace FFTTreasureMaster.Tests;

/// <summary>Finds the repository root from the test binary folder, shared by every test that
/// reads committed repo files.</summary>
internal static class RepoRoot
{
    public static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "FFTTreasureMaster")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + FFTTreasureMaster/) not found above the test bin dir");
    }
}
