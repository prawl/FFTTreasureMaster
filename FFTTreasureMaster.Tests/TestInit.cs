using System.Runtime.CompilerServices;
using FFTTreasureMaster;
using Xunit;

// ImageBase is process-wide static state that tests rebase (TM-8); run the assembly serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FFTTreasureMaster.Tests;

/// <summary>
/// Assembly-wide test defaults. Production code logs through the static ModLogger facade, whose
/// lazy default is the real FileConsoleLogger (console noise plus a treasuremaster.log in the
/// test bin dir); tests swap in the NullLogger once at load. This also keeps a production
/// LogError from arming the flight recorder's error flush while FlightRecorderTests has a live
/// Flight core initialized (the NullLogger never touches Flight). Tests that need a specific
/// logger set ModLogger.Instance themselves and restore the NullLogger when done.
/// </summary>
internal static class TestInit
{
    [ModuleInitializer]
    internal static void UseQuietLogger() => ModLogger.UseNullLogger();
}
