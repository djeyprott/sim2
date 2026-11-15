using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.Stats;
using Xunit;

namespace Sim.Core.Tests;

public class StatisticsTests : IClassFixture<EarthWorldFixture>
{
    private readonly WorldMap _world;

    public StatisticsTests(EarthWorldFixture fixture)
    {
        _world = fixture.World;
    }

    [Fact]
    public void Rows_match_the_header_and_track_the_run()
    {
        var sim = new Simulation(_world, new RunSettings { Seed = 3 }, SystemCatalog.CreateDefault());
        var stats = new RunStatistics(sim);
        string[] header = stats.Header().Split(',');
        string[] first = stats.Row().Split(',');
        Assert.Equal(header.Length, first.Length);
        Assert.Equal("-10000", first[0]);
        Assert.Contains("land_desert", header);

        sim.Run(20 * 12);
        string[] later = stats.Row().Split(',');
        Assert.Equal(header.Length, later.Length);
        Assert.Equal("-9980", later[0]);
        Assert.True(double.Parse(later[2], System.Globalization.CultureInfo.InvariantCulture) > 1e6);

        // Biome shares add up to about one.
        double share = later.Skip(Array.IndexOf(header, "world_temperature_shift") + 1)
            .Sum(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(share, 0.98, 1.0001);

        Assert.StartsWith("crop,cell,latitude,longitude,year,date", stats.Origins());
    }
}
