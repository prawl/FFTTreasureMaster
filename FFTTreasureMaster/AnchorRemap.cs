using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Remaps a dataset's tile addresses by each region's delta after a signature re-resolve.
/// A pair is dropped when its region is unknown, or when the new address lands outside the
/// module span or inside the UI render arena. A tile with no pairs left is dropped. A map is
/// never dropped, because the runtime nag still needs its name and tile count. BuildKey and
/// Anchors pass through unchanged.
/// </summary>
internal static class AnchorRemap
{
    public static TreasureDb Remap(TreasureDb db, IReadOnlyDictionary<string, long> regionDeltas)
    {
        var maps = new List<TreasureMap>(db.Maps.Count);
        foreach (var map in db.Maps)
        {
            var tiles = new List<TreasureTile>(map.Tiles.Count);
            foreach (var tile in map.Tiles)
            {
                var addrs = new List<TreasureAddrEntry>(tile.Addrs.Count);
                foreach (var entry in tile.Addrs)
                {
                    if (entry.Region is null || !regionDeltas.TryGetValue(entry.Region, out long delta))
                        continue;

                    long remapped = entry.Addr + delta;
                    if (remapped < TreasureDb.ModuleBase || remapped >= TreasureDb.ModuleEnd)
                        continue;
                    if (remapped >= TreasureDb.UiArenaLo && remapped < TreasureDb.UiArenaHi)
                        continue;

                    addrs.Add(new TreasureAddrEntry
                    {
                        Addr = remapped,
                        Off = entry.Off,
                        Region = entry.Region,
                    });
                }

                if (addrs.Count == 0) continue;

                tiles.Add(new TreasureTile
                {
                    X = tile.X,
                    Y = tile.Y,
                    Addrs = addrs,
                    RareItemId = tile.RareItemId,
                    CommonItemId = tile.CommonItemId,
                    Slot = tile.Slot,
                });
            }

            maps.Add(new TreasureMap
            {
                MapId = map.MapId,
                Name = map.Name,
                TileCount = map.TileCount,
                FpVer = map.FpVer,
                FpLen = map.FpLen,
                FpHashHex = map.FpHashHex,
                Tiles = tiles,
            });
        }

        return new TreasureDb(db.BuildKey, maps, db.Anchors);
    }
}
