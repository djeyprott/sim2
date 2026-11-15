using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;
using Sim.Core.People;

namespace Sim.Core.Farming;

/// <summary>
/// Farming. Each crop grows wild in a homeland (real or random, chosen when the run starts) and
/// can first be farmed there by foragers. Farming cells grow one crop; the farmer group grows
/// toward what the harvest feeds, foragers in the cell take up farming, and farming spreads to
/// neighbouring cells where the crop grows. Harvests come once a year: in October in the north
/// and April in the south, with good and bad years shared across regions.
/// Runs every season, after the forager system.
/// </summary>
public sealed class FarmingSystem : ISimSystem
{
    public const string SystemName = "farming";
    public const string CropField = "farming.crop";
    public const string SinceField = "farming.since";
    public const string CapacityField = "farming.capacity";
    public const string LandShareField = "farming.landShare";
    public const string WildCropsField = "farming.wildCrops";
    public const string OriginTableName = "farming.origins";
    public const string HomelandTableName = "farming.homelands";

    private const int NorthernHarvestMonth = 9;  // October, counting January as 0
    private const int SouthernHarvestMonth = 3;  // April

    // Share of a cell's foragers who become its first farmers when a crop is first farmed there.
    private const double FirstFarmerShare = 0.1;

    private readonly FarmingParameters _parameters;
    private readonly int _forager;
    private readonly int _farmer;
    private readonly bool[] _noFarmingBiome;

    // Saved state.
    private int[] _cropPlusOne = Array.Empty<int>();
    private int[] _sincePlusOne = Array.Empty<int>();
    private double[] _capacity = Array.Empty<double>();
    private double[] _landShare = Array.Empty<double>();
    private int[] _wildCrops = Array.Empty<int>();
    private EntityTable? _origins;
    private IntColumn? _originCrop;
    private IntColumn? _originCell;
    private IntColumn? _originTick;
    private EntityTable? _homelands;
    private IntColumn? _homelandCrop;
    private IntColumn? _homelandCell;

    // Read from other systems.
    private double[] _temperatureShift = Array.Empty<double>();
    private double[] _rainfallFactor = Array.Empty<double>();
    private int[] _biome = Array.Empty<int>();
    private double[] _foragerCapacity = Array.Empty<double>();
    private PopulationGroups? _groups;

    // Derived from the map when initializing.
    private WorldMap? _map;
    private int[] _northCells = Array.Empty<int>();
    private int[] _southCells = Array.Empty<int>();
    private double[] _baseWarmest = Array.Empty<double>();
    private double[] _baseColdest = Array.Empty<double>();
    private double[] _baseMean = Array.Empty<double>();
    private double[] _baseRain = Array.Empty<double>();
    private double[] _baseDriest = Array.Empty<double>();
    private double[] _summerShare = Array.Empty<double>();
    private double[] _riverWater = Array.Empty<double>();
    private int[] _harvestRegion = Array.Empty<int>();
    private double[] _harvestFactor = Array.Empty<double>();

    public FarmingSystem(FarmingParameters parameters, CropSet crops, BiomeSet biomes, OccupationSet occupations)
    {
        _parameters = parameters;
        Crops = crops;
        _forager = occupations.IndexOf(OccupationSet.Forager);
        _farmer = occupations.IndexOf(OccupationSet.Farmer);
        _noFarmingBiome = new bool[biomes.Count];
        foreach (string name in parameters.NoFarmingBiomes)
        {
            _noFarmingBiome[biomes.IndexOf(name)] = true;
        }
    }

    public string Name => SystemName;

    public int IntervalMonths => 3;

    public CropSet Crops { get; }

    public static FarmingSystem CreateDefault(BiomeSet biomes, OccupationSet occupations) =>
        new FarmingSystem(FarmingParameters.LoadDefault(), CropSet.LoadDefault(), biomes, occupations);

