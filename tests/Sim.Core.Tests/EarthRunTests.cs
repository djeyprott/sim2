using System.Diagnostics;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Geo;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;
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

    public double PopulationAt5000 { get; set; }

    public double FarmersAt5000 { get; set; }

    public double MinForagersToCapacity { get; set; } = double.MaxValue;

    public double MaxForagersToCapacity { get; set; }

    public bool BadPopulation { get; set; }

    public double MaxUnreachedPopulation { get; set; }

    public int MaxGroupsInACell { get; set; }

    public bool DuplicateGroups { get; set; }

    public int FarmingOnForbiddenLand { get; set; }

    public List<(int Crop, int Cell, long Tick)> Origins { get; set; } = new List<(int, int, long)>();

    public int Regions6000 { get; set; }

    public double SpreadKmPerYear { get; set; } = double.NaN;

    public double FarthestOriginFromRealHomelandKm { get; set; }

    public int FarmingCells { get; set; }

    public int SettlementsAt5000 { get; set; }

    /// <summary>Settlements whose cell does not point back at them, or cells sharing a settlement.</summary>
    public int SettlementProblems { get; set; }

    /// <summary>Famines recorded, counted once per cell and year.</summary>
    public int FamineCellYears { get; set; }

    /// <summary>Famine-hit cells that regained 90% of their people within 50 years, and those that did not.</summary>
    public int FaminesRecovered { get; set; }

    public int FaminesNotRecovered { get; set; }

    /// <summary>Largest share of a cell's people lost between the year before a famine and its low point.</summary>
    public double DeepestFamineDrop { get; set; }

    public double FamineRecoveredShare => FaminesRecovered + FaminesNotRecovered == 0
        ? 0.0 : FaminesRecovered / (double)(FaminesRecovered + FaminesNotRecovered);

    public override string ToString()
    {
        string origins = string.Join(", ", Origins.Select(o => $"crop {o.Crop} at cell {o.Cell} in {new GameDate(o.Tick)}"));
        return $"Seed {Seed}: {Seconds:F0} s; world shift up to {MaxWorldShift:F2} C, rainfall {MinRainFactor:F2} to {MaxRainFactor:F2}, "
            + $"{100.0 * BiomeChangedShare:F1}% of land changed biome; population {StartPopulation / 1e6:F2} M at start, "
            + $"{PopulationAt5000 / 1e6:F2} M after 5,000 years ({FarmersAt5000 / 1e6:F2} M farmers in {FarmingCells} cells); "
            + $"foragers {MinForagersToCapacity:F3} to {MaxForagersToCapacity:F3} of what their land feeds; "
            + $"up to {MaxGroupsInACell} groups in a cell; {Regions6000} separate farming regions within 6,000 years; "
            + $"spread {SpreadKmPerYear:F2} km a year; farthest origin {FarthestOriginFromRealHomelandKm:F0} km from a real homeland; "
            + $"{SettlementsAt5000} settlements after 5,000 years; "
            + $"famines: {FamineCellYears} cell-years, {100.0 * FamineRecoveredShare:F0}% of hit cells recovered within 50 years "
            + $"({FaminesRecovered} of {FaminesRecovered + FaminesNotRecovered}), deepest drop {100.0 * DeepestFamineDrop:F0}%; "
            + $"origins: {origins}";
    }
}

/// <summary>
/// Runs the full simulation on Earth with 10 seeds, once per test class, and records what the
/// exit tests check. Subclasses choose the settings.
/// </summary>
public abstract class EarthRunFixtureBase
{
    public const int Seeds = 10;

    protected EarthRunFixtureBase(bool farming, CropHomelands homelands, int years)
    {
        WorldMap world = WorldMap.Load(Path.Combine(AppContext.BaseDirectory, "earth.world"));
        Results = new EarthRunResult[Seeds];
        Parallel.For(0, Seeds, s => Results[s] = Run(world, (ulong)(s + 1), farming, homelands, years));
    }

    public EarthRunResult[] Results { get; }

