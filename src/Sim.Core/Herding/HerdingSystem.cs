using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Geo;
using Sim.Core.Goods;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

namespace Sim.Core.Herding;

/// <summary>
/// Herding (milestone 2, step 6). Each animal lives wild in one or more homelands (real or random,
/// as the run's crop homelands) and is first herded there by the people of a cell. A cell keeps a
/// set of animals. Herders live where the land is pasture and feed themselves from their herds'
/// meat and milk; they grow toward what the pasture feeds, using the land the cell's farmers leave,
/// and spread to neighbouring pasture. Foragers in a cell with animals take up herding, and so do
/// farmers where herds would feed more people than crops do. Animals also pass from village to
/// village among farmers, who keep a few for wool and hides. Runs once a year, after farming.
/// </summary>
public sealed class HerdingSystem : ISimSystem
{
    public const string SystemName = "herding";

    /// <summary>Bit set of the animals kept in each cell.</summary>
    public const string AnimalsField = "herding.animals";

    /// <summary>Herders the cell's pasture would feed if farmers used none of it (best herded animal kept there).</summary>
    public const string CapacityField = "herding.capacity";

    /// <summary>Bit set of the animal homelands (by index in livestock.json order) each cell lies in.</summary>
    public const string WildField = "herding.wild";
    public const string OriginTableName = "herding.origins";
    public const string HomelandTableName = "herding.homelands";

    private readonly HerdingParameters _parameters;
    private readonly FarmingParameters _farming;
    private readonly double[] _pasture;
    private readonly int _forager;
    private readonly int _farmer;
    private readonly int _herder;
    private readonly int _herdedMask;
    private readonly int _woolMask;
    private readonly int _herdedWoolMask;

    // Saved state.
    private int[] _animals = Array.Empty<int>();
    private double[] _capacity = Array.Empty<double>();
    private int[] _wild = Array.Empty<int>();
    private EntityTable? _origins;
    private IntColumn? _originAnimal;
    private IntColumn? _originHomeland;
    private IntColumn? _originCell;
    private IntColumn? _originTick;
    private EntityTable? _homelands;
    private IntColumn? _homelandAnimal;
    private IntColumn? _homelandIndex;
    private IntColumn? _homelandCell;

    // Read from other systems.
    private double[] _temperatureShift = Array.Empty<double>();
    private double[] _rainfallFactor = Array.Empty<double>();
    private int[] _biome = Array.Empty<int>();

    // Per animal, per biome: whether it cannot be kept there (livestock.json "notIn").
    private readonly bool[][] _notIn;
    private double[] _foragerCapacity = Array.Empty<double>();
    private int[] _crop = Array.Empty<int>();
    private double[] _farmCapacity = Array.Empty<double>();
    private double[] _farmLandShare = Array.Empty<double>();
    private int[]? _settlementPlusOne;
    private PopulationGroups? _groups;
    private Stockpiles? _stockpiles;
    private int _meat = -1;
    private int _wool = -1;
    private int _hides = -1;

    // Technologies herders and farmers use (null when the simulation has no technology).
    private TechnologyUse? _technology;
    private TechnologyEffect[] _densityEffects = Array.Empty<TechnologyEffect>();
    private TechnologyEffect[] _woolEffects = Array.Empty<TechnologyEffect>();
    private TechnologyEffect[] _spreadEffects = Array.Empty<TechnologyEffect>();

    // Animals kept through a technology (horses), with that technology's index; -1 where the
    // simulation has no such technology.
    private int[] _tamedBy = Array.Empty<int>();
    private int[] _farmingSincePlusOne = Array.Empty<int>();

    // Derived from the map when initializing.
    private WorldMap? _map;
    private int[] _cells = Array.Empty<int>();
    private double[] _baseWarmest = Array.Empty<double>();
    private double[] _baseColdest = Array.Empty<double>();
    private double[] _baseMean = Array.Empty<double>();
    private double[] _baseRain = Array.Empty<double>();
    private double[] _baseDriest = Array.Empty<double>();
    private double[] _summerShare = Array.Empty<double>();
    private double[] _riverWater = Array.Empty<double>();