    public void Register(WorldState state)
    {
        _cropPlusOne = state.AddIntField(CropField);
        _sincePlusOne = state.AddIntField(SinceField);
        _capacity = state.AddDoubleField(CapacityField);
        _landShare = state.AddDoubleField(LandShareField);
        _wildCrops = state.AddIntField(WildCropsField);

        _origins = state.AddTable(OriginTableName);
        _originCrop = _origins.AddIntColumn("crop");
        _originCell = _origins.AddIntColumn("cell");
        _originTick = _origins.AddIntColumn("tick");

        _homelands = state.AddTable(HomelandTableName);
        _homelandCrop = _homelands.AddIntColumn("crop");
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
        _groups = new PopulationGroups(state);

        int count = map.CellCount;
        _baseWarmest = new double[count];
        _baseColdest = new double[count];
        _baseMean = new double[count];
        _baseRain = new double[count];
        _baseDriest = new double[count];
        _summerShare = new double[count];
        _riverWater = new double[count];
        var north = new List<int>();
        var south = new List<int>();
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

            bool northern = map.Grid.Latitude(cell) >= 0.0;
            ClimateSummary baseline = ClimateSummary.FromMonths(temperature, rainfall, northern);
            _baseWarmest[cell] = baseline.Warmest;
            _baseColdest[cell] = baseline.Coldest;
            _baseMean[cell] = baseline.Mean;
            _baseRain[cell] = baseline.Rain;
            _baseDriest[cell] = baseline.Driest;
            _summerShare[cell] = baseline.SummerShare;
            _riverWater[cell] = map.River(cell) switch
            {
                RiverSize.Major => _parameters.MajorRiverWater,
                RiverSize.Minor => _parameters.MinorRiverWater,
                _ => 0.0,
            };
            (northern ? north : south).Add(cell);
        }

        _northCells = north.ToArray();
        _southCells = south.ToArray();

        GeodesicGrid regions = GeodesicGrid.Create(_parameters.HarvestRegionFrequency);
        _harvestRegion = new int[count];
        int hint = 0;
        for (int cell = 0; cell < count; cell++)
        {
            hint = regions.FindCellFrom(hint, map.Grid.Centre(cell));
            _harvestRegion[cell] = hint;
        }