    private static EarthRunResult Run(WorldMap world, ulong seed, bool farming, CropHomelands homelands, int years)
    {
        var watch = Stopwatch.StartNew();
        var sim = new Simulation(world, new RunSettings { Seed = seed, CropHomelands = homelands }, SystemCatalog.CreateDefault(farming));
        ClimateSystem climate = sim.Systems.OfType<ClimateSystem>().Single();
        ForagerSystem foragers = sim.Systems.OfType<ForagerSystem>().Single();
        FarmingSystem? farmingSystem = sim.Systems.OfType<FarmingSystem>().SingleOrDefault();

        int[] land = climate.LandCells.ToArray();
        int[] unreached = land.Where(cell => !foragers.IsReachable(cell)).ToArray();
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);
        double[] factor = sim.State.DoubleField(ClimateSystem.RainfallFactorField);
        double[] shift = sim.State.DoubleField(ClimateSystem.TemperatureShiftField);
        double[] foragerCapacity = sim.State.DoubleField(ForagerSystem.CapacityField);
        int[]? cropPlusOne = farming ? sim.State.IntField(FarmingSystem.CropField) : null;
        int[]? sincePlusOne = farming ? sim.State.IntField(FarmingSystem.SinceField) : null;
        int[]? lastFaminePlusOne = farming ? sim.State.IntField(FarmingSystem.LastFamineField) : null;
        long lastCheck = -1;
        var previousPeople = new double[world.CellCount];
        var famineBefore = new double[world.CellCount];
        var famineLow = new double[world.CellCount];
        var famineYear = new int[world.CellCount];
        Array.Fill(famineYear, -1);
        bool[] forbidden = new bool[climate.Biomes.Count];
        foreach (string name in FarmingParameters.LoadDefault().NoFarmingBiomes)
        {
            forbidden[climate.Biomes.IndexOf(name)] = true;
        }

        var groups = new PopulationGroups(sim.State);
        var people = new double[world.CellCount];
        int[] startBiome = land.Select(cell => biome[cell]).ToArray();
        var changed = new bool[land.Length];

        var result = new EarthRunResult { Seed = seed, StartPopulation = groups.WorldTotal() };
        for (int year = 0; year < years; year++)
        {
            sim.Run(12);
            for (int group = 0; group < groups.IdCount; group++)
            {
                if (groups.IsAlive(group))
                {
                    double size = groups.Size(group);
                    double food = groups.Food(group);
                    if (size < 0.0 || double.IsNaN(size) || double.IsInfinity(size) || food < 0.0 || food > 1.0 || double.IsNaN(food))
                    {
                        result.BadPopulation = true;
                    }
                }
            }

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

                // Farming that started since the last check on land where it may not.
                if (cropPlusOne != null && cropPlusOne[cell] > 0 && sincePlusOne![cell] - 1 > lastCheck
                    && biome[cell] >= 0 && forbidden[biome[cell]] && world.River(cell) != RiverSize.Major)
                {
                    result.FarmingOnForbiddenLand++;
                }
            }

            if (farming)
            {
                groups.FillCellTotals(people);
                MeasureFamines(land, people, previousPeople, lastFaminePlusOne!, lastCheck, year, famineBefore, famineLow, famineYear, result);
                Array.Copy(people, previousPeople, people.Length);
            }

            lastCheck = sim.Tick - 1;
            if (year % 10 == 0)
            {
                groups.FillCellTotals(people);
                foreach (int cell in unreached)
                {
                    result.MaxUnreachedPopulation = Math.Max(result.MaxUnreachedPopulation, people[cell]);
                }
            }

            if (year % 100 == 0)
            {
                CheckGroups(world, groups, result);
            }

            double ratio = foragers.WorldPopulation() / foragerCapacity.Sum();
            result.MinForagersToCapacity = Math.Min(result.MinForagersToCapacity, ratio);
            result.MaxForagersToCapacity = Math.Max(result.MaxForagersToCapacity, ratio);

