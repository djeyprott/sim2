using Sim.Core.Geo;
using Sim.Core.Map;
using Xunit;

namespace Sim.Core.Tests;

public class WorldMapTests
{
    private static WorldMap MakeSmallWorld(short firstElevation)
    {
        GeodesicGrid grid = GeodesicGrid.Create(4);
        var data = new WorldMapData("Test", grid);
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            data.ElevationMetres[cell] = (short)(cell * 7 - 500);
            data.Terrain[cell] = (TerrainClass)(cell % 6);
            data.River[cell] = (RiverSize)(cell % 3);
            if (cell % 6 >= 2)
            {
                for (int month = 0; month < 12; month++)
                {
                    data.TemperatureTenths[cell * 12 + month] = (short)(cell + month * 10 - 200);
                    data.RainfallMm[cell * 12 + month] = (short)(cell % 50 + month);
                }
            }
        }

        data.ElevationMetres[0] = firstElevation;
        return new WorldMap(data);
    }

    [Fact]
    public void Save_then_load_gives_the_same_world()
    {
        WorldMap world = MakeSmallWorld(123);
        using var stream = new MemoryStream();
        world.Save(stream);
        stream.Position = 0;
        WorldMap loaded = WorldMap.Load(stream);

        Assert.Equal(world.Name, loaded.Name);
        Assert.Equal(world.CellCount, loaded.CellCount);
        Assert.Equal(world.Checksum, loaded.Checksum);
        for (int cell = 0; cell < world.CellCount; cell++)
        {
            Assert.Equal(world.ElevationMetres(cell), loaded.ElevationMetres(cell));
            Assert.Equal(world.Terrain(cell), loaded.Terrain(cell));
            Assert.Equal(world.River(cell), loaded.River(cell));
            Assert.Equal(world.HasClimate(cell), loaded.HasClimate(cell));
            for (int month = 0; month < 12; month++)
            {
                Assert.Equal(world.TemperatureC(cell, month), loaded.TemperatureC(cell, month));
                Assert.Equal(world.RainfallMm(cell, month), loaded.RainfallMm(cell, month));
            }
        }
    }

    [Fact]
    public void Checksum_changes_when_content_changes()
    {
        Assert.Equal(MakeSmallWorld(123).Checksum, MakeSmallWorld(123).Checksum);
        Assert.NotEqual(MakeSmallWorld(123).Checksum, MakeSmallWorld(124).Checksum);
    }

    [Fact]
    public void Land_sea_and_lake_are_told_apart()
    {
        WorldMap world = MakeSmallWorld(0);
        for (int cell = 0; cell < world.CellCount; cell++)
        {
            TerrainClass terrain = world.Terrain(cell);
            Assert.Equal(terrain == TerrainClass.Ocean || terrain == TerrainClass.Coast, world.IsSea(cell));
            Assert.Equal(terrain == TerrainClass.Lake, world.IsLake(cell));
            Assert.Equal(!world.IsSea(cell) && !world.IsLake(cell), world.IsLand(cell));
        }
    }

    [Fact]
    public void Climate_is_read_per_month_and_summarised_per_year()
    {
        WorldMap world = MakeSmallWorld(0);
        int cell = 8; // 8 % 6 = 2, so it has climate.
        Assert.True(world.HasClimate(cell));
        Assert.Equal(-19.2, world.TemperatureC(cell, 0), 9);
        Assert.Equal(-8.2, world.TemperatureC(cell, 11), 9);
        Assert.Equal(-13.7, world.MeanTemperatureC(cell), 9);
        Assert.Equal(8.0, world.RainfallMm(cell, 0), 9);
        Assert.Equal(12 * 8 + 66, world.AnnualRainfallMm(cell), 9);
        Assert.False(world.HasClimate(0));
    }

    [Fact]
    public void Loading_something_else_fails_clearly()
    {
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<InvalidDataException>(() => WorldMap.Load(stream));

        // A valid file cut short.
        using var full = new MemoryStream();
        MakeSmallWorld(1).Save(full);
        byte[] bytes = full.ToArray();
        Assert.Throws<InvalidDataException>(() => WorldMap.Load(new MemoryStream(bytes, 0, bytes.Length / 2)));
    }
}