        _harvestFactor = new double[regions.CellCount];
        PlaceHomelands(context);
    }

    public void Update(SimContext context)
    {
        int month = (int)(context.Tick % GameDate.MonthsPerYear);
        int[] cells;
        if (month == NorthernHarvestMonth)
        {
            cells = _northCells;
        }
        else if (month == SouthernHarvestMonth)
        {
            cells = _southCells;
        }
        else
        {
            return;
        }

        Rng rng = context.Rng;
        for (int region = 0; region < _harvestFactor.Length; region++)
        {
            _harvestFactor[region] = Math.Clamp(1.0 + _parameters.HarvestSpread * rng.NextGaussian(), _parameters.HarvestMin, _parameters.HarvestMax);
        }

        foreach (int cell in cells)
        {
            if (_cropPlusOne[cell] > 0)
            {
                Harvest(cell, rng);
            }
        }

        foreach (int cell in cells)
        {
            if (_cropPlusOne[cell] > 0)
            {
                Spread(cell, context.Tick, rng);
            }
        }

        Invent(cells, context.Tick, rng);
    }

    /// <summary>How well a crop grows in a land cell's current climate, 0 to 1.</summary>
    public double Suitability(int cell, int crop)
    {
        int biome = _biome[cell];
        if (biome < 0 || (_noFarmingBiome[biome] && _map!.River(cell) != RiverSize.Major))
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
        return Crops.Get(crop).Suitability(climate, climate.Rain + _riverWater[cell]);
    }

    /// <summary>Every first farming of a crop so far: which crop, where and when (tick).</summary>
    public IReadOnlyList<(int Crop, int Cell, long Tick)> Origins()
    {
        var list = new List<(int, int, long)>();
        for (int id = 0; id < _origins!.IdCount; id++)
        {
            list.Add((_originCrop![id], _originCell![id], _originTick![id]));
        }

        return list;
    }

    /// <summary>The centre cell of each crop's homeland in this run.</summary>
    public IReadOnlyList<(int Crop, int Cell)> Homelands()
    {
        var list = new List<(int, int)>();
        for (int id = 0; id < _homelands!.IdCount; id++)
        {
            list.Add((_homelandCrop![id], _homelandCell![id]));
        }

        return list;
    }

    /// <summary>Total farmers in the world.</summary>
    public double WorldFarmers() => _groups?.WorldTotal(_farmer) ?? 0.0;

    private double FarmCapacity(int cell, double suitability) =>
        _map!.Grid.AreaKm2(cell) * _parameters.FarmDensity * suitability
        * (_map.River(cell) == RiverSize.Major ? _parameters.MajorRiverYield : 1.0);

    private void Harvest(int cell, Rng rng)
    {
        PopulationGroups groups = _groups!;
        int crop = _cropPlusOne[cell] - 1;

        // Switch to a neighbour's crop if it grows clearly better here.
        double suitability = Suitability(cell, crop);
        int bestCrop = crop;
        double bestSuitability = suitability + _parameters.CropSwitchMargin;
        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            int otherCrop = _cropPlusOne[other] - 1;
            if (otherCrop >= 0 && otherCrop != crop)
            {
                double s = Suitability(cell, otherCrop);
                if (s > bestSuitability)
                {
                    bestCrop = otherCrop;
                    bestSuitability = s;
                }
            }
        }

        if (bestCrop != crop && rng.Chance(_parameters.CropSwitchChance))
        {
            crop = bestCrop;
            _cropPlusOne[cell] = crop + 1;
            suitability = Suitability(cell, crop);
        }

        double capacity = FarmCapacity(cell, suitability);
        _capacity[cell] = capacity;
        double harvest = capacity * _harvestFactor[_harvestRegion[cell]];

        int farmerGroup = groups.FindOrAdd(cell, _farmer);
        double farmers = groups.Size(farmerGroup);

        // Foragers in the cell take up farming.
        int foragerGroup = groups.Find(cell, _forager);
        if (foragerGroup >= 0 && capacity > 0.0)
        {
            double foragers = groups.Size(foragerGroup);
            double joining = _parameters.ConversionRate * foragers;
            groups.SetSize(foragerGroup, foragers - joining);
            farmers += joining;
        }

        double food = farmers <= 0.0 ? 1.0 : harvest <= 0.0 ? 0.0 : Math.Min(1.0, harvest / farmers);
        if (harvest > 0.0)
        {
            double change = _parameters.GrowthRate * farmers * (1.0 - farmers / harvest);
            farmers += Math.Max(change, -_parameters.MaxLoss * farmers);
        }
        else
        {
            farmers -= _parameters.MaxLoss * farmers;
        }

        if (farmers < 1.0 && capacity <= 0.0)
        {
            // The crop no longer grows here and the farmers are gone: the cell stops farming.
            if (foragerGroup < 0 && farmers > 0.0)
            {
                foragerGroup = groups.FindOrAdd(cell, _forager);
            }

            if (foragerGroup >= 0)
            {
                groups.AddPeople(foragerGroup, farmers, food);
            }

            groups.SetSize(farmerGroup, 0.0);
            _cropPlusOne[cell] = 0;
            _sincePlusOne[cell] = 0;
            _capacity[cell] = 0.0;
            _landShare[cell] = 0.0;
            return;
        }

        groups.SetSize(farmerGroup, Math.Max(0.0, farmers));
        groups.SetFood(farmerGroup, food);
        _landShare[cell] = capacity > 0.0 ? Math.Min(1.0, farmers / capacity) : 0.0;
    }

    private void Spread(int cell, long tick, Rng rng)
    {
        double capacity = _capacity[cell];
        PopulationGroups groups = _groups!;
        int farmerGroup = groups.Find(cell, _farmer);
        if (capacity <= 0.0 || farmerGroup < 0)
        {
            return;
        }

        double farmers = groups.Size(farmerGroup);
        if (farmers < _parameters.SpreadWhenFull * capacity)
        {
            return;
        }

        int crop = _cropPlusOne[cell] - 1;
        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            if (!_map.IsLand(other) || _cropPlusOne[other] > 0 || _biome[other] < 0)
            {
                continue;
            }

            double suitability = Suitability(other, crop);
            if (suitability < _parameters.MinSpreadSuitability || !rng.Chance(_parameters.SpreadChance))
            {
                continue;
            }

            double moving = _parameters.SpreadShare * farmers;
            farmers -= moving;
            groups.SetSize(farmerGroup, farmers);
            StartFarming(other, crop, tick, suitability);
            groups.AddPeople(groups.FindOrAdd(other, _farmer), moving, groups.Food(farmerGroup));
        }
    }

    private void Invent(int[] cells, long tick, Rng rng)
    {
        int invented = 0;
        for (int id = 0; id < _origins!.IdCount; id++)
        {
            invented |= 1 << _originCrop![id];
        }

        PopulationGroups groups = _groups!;
        foreach (int cell in cells)
        {
            int wild = _wildCrops[cell] & ~invented;
            if (wild == 0 || _cropPlusOne[cell] > 0)
            {
                continue;
            }

            int foragerGroup = groups.Find(cell, _forager);
            double foragerCapacity = _foragerCapacity[cell];
            if (foragerGroup < 0 || foragerCapacity <= 0.0)
            {
                continue;
            }

            double fullness = Math.Min(1.0, groups.Size(foragerGroup) / foragerCapacity);
            for (int crop = 0; crop < Crops.Count; crop++)
            {
                if ((wild & (1 << crop)) == 0)
                {
                    continue;
                }

                double suitability = Suitability(cell, crop);
                if (!rng.Chance(_parameters.InventionChance * suitability * fullness))
                {
                    continue;
                }

                int origin = _origins.Add();
                _originCrop![origin] = crop;
                _originCell![origin] = cell;
                _originTick![origin] = (int)tick;
                invented |= 1 << crop;

                StartFarming(cell, crop, tick, suitability);
                double foragers = groups.Size(foragerGroup);
                double first = FirstFarmerShare * foragers;
                groups.SetSize(foragerGroup, foragers - first);
                groups.AddPeople(groups.FindOrAdd(cell, _farmer), first, groups.Food(foragerGroup));
                break;
            }
        }
    }

    private void StartFarming(int cell, int crop, long tick, double suitability)
    {
        _cropPlusOne[cell] = crop + 1;
        _sincePlusOne[cell] = (int)tick + 1;
        _capacity[cell] = FarmCapacity(cell, suitability);
    }

    // Records each crop's homeland and marks the cells where it grows wild.
    private void PlaceHomelands(SimContext context)
    {
        WorldMap map = _map!;
        GeodesicGrid grid = map.Grid;
        CropHomelands mode = RunSettings.ReadFrom(context.State).CropHomelands;
        PopulationGroups groups = _groups!;

        foreach (Crop crop in Crops.Crops)
        {
            int centre;
            if (mode == CropHomelands.Real)
            {
                centre = grid.FindCell(crop.HomelandLat, crop.HomelandLon);
            }
            else
            {
                var candidates = new List<int>();
                foreach (int cell in _northCells.Concat(_southCells).OrderBy(c => c))
                {
                    if (groups.Find(cell, _forager) >= 0 && Suitability(cell, crop.Index) >= _parameters.RandomHomelandMinSuitability)
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
            _homelandCrop![row] = crop.Index;
            _homelandCell![row] = centre;

            Vec3 centrePoint = grid.Centre(centre);
            foreach (int cell in _northCells.Concat(_southCells))
            {
                double km = GeodesicGrid.EarthRadiusKm * Vec3.AngleBetween(centrePoint, grid.Centre(cell));
                if (km <= crop.HomelandRadiusKm && Suitability(cell, crop.Index) >= _parameters.WildMinSuitability)
                {
                    _wildCrops[cell] |= 1 << crop.Index;
                }
            }
        }
    }
}
