using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Map;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

public class BiomeSetTests
{
    private static readonly BiomeSet Biomes = BiomeSet.LoadDefault();

    private static string Classify(double[] temperature, double[] rainfall, bool north = true) =>
        Biomes.Get(Biomes.Classify(ClimateSummary.FromMonths(temperature, rainfall, north))).Name;

    private static double[] Same(double value) => Enumerable.Repeat(value, 12).ToArray();

    // A simple seasonal curve: coldest in January, warmest in July.
    private static double[] Seasons(double january, double july) =>
        Enumerable.Range(0, 12).Select(m => january + (july - january) * (1.0 - Math.Cos(Math.PI * m / 6.0)) / 2.0).ToArray();

    [Fact]
    public void Default_file_loads_with_about_ten_biomes()
    {
        Assert.InRange(Biomes.Count, 8, 14);
        Assert.Equal("Ice", Biomes.Get(0).Name);
        Assert.All(Biomes.Biomes, b => Assert.Matches("^#[0-9a-fA-F]{6}$", b.Colour));
        Assert.Equal(Biomes.IndexOf("Desert"), Biomes.Biomes.Single(b => b.Name == "Desert").Index);
    }

    [Fact]
    public void Typical_climates_get_the_expected_biome()
    {
        Assert.Equal("Ice", Classify(Seasons(-35, -10), Same(70)));
        Assert.Equal("Tundra", Classify(Seasons(-25, 7), Same(25)));
        Assert.Equal("Desert", Classify(Seasons(13, 32), Same(1.5)));
        Assert.Equal("Rainforest", Classify(Same(27), Same(220)));
        Assert.Equal("Savanna", Classify(Same(26), new double[] { 0, 0, 5, 20, 80, 150, 200, 200, 120, 30, 5, 0 }));
        Assert.Equal("Temperate forest", Classify(Seasons(5, 18), Same(55)));
        Assert.Equal("Boreal forest", Classify(Seasons(-30, 17), Same(25)));
        Assert.Equal("Grassland", Classify(Seasons(-10, 22), Same(18)));
        Assert.Equal("Mediterranean", Classify(Seasons(10, 26), new double[] { 120, 100, 80, 40, 20, 5, 2, 3, 20, 60, 100, 120 }));
    }

    [Fact]
    public void Summer_is_April_to_September_in_the_north_and_the_reverse_in_the_south()
    {
        double[] rain = { 0, 0, 0, 10, 10, 10, 10, 10, 10, 0, 0, 0 };
        Assert.Equal(1.0, ClimateSummary.FromMonths(Same(10), rain, true).SummerShare, 9);
        Assert.Equal(0.0, ClimateSummary.FromMonths(Same(10), rain, false).SummerShare, 9);
    }

    [Fact]
    public void Bad_files_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => BiomeSet.Parse("{ \"biomes\": [ { \"name\": \"A\", \"colour\": \"#000000\" } ], \"rules\": [ { \"biome\": \"B\" } ] }"));
        Assert.Throws<InvalidDataException>(() => BiomeSet.Parse("{ \"biomes\": [ { \"name\": \"A\", \"colour\": \"#000000\" } ], \"rules\": [ { \"biome\": \"A\", \"maxRain\": 5 } ] }"));
        Assert.Throws<InvalidDataException>(() => BiomeSet.Parse("{ \"biomes\": [ { \"name\": \"A\", \"colour\": \"red\" } ], \"rules\": [ { \"biome\": \"A\" } ] }"));
        Assert.Throws<InvalidDataException>(() => BiomeSet.Parse("not json"));
    }

    [Fact]
    public void Climate_parameters_load_and_are_checked()
    {
        ClimateParameters parameters = ClimateParameters.LoadDefault();
        Assert.Equal(4, parameters.RegionGridFrequency);
        Assert.NotEmpty(parameters.RegionTemperature);
        Assert.NotEmpty(parameters.RegionRainfall);
        Assert.NotEmpty(parameters.WorldTemperature);
        Assert.Throws<InvalidDataException>(() => ClimateParameters.Parse("{ \"rainfallFactorMin\": 2 }"));
    }

    [Fact]
    public void Gaussian_draws_have_mean_zero_and_spread_one()
    {
        var rng = new Rng(5);
        double sum = 0.0;
        double sumSq = 0.0;
        const int n = 200000;
        for (int i = 0; i < n; i++)
        {
            double x = rng.NextGaussian();
            Assert.False(double.IsNaN(x) || double.IsInfinity(x));
            sum += x;
            sumSq += x * x;
        }

        Assert.InRange(sum / n, -0.01, 0.01);
        Assert.InRange(Math.Sqrt(sumSq / n), 0.99, 1.01);
    }
}