    public HerdingSystem(HerdingParameters parameters, LivestockSet livestock, FarmingParameters farming, BiomeSet biomes, OccupationSet occupations)
    {
        _parameters = parameters;
        _farming = farming;
        Livestock = livestock;
        _forager = occupations.IndexOf(OccupationSet.Forager);
        _farmer = occupations.IndexOf(OccupationSet.Farmer);
        _herder = occupations.IndexOf(OccupationSet.Herder);
        _pasture = new double[biomes.Count];
        foreach (KeyValuePair<string, double> pair in parameters.Pasture.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            _pasture[biomes.IndexOf(pair.Key)] = pair.Value;
        }

        _notIn = livestock.Animals.Select(animal =>
        {
            var excluded = new bool[biomes.Count];
            foreach (string name in animal.NotIn)
            {
                excluded[biomes.IndexOf(name)] = true;
            }

            return excluded;
        }).ToArray();
        foreach (Animal animal in livestock.Animals)
        {
            int bit = 1 << animal.Index;
            _herdedMask |= animal.Herded ? bit : 0;
            _woolMask |= animal.Wool ? bit : 0;
            _herdedWoolMask |= animal.Herded && animal.Wool ? bit : 0;
        }
    }

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public LivestockSet Livestock { get; }

    public static HerdingSystem CreateDefault(BiomeSet biomes, OccupationSet occupations) =>
        new HerdingSystem(HerdingParameters.LoadDefault(), LivestockSet.LoadDefault(), FarmingParameters.LoadDefault(), biomes, occupations);

    public void Register(WorldState state)
    {
        _animals = state.AddIntField(AnimalsField);
        _capacity = state.AddDoubleField(CapacityField);
        _wild = state.AddIntField(WildField);

        _origins = state.AddTable(OriginTableName);
        _originAnimal = _origins.AddIntColumn("animal");
        _originHomeland = _origins.AddIntColumn("homeland");
        _originCell = _origins.AddIntColumn("cell");
        _originTick = _origins.AddIntColumn("tick");

        _homelands = state.AddTable(HomelandTableName);
        _homelandAnimal = _homelands.AddIntColumn("animal");
        _homelandIndex = _homelands.AddIntColumn("homeland");
        _homelandCell = _homelands.AddIntColumn("cell");
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        WorldState state = context.State;
        _map = map;
        _temperatureShift = state.DoubleField(ClimateSystem.TemperatureShiftField);
        _rainfallFactor = state.DoubleField(ClimateSystem.RainfallFactorField);
        _biome = state.IntField(ClimateSystem.BiomeField);
        _foragerCapacity = state.DoubleField(ForagerSystem.CapacityField);
        _crop = state.IntField(FarmingSystem.CropField);
        _farmCapacity = state.DoubleField(FarmingSystem.CapacityField);
        _farmLandShare = state.DoubleField(FarmingSystem.LandShareField);
        _farmingSincePlusOne = state.IntField(FarmingSystem.SinceField);
        _groups = new PopulationGroups(state);
        if (state.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(state, GoodSet.LoadDefault());
            _meat = _stockpiles.Goods.IndexOf("Meat and milk");
            _wool = _stockpiles.Goods.IndexOf("Wool");
            _hides = _stockpiles.Goods.IndexOf("Hides");
        }

        if (state.HasField(SettlementSystem.SettlementField))
        {
            _settlementPlusOne = state.IntField(SettlementSystem.SettlementField);
        }

        int count = map.CellCount;
        _baseWarmest = new double[count];
        _baseColdest = new double[count];
        _baseMean = new double[count];
        _baseRain = new double[count];
        _baseDriest = new double[count];
        _summerShare = new double[count];
        _riverWater = new double[count];
        var cells = new List<int>();
        Span<double> temperature = stackalloc double[12];
        Span<double> rainfall = stackalloc double[12];
        for (int cell = 0; cell < count; cell++)
        {
            if (!map.IsLand(cell) || !map.HasClimate(cell))
            {
                continue;
            }

            for (int month = 0; month < 12; month++)
            {
                temperature[month] = map.TemperatureC(cell, month);
                rainfall[month] = map.RainfallMm(cell, month);
            }

            ClimateSummary baseline = ClimateSummary.FromMonths(temperature, rainfall, map.Grid.Latitude(cell) >= 0.0);
            _baseWarmest[cell] = baseline.Warmest;
            _baseColdest[cell] = baseline.Coldest;
            _baseMean[cell] = baseline.Mean;
            _baseRain[cell] = baseline.Rain;
            _baseDriest[cell] = baseline.Driest;
            _summerShare[cell] = baseline.SummerShare;
            _riverWater[cell] = map.River(cell) switch
            {
                RiverSize.Major => _farming.MajorRiverWater,
                RiverSize.Minor => _farming.MinorRiverWater,
                _ => 0.0,
            };
            cells.Add(cell);
        }

        _cells = cells.ToArray();
        _technology = TechnologyUse.TryCreate(state, map);
        if (_technology != null)
        {
            _densityEffects = _technology.WithEffect(TechnologySet.HerdDensity);
            _woolEffects = _technology.WithEffect(TechnologySet.Wool);
            _spreadEffects = _technology.WithEffect(TechnologySet.HerderSpread);
        }

        _tamedBy = Livestock.Animals
            .Select(a => a.TamedBy.Length == 0 || _technology == null ? -1 : _technology.Technologies.IndexOf(a.TamedBy))
            .ToArray();

        PlaceHomelands(context);
    }

