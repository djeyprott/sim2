using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.Map;
using Sim.Core.People;

namespace Sim.Core.Technologies;

/// <summary>
/// Whether a cell meets a technology's conditions (river, dry climate, coast, terrain, animals
/// kept, crops farmed, and the goods or animals it works with near), not counting who knows what,
/// and for discovery whether farming has stood long enough. Shared by the technology system and the
/// systems that apply technologies' effects, so both use the same rules. The technology system
/// also counts goods a trade partner holds (see <see cref="TechnologySystem.CellAllows"/>).
/// </summary>
public sealed class TechnologyConditions
{
    private readonly TechnologySet _technologies;
    private readonly WorldMap _map;
    private readonly double[] _rainfallFactor;
    private readonly int[]? _animals;
    private readonly bool[] _coast;
    private readonly double[] _baseRain;
    private readonly int[] _animalMask;

    // Per technology: the crops (index + 1 as in the crop field) one of which must be farmed in the cell (empty: none needed).
    private readonly int[][] _cropsPlusOne;
    private readonly int[]? _cropPlusOne;

    // Per technology with near goods: the cells where one of them can be gathered, and the goods.
    private readonly bool[]?[] _goodsSource;
    private readonly int[][] _nearGoods;
    private readonly Stockpiles? _stockpiles;

    // Per technology: the bits of the animal homelands one of which must hold the cell (0: none needed).
    private readonly int[] _wildMask;

    // Per technology: the bits (in the kept-animals field) of its near animals.
    private readonly int[] _animalsOf;
    private readonly int[]? _wild;

    // When farming started in each cell (tick + 1, 0 for none), for technologies that need long-settled farming.
    private readonly int[]? _farmingSincePlusOne;
    private readonly PopulationGroups _groups;
    private readonly int _farmer;

    public TechnologyConditions(WorldState state, WorldMap map, TechnologySet technologies)
    {
        _technologies = technologies;
        _map = map;
        _rainfallFactor = state.DoubleField(ClimateSystem.RainfallFactorField);
        _animals = state.HasField(HerdingSystem.AnimalsField) ? state.IntField(HerdingSystem.AnimalsField) : null;

        LivestockSet livestock = LivestockSet.LoadDefault();
        _wild = state.HasField(HerdingSystem.WildField) ? state.IntField(HerdingSystem.WildField) : null;
        _farmingSincePlusOne = state.HasField("farming.since") ? state.IntField("farming.since") : null;
        _cropPlusOne = state.HasField(Farming.FarmingSystem.CropField) ? state.IntField(Farming.FarmingSystem.CropField) : null;
        Farming.CropSet? crops = technologies.Technologies.Any(t => t.Crops.Count > 0) ? Farming.CropSet.LoadDefault() : null;
        _cropsPlusOne = technologies.Technologies.Select(t => t.Crops.Select(c => crops!.IndexOf(c) + 1).ToArray()).ToArray();
        _groups = new PopulationGroups(state);
        _farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        _wildMask = technologies.Technologies.Select(t =>
        {
            int mask = 0;
            foreach (string name in t.NearWild)
            {
                Animal animal = livestock.Get(livestock.IndexOf(name));
                for (int h = animal.FirstHomeland; h < animal.FirstHomeland + animal.HomelandCount; h++)
                {
                    mask |= 1 << h;
                }
            }

            return mask;
        }).ToArray();
        _animalMask = technologies.Technologies.Select(t =>
        {
            int mask = 0;
            foreach (string name in t.Animals)
            {
                mask |= 1 << livestock.IndexOf(name);
            }

            return mask;
        }).ToArray();
        _animalsOf = technologies.Technologies.Select(t =>
        {
            int mask = 0;
            foreach (string name in t.NearWild)
            {
                mask |= 1 << livestock.IndexOf(name);
            }

            return mask;
        }).ToArray();

        _stockpiles = state.HasTable(Stockpiles.LedgerTableName) ? new Stockpiles(state, GoodSet.LoadDefault()) : null;
        _goodsSource = new bool[]?[technologies.Count];
        _nearGoods = new int[technologies.Count][];
        ResourceRules? resources = technologies.Technologies.Any(t => t.NearGoods.Count > 0) ? ResourceRules.LoadDefault() : null;
        GoodSet goods = GoodSet.LoadDefault();
        foreach (Technology t in technologies.Technologies)
        {
            _nearGoods[t.Index] = t.NearGoods.Select(goods.IndexOf).ToArray();
            if (t.NearGoods.Count == 0)
            {
                continue;
            }

            var source = new bool[map.CellCount];
            foreach (string name in t.NearGoods)
            {
                ResourceRule? rule = resources!.Goods.FirstOrDefault(r => r.Good == name);
                if (rule == null)
                {
                    continue;
                }

                double[] richness = ResourceSystem.FixedRichness(map, rule, resources.DryRainMm);
                for (int cell = 0; cell < source.Length; cell++)
                {
                    source[cell] |= richness[cell] > 0.0;
                }
            }

            _goodsSource[t.Index] = source;
        }

        int count = map.CellCount;
        _coast = new bool[count];
        _baseRain = new double[count];
        for (int cell = 0; cell < count; cell++)
        {
            if (!map.IsLand(cell))
            {
                continue;
            }

            foreach (int other in map.Grid.Neighbours(cell))
            {
                _coast[cell] |= map.IsSea(other);
            }

            if (map.HasClimate(cell))
            {
                for (int month = 0; month < 12; month++)
                {
                    _baseRain[cell] += map.RainfallMm(cell, month);
                }
            }
        }
    }