            if (year == 4999)
            {
                result.PopulationAt5000 = groups.WorldTotal();
                result.FarmersAt5000 = farmingSystem?.WorldFarmers() ?? 0.0;
                SettlementSystem? settlementSystem = sim.Systems.OfType<SettlementSystem>().SingleOrDefault();
                if (settlementSystem != null)
                {
                    List<SettlementInfo> all = settlementSystem.All();
                    result.SettlementsAt5000 = all.Count;
                    int[] index = sim.State.IntField(SettlementSystem.SettlementField);
                    result.SettlementProblems += all.Count(st => index[st.Cell] != st.Id + 1 || !world.IsLand(st.Cell));
                    result.SettlementProblems += Math.Abs(index.Count(i => i > 0) - all.Count);
                }
            }
        }

        result.BiomeChangedShare = changed.Count(c => c) / (double)land.Length;
        CheckGroups(world, groups, result);

        if (farmingSystem != null)
        {
            result.Origins = farmingSystem.Origins().ToList();
            result.Regions6000 = SeparateRegions(world.Grid, result.Origins.Where(o => o.Tick < 6000L * 12).Select(o => o.Cell).ToList());
            result.SpreadKmPerYear = SpreadSpeed(world.Grid, sim.State.IntField(FarmingSystem.SinceField), cropPlusOne!, result.Origins);
            result.FarmingCells = cropPlusOne!.Count(c => c > 0);
            CropSet crops = farmingSystem.Crops;
            result.FarthestOriginFromRealHomelandKm = result.Origins.Count == 0 ? 0.0 : result.Origins.Max(o =>
                crops.Crops.Min(c => Km(world.Grid, o.Cell, world.Grid.FindCell(c.HomelandLat, c.HomelandLon))));
        }

        result.Seconds = watch.Elapsed.TotalSeconds;
        return result;
    }

    public static double Km(GeodesicGrid grid, int a, int b) => grid.DistanceKm(a, b);

    private static void MeasureFamines(int[] land, double[] people, double[] previousPeople, int[] lastFaminePlusOne,
        long lastCheck, int year, double[] before, double[] low, int[] start, EarthRunResult result)
    {
        foreach (int cell in land)
        {
            if (lastFaminePlusOne[cell] - 1 > lastCheck)
            {
                result.FamineCellYears++;
                if (start[cell] < 0 && previousPeople[cell] > 0.0)
                {
                    start[cell] = year;
                    before[cell] = previousPeople[cell];
                    low[cell] = people[cell];
                }
            }

            if (start[cell] < 0)
            {
                continue;
            }

            low[cell] = Math.Min(low[cell], people[cell]);
            if (people[cell] >= 0.9 * before[cell] && year > start[cell])
            {
                result.FaminesRecovered++;
            }
            else if (year - start[cell] >= 50)
            {
                result.FaminesNotRecovered++;
            }
            else
            {
                continue;
            }

            result.DeepestFamineDrop = Math.Max(result.DeepestFamineDrop, 1.0 - low[cell] / before[cell]);
            start[cell] = -1;
        }
    }

    // How many origins are at least 1,000 km from every origin counted before them.
    private static int SeparateRegions(GeodesicGrid grid, List<int> cells)
    {
        var counted = new List<int>();
        foreach (int cell in cells)
        {
            if (counted.All(other => grid.DistanceKm(cell, other) >= 1000.0))
            {
                counted.Add(cell);
            }
        }

        return counted.Count;
    }

    // Average speed farming reached cells at least 300 km from its nearest earlier origin.
    private static double SpreadSpeed(GeodesicGrid grid, int[] sincePlusOne, int[] cropPlusOne, List<(int Crop, int Cell, long Tick)> origins)
    {
        double total = 0.0;
        int count = 0;
        for (int cell = 0; cell < cropPlusOne.Length; cell++)
        {
            if (cropPlusOne[cell] == 0 || sincePlusOne[cell] == 0)
            {
                continue;
            }

            long since = sincePlusOne[cell] - 1;
            double best = double.MaxValue;
            long bestTick = 0;
            foreach (var origin in origins)
            {
                if (origin.Tick <= since)
                {
                    double km = grid.DistanceKm(cell, origin.Cell);
                    if (km < best)
                    {
                        best = km;
                        bestTick = origin.Tick;
                    }
                }
            }

            double years = (since - bestTick) / 12.0;
            if (best >= 300.0 && best < double.MaxValue && years > 0.0)
            {
                total += best / years;
                count++;
            }
        }

        return count == 0 ? double.NaN : total / count;
    }

    private static void CheckGroups(WorldMap world, PopulationGroups groups, EarthRunResult result)
    {
        var occupations = new List<int>();
        for (int cell = 0; cell < world.CellCount; cell++)
        {
            occupations.Clear();
            foreach (int group in groups.InCell(cell))
            {
                if (occupations.Contains(groups.Occupation(group)))
                {
                    result.DuplicateGroups = true;
                }

                occupations.Add(groups.Occupation(group));
            }

            result.MaxGroupsInACell = Math.Max(result.MaxGroupsInACell, occupations.Count);
        }
    }
}

public sealed class RealHomelandRunFixture : EarthRunFixtureBase
{
    public RealHomelandRunFixture()
        : base(farming: true, CropHomelands.Real, years: 6000)
    {
    }
}

public sealed class RandomHomelandRunFixture : EarthRunFixtureBase
{
    public RandomHomelandRunFixture()
        : base(farming: true, CropHomelands.Random, years: 6000)
    {
    }
}

public sealed class NoFarmingRunFixture : EarthRunFixtureBase
{
    public NoFarmingRunFixture()
        : base(farming: false, CropHomelands.Real, years: 5000)
    {
    }
}

[Trait("Category", "Exit")]
public class EarthRealHomelandTests : IClassFixture<RealHomelandRunFixture>
{
    private readonly EarthRunResult[] _results;