    public void Update(SimContext context)
    {
        Rng rng = context.Rng;
        Invent(context.Tick, rng);
        Tame(context.Tick);
        foreach (int cell in _cells)
        {
            if (_animals[cell] != 0)
            {
                Tend(cell);
            }
        }

        Spread(rng);
    }

    /// <summary>How well an animal does in a land cell's current climate, 0 to 1.</summary>
    public double Suitability(int cell, int animal)
    {
        if (_biome[cell] < 0 || _notIn[animal][_biome[cell]])
        {
            return 0.0;
        }

        double shift = _temperatureShift[cell];
        double factor = _rainfallFactor[cell];
        var climate = new ClimateSummary(
            _baseMean[cell] + shift,
            _baseWarmest[cell] + shift,
            _baseColdest[cell] + shift,
            _baseRain[cell] * factor,
            _baseDriest[cell] * factor,
            _summerShare[cell]);
        return Livestock.Get(animal).Suitability(climate, climate.Rain + _riverWater[cell]);
    }

    /// <summary>How much of a cell's land is pasture herders can live on (0 below the minimum).</summary>
    public double Pasture(int cell)
    {
        int biome = _biome[cell];
        double pasture = biome < 0 ? 0.0 : _pasture[biome];
        return pasture >= _parameters.MinPasture ? pasture : 0.0;
    }

    /// <summary>The herded animal in the mask that does best in the cell, with its suitability, or (-1, 0).</summary>
    public (int Animal, double Suitability) BestHerded(int cell, int mask)
    {
        int best = -1;
        double bestSuitability = 0.0;
        mask &= _herdedMask;
        for (int animal = 0; mask != 0; animal++, mask >>= 1)
        {
            if ((mask & 1) != 0)
            {
                double s = Suitability(cell, animal);
                if (s > bestSuitability)
                {
                    best = animal;
                    bestSuitability = s;
                }
            }
        }

        return (best, bestSuitability);
    }

    /// <summary>
    /// Herders a cell would feed with this suitability if farmers used none of its land, herded by
    /// a group with its technology (-1: none; the group may live in a neighbouring cell).
    /// </summary>
    private double FullCapacity(int cell, double suitability, int group) =>
        _map!.Grid.AreaKm2(cell) * _parameters.HerdDensity * Pasture(cell) * suitability * Factor(group, cell, _densityEffects);

