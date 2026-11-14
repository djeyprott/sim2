using Sim.Core.Geo;
using Xunit;

namespace Sim.Core.Tests;

public sealed class EarthGridFixture
{
    public GeodesicGrid Grid { get; } = GeodesicGrid.Create(GeodesicGrid.EarthFrequency);
}

public class GeodesicGridTests
{
    [Theory]
    [InlineData(1, 12)]
    [InlineData(2, 42)]
    [InlineData(3, 92)]
    [InlineData(10, 1002)]
    [InlineData(20, 4002)]
    public void Small_grids_have_the_right_structure(int frequency, int expectedCells)
    {
        GeodesicGrid grid = GeodesicGrid.Create(frequency);
        Assert.Equal(expectedCells, grid.CellCount);
        GridChecks.AssertStructure(grid);
    }

    [Fact]
    public void Same_frequency_gives_identical_grids()
    {
        GeodesicGrid a = GeodesicGrid.Create(20);
        GeodesicGrid b = GeodesicGrid.Create(20);
        for (int cell = 0; cell < a.CellCount; cell++)
        {
            Assert.Equal(a.Centre(cell).X, b.Centre(cell).X);
            Assert.Equal(a.Centre(cell).Y, b.Centre(cell).Y);
            Assert.Equal(a.Centre(cell).Z, b.Centre(cell).Z);
            Assert.Equal(a.AreaKm2(cell), b.AreaKm2(cell));
            Assert.True(a.Neighbours(cell).SequenceEqual(b.Neighbours(cell)));
        }
    }

    [Fact]
    public void Find_cell_matches_brute_force_on_a_small_grid()
    {
        GeodesicGrid grid = GeodesicGrid.Create(20);
        GridChecks.AssertFindCellMatchesBruteForce(grid, 2000);
    }

    [Fact]
    public void Invalid_frequency_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GeodesicGrid.Create(0));
    }
}

public class EarthGridTests : IClassFixture<EarthGridFixture>
{
    private readonly GeodesicGrid _grid;

    public EarthGridTests(EarthGridFixture fixture)
    {
        _grid = fixture.Grid;
    }

    [Fact]
    public void Has_225002_cells_with_valid_structure()
    {
        Assert.Equal(225002, _grid.CellCount);
        GridChecks.AssertStructure(_grid);
    }

    [Fact]
    public void Cell_areas_sum_to_the_surface_of_Earth()
    {
        double total = 0.0;
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            total += _grid.AreaKm2(cell);
        }

        double expected = 4.0 * Math.PI * GeodesicGrid.EarthRadiusKm * GeodesicGrid.EarthRadiusKm;
        Assert.InRange(total, expected * 0.999, expected * 1.001);
    }

    [Fact]
    public void Cells_are_nearly_equal_in_size()
    {
        double min = double.MaxValue;
        double max = 0.0;
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            double area = _grid.AreaKm2(cell);
            min = Math.Min(min, area);
            max = Math.Max(max, area);
        }

        // Mean cell is about 2,267 square km. Largest and smallest stay within about 12% of it.
        Assert.InRange(min, 1950.0, 2267.0);
        Assert.InRange(max, 2267.0, 2550.0);
    }

    [Fact]
    public void Neighbours_are_about_51_km_apart()
    {
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            foreach (int other in _grid.Neighbours(cell))
            {
                Assert.InRange(_grid.DistanceKm(cell, other), 45.0, 57.0);
            }
        }
    }

    [Fact]
    public void Distance_is_zero_to_self_symmetric_and_right_between_the_poles()
    {
        int north = _grid.FindCell(90.0, 0.0);
        int south = _grid.FindCell(-90.0, 0.0);
        Assert.Equal(90.0, _grid.Latitude(north), 9);
        Assert.Equal(-90.0, _grid.Latitude(south), 9);
        Assert.Equal(0.0, _grid.DistanceKm(north, north), 9);
        Assert.Equal(_grid.DistanceKm(north, south), _grid.DistanceKm(south, north), 9);
        Assert.Equal(Math.PI * GeodesicGrid.EarthRadiusKm, _grid.DistanceKm(north, south), 6);
    }

    [Fact]
    public void Every_cell_is_found_from_its_own_coordinates()
    {
        for (int cell = 0; cell < _grid.CellCount; cell++)
        {
            Assert.Equal(cell, _grid.FindCell(_grid.Centre(cell)));
        }

        for (int cell = 0; cell < _grid.CellCount; cell += 97)
        {
            Assert.Equal(cell, _grid.FindCell(_grid.Latitude(cell), _grid.Longitude(cell)));
        }
    }

    [Fact]
    public void Find_cell_matches_brute_force()
    {
        GridChecks.AssertFindCellMatchesBruteForce(_grid, 200);
    }
}

