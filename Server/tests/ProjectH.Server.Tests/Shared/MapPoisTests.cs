using System;
using System.Collections.Generic;
using System.Text;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.Shared;

// Phase 6 D7: named places, shown by the client's PoiLabel.
public class MapPoisTests
{
    [Fact]
    public void TheFirstIsTheCrossroadsPlaza()
    {
        MapPoi first = MapPois.All[0];
        Assert.Equal("Crossroads", first.Name);
        Assert.Equal(0f, first.X);
        Assert.Equal(0f, first.Z);
        Assert.Equal(GameMap.PlazaRadius, first.Radius);
    }

    [Fact]
    public void Names_AreShortUniqueAscii()
    {
        var seen = new HashSet<string>();
        foreach (MapPoi poi in MapPois.All)
        {
            Assert.False(string.IsNullOrEmpty(poi.Name));
            Assert.InRange(Encoding.UTF8.GetByteCount(poi.Name), 1, 16);
            foreach (char c in poi.Name) Assert.InRange(c, ' ', '~');
            Assert.True(seen.Add(poi.Name), $"duplicate {poi.Name}");
        }
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void EveryPoi_LiesInsideTheWalls()
    {
        foreach (MapPoi poi in MapPois.All)
        {
            Assert.True(poi.Radius > 0f);
            Assert.True(MathF.Abs(poi.X) + poi.Radius <= GameMap.HalfSize, poi.Name);
            Assert.True(MathF.Abs(poi.Z) + poi.Radius <= GameMap.HalfSize, poi.Name);
        }
    }
}