    private double Factor(int group, int cell, TechnologyEffect[] effects) =>
        _technology == null || group < 0 ? 1.0 : _technology.Factor(group, cell, effects);

    /// <summary>Every first herding of an animal in one of its homelands: animal, homeland, cell and tick.</summary>
    public IReadOnlyList<(int Animal, int Homeland, int Cell, long Tick)> Origins()
    {
        var list = new List<(int, int, int, long)>();
        for (int id = 0; id < _origins!.IdCount; id++)
        {
            list.Add((_originAnimal![id], _originHomeland![id], _originCell![id], _originTick![id]));
        }

        return list;
    }

    /// <summary>The centre cell of each animal homeland in this run.</summary>
    public IReadOnlyList<(int Animal, int Homeland, int Cell)> Homelands()
    {
        var list = new List<(int, int, int)>();
        for (int id = 0; id < _homelands!.IdCount; id++)
        {
            list.Add((_homelandAnimal![id], _homelandIndex![id], _homelandCell![id]));
        }

        return list;
    }

    /// <summary>Total herders in the world.</summary>
    public double WorldHerders() => _groups?.WorldTotal(_herder) ?? 0.0;

    private double SizeOf(int cell, int occupation)
    {
        int group = _groups!.Find(cell, occupation);
        return group >= 0 ? _groups.Size(group) : 0.0;
    }

    // Moves people from one of a cell's groups to another, creating the target group if needed.
    private void Move(int cell, int from, int to, double people)
    {
        if (people <= 0.0)
        {
            return;
        }

        PopulationGroups groups = _groups!;
        int target = groups.FindOrAdd(cell, to);
        int source = groups.Find(cell, from);
        if (source < 0)
        {
            return;
        }

        double size = groups.Size(source);
        people = Math.Min(people, size);
        double food = groups.Food(source);
        groups.SetSize(source, size - people);
        target = groups.Find(cell, to);
        groups.AddPeople(target, people, food, from: source);
    }

    // One cell's year: people take up herding, herders eat and grow, and wool and hides go to a store.
    private void Tend(int cell)
    {
        PopulationGroups groups = _groups!;
        int mask = _animals[cell];
        (_, double suitability) = BestHerded(cell, mask);
        double full = FullCapacity(cell, suitability, groups.Find(cell, _herder));
        _capacity[cell] = full;
        double landShare = _crop[cell] > 0 ? Math.Clamp(_farmLandShare[cell], 0.0, 1.0) : 0.0;
        double usable = full * (1.0 - landShare);
        double farmCapacity = _crop[cell] > 0 ? _farmCapacity[cell] : 0.0;

        // Foragers take up herding while the pasture has room; farmers where herds feed more people.
        double herders = SizeOf(cell, _herder);
        if (usable > herders)
        {
            Move(cell, _forager, _herder, _parameters.ConversionRate * SizeOf(cell, _forager));
        }

        double farmers = SizeOf(cell, _farmer);
        if (full > farmCapacity && farmers > 0.0)
        {
            Move(cell, _farmer, _herder, _parameters.ConversionRate * farmers);
        }

        herders = SizeOf(cell, _herder);
        if (herders > 0.0)
        {
            int group = groups.Find(cell, _herder);
            double eaten;
            if (usable <= 0.0)
            {
                // The pasture is gone (farmers took it all, or the climate turned): back to foraging.
                eaten = 0.0;
                Move(cell, _herder, _forager, herders);
            }
            else if (herders <= usable)
            {
                eaten = herders;
                groups.SetFood(group, 1.0);
                groups.SetSize(group, herders + _parameters.GrowthRate * herders * (1.0 - herders / usable));
            }
            else
            {
                eaten = usable;
                double food = usable / herders;
                double shortfall = 1.0 - food;
                groups.SetFood(group, food);
                groups.SetSize(group, herders - _parameters.DeathShare * shortfall * herders);
                Move(cell, _herder, _forager, _parameters.LeaveShare * shortfall * herders);
            }

            _stockpiles?.ProduceAndConsume(_meat, eaten, eaten);
        }

        if (_stockpiles == null || _settlementPlusOne == null)
        {
            return;
        }

        // Herders bring wool and hides to a settlement in their own or a neighbouring cell.
        herders = SizeOf(cell, _herder);
        int store = StoreFor(cell);
        if (herders > 0.0 && store >= 0)
        {
            if ((mask & _herdedWoolMask) != 0)
            {
                double wool = Factor(groups.Find(cell, _herder), cell, _woolEffects);
                _stockpiles.Produce(store, _wool, herders * _parameters.WoolPerHerder * wool);
            }

            _stockpiles.Produce(store, _hides, herders * _parameters.HidesPerHerder);
        }

        // Farmers keep a few animals around their own settlement.
        farmers = SizeOf(cell, _farmer);
        if (farmers > 0.0 && _settlementPlusOne[cell] > 0)
        {
            if ((mask & _woolMask) != 0)
            {
                double wool = Factor(groups.Find(cell, _farmer), cell, _woolEffects);
                _stockpiles.Produce(cell, _wool, farmers * _parameters.WoolPerFarmer * wool);
            }

            _stockpiles.Produce(cell, _hides, farmers * _parameters.HidesPerFarmer);
        }
    }