internal static class GridChecks
{
    public static void AssertStructure(GeodesicGrid grid)
    {
        int pentagons = 0;
        Span<Vec3> corners = stackalloc Vec3[GeodesicGrid.MaxNeighbours];

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            ReadOnlySpan<int> neighbours = grid.Neighbours(cell);
            int m = neighbours.Length;
            Assert.Equal(grid.NeighbourCount(cell), m);
            Assert.True(m == 5 || m == 6, $"Cell {cell} has {m} neighbours.");
            if (m == 5)
            {
                pentagons++;
            }

            Vec3 p = grid.Centre(cell);
            Assert.Equal(1.0, p.Length, 12);

            for (int x = 0; x < m; x++)
            {
                int other = neighbours[x];
                Assert.NotEqual(cell, other);
                Assert.InRange(other, 0, grid.CellCount - 1);

                // No duplicates.
                for (int y = x + 1; y < m; y++)
                {
                    Assert.NotEqual(other, neighbours[y]);
                }

                // Every link is mutual.
                Assert.True(grid.Neighbours(other).Contains(cell), $"Link {cell} to {other} is not mutual.");

                // Counter-clockwise order seen from outside.
                Vec3 a = grid.Centre(other);
                Vec3 b = grid.Centre(neighbours[(x + 1) % m]);
                Assert.True(Vec3.Dot(Vec3.Cross(a, b), p) > 0.0, $"Neighbours of cell {cell} are out of order.");
            }

            // Each corner of the outline is equally far from this cell and the two neighbours it lies between.
            Assert.Equal(m, grid.GetCorners(cell, corners));
            for (int x = 0; x < m; x++)
            {
                double own = Vec3.Dot(corners[x], p);
                double first = Vec3.Dot(corners[x], grid.Centre(neighbours[x]));
                double second = Vec3.Dot(corners[x], grid.Centre(neighbours[(x + 1) % m]));
                Assert.Equal(own, first, 10);
                Assert.Equal(own, second, 10);
            }

            Assert.True(grid.AreaKm2(cell) > 0.0);
        }

        Assert.Equal(12, pentagons);
    }

    public static void AssertFindCellMatchesBruteForce(GeodesicGrid grid, int samples)
    {
        var random = new Random(12345);
        for (int s = 0; s < samples; s++)
        {
            double z = random.NextDouble() * 2.0 - 1.0;
            double lon = random.NextDouble() * 2.0 * Math.PI;
            double r = Math.Sqrt(1.0 - z * z);
            var point = new Vec3(r * Math.Cos(lon), r * Math.Sin(lon), z);

            int nearest = 0;
            double best = double.NegativeInfinity;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                double d = Vec3.Dot(grid.Centre(cell), point);
                if (d > best)
                {
                    best = d;
                    nearest = cell;
                }
            }

            int found = grid.FindCell(point);
            if (found != nearest)
            {
                // Only acceptable when the point is on a boundary to within rounding.
                Assert.Equal(best, Vec3.Dot(grid.Centre(found), point), 12);
            }
        }
    }
}
