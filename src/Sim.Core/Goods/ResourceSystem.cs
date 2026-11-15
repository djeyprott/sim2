using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Goods;

/// <summary>
/// Natural resources and gathering (milestone 2, step 2). Works out at the start of a run where
/// each gathered good can be found, from terrain, rivers, coasts and a list of real sources;
/// richness that depends on the biome or on farming is worked out each year. Each January the
/// gatherers (farmers) of each settlement gather into its store what it wants (see
/// <see cref="Prices"/>), but only goods worth their time at local prices.
/// Nothing is gathered where there is no settlement, since there is nowhere to keep it.
/// </summary>
public sealed class ResourceSystem : ISimSystem
{
    public const string SystemName = "resources";

    private const string CropField = "farming.crop";

    private readonly ResourceRules _rules;
    private readonly BiomeSet _biomes;
    private readonly int[] _gatherers;
    private readonly OccupationSet _occupations;
    private Prices? _prices;
    private double[] _wanted = Array.Empty<double>();
    private double[] _price = Array.Empty<double>();
    private int[] _good = Array.Empty<int>();
    private double[][] _fixedRichness = Array.Empty<double[]>();
    private double[][] _biomeRichness = Array.Empty<double[]>();
    private Stockpiles? _stockpiles;
    private PopulationGroups? _groups;
    private EntityTable? _settlements;
    private IntColumn? _settlementCell;
    private int[] _biome = Array.Empty<int>();
    private int[]? _cropPlusOne;

    public ResourceSystem(ResourceRules rules, BiomeSet biomes, OccupationSet occupations)
    {
        _rules = rules;
        _biomes = biomes;
        _occupations = occupations;
        _gatherers = rules.Gatherers.Select(occupations.IndexOf).ToArray();
    }

    public static ResourceSystem CreateDefault(BiomeSet biomes, OccupationSet occupations) =>
        new ResourceSystem(ResourceRules.LoadDefault(), biomes, occupations);

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public ResourceRules Rules => _rules;

    public void Register(WorldState state)
    {
    }

    public void Initialize(SimContext context)
    {
        WorldState state = context.State;
        WorldMap map = context.Map;
        _stockpiles = new Stockpiles(state, GoodSet.LoadDefault());
        _groups = new PopulationGroups(state);
        _biome = state.IntField(ClimateSystem.BiomeField);
        _cropPlusOne = state.HasField(CropField) ? state.IntField(CropField) : null;
        if (state.HasTable(SettlementSystem.TableName))
        {
            _settlements = state.Table(SettlementSystem.TableName);
            _settlementCell = _settlements.IntColumn("cell");
        }

        GoodSet goods = _stockpiles.Goods;
        _prices = new Prices(state, map, PriceParameters.LoadDefault(), CraftRules.LoadDefault(goods), _occupations);
        _wanted = new double[goods.Count];
        _price = new double[goods.Count];
        int count = _rules.Goods.Count;
        _good = _rules.Goods.Select(r => _stockpiles.Goods.IndexOf(r.Good)).ToArray();
        _fixedRichness = new double[count][];
        _biomeRichness = new double[count][];
        for (int r = 0; r < count; r++)
        {
            ResourceRule rule = _rules.Goods[r];
            _biomeRichness[r] = new double[_biomes.Count];
            foreach (KeyValuePair<string, double> pair in rule.Biomes)
            {
                _biomeRichness[r][_biomes.IndexOf(pair.Key)] = pair.Value;
            }

            _fixedRichness[r] = FixedRichness(map, rule);
        }
    }

