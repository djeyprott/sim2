using Sim.Core.Map;
using Xunit;

namespace Sim.Core.Tests;

public sealed class EarthWorldFixture
{
    public WorldMap World { get; } = WorldMap.Load(Path.Combine(AppContext.BaseDirectory, "earth.world"));
}

public class EarthWorldTests : IClassFixture<EarthWorldFixture>
{
    private readonly WorldMap _world;

    public EarthWorldTests(EarthWorldFixture fixture)
    {
        _world = fixture.World;
    }

    private int CellAt(double lat, double lon) => _world.Grid.FindCell(lat, lon);

    [Fact]
    public void Is_the_full_size_Earth()
    {
        Assert.Equal("Earth", _world.Name);
        Assert.Equal(225002, _world.CellCount);
        Assert.Equal(64, _world.Checksum.Length);
    }

    [Theory]
    [InlineData("Central Sahara", 23.0, 12.0, true)]
    [InlineData("Amazon basin", -3.0, -62.0, true)]
    [InlineData("Tibet", 33.0, 88.0, true)]
    [InlineData("Antarctica", -80.0, 0.0, true)]
    [InlineData("Sicily", 37.6, 14.2, true)]
    [InlineData("Isthmus of Panama", 9.1, -79.7, true)]
    [InlineData("Mid-Pacific", 0.0, -140.0, false)]
    [InlineData("Mediterranean", 35.0, 18.0, false)]
    [InlineData("Black Sea", 43.0, 34.0, false)]
    [InlineData("Baltic", 58.0, 20.0, false)]
    [InlineData("Red Sea", 20.0, 38.5, false)]
    [InlineData("Persian Gulf", 27.0, 51.0, false)]
    [InlineData("Bosporus", 41.1, 29.05, false)]
    [InlineData("Strait of Dover", 51.0, 1.5, false)]
    [InlineData("Strait of Gibraltar", 35.95, -5.6, false)]
    public void Known_places_are_land_or_sea(string place, double lat, double lon, bool expectLand)
    {
        Assert.True(expectLand == _world.IsLand(CellAt(lat, lon)), $"{place} should be {(expectLand ? "land" : "sea")}.");
    }

    [Fact]
    public void Known_places_have_the_right_terrain()
    {
        Assert.Equal(TerrainClass.Mountains, _world.Terrain(CellAt(33.0, 88.0)));
        Assert.Equal(TerrainClass.Mountains, _world.Terrain(CellAt(46.5, 10.0)));
        Assert.Equal(TerrainClass.Lowland, _world.Terrain(CellAt(-3.0, -62.0)));
        Assert.Equal(TerrainClass.Ocean, _world.Terrain(CellAt(0.0, -140.0)));
        Assert.True(_world.ElevationMetres(CellAt(0.0, -140.0)) < -1000);
        Assert.True(_world.ElevationMetres(CellAt(33.0, 88.0)) > 4000);
    }

    [Fact]
    public void Land_covers_about_29_percent_of_the_surface()
    {
        int landCells = 0;
        double landArea = 0.0;
        double totalArea = 0.0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            double area = _world.Grid.AreaKm2(cell);
            totalArea += area;
            if (_world.IsLand(cell))
            {
                landCells++;
                landArea += area;
            }
        }

        Assert.InRange(landCells, 60000, 70000);
        Assert.InRange(landArea / totalArea, 0.28, 0.305);
    }

    [Fact]
    public void Terrain_classes_are_consistent()
    {
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (_world.IsLand(cell))
            {
                continue;
            }

            Assert.True(_world.ElevationMetres(cell) < 0);
            bool touchesLand = false;
            foreach (int other in _world.Grid.Neighbours(cell))
            {
                touchesLand |= _world.IsLand(other);
            }

            Assert.Equal(touchesLand ? TerrainClass.Coast : TerrainClass.Ocean, _world.Terrain(cell));
        }
    }

    [Fact]
    public void All_sea_is_one_connected_ocean()
    {
        int seaCells = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (!_world.IsLand(cell))
            {
                seaCells++;
            }
        }

        bool[] reached = Flood(CellAt(0.0, -30.0), land: false);
        Assert.Equal(seaCells, reached.Count(r => r));
    }

    [Fact]
    public void Continents_and_islands_connect_as_on_Earth()
    {
        bool[] fromSahara = Flood(CellAt(23.0, 12.0), land: true);
        Assert.True(fromSahara[CellAt(33.0, 88.0)], "Africa should join Asia.");
        Assert.True(fromSahara[CellAt(48.8, 2.3)], "Africa should join Europe by land through Asia.");
        Assert.False(fromSahara[CellAt(-3.0, -62.0)], "Africa should not join South America.");
        Assert.False(fromSahara[CellAt(51.5, -0.1)], "Britain should be an island.");
        Assert.False(fromSahara[CellAt(37.6, 14.2)], "Sicily should be an island.");
        Assert.False(fromSahara[CellAt(-25.0, 135.0)], "Australia should be separate.");
        Assert.False(fromSahara[CellAt(36.2, 138.2)], "Honshu should be an island.");

        bool[] fromKansas = Flood(CellAt(39.0, -98.0), land: true);
        Assert.True(fromKansas[CellAt(-3.0, -62.0)], "North and South America should join at Panama.");
        Assert.False(fromKansas[CellAt(62.0, 130.0)], "America should not join Asia.");
    }

    // Every cell of the same kind (land or sea) reachable from the start through neighbours.
    private bool[] Flood(int start, bool land)
    {
        Assert.Equal(land, _world.IsLand(start));
        var reached = new bool[_world.CellCount];
        var queue = new Queue<int>();
        reached[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            foreach (int other in _world.Grid.Neighbours(cell))
            {
                if (!reached[other] && _world.IsLand(other) == land)
                {
                    reached[other] = true;
                    queue.Enqueue(other);
                }
            }
        }

        return reached;
    }
}
