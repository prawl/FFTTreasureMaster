using System.Collections.Generic;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>LoaderJobs job bodies, driven entirely through delegates.</summary>
public class LoaderJobsTests
{
    [Fact]
    public void ControllerNullWhileLoaderActive_WarnsOnce_GrantNotRun()
    {
        var log = new List<string>();
        var warn = new List<string>();

        LoaderJobs.RunRareTreasure(() => null, () => true, log.Add, warn.Add, _ => { });

        Assert.Single(warn);
        Assert.Contains("could not be acquired", warn[0]);
        Assert.Empty(log);
    }

    [Fact]
    public void ControllerNullLoaderInactive_SeamNullLineOnly()
    {
        var log = new List<string>();
        var warn = new List<string>();

        LoaderJobs.RunRareTreasure(() => null, () => false, log.Add, warn.Add, _ => { });

        Assert.Single(log);
        Assert.Contains("not installed or not enabled", log[0]);
        Assert.Empty(warn);
    }
}
