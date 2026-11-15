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
            if (!_world.IsSea(cell))
            {
                continue;
            }

            Assert.True(_world.ElevationMetres(cell) < 0);
            Assert.False(_world.HasClimate(cell));
            Assert.Equal(RiverSize.None, _world.River(cell));
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
            if (_world.IsSea(cell))
            {
                seaCells++;
            }
        }

        bool[] reached = Flood(CellAt(0.0, -30.0), _world.IsSea);
        Assert.Equal(seaCells, reached.Count(r => r));
    }

    [Fact]
    public void Continents_and_islands_connect_as_on_Earth()
    {
        bool[] fromSahara = Flood(CellAt(23.0, 12.0), _world.IsLand);
        Assert.True(fromSahara[CellAt(33.0, 88.0)], "Africa should join Asia.");
        Assert.True(fromSahara[CellAt(48.8, 2.3)], "Africa should join Europe by land through Asia.");
        Assert.False(fromSahara[CellAt(-3.0, -62.0)], "Africa should not join South America.");
        Assert.False(fromSahara[CellAt(51.5, -0.1)], "Britain should be an island.");
        Assert.False(fromSahara[CellAt(37.6, 14.2)], "Sicily should be an island.");
        Assert.False(fromSahara[CellAt(-25.0, 135.0)], "Australia should be separate.");
        Assert.False(fromSahara[CellAt(36.2, 138.2)], "Honshu should be an island.");

        bool[] fromKansas = Flood(CellAt(39.0, -98.0), _world.IsLand);
        Assert.True(fromKansas[CellAt(-3.0, -62.0)], "North and South America should join at Panama.");
        Assert.False(fromKansas[CellAt(62.0, 130.0)], "America should not join Asia.");
    }

    [Theory]
    [InlineData("Caspian Sea", 41.0, 51.0)]
    [InlineData("Lake Superior", 47.7, -87.5)]
    [InlineData("Lake Victoria", -1.0, 33.0)]
    [InlineData("Lake Baikal", 53.5, 108.0)]
    public void Great_lakes_and_inland_seas_are_lakes(string place, double lat, double lon)
    {
        int cell = CellAt(lat, lon);
        Assert.True(_world.IsLake(cell), $"{place} should be a lake but is {_world.Terrain(cell)}.");
        Assert.False(_world.IsLand(cell));
        Assert.False(_world.IsSea(cell));
    }

    [Fact]
    public void Lake_and_river_counts_are_sensible()
    {
        int lakes = 0;
        int minor = 0;
        int major = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (_world.IsLake(cell))
            {
                lakes++;
                Assert.Equal(RiverSize.None, _world.River(cell));
            }

            if (_world.River(cell) == RiverSize.Minor)
            {
                minor++;
            }
            else if (_world.River(cell) == RiverSize.Major)
            {
                major++;
            }
        }

        Assert.InRange(lakes, 450, 1000);
        Assert.InRange(minor, 3000, 8000);
        Assert.InRange(major, 3000, 6500);
    }

    [Theory]
    [InlineData("Nile at Luxor", 25.7, 32.65)]
    [InlineData("Nile delta", 30.6, 31.2)]
    [InlineData("Mississippi at Vicksburg", 32.3, -90.9)]
    [InlineData("Amazon at Manaus", -3.1, -60.0)]
    [InlineData("Yangtze at Wuhan", 30.6, 114.3)]
    [InlineData("Danube at Budapest", 47.5, 19.05)]
    public void Great_rivers_are_major(string place, double lat, double lon)
    {
        Assert.True(RiverSize.Major == _world.River(CellAt(lat, lon)), $"{place} should have a major river.");
    }

    [Fact]
    public void Every_land_and_lake_cell_has_believable_climate()
    {
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (_world.IsSea(cell))
            {
                continue;
            }

            Assert.True(_world.HasClimate(cell), $"Cell {cell} has no climate.");
            for (int month = 0; month < 12; month++)
            {
                Assert.InRange(_world.TemperatureC(cell, month), -75.0, 45.0);
                Assert.InRange(_world.RainfallMm(cell, month), 0.0, 3000.0);
            }
        }
    }

    [Fact]
    public void Known_places_have_the_right_climate()
    {
        int sahara = CellAt(23.0, 12.0);
        Assert.True(_world.AnnualRainfallMm(sahara) < 100.0);
        Assert.True(_world.MeanTemperatureC(sahara) > 20.0);

        int amazon = CellAt(-3.0, -62.0);
        Assert.True(_world.AnnualRainfallMm(amazon) > 2000.0);
        Assert.True(_world.MeanTemperatureC(amazon) > 24.0);

        int greenland = CellAt(72.0, -40.0);
        for (int month = 0; month < 12; month++)
        {
            Assert.True(_world.TemperatureC(greenland, month) < 0.0);
        }

        int yakutsk = CellAt(62.0, 129.7);
        Assert.True(_world.TemperatureC(yakutsk, 0) < -30.0);
        Assert.True(_world.TemperatureC(yakutsk, 6) > 14.0);

        int london = CellAt(51.5, -0.1);
        Assert.InRange(_world.MeanTemperatureC(london), 8.0, 13.0);
        Assert.InRange(_world.AnnualRainfallMm(london), 450.0, 900.0);
    }

    [Fact]
    public void Seasons_are_opposite_in_the_two_hemispheres()
    {
        int north = CellAt(51.5, -0.1);
        int south = CellAt(-33.5, 150.0);
        Assert.True(_world.IsLand(south));
        Assert.True(_world.TemperatureC(north, 6) > _world.TemperatureC(north, 0));
        Assert.True(_world.TemperatureC(south, 0) > _world.TemperatureC(south, 6));
    }

    // Every cell of the given kind reachable from the start through neighbours.
    private bool[] Flood(int start, Func<int, bool> isKind)
    {
        Assert.True(isKind(start));
        var reached = new bool[_world.CellCount];
        var queue = new Queue<int>();
        reached[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            foreach (int other in _world.Grid.Neighbours(cell))
            {
                if (!reached[other] && isKind(other))
                {
                    reached[other] = true;
                    queue.Enqueue(other);
                }
            }
        }

        return reached;
    }
}