public class EarthClimateTests : IClassFixture<EarthWorldFixture>
{
    private readonly WorldMap _world;
    private readonly ITestOutputHelper _output;

    public EarthClimateTests(EarthWorldFixture fixture, ITestOutputHelper output)
    {
        _world = fixture.World;
        _output = output;
    }

    private Simulation NewSimulation(ulong seed) =>
        new Simulation(_world, seed, new ISimSystem[] { ClimateSystem.CreateDefault() });

    private static ClimateSystem Climate(Simulation sim) => sim.Systems.OfType<ClimateSystem>().Single();

    private string BiomeAt(Simulation sim, double lat, double lon)
    {
        int cell = _world.Grid.FindCell(lat, lon);
        int biome = sim.State.IntField(ClimateSystem.BiomeField)[cell];
        return biome == ClimateSystem.NoBiome ? "none" : Climate(sim).Biomes.Get(biome).Name;
    }

    [Fact]
    public void At_the_start_known_places_have_the_right_biome()
    {
        Simulation sim = NewSimulation(1);
        Assert.Equal("Desert", BiomeAt(sim, 23.0, 12.0));
        Assert.Equal("Rainforest", BiomeAt(sim, -3.0, -62.0));
        Assert.Equal("Ice", BiomeAt(sim, 72.0, -40.0));
        Assert.Equal("Ice", BiomeAt(sim, -80.0, 0.0));
        Assert.Equal("Temperate forest", BiomeAt(sim, 51.5, -0.1));
        Assert.Equal("none", BiomeAt(sim, 0.0, -140.0));
        Assert.Equal("none", BiomeAt(sim, 47.7, -87.5));

        // Print the share of land in each biome for reference.
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);
        ClimateSystem climate = Climate(sim);
        foreach (Biome b in climate.Biomes.Biomes)
        {
            int count = climate.LandCells.Count(cell => biome[cell] == b.Index);
            _output.WriteLine($"{b.Name}: {100.0 * count / climate.LandCells.Count:F1}% of land cells");
        }

        Assert.All(climate.LandCells, cell => Assert.InRange(biome[cell], 0, climate.Biomes.Count - 1));
    }

    [Fact]
    public void Climate_is_identical_for_the_same_seed_and_differs_between_seeds()
    {
        Simulation a = NewSimulation(7);
        Simulation b = NewSimulation(7);
        Simulation c = NewSimulation(8);
        a.Run(300 * 12);
        b.Run(300 * 12);
        c.Run(300 * 12);
        Assert.Equal(a.StateChecksum(), b.StateChecksum());
        Assert.NotEqual(a.StateChecksum(), c.StateChecksum());
        Assert.NotEqual(Climate(a).WorldTemperatureShift(), Climate(c).WorldTemperatureShift());
    }

    [Fact]
    public void Save_and_load_mid_run_gives_the_same_climate()
    {
        Simulation straight = NewSimulation(3);
        straight.Run(80 * 12);

        Simulation first = NewSimulation(3);
        first.Run(50 * 12 + 5);
        using var save = new MemoryStream();
        first.Save(save);
        _output.WriteLine($"Save size: {save.Length} bytes");

        Simulation second = NewSimulation(99);
        second.Load(new MemoryStream(save.ToArray()));
        second.Run(30 * 12 - 5);
        Assert.Equal(straight.StateChecksum(), second.StateChecksum());
    }
}