    // Whether farming has stood (with farmers) in the cell or a neighbour for at least this many months.
    private bool FarmingStood(int cell, long months, long tick)
    {
        if (_farmingSincePlusOne == null)
        {
            return false;
        }

        if (Stood(cell))
        {
            return true;
        }

        foreach (int other in _map.Grid.Neighbours(cell))
        {
            if (Stood(other))
            {
                return true;
            }
        }

        return false;

        bool Stood(int c) => _farmingSincePlusOne[c] > 0 && tick - (_farmingSincePlusOne[c] - 1) >= months
            && _groups.Find(c, _farmer) is int g && g >= 0 && _groups.Size(g) > 0.0;
    }

    /// <summary>Whether a cell meets a technology's conditions (not counting who knows what), with only the cell's own goods counting as near.</summary>
    public bool Allows(int cell, int technology) => AllowsBesidesSupply(cell, technology) && SuppliedHere(cell, technology);

    /// <summary>Whether a technology needs goods or animals near (see <see cref="SuppliedHere"/>).</summary>
    public bool NeedsSupply(int technology) => _goodsSource[technology] != null || _wildMask[technology] != 0;

    /// <summary>
    /// Whether the goods or animals a technology works with are near the cell: one of its goods
    /// gatherable in the cell or in its store, or one of its animals wild or kept there (true when
    /// it needs none).
    /// </summary>
    public bool SuppliedHere(int cell, int technology)
    {
        int wild = _wildMask[technology];
        if (wild != 0)
        {
            int kept = _animalsOf[technology];
            return (_wild != null && (_wild[cell] & wild) != 0) || (_animals != null && (_animals[cell] & kept) != 0);
        }

        return GoodsNear(cell, technology);
    }