    public EarthRealHomelandTests(RealHomelandRunFixture fixture, ITestOutputHelper output)
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
    public void Settlements_are_founded_and_stand_one_per_land_cell()
    {
        Assert.All(_results, r =>
        {
            Assert.True(r.SettlementsAt5000 > 0, $"Seed {r.Seed}: no settlements.");
            Assert.Equal(0, r.SettlementProblems);
        });
    }

    [Fact]
    public void Famines_happen_and_most_hit_cells_recover()
    {
        Assert.All(_results, r =>
        {
            Assert.True(r.FamineCellYears > 0, $"Seed {r.Seed}: no famines.");
            Assert.True(r.FamineRecoveredShare >= 0.5, $"Seed {r.Seed}: only {100.0 * r.FamineRecoveredShare:F0}% of famine-hit cells recovered.");
        });
    }

    [Fact]
    public void Farming_starts_in_at_least_3_separate_regions_within_6000_years_in_8_of_10_seeds()
    {
        Assert.True(_results.Count(r => r.Regions6000 >= 3) >= 8, string.Join(", ", _results.Select(r => r.Regions6000)));
    }

    [Fact]
    public void The_first_farming_differs_between_seeds()
    {
        var firsts = _results.Where(r => r.Origins.Count > 0).Select(r => r.Origins.OrderBy(o => o.Tick).First()).ToList();
        Assert.True(firsts.Select(o => o.Cell).Distinct().Count() >= 5, "The first farming cell should vary between seeds.");
        Assert.True(firsts.Select(o => o.Tick).Distinct().Count() >= 8, "The first farming date should vary between seeds.");
    }

    [Fact]
    public void Farming_spreads_at_0_3_to_3_km_a_year()
    {
        Assert.All(_results, r => Assert.InRange(r.SpreadKmPerYear, 0.3, 3.0));
    }

    [Fact]
    public void After_5000_years_population_is_between_5_and_40_million()
    {
        Assert.All(_results, r => Assert.InRange(r.PopulationAt5000, 5e6, 40e6));
    }

    [Fact]
    public void No_farming_in_ice_tundra_or_desert_without_a_major_river()
    {
        Assert.All(_results, r => Assert.Equal(0, r.FarmingOnForbiddenLand));
    }

    [Fact]
    public void Groups_stay_valid()
    {
        foreach (EarthRunResult r in _results)
        {
            Assert.False(r.BadPopulation, $"Seed {r.Seed} had an invalid population.");
            Assert.InRange(r.MaxGroupsInACell, 1, PopulationGroups.MaxGroupsPerCell);
            Assert.False(r.DuplicateGroups, $"Seed {r.Seed} had two groups with the same traits in a cell.");
            Assert.Equal(0.0, r.MaxUnreachedPopulation);
        }
    }
}

[Trait("Category", "Exit")]
public class EarthRandomHomelandTests : IClassFixture<RandomHomelandRunFixture>
{
    private readonly EarthRunResult[] _results;

    public EarthRandomHomelandTests(RandomHomelandRunFixture fixture, ITestOutputHelper output)
    {
        _results = fixture.Results;
        foreach (EarthRunResult result in _results)
        {
            output.WriteLine(result.ToString());
        }
    }

    [Fact]
    public void Farming_starts_in_at_least_3_separate_regions_within_6000_years_in_8_of_10_seeds()
    {
        Assert.True(_results.Count(r => r.Regions6000 >= 3) >= 8, string.Join(", ", _results.Select(r => r.Regions6000)));
    }

    [Fact]
    public void In_8_of_10_seeds_an_origin_lies_more_than_2000_km_from_every_real_homeland()
    {
        Assert.True(_results.Count(r => r.FarthestOriginFromRealHomelandKm > 2000.0) >= 8,
            string.Join(", ", _results.Select(r => r.FarthestOriginFromRealHomelandKm.ToString("F0"))));
    }

    [Fact]
    public void Groups_stay_valid()
    {
        Assert.All(_results, r => Assert.False(r.BadPopulation || r.DuplicateGroups || r.FarmingOnForbiddenLand > 0));
    }
}

[Trait("Category", "Exit")]
public class EarthNoFarmingTests : IClassFixture<NoFarmingRunFixture>
{
    private readonly EarthRunResult[] _results;

    public EarthNoFarmingTests(NoFarmingRunFixture fixture, ITestOutputHelper output)
    {
        _results = fixture.Results;
        foreach (EarthRunResult result in _results)
        {
            output.WriteLine(result.ToString());
        }
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
            Assert.InRange(r.MinForagersToCapacity, 0.8, 1.2);
            Assert.InRange(r.MaxForagersToCapacity, 0.8, 1.2);
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
