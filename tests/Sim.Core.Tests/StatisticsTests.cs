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
        double Value(string column) => double.Parse(later[Array.IndexOf(header, column)], System.Globalization.CultureInfo.InvariantCulture);
        double share = header.Where(h => h.StartsWith("land_", StringComparison.Ordinal)).Sum(Value);
        Assert.InRange(share, 0.98, 1.0001);

        // Goods: wild food is eaten as it is gathered, so over the 20 years produced equals consumed plus lost.
        Assert.Contains("grain_traded", header);
        Assert.Contains("obsidian_in_transit", header);
        Assert.True(Value("wild_food_produced") > 1e6);
        Assert.InRange(Value("wild_food_consumed") + Value("wild_food_lost"), 0.999 * Value("wild_food_produced"), 1.001 * Value("wild_food_produced"));
        Assert.Contains("herders", header);

        Assert.StartsWith("crop,cell,latitude,longitude,year,date", stats.Origins());
        Assert.StartsWith("animal,homeland,cell,latitude,longitude,year,date", stats.HerdingOrigins());

        // Technology: each column is a share of the world's people, and every recorded event is listed.
        Assert.Contains("know_the_plough", header);
        Assert.All(header.Where(h => h.StartsWith("know_", StringComparison.Ordinal)), h => Assert.InRange(Value(h), 0.0, 1.0));
        string events = stats.TechnologyEvents();
        Assert.StartsWith("technology,event,cell,latitude,longitude,year,date,contact", events);
        Assert.Equal(sim.Systems.OfType<Sim.Core.Technologies.TechnologySystem>().Single().Events().Count + 1,
            events.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.StartsWith("cell,latitude,longitude,year,date,from_cell", stats.Landings());
    }
}
