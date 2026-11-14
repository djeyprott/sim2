using Sim.Core.Geo;
using Sim.Core.Map;
using Xunit;

namespace Sim.Core.Tests;

public class WorldMapTests
{
    private static WorldMap MakeSmallWorld(short firstElevation)
    {
        GeodesicGrid grid = GeodesicGrid.Create(4);
        var elevation = new short[grid.CellCount];
        var terrain = new TerrainClass[grid.CellCount];
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            elevation[cell] = (short)(cell * 7 - 500);
            terrain[cell] = (TerrainClass)(cell % 5);
        }

        elevation[0] = firstElevation;
        return new WorldMap("Test", grid, elevation, terrain);
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
            Assert.Equal(world.IsLand(cell), loaded.IsLand(cell));
        }
    }

    [Fact]
    public void Checksum_changes_when_content_changes()
    {
        Assert.Equal(MakeSmallWorld(123).Checksum, MakeSmallWorld(123).Checksum);
        Assert.NotEqual(MakeSmallWorld(123).Checksum, MakeSmallWorld(124).Checksum);
    }

    [Fact]
    public void Land_is_lowland_hills_and_mountains()
    {
        WorldMap world = MakeSmallWorld(0);
        for (int cell = 0; cell < world.CellCount; cell++)
        {
            bool expected = world.Terrain(cell) != TerrainClass.Ocean && world.Terrain(cell) != TerrainClass.Coast;
            Assert.Equal(expected, world.IsLand(cell));
        }
    }

    [Fact]
    public void Loading_something_else_fails_clearly()
    {
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<InvalidDataException>(() => WorldMap.Load(stream));
    }
}