    // Richness that does not change during a run: terrain, rivers, coasts, lake shores and listed sources.
    private double[] FixedRichness(WorldMap map, ResourceRule rule)
    {
        var richness = new double[map.CellCount];
        double terrainLowland = rule.Terrain.GetValueOrDefault("lowland");
        double terrainHills = rule.Terrain.GetValueOrDefault("hills");
        double terrainMountains = rule.Terrain.GetValueOrDefault("mountains");
        for (int cell = 0; cell < map.CellCount; cell++)
        {
            if (!map.IsLand(cell))
            {
                continue;
            }

            double r = map.Terrain(cell) switch
            {
                TerrainClass.Lowland => terrainLowland,
                TerrainClass.Hills => terrainHills,
                TerrainClass.Mountains => terrainMountains,
                _ => 0.0,
            };
            if (map.River(cell) != RiverSize.None)
            {
                r = Math.Max(r, rule.River);
            }

            if (rule.Coast > 0.0 || rule.DryCoast > 0.0 || rule.DryLakeShore > 0.0)
            {
                bool coast = false;
                bool lakeShore = false;
                foreach (int other in map.Grid.Neighbours(cell))
                {
                    coast |= map.IsSea(other);
                    lakeShore |= map.IsLake(other);
                }

                bool dry = map.HasClimate(cell) && map.AnnualRainfallMm(cell) < _rules.DryRainMm;
                if (coast)
                {
                    r = Math.Max(r, dry ? Math.Max(rule.DryCoast, rule.Coast) : rule.Coast);
                }

                if (lakeShore && dry)
                {
                    r = Math.Max(r, rule.DryLakeShore);
                }
            }

            richness[cell] = r;
        }

        foreach (ResourceSource source in rule.Sources)
        {
            foreach (int cell in CellsWithin(map, source.Lat, source.Lon, rule.SourceRadiusKm))
            {
                if (map.IsLand(cell))
                {
                    richness[cell] = 1.0;
                }
            }
        }

        return richness;
    }

    private static List<int> CellsWithin(WorldMap map, double lat, double lon, double radiusKm)
    {
        GeodesicGrid grid = map.Grid;
        int centre = grid.FindCell(lat, lon);
        var found = new List<int> { centre };
        var seen = new HashSet<int> { centre };
        for (int i = 0; i < found.Count; i++)
        {
            foreach (int other in grid.Neighbours(found[i]))
            {
                if (seen.Add(other) && grid.DistanceKm(centre, other) <= radiusKm)
                {
                    found.Add(other);
                }
            }
        }

        return found;
    }

    /// <summary>How rich a cell is in the good of a rule, now (0 to 1).</summary>
    public double Richness(int rule, int cell)
    {
        double r = _fixedRichness[rule][cell];
        int biome = _biome[cell];
        if (biome >= 0)
        {
            r = Math.Max(r, _biomeRichness[rule][biome]);
        }

        if (_cropPlusOne != null && _cropPlusOne[cell] > 0)
        {
            r = Math.Max(r, _rules.Goods[rule].Farmland);
        }

        return r;
    }

    /// <summary>The goods that can be gathered in a cell now, with their richness, in rule order.</summary>
    public List<(string Good, double Richness)> ResourcesIn(int cell)
    {
        var list = new List<(string, double)>();
        for (int r = 0; r < _rules.Goods.Count; r++)
        {
            double richness = Richness(r, cell);
            if (richness > 0.0)
            {
                list.Add((_rules.Goods[r].Good, richness));
            }
        }

        return list;
    }

    public void Update(SimContext context)
    {
        if (_settlements == null)
        {
            return;
        }

        Stockpiles stockpiles = _stockpiles!;
        PopulationGroups groups = _groups!;
        for (int id = 0; id < _settlements.IdCount; id++)
        {
            if (!_settlements.IsAlive(id))
            {
                continue;
            }

            int cell = _settlementCell![id];
            double gatherers = 0.0;
            foreach (int occupation in _gatherers)
            {
                int group = groups.Find(cell, occupation);
                gatherers += group >= 0 ? groups.Size(group) : 0.0;
            }

            if (gatherers <= 0.0)
            {
                continue;
            }

            _prices!.Work(cell, context.Tick, _wanted, _price);
            double timeValue = _prices.Parameters.GatherTimeValue;
            for (int r = 0; r < _good.Length; r++)
            {
                ResourceRule rule = _rules.Goods[r];
                int good = _good[r];
                double wanted = _wanted[good] - stockpiles.Stock(cell, good);
                double richness = Richness(r, cell);
                double perGatherer = rule.PerPerson * richness;
                if (wanted <= 0.0 || perGatherer <= 0.0 || perGatherer * _price[good] < timeValue)
                {
                    continue;
                }

                stockpiles.Produce(cell, good, Math.Min(wanted, gatherers * perGatherer));
            }
        }
    }
}
