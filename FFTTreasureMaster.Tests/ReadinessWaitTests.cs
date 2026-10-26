using System.Collections.Generic;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>ReadinessWait.Until, the shared poll loop behind both loader-backed grants.</summary>
public class ReadinessWaitTests
{
    [Fact]
    public void ReturnsTrue_AfterNProbes_RecordsNSleeps()
    {
        int probes = 0, falses = 3;
        var sleeps = new List<int>();

        bool ok = ReadinessWait.Until(() => { probes++; return probes > falses; },
                                      ms => sleeps.Add(ms), 10, 100);

        Assert.True(ok);
        Assert.Equal(new[] { 10, 10, 10 }, sleeps);
        Assert.Equal(4, probes);
    }

    [Fact]
    public void ReturnsFalse_AtCap()
    {
        int probes = 0;
        var sleeps = new List<int>();

        bool ok = ReadinessWait.Until(() => { probes++; return false; },
                                      ms => sleeps.Add(ms), 10, 30);

        Assert.False(ok);
        Assert.Equal(3, sleeps.Count);
        Assert.Equal(4, probes);
    }
}
