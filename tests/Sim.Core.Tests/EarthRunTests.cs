using System.Diagnostics;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.People;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>What one long run of the full simulation measured.</summary>
public sealed class EarthRunResult
{
    public ulong Seed { get; init; }

    public double Seconds { get; set; }

    public double MaxWorldShift { get; set; }

    public double MaxCellShift { get; set; }

    public double MinRainFactor { get; set; } = 1.0;

    public double MaxRainFactor { get; set; } = 1.0;

    public double BiomeChangedShare { get; set; }

    public double StartPopulation { get; set; }

    public double EndPopulation { get; set; }

    public double MinPopulationToCapacity { get; set; } = double.MaxValue;

    public double MaxPopulationToCapacity { get; set; }

    public bool BadPopulation { get; set; }

    public double MaxUnreachedPopulation { get; set; }

    public override string ToString() =>
        $"Seed {Seed}: {Seconds:F0} s; world shift up to {MaxWorldShift:F2} C, cell shift up to {MaxCellShift:F2} C, "
        + $"rainfall {MinRainFactor:F2} to {MaxRainFactor:F2}, {100.0 * BiomeChangedShare:F1}% of land changed biome; "
        + $"population {StartPopulation / 1e6:F2} M at start, {EndPopulation / 1e6:F2} M at end, "
        + $"{MinPopulationToCapacity:F3} to {MaxPopulationToCapacity:F3} of what the land can feed";
}

/// <summary>
/// Runs the full simulation on Earth for 5,000 years with 10 seeds, once, and records what the
/// exit tests check. Shared by every test in <see cref="EarthRunTests"/>.
/// </summary>
public sealed class EarthRunFixture
{
    public const int Seeds = 10;
    public const int Years = 5000;

    public EarthRunFixture()
    {
        WorldMap world = WorldMap.Load(Path.Combine(AppContext.BaseDirectory, "earth.world"));
        Results = new EarthRunResult[Seeds];
        Parallel.For(0, Seeds, s => Results[s] = Run(world, (ulong)(s + 1)));
    }

    public EarthRunResult[] Results { get; }

    private static EarthRunResult Run(WorldMap world, ulong seed)
    {
        var watch = Stopwatch.StartNew();
        var sim = new Simulation(world, new RunSettings { Seed = seed }, SystemCatalog.CreateDefault());
        ClimateSystem climate = sim.Systems.OfType<ClimateSystem>().Single();
        ForagerSystem foragers = sim.Systems.OfType<ForagerSystem>().Single();

        int[] land = climate.LandCells.ToArray();
        int[] unreached = land.Where(cell => !foragers.IsReachable(cell)).ToArray();
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);
        double[] factor = sim.State.DoubleField(ClimateSystem.RainfallFactorField);
        double[] shift = sim.State.DoubleField(ClimateSystem.TemperatureShiftField);
        double[] people = sim.State.DoubleField(ForagerSystem.PopulationField);
        int[] startBiome = land.Select(cell => biome[cell]).ToArray();
        var changed = new bool[land.Length];

        var result = new EarthRunResult { Seed = seed, StartPopulation = foragers.WorldPopulation() };
        for (int year = 0; year < Years; year++)
        {
            sim.Run(12);
            result.MaxWorldShift = Math.Max(result.MaxWorldShift, Math.Abs(climate.WorldTemperatureShift()));
            for (int i = 0; i < land.Length; i++)
            {
                int cell = land[i];
                result.MinRainFactor = Math.Min(result.MinRainFactor, factor[cell]);
                result.MaxRainFactor = Math.Max(result.MaxRainFactor, factor[cell]);
                result.MaxCellShift = Math.Max(result.MaxCellShift, Math.Abs(shift[cell]));
                if (biome[cell] != startBiome[i])
                {
                    changed[i] = true;
                }

                double p = people[cell];
                if (p < 0.0 || double.IsNaN(p) || double.IsInfinity(p))
                {
                    result.BadPopulation = true;
                }
            }

            foreach (int cell in unreached)
            {
                result.MaxUnreachedPopulation = Math.Max(result.MaxUnreachedPopulation, people[cell]);
            }

            double ratio = foragers.WorldPopulation() / foragers.WorldCapacity();
            result.MinPopulationToCapacity = Math.Min(result.MinPopulationToCapacity, ratio);
            result.MaxPopulationToCapacity = Math.Max(result.MaxPopulationToCapacity, ratio);
        }

        result.BiomeChangedShare = changed.Count(c => c) / (double)land.Length;
        result.EndPopulation = foragers.WorldPopulation();
        result.Seconds = watch.Elapsed.TotalSeconds;
        return result;
    }
}

public class EarthRunTests : IClassFixture<EarthRunFixture>
{
    private readonly EarthRunResult[] _results;

    public EarthRunTests(EarthRunFixture fixture, ITestOutputHelper output)
    {
        _results = fixture.Results;
        foreach (EarthRunResult result in _results)
        {
            output.WriteLine(result.ToString());
        }
    }

    [Fact]
    public void Climate_stays_in_bounds()
    {
        foreach (EarthRunResult r in _results)
        {
            Assert.True(r.MaxWorldShift <= 2.5, $"Seed {r.Seed}: world temperature shift reached {r.MaxWorldShift:F2} C.");
            Assert.True(r.MinRainFactor >= 0.4 && r.MaxRainFactor <= 1.6, $"Seed {r.Seed}: rainfall reached {r.MinRainFactor:F2} to {r.MaxRainFactor:F2}.");
        }
    }

    [Fact]
    public void Between_2_and_25_percent_of_land_changes_biome()
    {
        Assert.All(_results, r => Assert.InRange(r.BiomeChangedShare, 0.02, 0.25));
    }

    [Fact]
    public void Starting_population_is_between_2_and_6_million()
    {
        Assert.All(_results, r => Assert.InRange(r.StartPopulation, 2e6, 6e6));
    }

    [Fact]
    public void Without_farming_population_stays_within_20_percent_of_what_the_land_can_feed()
    {
        foreach (EarthRunResult r in _results)
        {
            Assert.InRange(r.MinPopulationToCapacity, 0.8, 1.2);
            Assert.InRange(r.MaxPopulationToCapacity, 0.8, 1.2);
        }
    }

    [Fact]
    public void Unreached_islands_stay_empty()
    {
        Assert.All(_results, r => Assert.Equal(0.0, r.MaxUnreachedPopulation));
    }

    [Fact]
    public void Population_is_never_negative_or_invalid()
    {
        Assert.All(_results, r => Assert.False(r.BadPopulation, $"Seed {r.Seed} had an invalid population."));
    }
}
