using System.Diagnostics;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Geo;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Sea;
using Sim.Core.Settlements;
using Sim.Core.Technologies;
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

    /// <summary>Landings by sea on land not reached at the start (where and when), over the whole run.</summary>
    public List<string> Landings { get; set; } = new List<string>();

    /// <summary>At the end, per sea region (Aegean, Gulf, Red Sea): trade links across the sea there, and settlements there knowing the sail.</summary>
    public Dictionary<string, int> SeaLinks { get; set; } = new Dictionary<string, int>();

    public Dictionary<string, int> SailSettlements { get; set; } = new Dictionary<string, int>();

    public int MaxGroupsInACell { get; set; }

    public bool DuplicateGroups { get; set; }

    public int FarmingOnForbiddenLand { get; set; }

    public List<(int Crop, int Cell, long Tick)> Origins { get; set; } = new List<(int, int, long)>();

    public int Regions6000 { get; set; }

    public double SpreadKmPerYear { get; set; } = double.NaN;

    public double FarthestOriginFromRealHomelandKm { get; set; }

    public int FarmingCells { get; set; }

    public int SettlementsAt5000 { get; set; }

    /// <summary>Farthest a settlement holding obsidian lies from the nearest obsidian source, after 5,000 years.</summary>
    public double ObsidianReachKm { get; set; }

    /// <summary>At the end: the farthest settlement holding tin from every tin source (km).</summary>
    public double TinReachKm { get; set; }

    /// <summary>Copper and bronze tools made over the whole run, and settlements holding any metal tools at the end.</summary>
    public double CopperToolsMade { get; set; }

    public double BronzeToolsMade { get; set; }

    public int MetalToolSettlements { get; set; }

    /// <summary>Goods whose average price gap along links was not smaller after a season's trade, by name.</summary>
    public List<string> GapsNotShrinking { get; set; } = new List<string>();

    /// <summary>Goods sent along links in the measured Januaries.</summary>
    public int TradedGoods { get; set; }

    /// <summary>Groups of five or more linked settlements with no trade route, and settlements on a route, after 5,000 years.</summary>
    public int RegionsWithoutRoutes { get; set; }

    public double ShareOnRoutes { get; set; }

    /// <summary>Largest share by which any good's ledger failed to balance, checked every 100 years.</summary>
    public double MaxLedgerImbalance { get; set; }

    /// <summary>True if any stockpile ever held a negative or invalid amount.</summary>
    public bool BadStock { get; set; }

    /// <summary>Grain in store worldwide after 5,000 years.</summary>
    public double GrainInStoreAt5000 { get; set; }

    /// <summary>Farming cells counted once per year, for the share of farming cell-years with famine.</summary>
    public long FarmingCellYears { get; set; }

    public double FamineShare => FarmingCellYears == 0 ? 0.0 : FamineCellYears / (double)FarmingCellYears;

    /// <summary>Famine deaths and farmer-years in cells eating from a store, and in cells without one.</summary>
    public double StoreFamineDeaths { get; set; }

    public double StoreFarmerYears { get; set; }

    public double NoStoreFamineDeaths { get; set; }

    public double NoStoreFarmerYears { get; set; }

    public double StoreDeathRate => StoreFarmerYears <= 0.0 ? 0.0 : StoreFamineDeaths / StoreFarmerYears;

    /// <summary>
    /// Famine deaths and farmer-years by how full the cell was at the start of the year (farmers over
    /// what its land feeds: under 0.6, 0.6 to 0.85, 0.85 to 1, 1 or more), with a store [0] and without [1].
    /// </summary>
    public double[,] DeathsByFullness { get; } = new double[2, 4];

    public double[,] YearsByFullness { get; } = new double[2, 4];

    /// <summary>
    /// The store death rate over the no-store one, comparing like with like: each fullness band's
    /// store rate weighted by the no-store farmer-years in that band.
    /// </summary>
    public double LikeForLikeStoreRatio
    {
        get
        {
            double expected = 0.0;
            double actual = 0.0;
            for (int band = 0; band < 4; band++)
            {
                if (YearsByFullness[0, band] > 0.0)
                {
                    expected += DeathsByFullness[0, band] / YearsByFullness[0, band] * YearsByFullness[1, band];
                    actual += DeathsByFullness[1, band];
                }
            }

            return actual <= 0.0 ? double.NaN : expected / actual;
        }
    }

    public string FullnessTable()
    {
        var parts = new List<string>();
        string[] names = { "<0.6", "0.6-0.85", "0.85-1", ">=1" };
        for (int band = 0; band < 4; band++)
        {
            double Rate(int kind) => YearsByFullness[kind, band] <= 0.0 ? 0.0 : 1000.0 * DeathsByFullness[kind, band] / YearsByFullness[kind, band];
            parts.Add($"{names[band]}: store {Rate(0):F2}/1000 over {YearsByFullness[0, band] / 1e6:F0} M yrs, none {Rate(1):F2}/1000 over {YearsByFullness[1, band] / 1e6:F0} M yrs");
        }

        return string.Join("; ", parts);
    }

    public double NoStoreDeathRate => NoStoreFarmerYears <= 0.0 ? 0.0 : NoStoreFamineDeaths / NoStoreFarmerYears;

    /// <summary>Farming cell-years with any famine deaths, and those where deaths passed 15% of the cell's farmers.</summary>
    public long FamineDeathCellYears { get; set; }

    public long SevereFamineCellYears { get; set; }

    public double SevereShare => FamineDeathCellYears == 0 ? 0.0 : SevereFamineCellYears / (double)FamineDeathCellYears;

    /// <summary>Each technology's first discovery: name, year after the start and cell; and how many independent discoveries followed.</summary>
    public List<string> TechnologyFirsts { get; set; } = new List<string>();

    public int IndependentDiscoveries { get; set; }

    /// <summary>Recorded losses, and the most people in contact with a group when it lost a technology.</summary>
    public int TechnologyLosses { get; set; }

    public double MaxContactAtLoss { get; set; }

    /// <summary>For each technology discovered at least 1,000 years before the end: how far from its first discovery a group used it 1,000 years later (km).</summary>
    public List<string> TechnologyReach1000 { get; set; } = new List<string>();

    /// <summary>By technology name: the year after the start and the cell of its first discovery (absent: never).</summary>
    public Dictionary<string, (int Year, int Cell)> FirstDiscovery { get; set; } = new Dictionary<string, (int, int)>();

    /// <summary>By technology name: separate places it was discovered (the first, and each independent discovery).</summary>
    public Dictionary<string, int> DiscoveryRegions { get; set; } = new Dictionary<string, int>();

    /// <summary>By technology name: 1,000 years after its first discovery, the farthest (km) a group using it lies from there, not counting groups nearer a later independent discovery.</summary>
    public Dictionary<string, double> Reach1000 { get; set; } = new Dictionary<string, double>();

    /// <summary>
    /// For each technology, 300 years after its first discovery: of the settlements 300 to
    /// 1,500 km from there, the share using it among the busier half by goods sold and among the quieter half.
    /// </summary>
    public List<(string Technology, double Busy, double Quiet)> SpreadByTrade { get; set; } = new List<(string, double, double)>();

    public double FinalPopulation { get; set; }

    /// <summary>
    /// At the end: farmers per km² in farming cells whose farmers use the plough over those in
    /// like cells (same crop, river and suitability without technology, open grassland left out) that
    /// do not, averaged over the kinds of cell with both; 0 when there are none to compare.
    /// </summary>
    public double PloughDensityRatio { get; set; }

    /// <summary>How many like-cell kinds the plough ratio compares, and how many ploughing cells they hold.</summary>
    public int PloughKinds { get; set; }

    public int PloughCells { get; set; }

    /// <summary>At the end: open grassland farming cells (away from rivers) whose farmers use the plough, and all farmed.</summary>
    public int PloughedOpenGrassland { get; set; }

    public int FarmedOpenGrassland { get; set; }

    /// <summary>True if any group's share of a technology left 0 to 1.</summary>
    public bool BadKnowledge { get; set; }

    /// <summary>Separate regions (1,000 km apart) where an animal was first herded within 6,000 years.</summary>
    public int HerdingRegions6000 { get; set; }

    /// <summary>Herders, and farmers, living on grassland away from rivers after 5,000 years.</summary>
    public double HerdersOnOpenGrassland { get; set; }

    public double FarmersOnOpenGrassland { get; set; }

    public double HerdersAt5000 { get; set; }

    /// <summary>Year after the start when sheep and goats, and llamas, were first herded (-1: never).</summary>
    public int FirstSheepYear { get; set; } = -1;

    public int FirstLlamaYear { get; set; } = -1;

    /// <summary>Each first herding: animal name and year after the start.</summary>
    public List<string> HerdingOrigins { get; set; } = new List<string>();

    /// <summary>Share of farming cells on grassland away from rivers after 5,000 years.</summary>
    public double OpenGrasslandFarmingShare { get; set; }

    /// <summary>Share of Pontic steppe land (45 to 52 N, 30 to 50 E) that is farmed after 5,000 years.</summary>
    public double PonticSteppeFarmedShare { get; set; }

    /// <summary>Share of the open grassland in that box that is farmed, and how many such cells there are.</summary>
    public double PonticOpenGrasslandFarmedShare { get; set; }

    public int PonticOpenGrasslandCells { get; set; }

    /// <summary>Share of all open grassland, and of all temperate forest, that is farmed after 5,000 years.</summary>
    public double OpenGrasslandFarmedShare { get; set; }

    public double TemperateForestFarmedShare { get; set; }

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
            + $"{100.0 * OpenGrasslandFarmingShare:F1}% of farming on open grassland, {100.0 * PonticSteppeFarmedShare:F0}% of the Pontic steppe farmed "
            + $"({100.0 * PonticOpenGrasslandFarmedShare:F0}% of its {PonticOpenGrasslandCells} open grassland cells); "
            + $"farmed: {100.0 * OpenGrasslandFarmedShare:F0}% of all open grassland, {100.0 * TemperateForestFarmedShare:F0}% of temperate forest; "
            + $"obsidian up to {ObsidianReachKm:F0} km from a source; "
            + $"sea: {Landings.Count} landings [{string.Join(", ", Landings.Take(8))}], sea links {string.Join(", ", SeaLinks.Select(p => $"{p.Key} {p.Value} ({SailSettlements.GetValueOrDefault(p.Key)} settlements with the sail)"))}; "
            + $"metals: tin up to {TinReachKm:F0} km from a source at the end, {CopperToolsMade / 1e3:F0} k copper and {BronzeToolsMade / 1e3:F0} k bronze tools made, {MetalToolSettlements} settlements with metal tools at the end; "
            + $"{TradedGoods} goods traded, gaps not shrinking: [{string.Join(", ", GapsNotShrinking)}]; "
            + $"{RegionsWithoutRoutes} linked regions without routes, {100.0 * ShareOnRoutes:F0}% of settlements on a route; "
            + $"herding: {HerdersAt5000 / 1e6:F2} M herders after 5,000 years, {HerdersOnOpenGrassland / 1e6:F2} M on open grassland against {FarmersOnOpenGrassland / 1e6:F2} M farmers; "
            + $"{HerdingRegions6000} herding regions within 6,000 years [{string.Join(", ", HerdingOrigins)}]; "
            + $"famine deaths by fullness [{FullnessTable()}], like-for-like store ratio {LikeForLikeStoreRatio:F2}; "
            + $"technology: firsts [{string.Join(", ", TechnologyFirsts)}], {IndependentDiscoveries} independent discoveries, {TechnologyLosses} losses (contact at most {MaxContactAtLoss:F0}), reach after 1,000 years [{string.Join(", ", TechnologyReach1000)}], "
            + $"using it 300 to 1,500 km away, busier against quieter settlements [{string.Join(", ", SpreadByTrade.Select(x => $"{x.Technology} {x.Busy:F2}/{x.Quiet:F2}"))}]; "
            + $"population {FinalPopulation / 1e6:F2} M at the end; "
            + $"plough: {PloughDensityRatio:F2} times the farmers per km2 of like cells ({PloughKinds} kinds, {PloughCells} cells), {PloughedOpenGrassland} of {FarmedOpenGrassland} farmed open grassland cells ploughed; "
            + $"ledger out by at most {MaxLedgerImbalance:E1}, {GrainInStoreAt5000 / 1e6:F2} M person-years of grain in store; "
            + $"famine in {100.0 * FamineShare:F2}% of farming cell-years; "
            + $"famine deaths per 1,000 farmer-years {1000.0 * StoreDeathRate:F2} with a store, {1000.0 * NoStoreDeathRate:F2} without; "
            + $"{100.0 * SevereShare:F1}% of famine-death cell-years severe (over 15%); "
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
        foreach (TradeSystem trade in sim.Systems.OfType<TradeSystem>())
        {
            trade.MeasureGaps = true;
        }
        ClimateSystem climate = sim.Systems.OfType<ClimateSystem>().Single();
        ForagerSystem foragers = sim.Systems.OfType<ForagerSystem>().Single();
        FarmingSystem? farmingSystem = sim.Systems.OfType<FarmingSystem>().SingleOrDefault();

        int[] land = climate.LandCells.ToArray();
        int[] unreached = land.Where(cell => !foragers.IsReachable(cell)).ToArray();
        SeafaringSystem? seafaring = sim.Systems.OfType<SeafaringSystem>().SingleOrDefault();
        SeaMap sea = SeaMap.Of(world);
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);
        double[] factor = sim.State.DoubleField(ClimateSystem.RainfallFactorField);
        double[] shift = sim.State.DoubleField(ClimateSystem.TemperatureShiftField);
        double[] foragerCapacity = sim.State.DoubleField(ForagerSystem.CapacityField);
        int[]? cropPlusOne = farming ? sim.State.IntField(FarmingSystem.CropField) : null;
        int[]? sincePlusOne = farming ? sim.State.IntField(FarmingSystem.SinceField) : null;
        int[]? lastFaminePlusOne = farming ? sim.State.IntField(FarmingSystem.LastFamineField) : null;
        double[]? famineDeaths = farming ? sim.State.DoubleField(FarmingSystem.FamineDeathsField) : null;
        int[]? storeFed = farming ? sim.State.IntField(FarmingSystem.StoreFedField) : null;
        int farmerOccupation = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        var farmersAtYearStart = new double[world.CellCount];
        var deathsAtYearStart = new double[world.CellCount];
        var storeFedAtYearStart = new int[world.CellCount];
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
        var stockpiles = new Stockpiles(sim.State, GoodSet.LoadDefault());
        var people = new double[world.CellCount];
        int[] startBiome = land.Select(cell => biome[cell]).ToArray();
        var changed = new bool[land.Length];

        var result = new EarthRunResult { Seed = seed, StartPopulation = groups.WorldTotal() };
        TechnologySystem? technology = sim.Systems.OfType<TechnologySystem>().SingleOrDefault();
        var reachMeasured = new bool[technology?.Technologies.Count ?? 0];
        for (int year = 0; year < years; year++)
        {
            sim.Run(12);
            if (technology != null)
            {
                MeasureTechnologyReach(sim, world, groups, technology, year, reachMeasured, result);
            }
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

                if (cropPlusOne != null && cropPlusOne[cell] > 0)
                {
                    result.FarmingCellYears++;
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
                MeasureFamineDeaths(land, groups, farmerOccupation, famineDeaths!, storeFed!,
                    farmersAtYearStart, deathsAtYearStart, storeFedAtYearStart, sim.State.DoubleField(FarmingSystem.CapacityField), result);
                groups.FillCellTotals(people);
                MeasureFamines(land, people, previousPeople, lastFaminePlusOne!, lastCheck, year, famineBefore, famineLow, famineYear, result);
                Array.Copy(people, previousPeople, people.Length);
            }

            lastCheck = sim.Tick - 1;
            if (year % 10 == 0)
            {
                // Land not reached at the start must stay empty until people land on it by sea.
                groups.FillCellTotals(people);
                foreach (int cell in unreached)
                {
                    if (seafaring == null || !seafaring.Reached(sea.Landmass[cell]))
                    {
                        result.MaxUnreachedPopulation = Math.Max(result.MaxUnreachedPopulation, people[cell]);
                    }
                }
            }

            if (year % 100 == 0 || year == years - 1)
            {
                CheckGroups(world, groups, result);
                CheckGoods(stockpiles, result);
            }

            double ratio = foragers.WorldPopulation() / foragerCapacity.Sum();
            result.MinForagersToCapacity = Math.Min(result.MinForagersToCapacity, ratio);
            result.MaxForagersToCapacity = Math.Max(result.MaxForagersToCapacity, ratio);

            if (year == 4999)
            {
                result.PopulationAt5000 = groups.WorldTotal();
                result.FarmersAt5000 = farmingSystem?.WorldFarmers() ?? 0.0;
                result.GrainInStoreAt5000 = stockpiles.InStore(stockpiles.Goods.IndexOf(GoodSet.Grain));
                if (cropPlusOne != null)
                {
                    int grassland = climate.Biomes.IndexOf("Grassland");
                    int farmed = 0;
                    int open = 0;
                    int steppe = 0;
                    int steppeFarmed = 0;
                    int forest = climate.Biomes.IndexOf("Temperate forest");
                    int[] totals = new int[4];
                    int[] farmedIn = new int[4];
                    foreach (int cell in land)
                    {
                        double lat = world.Grid.Latitude(cell);
                        double lon = world.Grid.Longitude(cell);
                        bool inSteppe = lat >= 45.0 && lat <= 52.0 && lon >= 30.0 && lon <= 50.0;
                        steppe += inSteppe ? 1 : 0;
                        bool openGrass = biome[cell] == grassland && world.River(cell) == RiverSize.None;
                        int kind = openGrass ? (inSteppe ? 0 : 1) : biome[cell] == forest ? 2 : 3;
                        totals[kind]++;
                        farmedIn[kind] += cropPlusOne[cell] > 0 ? 1 : 0;
                        if (cropPlusOne[cell] > 0)
                        {
                            farmed++;
                            open += biome[cell] == grassland && world.River(cell) == RiverSize.None ? 1 : 0;
                            steppeFarmed += inSteppe ? 1 : 0;
                        }
                    }

                    result.OpenGrasslandFarmingShare = farmed == 0 ? 0.0 : open / (double)farmed;
                    result.PonticSteppeFarmedShare = steppe == 0 ? 0.0 : steppeFarmed / (double)steppe;
                    result.PonticOpenGrasslandCells = totals[0];
                    result.PonticOpenGrasslandFarmedShare = totals[0] == 0 ? 0.0 : farmedIn[0] / (double)totals[0];
                    result.OpenGrasslandFarmedShare = (farmedIn[0] + farmedIn[1]) / (double)Math.Max(1, totals[0] + totals[1]);
                    result.TemperateForestFarmedShare = farmedIn[2] / (double)Math.Max(1, totals[2]);

                    var occupations = OccupationSet.LoadDefault();
                    if (occupations.Contains(OccupationSet.Herder))
                    {
                        var herders = new double[world.CellCount];
                        var farmers = new double[world.CellCount];
                        groups.FillCellTotals(herders, occupations.IndexOf(OccupationSet.Herder));
                        groups.FillCellTotals(farmers, occupations.IndexOf(OccupationSet.Farmer));
                        result.HerdersAt5000 = herders.Sum();
                        foreach (int cell in land)
                        {
                            if (biome[cell] == grassland && world.River(cell) == RiverSize.None)
                            {
                                result.HerdersOnOpenGrassland += herders[cell];
                                result.FarmersOnOpenGrassland += farmers[cell];
                            }
                        }
                    }
                }

                MeasureTrade(sim, world, stockpiles, result);
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
        result.FinalPopulation = groups.WorldTotal();
        FarmingSystem? farmingAtEnd = sim.Systems.OfType<FarmingSystem>().SingleOrDefault();
        if (technology != null && farmingAtEnd != null)
        {
            MeasurePlough(world, groups, technology, farmingAtEnd, sim.State.IntField(FarmingSystem.CropField), biome,
                sim.Systems.OfType<ClimateSystem>().Single().Biomes.IndexOf("Grassland"), land, result);
        }
        CheckGroups(world, groups, result);

        if (technology != null)
        {
            var events = technology.Events();
            foreach (TechnologyEvent e in events.Where(e => e.Kind == TechnologyEventKind.First))
            {
                result.TechnologyFirsts.Add($"{technology.Technologies.Get(e.Technology).Name} year {e.Tick / 12} at {world.Grid.Latitude(e.Cell):F0},{world.Grid.Longitude(e.Cell):F0}");
            }

            foreach (TechnologyEvent e in events.Where(e => e.Kind != TechnologyEventKind.Lost))
            {
                string name = technology.Technologies.Get(e.Technology).Name;
                if (e.Kind == TechnologyEventKind.First)
                {
                    result.FirstDiscovery[name] = ((int)(e.Tick / 12), e.Cell);
                }

                result.DiscoveryRegions[name] = result.DiscoveryRegions.GetValueOrDefault(name) + 1;
            }

            result.IndependentDiscoveries = events.Count(e => e.Kind == TechnologyEventKind.Independent);
            result.TechnologyLosses = events.Count(e => e.Kind == TechnologyEventKind.Lost);
            result.MaxContactAtLoss = events.Where(e => e.Kind == TechnologyEventKind.Lost).Select(e => e.Contact).DefaultIfEmpty(0.0).Max();
            for (int g = 0; g < groups.IdCount; g++)
            {
                for (int t = 0; groups.IsAlive(g) && t < technology.Technologies.Count; t++)
                {
                    double share = technology.Knowledge(g, t);
                    result.BadKnowledge |= double.IsNaN(share) || share < 0.0 || share > 1.0;
                }
            }
        }

        HerdingSystem? herding = sim.Systems.OfType<HerdingSystem>().SingleOrDefault();
        if (herding != null)
        {
            var origins = herding.Origins();
            result.HerdingRegions6000 = SeparateRegions(world.Grid, origins.Where(o => o.Tick < 6000L * 12).Select(o => o.Cell).ToList());
            result.HerdingOrigins = origins.Select(o => $"{herding.Livestock.Get(o.Animal).Name} year {o.Tick / 12}").ToList();
            int FirstYear(string name)
            {
                int animal = herding.Livestock.IndexOf(name);
                return origins.Where(o => o.Animal == animal).Select(o => (int)(o.Tick / 12)).DefaultIfEmpty(-1).Min();
            }

            result.FirstSheepYear = FirstYear("Sheep and goats");
            result.FirstLlamaYear = FirstYear("Llamas and alpacas");
        }

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

        if (farmingSystem != null)
        {
            MeasureMetals(sim, world, stockpiles, result);
            MeasureSea(sim, world, result);
        }

        result.Seconds = watch.Elapsed.TotalSeconds;
        return result;
    }

    public static double Km(GeodesicGrid grid, int a, int b) => grid.DistanceKm(a, b);

    // Farmers per km² where the farmers plough against like cells where they do not, and ploughed open grassland.
    private static void MeasurePlough(WorldMap world, PopulationGroups groups, TechnologySystem technology, FarmingSystem farming,
        int[] cropPlusOne, int[] biome, int grassland, int[] land, EarthRunResult result)
    {
        int plough = technology.Technologies.IndexOf("The plough");
        int farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        var kinds = new SortedDictionary<long, (double PloughDensity, int Ploughed, double OtherDensity, int Other)>();
        foreach (int cell in land)
        {
            if (cropPlusOne[cell] <= 0)
            {
                continue;
            }

            int crop = cropPlusOne[cell] - 1;
            int group = groups.Find(cell, farmer);
            bool ploughs = group >= 0 && technology.Knowledge(group, plough) >= technology.Parameters.InUse && technology.CellAllows(cell, plough);
            bool open = biome[cell] == grassland && world.River(cell) == RiverSize.None;
            if (open)
            {
                result.FarmedOpenGrassland++;
                result.PloughedOpenGrassland += ploughs ? 1 : 0;
                continue;
            }

            double density = (group >= 0 ? groups.Size(group) : 0.0) / world.Grid.AreaKm2(cell);
            int band = (int)Math.Floor(farming.Suitability(cell, crop) * 10.0);
            long key = (crop * 10L + (int)world.River(cell)) * 100L + band;
            kinds.TryGetValue(key, out var k);
            kinds[key] = ploughs ? (k.PloughDensity + density, k.Ploughed + 1, k.OtherDensity, k.Other) : (k.PloughDensity, k.Ploughed, k.OtherDensity + density, k.Other + 1);
        }

        double weighted = 0.0;
        double weights = 0.0;
        foreach (var k in kinds.Values)
        {
            if (k.Ploughed > 0 && k.Other > 0 && k.OtherDensity > 0.0)
            {
                double w = Math.Min(k.Ploughed, k.Other);
                weighted += w * (k.PloughDensity / k.Ploughed) / (k.OtherDensity / k.Other);
                weights += w;
                result.PloughKinds++;
                result.PloughCells += k.Ploughed;
            }
        }

        result.PloughDensityRatio = weights > 0.0 ? weighted / weights : 0.0;
    }

    // 1,000 years after a technology's first discovery: the farthest a group using it lies from there.
    private static void MeasureTechnologyReach(Simulation sim, WorldMap world, PopulationGroups groups, TechnologySystem technology, int year,
        bool[] measured, EarthRunResult result)
    {
        IReadOnlyList<TechnologyEvent> events = technology.Events();
        foreach (TechnologyEvent e in events)
        {
            if (e.Kind == TechnologyEventKind.First && e.Tick / 12 + 300 == year)
            {
                MeasureSpreadByTrade(sim, world, groups, technology, e, result);
            }

            if (e.Kind != TechnologyEventKind.First || measured[e.Technology] || e.Tick / 12 + 1000 != year)
            {
                continue;
            }

            measured[e.Technology] = true;
            string name = technology.Technologies.Get(e.Technology).Name;

            // Spread from the first discovery: groups nearer a later independent discovery learned it there.
            int[] others = events.Where(o => o.Kind == TechnologyEventKind.Independent && o.Technology == e.Technology)
                .Select(o => o.Cell).ToArray();
            double farthest = 0.0;
            for (int g = 0; g < groups.IdCount; g++)
            {
                if (!groups.IsAlive(g) || groups.Size(g) <= 0.0 || technology.Knowledge(g, e.Technology) < technology.Parameters.InUse)
                {
                    continue;
                }

                int cell = groups.Cell(g);
                double distance = world.Grid.DistanceKm(e.Cell, cell);
                if (others.All(o => world.Grid.DistanceKm(o, cell) >= distance))
                {
                    farthest = Math.Max(farthest, distance);
                }
            }

            result.Reach1000[name] = farthest;
            result.TechnologyReach1000.Add($"{name} {farthest:F0} km");
        }
    }

    // Settlements on busier routes learn sooner: 300 years after a first discovery, of the settlements
    // 300 to 1,500 km from there, the share using it among the busier half (goods sold, by value) and the quieter half.
    private static void MeasureSpreadByTrade(Simulation sim, WorldMap world, PopulationGroups groups, TechnologySystem technology,
        TechnologyEvent e, EarthRunResult result)
    {
        {
            string name = technology.Technologies.Get(e.Technology).Name;
            var stockpiles = new Stockpiles(sim.State, GoodSet.LoadDefault());
            int[] settlement = sim.State.IntField(SettlementSystem.SettlementField);
            var alike = new List<(double Sold, bool Uses)>();
            for (int cell = 0; cell < world.CellCount; cell++)
            {
                if (settlement[cell] <= 0)
                {
                    continue;
                }

                double distance = world.Grid.DistanceKm(e.Cell, cell);
                if (distance < 300.0 || distance > 1500.0)
                {
                    continue;
                }

                double sold = 0.0;
                for (int good = 0; good < stockpiles.Goods.Count; good++)
                {
                    sold += stockpiles.Sold(cell, good) * stockpiles.Goods.Get(good).Value;
                }

                bool uses = false;
                foreach (int g in groups.InCell(cell))
                {
                    uses |= groups.Size(g) > 0.0 && technology.Knowledge(g, e.Technology) >= technology.Parameters.InUse;
                }

                alike.Add((sold, uses));
            }

            if (alike.Count >= 20)
            {
                var sorted = alike.OrderByDescending(a => a.Sold).ToList();
                int half = sorted.Count / 2;
                double busy = sorted.Take(half).Count(a => a.Uses) / (double)half;
                double quiet = sorted.Skip(sorted.Count - half).Count(a => a.Uses) / (double)half;
                result.SpreadByTrade.Add((name, busy, quiet));
            }
        }
    }

    // Famine deaths over the year just run, by cell, split by whether the cell ate from a store
    // at the start of the year; then records this year's start for the next call.
    private static void MeasureFamineDeaths(int[] land, PopulationGroups groups, int farmer, double[] famineDeaths,
        int[] storeFed, double[] farmersAtStart, double[] deathsAtStart, int[] storeFedAtStart, double[] capacity, EarthRunResult result)
    {
        foreach (int cell in land)
        {
            double farmers = farmersAtStart[cell];
            if (farmers > 0.0)
            {
                double deaths = famineDeaths[cell] - deathsAtStart[cell];
                double fullness = capacity[cell] > 0.0 ? farmers / capacity[cell] : 1.0;
                int band = fullness < 0.6 ? 0 : fullness < 0.85 ? 1 : fullness < 1.0 ? 2 : 3;
                int kind = storeFedAtStart[cell] != 0 ? 0 : 1;
                result.DeathsByFullness[kind, band] += deaths;
                result.YearsByFullness[kind, band] += farmers;
                if (storeFedAtStart[cell] != 0)
                {
                    result.StoreFamineDeaths += deaths;
                    result.StoreFarmerYears += farmers;
                }
                else
                {
                    result.NoStoreFamineDeaths += deaths;
                    result.NoStoreFarmerYears += farmers;
                }

                if (deaths > 0.0)
                {
                    result.FamineDeathCellYears++;
                    if (deaths > 0.15 * farmers)
                    {
                        result.SevereFamineCellYears++;
                    }
                }
            }

            deathsAtStart[cell] = famineDeaths[cell];
            storeFedAtStart[cell] = storeFed[cell];
        }

        groups.FillCellTotals(farmersAtStart, farmer);
    }

    // The farthest settlement holding a gathered good (in store or on its way) from all of the good's listed sources.
    private static double ReachKm(Simulation sim, WorldMap world, Stockpiles stockpiles, ResourceSystem resources, string name)
    {
        int good = stockpiles.Goods.IndexOf(name);
        ResourceRule rule = resources.Rules.Goods.Single(r => r.Good == name);
        int[] sources = rule.Sources.Select(s => world.Grid.FindCell(s.Lat, s.Lon)).ToArray();
        int[] settlementPlusOne = sim.State.IntField(SettlementSystem.SettlementField);
        double reach = 0.0;
        for (int cell = 0; cell < world.CellCount; cell++)
        {
            if (settlementPlusOne[cell] > 0 && stockpiles.Stock(cell, good) + stockpiles.Incoming(cell, good) > 0.01)
            {
                reach = Math.Max(reach, sources.Min(source => world.Grid.DistanceKm(cell, source)));
            }
        }

        return reach;
    }

    // Landings, and trade links across three seas where the sail was known, at the end of the run.
    private static void MeasureSea(Simulation sim, WorldMap world, EarthRunResult result)
    {
        SeafaringSystem? seafaring = sim.Systems.OfType<SeafaringSystem>().SingleOrDefault();
        TradeSystem? trade = sim.Systems.OfType<TradeSystem>().SingleOrDefault();
        if (seafaring == null || trade == null)
        {
            return;
        }

        foreach (Landing landing in seafaring.Landings())
        {
            result.Landings.Add($"year {landing.Tick / 12} at {world.Grid.Latitude(landing.Cell):F0},{world.Grid.Longitude(landing.Cell):F0} "
                + $"from {world.Grid.DistanceKm(landing.From, landing.Cell):F0} km");
        }

        var regions = new (string Name, double South, double North, double West, double East)[]
        {
            ("Aegean", 34.5, 41.5, 22.0, 29.0),
            ("Gulf", 23.5, 31.0, 47.5, 57.0),
            ("Red Sea", 12.0, 30.0, 32.0, 44.0),
        };
        bool In((string Name, double South, double North, double West, double East) r, int cell)
        {
            double lat = world.Grid.Latitude(cell), lon = world.Grid.Longitude(cell);
            return lat >= r.South && lat <= r.North && lon >= r.West && lon <= r.East;
        }

        TechnologyUse? use = TechnologyUse.TryCreate(sim.State, world);
        int sail = use?.Technologies.IndexOf("The sail") ?? -1;
        int[] settlement = sim.State.IntField(SettlementSystem.SettlementField);
        var seaLinks = trade.SeaLinksByCell();
        foreach (var region in regions)
        {
            result.SeaLinks[region.Name] = seaLinks.Count(l => In(region, l.CellA) && In(region, l.CellB));
            result.SailSettlements[region.Name] = use == null ? 0 : Enumerable.Range(0, world.CellCount)
                .Count(c => settlement[c] > 0 && In(region, c) && use.KnownInCell(c, sail));
        }
    }

    // Tin's reach and metal tools, at the end of the run.
    private static void MeasureMetals(Simulation sim, WorldMap world, Stockpiles stockpiles, EarthRunResult result)
    {
        ResourceSystem? resources = sim.Systems.OfType<ResourceSystem>().SingleOrDefault();
        if (resources == null || !resources.Rules.Goods.Any(r => r.Good == "Tin"))
        {
            return;
        }

        result.TinReachKm = ReachKm(sim, world, stockpiles, resources, "Tin");
        int copperTools = stockpiles.Goods.IndexOf("Copper tools");
        int bronzeTools = stockpiles.Goods.IndexOf("Bronze tools");
        result.CopperToolsMade = stockpiles.Produced(copperTools);
        result.BronzeToolsMade = stockpiles.Produced(bronzeTools);
        int[] settlementPlusOne = sim.State.IntField(SettlementSystem.SettlementField);
        for (int cell = 0; cell < world.CellCount; cell++)
        {
            if (settlementPlusOne[cell] > 0 && stockpiles.Stock(cell, copperTools) + stockpiles.Stock(cell, bronzeTools) > 0.01)
            {
                result.MetalToolSettlements++;
            }
        }
    }

    private static void MeasureTrade(Simulation sim, WorldMap world, Stockpiles stockpiles, EarthRunResult result)
    {
        TradeSystem? trade = sim.Systems.OfType<TradeSystem>().SingleOrDefault();
        ResourceSystem? resources = sim.Systems.OfType<ResourceSystem>().SingleOrDefault();
        if (trade == null || resources == null)
        {
            return;
        }

        // Obsidian's reach.
        result.ObsidianReachKm = ReachKm(sim, world, stockpiles, resources, "Obsidian");

        // Price gaps before and after a season's trade.
        for (int g = 0; g < stockpiles.Goods.Count; g++)
        {
            double before = trade.GapBeforeTrade[g];
            double after = trade.GapAfterTrade[g];
            if (before > 1e-6 && trade.SentInJanuary[g] > 0.0)
            {
                result.TradedGoods++;
                if (after >= before)
                {
                    result.GapsNotShrinking.Add(stockpiles.Goods.Get(g).Name);
                }
            }
        }

        // Routes in every linked region: join linked settlements into groups.
        List<(int A, int B, double Value)> links = trade.Links();
        int ids = links.Count == 0 ? 0 : links.Max(l => Math.Max(l.A, l.B)) + 1;
        var parent = Enumerable.Range(0, ids).ToArray();
        int Root(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var onRoute = new bool[ids];
        var linked = new bool[ids];
        foreach ((int a, int b, double value) in links)
        {
            parent[Root(a)] = Root(b);
            linked[a] = linked[b] = true;
            if (value >= trade.Parameters.RouteValue)
            {
                onRoute[a] = onRoute[b] = true;
            }
        }

        var size = new Dictionary<int, int>();
        var routed = new HashSet<int>();
        for (int id = 0; id < ids; id++)
        {
            if (!linked[id])
            {
                continue;
            }

            int root = Root(id);
            size[root] = size.GetValueOrDefault(root) + 1;
            if (onRoute[id])
            {
                routed.Add(root);
            }
        }

        result.RegionsWithoutRoutes = size.Count(pair => pair.Value >= 5 && !routed.Contains(pair.Key));
        int linkedCount = linked.Count(x => x);
        result.ShareOnRoutes = linkedCount == 0 ? 0.0 : onRoute.Count(x => x) / (double)linkedCount;
    }

    private static void CheckGoods(Stockpiles stockpiles, EarthRunResult result)
    {
        foreach (Good good in stockpiles.Goods.Goods)
        {
            result.MaxLedgerImbalance = Math.Max(result.MaxLedgerImbalance, stockpiles.Imbalance(good.Index));
            foreach (double v in stockpiles.StockField(good.Index))
            {
                if (v < 0.0 || double.IsNaN(v) || double.IsInfinity(v))
                {
                    result.BadStock = true;
                }
            }
        }
    }

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
        : base(farming: true, CropHomelands.Real, years: 7000)
    {
    }
}

public sealed class RandomHomelandRunFixture : EarthRunFixtureBase
{
    public RandomHomelandRunFixture()
        : base(farming: true, CropHomelands.Random, years: 7000)
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
    public void Trade_crosses_the_Aegean_the_Gulf_and_the_Red_Sea_once_the_sail_is_known_there()
    {
        foreach (string region in new[] { "Aegean", "Gulf", "Red Sea" })
        {
            var sailing = _results.Where(r => r.SailSettlements.GetValueOrDefault(region) > 0).ToList();
            Assert.True(sailing.Count(r => r.SeaLinks.GetValueOrDefault(region) > 0) >= Math.Ceiling(0.8 * sailing.Count),
                $"{region}: " + string.Join(", ", _results.Select(r => $"{r.SeaLinks.GetValueOrDefault(region)} sea links, {r.SailSettlements.GetValueOrDefault(region)} with the sail")));
        }
    }

    [Fact]
    public void Islands_are_settled_by_sea_in_at_least_half_the_seeds()
    {
        Assert.True(_results.Count(r => r.Landings.Count > 0) >= 5,
            "Landings: " + string.Join("; ", _results.Select(r => $"{r.Landings.Count} [{string.Join(", ", r.Landings.Take(5))}]")));
    }

    [Fact]
    public void Tin_travels_more_than_1000_km_from_its_sources_in_6_of_10_seeds()
    {
        Assert.True(_results.Count(r => r.TinReachKm > 1000.0) >= 6,
            "Tin reach: " + string.Join(", ", _results.Select(r => $"{r.TinReachKm:F0} km ({r.BronzeToolsMade / 1e3:F0} k bronze tools made)")));
    }

    [Fact]
    public void Obsidian_travels_more_than_500_km_from_its_sources_in_8_of_10_seeds()
    {
        Assert.True(_results.Count(r => r.ObsidianReachKm > 500.0) >= 8,
            "Obsidian reach: " + string.Join(", ", _results.Select(r => $"{r.ObsidianReachKm:F0} km")));
    }

    [Fact]
    public void Trade_shrinks_the_price_gap_for_every_traded_good()
    {
        Assert.All(_results, r => Assert.True(r.TradedGoods > 0 && r.GapsNotShrinking.Count == 0,
            $"Seed {r.Seed}: {r.TradedGoods} goods traded; gaps not shrinking for {string.Join(", ", r.GapsNotShrinking)}."));
    }

    [Fact]
    public void Every_linked_region_has_trade_routes()
    {
        Assert.All(_results, r => Assert.Equal(0, r.RegionsWithoutRoutes));
    }

    [Fact]
    public void Goods_ledger_balances_and_stocks_stay_valid()
    {
        Assert.All(_results, r =>
        {
            Assert.True(r.MaxLedgerImbalance < 1e-6, $"Seed {r.Seed}: ledger out by {r.MaxLedgerImbalance:E2}.");
            Assert.False(r.BadStock, $"Seed {r.Seed}: a stockpile went negative or invalid.");
            Assert.True(r.GrainInStoreAt5000 > 0.0, $"Seed {r.Seed}: no grain in store.");
        });
    }

    [Fact]
    public void Shortages_hit_2_to_5_percent_of_farming_cell_years()
    {
        Assert.All(_results, r => Assert.InRange(r.FamineShare, 0.02, 0.05));
    }

    [Fact]
    public void Famine_kills_at_most_half_as_many_where_people_eat_from_a_store()
    {
        // Compared like with like (spec 1.2): cells with stores are mostly long-settled cells near what
        // their land feeds, and cells without are mostly young or small ones with room to spare, so
        // each fullness band's store death rate is weighed by the no-store farmer-years in that band.
        Assert.All(_results, r => Assert.True(r.LikeForLikeStoreRatio <= 0.5,
            $"Seed {r.Seed}: like for like, stores leave {r.LikeForLikeStoreRatio:F2} of the famine deaths "
            + $"(overall {1000.0 * r.StoreDeathRate:F2} per 1,000 farmer-years with a store, {1000.0 * r.NoStoreDeathRate:F2} without; {r.FullnessTable()})."));
    }

    [Fact]
    public void Severe_famines_are_rare()
    {
        Assert.All(_results, r => Assert.True(r.SevereShare < 0.1,
            $"Seed {r.Seed}: {100.0 * r.SevereShare:F1}% of famine-death cell-years killed over 15%."));
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

    // Technologies first seen before 3000 BCE, and the dates (years after 10,000 BCE) the spec checks.
    private static readonly string[] BronzeAgeTechnologies =
    {
        "Granaries", "Irrigation", "Milk and wool", "The plough", "The wheel", "The sail", "Terraced fields",
        "Copper working", "Bronze", "Horse herding",
    };

    [Fact]
    public void Every_technology_first_seen_before_3000_BCE_is_discovered_in_7_of_10_seeds()
    {
        var missing = BronzeAgeTechnologies.Where(t => _results.Count(r => r.FirstDiscovery.ContainsKey(t)) < 7).ToList();
        Assert.True(missing.Count == 0, "Discovered in fewer than 7 seeds: " + string.Join(", ", missing.Select(t =>
            $"{t} ({_results.Count(r => r.FirstDiscovery.ContainsKey(t))})")));
    }

    [Fact]
    public void Irrigation_copper_the_plough_and_bronze_come_near_their_dates_in_6_of_10_seeds()
    {
        var dates = new (string Name, int Year)[] { ("Irrigation", 4000), ("Copper working", 5000), ("The plough", 5500), ("Bronze", 6700) };
        foreach ((string name, int date) in dates)
        {
            var firsts = _results.Where(r => r.FirstDiscovery.ContainsKey(name)).Select(r => r.FirstDiscovery[name]).ToList();
            string seen = $"{name} (year {date}): " + string.Join(", ", _results.Select(r => r.FirstDiscovery.TryGetValue(name, out var f) ? $"{f.Year}" : "never"));
            Assert.True(firsts.Count(f => Math.Abs(f.Year - date) <= 1500) >= 6, seen);
            Assert.True(firsts.Distinct().Count() >= Math.Min(2, firsts.Count), seen);
        }
    }

    [Fact]
    public void The_plough_the_wheel_and_bronze_arise_in_2_separate_regions_in_half_the_seeds_where_they_appear()
    {
        foreach (string name in new[] { "The plough", "The wheel", "Bronze" })
        {
            var seen = _results.Where(r => r.FirstDiscovery.ContainsKey(name)).ToList();
            Assert.True(seen.Count(r => r.DiscoveryRegions[name] >= 2) * 2 >= seen.Count,
                $"{name}: " + string.Join(", ", seen.Select(r => r.DiscoveryRegions[name])));
        }
    }

    [Fact]
    public void Knowledge_spreads_1000_km_in_1000_years_in_8_of_10_seeds()
    {
        // Technologies whose conditions hold over wide lands (not rivers in dry country, hills or coasts),
        // and that are not kept to farmland (granaries, found when farming had spread little, cannot
        // go farther than farming itself in 1,000 years). Milk and wool is found in every seed long
        // enough before the end; the others where they are found that long before it, and 8 in 10
        // of those must reach.
        foreach (string name in new[] { "Milk and wool", "The plough", "Copper working", "Horse herding" })
        {
            var measured = _results.Where(r => r.Reach1000.ContainsKey(name)).ToList();
            Assert.True(measured.Count(r => r.Reach1000[name] >= 1000.0) >= 0.8 * measured.Count && measured.Count >= (name == "Milk and wool" ? 8 : 0),
                $"{name}: " + string.Join(", ", _results.Select(r => r.Reach1000.TryGetValue(name, out double km) ? $"{km:F0} km" : "not measured")));
        }
    }

    [Fact]
    public void Settlements_on_busier_routes_learn_new_technologies_sooner()
    {
        // Where nearly all or none use it yet, there is nothing to compare. Technologies herders use
        // (milk and wool, the wheel, horses) are carried by herders across open country, away from settlements
        // and their trade, so they are left out.
        var herderTechnologies = new HashSet<string> { "Milk and wool", "Horse herding", "Horse riding", "The wheel" };
        var all = _results.SelectMany(r => r.SpreadByTrade)
            .Where(x => !herderTechnologies.Contains(x.Technology) && Math.Max(x.Busy, x.Quiet) >= 0.05 && Math.Min(x.Busy, x.Quiet) <= 0.95).ToList();
        Assert.True(all.Count >= 10 && all.Count(x => x.Busy > x.Quiet) >= 2.0 / 3.0 * all.Count,
            string.Join(", ", all.Select(x => $"{x.Technology} {x.Busy:F2}/{x.Quiet:F2}")));
    }

    [Fact]
    public void After_7000_years_population_is_between_15_and_60_million()
    {
        Assert.All(_results, r => Assert.InRange(r.FinalPopulation, 15e6, 60e6));
    }

    [Fact]
    public void Group_knowledge_stays_valid_and_only_small_contact_loses_it()
    {
        Assert.All(_results, r =>
        {
            Assert.False(r.BadKnowledge, $"Seed {r.Seed}: a knowledge share left 0 to 1.");
            Assert.True(r.MaxContactAtLoss <= 10000.0, $"Seed {r.Seed}: a group in contact with {r.MaxContactAtLoss:F0} people lost a technology.");
        });
        Assert.True(_results.Any(r => r.TechnologyLosses > 0), "No technology was ever lost.");
    }

    [Fact]
    public void Herding_is_first_taken_up_in_at_least_2_separate_regions_within_6000_years_in_8_of_10_seeds()
    {
        Assert.True(_results.Count(r => r.HerdingRegions6000 >= 2) >= 8, string.Join(", ", _results.Select(r => r.HerdingRegions6000)));
    }

    [Fact]
    public void Ploughing_farmers_feed_more_people_and_farm_open_grassland()
    {
        var compared = _results.Where(r => r.PloughKinds > 0).ToList();
        Assert.True(compared.Count >= 8, $"Only {compared.Count} seeds have ploughing and other farming cells to compare at the end.");
        Assert.True(compared.Count(r => r.PloughDensityRatio > 1.0) >= 8,
            string.Join(", ", compared.Select(r => $"{r.PloughDensityRatio:F2} ({r.PloughKinds} kinds, {r.PloughCells} cells)")));
        Assert.True(_results.Count(r => r.PloughedOpenGrassland > 0) >= 8,
            string.Join(", ", _results.Select(r => $"{r.PloughedOpenGrassland} of {r.FarmedOpenGrassland}")));
    }

    [Fact]
    public void Sheep_and_goats_come_near_8500_BCE_and_llamas_not_before_6000_BCE_in_7_of_10_seeds()
    {
        // The run starts in 10,000 BCE: 8500 BCE is year 1,500, and 6000 BCE year 4,000.
        Assert.True(_results.Count(r => r.FirstSheepYear >= 0 && r.FirstSheepYear <= 3000) >= 7,
            "First sheep and goats: " + string.Join(", ", _results.Select(r => r.FirstSheepYear)));
        Assert.True(_results.Count(r => r.FirstLlamaYear < 0 || r.FirstLlamaYear >= 4000) >= 7,
            "First llamas: " + string.Join(", ", _results.Select(r => r.FirstLlamaYear)));
    }

    [Fact]
    public void After_5000_years_herders_outnumber_farmers_on_open_grassland()
    {
        Assert.All(_results, r => Assert.True(r.HerdersOnOpenGrassland > r.FarmersOnOpenGrassland,
            $"Seed {r.Seed}: {r.HerdersOnOpenGrassland:F0} herders against {r.FarmersOnOpenGrassland:F0} farmers on open grassland."));
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
    public void Unreached_islands_stay_empty_until_settled_by_sea()
    {
        Assert.All(_results, r => Assert.Equal(0.0, r.MaxUnreachedPopulation));
    }

    [Fact]
    public void Population_is_never_negative_or_invalid()
    {
        Assert.All(_results, r => Assert.False(r.BadPopulation, $"Seed {r.Seed} had an invalid population."));
    }
}