    private int StoreFor(int cell)
    {
        if (_settlementPlusOne![cell] > 0)
        {
            return cell;
        }

        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            if (_settlementPlusOne[other] > 0)
            {
                return other;
            }
        }

        return -1;
    }

    // Herders move onto neighbouring pasture, and animals pass to neighbouring farming cells.
    // New animals are recorded only after every cell has been visited, so they move one cell a year.
    private void Spread(Rng rng)
    {
        PopulationGroups groups = _groups!;
        var gained = new List<(int Cell, int Animal)>();
        foreach (int cell in _cells)
        {
            int mask = _animals[cell];
            if (mask == 0)
            {
                continue;
            }

            double herders = SizeOf(cell, _herder);
            (int herded, double suitability) = BestHerded(cell, mask);
            int herderGroup = groups.Find(cell, _herder);
            double usable = FullCapacity(cell, suitability, herderGroup) * (1.0 - (_crop[cell] > 0 ? Math.Clamp(_farmLandShare[cell], 0.0, 1.0) : 0.0));
            bool herdersSpread = herded >= 0 && herders > 0.0 && usable > 0.0 && herders >= _parameters.SpreadWhenFull * usable;
            bool keepers = herders > 0.0 || SizeOf(cell, _farmer) > 0.0;
            if (!keepers)
            {
                continue;
            }

            foreach (int other in _map!.Grid.Neighbours(cell))
            {
                if (!_map.IsLand(other) || _biome[other] < 0)
                {
                    continue;
                }

                // Herders move onto pasture where nobody herds yet and herds would feed more than crops.
                if (herdersSpread && SizeOf(other, _herder) <= 0.0 && Pasture(other) > 0.0)
                {
                    double s = Suitability(other, herded);
                    double farmCapacity = _crop[other] > 0 ? _farmCapacity[other] : 0.0;
                    if (s >= _parameters.MinSpreadSuitability && FullCapacity(other, s, herderGroup) > farmCapacity
                        && rng.Chance(Math.Min(1.0, _parameters.SpreadChance * Factor(herderGroup, cell, _spreadEffects))))
                    {
                        double moving = _parameters.SpreadShare * SizeOf(cell, _herder);
                        double food = groups.Food(groups.Find(cell, _herder));
                        int target = groups.FindOrAdd(other, _herder);
                        int source = groups.Find(cell, _herder);
                        groups.SetSize(source, groups.Size(source) - moving);
                        groups.AddPeople(target, moving, food, from: source);
                        gained.Add((other, herded));
                    }
                }

                // Animals pass to farmers in neighbouring cells.
                if (_crop[other] > 0 && SizeOf(other, _farmer) > 0.0)
                {
                    int missing = mask & ~_animals[other];
                    for (int animal = 0; missing != 0; animal++, missing >>= 1)
                    {
                        if ((missing & 1) != 0 && Suitability(other, animal) >= _parameters.MinSpreadSuitability && rng.Chance(_parameters.SpreadChance))
                        {
                            gained.Add((other, animal));
                        }
                    }
                }
            }
        }

        foreach ((int cell, int animal) in gained)
        {
            _animals[cell] |= 1 << animal;
        }
    }

    // Animals are first herded in their homelands where farming has stood nearby long enough
    // (livestock.json "afterFarmingYears"): herded animals by the people of a cell on pasture or
    // beside farmland, farm animals (pigs) by farmers in the cell itself. In a homeland marked for
    // foragers (eastern Sahel cattle), foragers on pasture far from farmers may also, less readily.
    // Animals kept through a technology (horses) are not first herded this way (see Tame).
    private void Invent(long tick, Rng rng)
    {
        int invented = 0;
        for (int id = 0; id < _origins!.IdCount; id++)
        {
            invented |= 1 << _originHomeland![id];
        }

        foreach (int cell in _cells)
        {
            int wild = _wild[cell] & ~invented;
            if (wild == 0)
            {
                continue;
            }

            double foragers = SizeOf(cell, _forager);
            double farmers = SizeOf(cell, _farmer);
            if (foragers <= 0.0 && farmers <= 0.0)
            {
                continue;
            }

            for (int homeland = 0; wild != 0; homeland++, wild >>= 1)
            {
                if ((wild & 1) == 0)
                {
                    continue;
                }

                AnimalHomeland place = Livestock.Homelands[homeland];
                int animal = place.Animal;
                Animal kind = Livestock.Get(animal);
                if (kind.TamedBy.Length > 0)
                {
                    continue;
                }

                double fullness;
                if (!kind.Herded)
                {
                    fullness = farmers > 0.0 && FarmingStood(cell, place.AfterFarmingYears, tick, neighbours: false) ? 1.0 : 0.0;
                }
                else if (FarmingStood(cell, place.AfterFarmingYears, tick, neighbours: true))
                {
                    fullness = 1.0;
                }
                else if (place.Foragers && !FarmersNear(cell))
                {
                    double foragerCapacity = _foragerCapacity[cell];
                    fullness = Pasture(cell) > 0.0 && foragers > 0.0 && foragerCapacity > 0.0
                        ? _parameters.ForagerInventionFactor * Math.Min(1.0, foragers / foragerCapacity)
                        : 0.0;
                }
                else
                {
                    fullness = 0.0;
                }

                double suitability = Suitability(cell, animal);
                if (fullness <= 0.0 || !rng.Chance(_parameters.InventionChance * suitability * fullness))
                {
                    continue;
                }

                RecordOrigin(animal, homeland, cell, tick);
                invented |= 1 << homeland;
                _animals[cell] |= 1 << animal;
                if (kind.Herded && Pasture(cell) > 0.0)
                {
                    int from = foragers > 0.0 ? _forager : _farmer;
                    Move(cell, from, _herder, _parameters.FirstHerderShare * SizeOf(cell, from));
                }
            }
        }
    }

    private void RecordOrigin(int animal, int homeland, int cell, long tick)
    {
        int origin = _origins!.Add();
        _originAnimal![origin] = animal;
        _originHomeland![origin] = homeland;
        _originCell![origin] = cell;
        _originTick![origin] = (int)tick;
    }

    // Whether farming has stood in the cell (or a neighbour) for at least this many years.
    private bool FarmingStood(int cell, double years, long tick, bool neighbours)
    {
        long ticks = (long)Math.Round(years * GameDate.MonthsPerYear);
        if (Stood(cell))
        {
            return true;
        }

        if (neighbours)
        {
            foreach (int other in _map!.Grid.Neighbours(cell))
            {
                if (Stood(other))
                {
                    return true;
                }
            }
        }

        return false;

        bool Stood(int c) => _farmingSincePlusOne[c] > 0 && SizeOf(c, _farmer) > 0.0 && tick - (_farmingSincePlusOne[c] - 1) >= ticks;
    }

    // Animals kept through a technology (horses): where they live wild and some group in the cell
    // knows the technology, the cell keeps them; from there they spread with herders and to
    // farmers like any animal. The first such cell of each homeland is recorded as its origin.
    private void Tame(long tick)
    {
        if (_technology == null)
        {
            return;
        }

        int recorded = 0;
        for (int id = 0; id < _origins!.IdCount; id++)
        {
            recorded |= 1 << _originHomeland![id];
        }

        for (int animal = 0; animal < _tamedBy.Length; animal++)
        {
            int technology = _tamedBy[animal];
            if (technology < 0)
            {
                continue;
            }

            Animal kind = Livestock.Get(animal);
            int homelands = 0;
            for (int h = kind.FirstHomeland; h < kind.FirstHomeland + kind.HomelandCount; h++)
            {
                homelands |= 1 << h;
            }

            int bit = 1 << animal;
            foreach (int cell in _cells)
            {
                int wild = _wild[cell] & homelands;
                if (wild == 0 || (_animals[cell] & bit) != 0 || !_technology.KnownInCell(cell, technology)
                    || Suitability(cell, animal) < _parameters.MinSpreadSuitability)
                {
                    continue;
                }

                _animals[cell] |= bit;
                for (int h = kind.FirstHomeland; h < kind.FirstHomeland + kind.HomelandCount; h++)
                {
                    if ((wild & (1 << h)) != 0 && (recorded & (1 << h)) == 0)
                    {
                        RecordOrigin(animal, h, cell, tick);
                        recorded |= 1 << h;
                    }
                }
            }
        }
    }

    private bool FarmersNear(int cell)
    {
        if (SizeOf(cell, _farmer) > 0.0)
        {
            return true;
        }

        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            if (SizeOf(other, _farmer) > 0.0)
            {
                return true;
            }
        }

        return false;
    }

    // Records each homeland's centre and marks the cells where its animal lives wild.
    private void PlaceHomelands(SimContext context)
    {
        WorldMap map = _map!;
        GeodesicGrid grid = map.Grid;
        CropHomelands mode = RunSettings.ReadFrom(context.State).CropHomelands;
        PopulationGroups groups = _groups!;

        foreach (AnimalHomeland homeland in Livestock.Homelands)
        {
            Animal animal = Livestock.Get(homeland.Animal);
            double Fit(int cell) => Suitability(cell, animal.Index) * (animal.Herded ? Pasture(cell) : 1.0);
            int centre;
            if (mode == CropHomelands.Real)
            {
                centre = grid.FindCell(homeland.Lat, homeland.Lon);
            }
            else
            {
                var candidates = new List<int>();
                foreach (int cell in _cells)
                {
                    if (groups.Find(cell, _forager) >= 0 && Fit(cell) >= _parameters.RandomHomelandMinSuitability)
                    {
                        candidates.Add(cell);
                    }
                }

                if (candidates.Count == 0)
                {
                    continue;
                }

                centre = candidates[context.Rng.NextInt(candidates.Count)];
            }

            int row = _homelands!.Add();
            _homelandAnimal![row] = animal.Index;
            _homelandIndex![row] = homeland.Index;
            _homelandCell![row] = centre;

            foreach (int cell in _cells)
            {
                if (grid.DistanceKm(centre, cell) <= homeland.RadiusKm && Suitability(cell, animal.Index) >= _parameters.WildMinSuitability)
                {
                    _wild[cell] |= 1 << homeland.Index;
                }
            }
        }
    }
}
