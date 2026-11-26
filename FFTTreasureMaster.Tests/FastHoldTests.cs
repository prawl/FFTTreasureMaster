using System.Collections.Generic;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// FastHold.Holding means a published map still has tiles to hold, not that its tile count is set.
/// </summary>
public class FastHoldTests
{
    private static TreasureMap Map(int tiles)
    {
        var list = new List<TreasureTile>();
        for (int i = 0; i < tiles; i++) list.Add(new TreasureTile { X = i, Y = 0 });
        return new TreasureMap { MapId = 74, TileCount = 3, Tiles = list };
    }

    [Fact]
    public void Holding_follows_the_published_tiles()
    {
        var fh = new FastHold(new TileHolder(new FakeSparseMemory()), intervalMs: 8);
        Assert.False(fh.Holding);
        fh.Publish(Map(0));
        Assert.False(fh.Holding);
        fh.Publish(Map(1));
        Assert.True(fh.Holding);
        fh.Publish(null);
        Assert.False(fh.Holding);
    }
}