    private bool GoodsNear(int cell, int technology)
    {
        bool[]? source = _goodsSource[technology];
        if (source == null || source[cell])
        {
            return true;
        }

        if (_stockpiles != null)
        {
            foreach (int good in _nearGoods[technology])
            {
                if (_stockpiles.Stock(cell, good) > 0.0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a cell meets a technology's conditions other than the goods or animals it works with.</summary>
    public bool AllowsBesidesSupply(int cell, int technology)
    {
        Technology t = _technologies.Get(technology);
        if (t.River && _map.River(cell) == RiverSize.None)
        {
            return false;
        }

        if (t.MaxRainMm > 0.0 && _baseRain[cell] * _rainfallFactor[cell] > t.MaxRainMm)
        {
            return false;
        }

        if (t.Coast && !_coast[cell])
        {
            return false;
        }

        if (t.Terrain.Count > 0 && !t.Terrain.Contains(_map.Terrain(cell)))
        {
            return false;
        }

        int[] crops = _cropsPlusOne[technology];
        if (crops.Length > 0 && (_cropPlusOne == null || Array.IndexOf(crops, _cropPlusOne[cell]) < 0))
        {
            return false;
        }

        int animals = _animalMask[technology];
        return animals == 0 || (_animals != null && (_animals[cell] & animals) != 0);
    }

    /// <summary>
    /// For discovery: whether one of the technology's near goods can be gathered in the cell or is
    /// in its store, one of its wild animals lives in the cell, and farming has stood in the cell
    /// or a neighbour as long as it needs (each true when it needs none).
    /// </summary>
    public bool NearForDiscovery(int cell, int technology, long tick) => NearForDiscovery(cell, technology, tick, goodsNearAnyway: false);

    /// <summary>As <see cref="NearForDiscovery(int, int, long)"/>, counting its goods as near when <paramref name="goodsNearAnyway"/> (held by a trade partner).</summary>
    public bool NearForDiscovery(int cell, int technology, long tick, bool goodsNearAnyway)
    {
        double settled = _technologies.Get(technology).SettledYears;
        if (settled > 0.0 && !FarmingStood(cell, (long)Math.Round(settled * GameDate.MonthsPerYear), tick))
        {
            return false;
        }

        int wild = _wildMask[technology];
        if (wild != 0 && (_wild == null || (_wild[cell] & wild) == 0))
        {
            return false;
        }

        return goodsNearAnyway || GoodsNear(cell, technology);
    }
}

/// <summary>
/// For the systems that apply technologies' effects (farming, herding, goods): which technologies
/// a group uses, and how much they change a number. A group uses a technology when at least
/// technology.json's "inUse" share of its people know it and its cell meets the technology's
/// conditions. Effects are listed per technology in technologies.json; several technologies with
/// the same effect multiply. Reads last year's knowledge (the technology system runs last).
/// </summary>
public sealed class TechnologyUse
{
    private readonly PopulationGroups _groups;
    private readonly DoubleColumn[] _knowledge;
    private readonly double _inUse;

    private TechnologyUse(WorldState state, WorldMap map, TechnologySet technologies, TechnologyParameters parameters, DoubleColumn[] knowledge)
    {
        Technologies = technologies;
        Conditions = new TechnologyConditions(state, map, technologies);
        _groups = new PopulationGroups(state);
        _knowledge = knowledge;
        _inUse = parameters.InUse;
    }

    public TechnologySet Technologies { get; }

    public TechnologyConditions Conditions { get; }

    /// <summary>
    /// The technologies of the content files, read from the world state's group knowledge, or null
    /// when the simulation has no technology (no knowledge columns), so effects are left out.
    /// </summary>
    public static TechnologyUse? TryCreate(WorldState state, WorldMap map)
    {
        if (!state.HasTable(PopulationGroups.TableName))
        {
            return null;
        }

        EntityTable table = state.Table(PopulationGroups.TableName);
        TechnologySet technologies = TechnologySet.LoadDefault(OccupationSet.LoadDefault());
        var columns = new DoubleColumn[technologies.Count];
        for (int t = 0; t < technologies.Count; t++)
        {
            string name = PopulationGroups.KnowledgeColumnPrefix + technologies.Get(t).Slug;
            if (!table.ColumnNames.Contains(name))
            {
                return null;
            }

            columns[t] = table.DoubleColumn(name);
        }

        return new TechnologyUse(state, map, technologies, TechnologyParameters.LoadDefault(), columns);
    }

    /// <summary>The technologies with an effect, and its size.</summary>
    public TechnologyEffect[] WithEffect(string effect) =>
        Technologies.Technologies
            .Where(t => t.Effects.ContainsKey(effect))
            .Select(t => new TechnologyEffect(t.Index, t.Effects[effect]))
            .ToArray();

    /// <summary>Whether at least the in-use share of a group's people know a technology (ignoring the cell).</summary>
    public bool Knows(int group, int technology) =>
        group >= 0 && _groups.IsAlive(group) && _knowledge[technology][group] >= _inUse;

    /// <summary>
    /// Whether some group in a cell, of an occupation that can use the technology, knows it (at
    /// least the in-use share), whatever the cell's conditions. Recipes go by this.
    /// </summary>
    public bool KnownInCell(int cell, int technology)
    {
        IReadOnlyList<bool> canUse = Technologies.Get(technology).CanUse;
        DoubleColumn knowledge = _knowledge[technology];
        foreach (int group in _groups.InCell(cell))
        {
            if (canUse[_groups.Occupation(group)] && _groups.Size(group) > 0.0 && knowledge[group] >= _inUse)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The product of the effect sizes of the technologies some group in a cell knows, where the
    /// cell meets their conditions (1 when none): for effects on a whole settlement, such as trade.
    /// </summary>
    public double CellFactor(int cell, TechnologyEffect[] effects)
    {
        double factor = 1.0;
        foreach (TechnologyEffect e in effects)
        {
            if (Conditions.Allows(cell, e.Technology) && KnownInCell(cell, e.Technology))
            {
                factor *= e.Size;
            }
        }

        return factor;
    }

    /// <summary>Whether a group uses a technology in a cell: it knows it, and the cell meets its conditions.</summary>
    public bool Uses(int group, int cell, int technology) => Knows(group, technology) && Conditions.Allows(cell, technology);

    /// <summary>
    /// The product of the effect sizes of the technologies a group uses in a cell (1 when none).
    /// The group may live in another cell (people carrying what they know to new land).
    /// </summary>
    public double Factor(int group, int cell, TechnologyEffect[] effects)
    {
        double factor = 1.0;
        foreach (TechnologyEffect e in effects)
        {
            if (Uses(group, cell, e.Technology))
            {
                factor *= e.Size;
            }
        }

        return factor;
    }

    /// <summary>The largest effect size among the technologies a group uses in a cell (0 when none).</summary>
    public double Largest(int group, int cell, TechnologyEffect[] effects)
    {
        double largest = 0.0;
        foreach (TechnologyEffect e in effects)
        {
            if (e.Size > largest && Uses(group, cell, e.Technology))
            {
                largest = e.Size;
            }
        }

        return largest;
    }
}

/// <summary>One technology's size of one effect.</summary>
public readonly record struct TechnologyEffect(int Technology, double Size);
