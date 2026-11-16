using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Goods;
using Sim.Core.Map;

namespace Sim.Core.People;

/// <summary>
/// Hunter-gatherers: the population groups with the occupation "Forager". Each reached land cell
/// can feed a number of foragers set by its current biome, coast and rivers. Each year forager
/// groups grow toward that number, shrink when it falls, and move into neighbouring land cells
/// that are less full. A group's food is the share of its needs the land meets. Runs yearly,
/// after climate and population.
/// </summary>
public sealed class ForagerSystem : ISimSystem
{
    public const string SystemName = "foragers";
    public const string CapacityField = "people.foragerCapacity";

    // The farming system's field for the share of a cell's land that is farmed. Foragers can
    // only use what is left.
    private const string FarmingLandShareField = "farming.landShare";

    // Below this many people a cell is treated as empty.
    private const double Extinct = 0.01;

    private readonly ForagerParameters _parameters;
    private readonly double[] _densityByBiome;
    private readonly int _occupation;

    private PopulationGroups? _groups;
    private Stockpiles? _stockpiles;
    private int _wildFood = -1;
    private int[] _groupOfCell = Array.Empty<int>();

    // Working copy of each cell's foragers during an update; the groups hold the saved values.
    private double[] _population = Array.Empty<double>();
    private double[] _capacity = Array.Empty<double>();
    private int[] _biome = Array.Empty<int>();

    // Share of each cell's land given over to farming, when the farming system is running.
    private double[]? _farmedShare;

    private WorldMap? _map;
    private int[] _cells = Array.Empty<int>();
    private double[] _shoreFactor = Array.Empty<double>();
    private double[] _riverBonus = Array.Empty<double>();
    private bool[] _reachable = Array.Empty<bool>();
    private int[] _neighbourStart = Array.Empty<int>();
    private int[] _neighbours = Array.Empty<int>();
    private double[] _flowIn = Array.Empty<double>();
    private double[] _fullness = Array.Empty<double>();

    // Landmasses reached at the start, and the landings by sea since (the seafaring system's saved
    // table: landmass reached, cell and tick); the reached cells are rebuilt when a landing is added
    // or a run is loaded.
    public const string LandingTableName = "seafaring.landings";
    private bool[] _startLandmass = Array.Empty<bool>();
    private EntityTable? _landings;
    private IntColumn? _landingCell;
    private int _builtLandings = -1;

    public ForagerSystem(ForagerParameters parameters, BiomeSet biomes, OccupationSet occupations)
    {
        _parameters = parameters;
        _occupation = occupations.IndexOf(OccupationSet.Forager);
        _densityByBiome = new double[biomes.Count];
        foreach (Biome biome in biomes.Biomes)
        {
            if (!parameters.Density.TryGetValue(biome.Name, out double density))
            {
                throw new InvalidDataException($"The foragers file gives no density for biome '{biome.Name}'.");
            }

            _densityByBiome[biome.Index] = density;
        }
    }

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public static ForagerSystem CreateDefault(BiomeSet biomes, OccupationSet occupations) =>
        new ForagerSystem(ForagerParameters.LoadDefault(), biomes, occupations);

    /// <summary>The occupation index of foragers.</summary>
    public int Occupation => _occupation;

    /// <summary>True for land cells people had reached at the start of the run. Fixed after initializing.</summary>
    public bool IsReachable(int cell) => _reachable.Length > cell && _reachable[cell];

    /// <summary>The reached land cells, in id order. Fixed after initializing.</summary>
    public IReadOnlyList<int> ReachableCells => _cells;

    public void Register(WorldState state)
    {
        _capacity = state.AddDoubleField(CapacityField);
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        _map = map;
        _biome = context.State.IntField(ClimateSystem.BiomeField);
        _farmedShare = context.State.HasField(FarmingLandShareField) ? context.State.DoubleField(FarmingLandShareField) : null;
        _groups = new PopulationGroups(context.State);
        if (context.State.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(context.State, GoodSet.LoadDefault());
            _wildFood = _stockpiles.Goods.IndexOf(GoodSet.WildFood);
        }
        _population = new double[map.CellCount];
        _groupOfCell = new int[map.CellCount];
        _flowIn = new double[map.CellCount];
        _fullness = new double[map.CellCount];
        _startLandmass = ReachedAtStart(map, _parameters);
        if (context.State.HasTable(LandingTableName))
        {
            _landings = context.State.Table(LandingTableName);
            _landingCell = _landings.IntColumn("cell");
        }

        BuildReach();

        // Start every reached cell with a full forager group.
        UpdateCapacity();
        foreach (int cell in _cells)
        {
            if (_capacity[cell] > 0.0)
            {
                int group = _groups.FindOrAdd(cell, _occupation);
                _groups.SetSize(group, _capacity[cell]);
                _groups.SetFood(group, 1.0);
            }
        }
    }

    /// <summary>The landmasses people had reached at the start of a run (by <see cref="SeaMap"/> landmass).</summary>
    public bool[] StartLandmasses => _startLandmass;

    // The reached land cells (reached at the start, or on a landmass landed on by sea since), their
    // fixed shares of capacity, and the reached neighbours people can move to.
    private void BuildReach()
    {
        WorldMap map = _map!;
        int[] landmass = SeaMap.Of(map).Landmass;
        var reachedLandmass = (bool[])_startLandmass.Clone();
        int landings = _landings?.IdCount ?? 0;
        for (int id = 0; id < landings; id++)
        {
            reachedLandmass[landmass[_landingCell![id]]] = true;
        }

        _builtLandings = landings;
        _reachable = new bool[map.CellCount];
        var cells = new List<int>();
        for (int cell = 0; cell < map.CellCount; cell++)
        {
            if (map.IsLand(cell) && reachedLandmass[landmass[cell]])
            {
                _reachable[cell] = true;
                cells.Add(cell);
            }
        }

        _cells = cells.ToArray();

        // Fixed parts of each cell's capacity: area times the coast or lake shore factor, and the
        // river's extra people.
        _shoreFactor = new double[_cells.Length];
        _riverBonus = new double[_cells.Length];
        for (int i = 0; i < _cells.Length; i++)
        {
            int cell = _cells[i];
            bool coast = false;
            bool lakeShore = false;
            foreach (int other in map.Grid.Neighbours(cell))
            {
                coast |= map.IsSea(other);
                lakeShore |= map.IsLake(other);
            }

            double area = map.Grid.AreaKm2(cell);
            _shoreFactor[i] = area * (coast ? _parameters.CoastFactor : lakeShore ? _parameters.LakeShoreFactor : 1.0);
            _riverBonus[i] = area * map.River(cell) switch
            {
                RiverSize.Major => _parameters.MajorRiverBonus,
                RiverSize.Minor => _parameters.MinorRiverBonus,
                _ => 0.0,
            };
        }

        // Neighbours people can move to: reached land cells only.
        var starts = new int[map.CellCount + 1];
        var list = new List<int>();
        for (int cell = 0; cell < map.CellCount; cell++)
        {
            starts[cell] = list.Count;
            if (!_reachable[cell])
            {
                continue;
            }

            foreach (int other in map.Grid.Neighbours(cell))
            {
                if (_reachable[other])
                {
                    list.Add(other);
                }
            }
        }

        starts[map.CellCount] = list.Count;
        _neighbourStart = starts;
        _neighbours = list.ToArray();
    }

    public void Update(SimContext context)
    {
        if ((_landings?.IdCount ?? 0) != _builtLandings)
        {
            BuildReach();
        }

        PopulationGroups groups = _groups!;
        groups.FillGroupIndex(_groupOfCell, _occupation);
        foreach (int cell in _cells)
        {
            int group = _groupOfCell[cell];
            _population[cell] = group >= 0 ? groups.Size(group) : 0.0;
        }

        UpdateCapacity();
        Grow();
        Migrate();

        // Write back to the groups, creating a forager group where people have just arrived.
        foreach (int cell in _cells)
        {
            double people = _population[cell];
            int group = _groupOfCell[cell];
            if (group < 0)
            {
                if (people <= 0.0)
                {
                    continue;
                }

                group = groups.FindOrAdd(cell, _occupation);
            }

            groups.SetSize(group, people);
            double capacity = _capacity[cell];

            // Foragers gather wild food and eat it the same year; it is never stored.
            double eaten = Math.Min(people, capacity);
            _stockpiles?.ProduceAndConsume(_wildFood, eaten, eaten);
            groups.SetFood(group, people <= 0.0 ? 1.0 : capacity <= 0.0 ? 0.0 : Math.Min(1.0, capacity / people));
        }
    }

    /// <summary>Total foragers in the world.</summary>
    public double WorldPopulation() => _groups?.WorldTotal(_occupation) ?? 0.0;

    /// <summary>Total foragers the world's land can currently feed.</summary>
    public double WorldCapacity()
    {
        double total = 0.0;
        foreach (int cell in _cells)
        {
            total += _capacity[cell];
        }

        return total;
    }

    private void UpdateCapacity()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            int cell = _cells[i];
            int biome = _biome[cell];
            double density = biome >= 0 ? _densityByBiome[biome] : 0.0;
            double capacity = density > 0.0 ? _shoreFactor[i] * density + _riverBonus[i] : 0.0;
            if (_farmedShare != null)
            {
                capacity *= 1.0 - Math.Clamp(_farmedShare[cell], 0.0, 1.0);
            }

            _capacity[cell] = capacity;
        }
    }

    private void Grow()
    {
        double rate = _parameters.GrowthRate;
        double loss = _parameters.LossWhereNoFood;
        foreach (int cell in _cells)
        {
            double people = _population[cell];
            if (people <= 0.0)
            {
                continue;
            }

            double capacity = _capacity[cell];
            if (capacity > 0.0)
            {
                // Logistic: grows toward capacity, shrinks when above it (at most by the loss rate).
                double change = rate * people * (1.0 - people / capacity);
                people += Math.Max(change, -loss * people);
            }
            else
            {
                people -= loss * people;
            }

            _population[cell] = people < Extinct ? 0.0 : people;
        }
    }

    // Moves people toward less full neighbours. All flows are worked out from the populations
    // before any move, so the result does not depend on the order cells are visited in.
    private void Migrate()
    {
        double rate = _parameters.MigrationRate / GeodesicGrid.MaxNeighbours;
        foreach (int cell in _cells)
        {
            double capacity = _capacity[cell];
            double people = _population[cell];
            _fullness[cell] = capacity > 0.0 ? people / capacity : people > 0.0 ? double.PositiveInfinity : 0.0;
            _flowIn[cell] = 0.0;
        }

        foreach (int cell in _cells)
        {
            double people = _population[cell];
            if (people <= 0.0)
            {
                continue;
            }

            double fullness = _fullness[cell];
            double leaving = 0.0;
            for (int k = _neighbourStart[cell]; k < _neighbourStart[cell + 1]; k++)
            {
                int other = _neighbours[k];
                double otherFullness = _fullness[other];
                if (_capacity[other] <= 0.0 || otherFullness >= fullness)
                {
                    continue;
                }

                double share = double.IsPositiveInfinity(fullness) ? 1.0 : (fullness - otherFullness) / fullness;
                double moving = rate * people * share;
                _flowIn[other] += moving;
                leaving += moving;
            }

            _flowIn[cell] -= leaving;
        }

        foreach (int cell in _cells)
        {
            double people = _population[cell] + _flowIn[cell];
            _population[cell] = people < Extinct ? 0.0 : people;
        }
    }

    /// <summary>
    /// Land people had reached at the start, by landmass: landmasses holding a reached place, plus
    /// any landmass within a short sea crossing of a reached one, except landmasses listed as unreached.
    /// </summary>
    public static bool[] ReachedAtStart(WorldMap map, ForagerParameters parameters)
    {
        GeodesicGrid grid = map.Grid;
        int count = map.CellCount;
        SeaMap sea = SeaMap.Of(map);
        int[] landmass = sea.Landmass;
        int landmassCount = sea.LandmassCount;

        int LandmassAt(Place place)
        {
            int cell = grid.FindCell(place.Lat, place.Lon);
            if (!map.IsLand(cell))
            {
                throw new InvalidDataException($"The foragers file's place '{place.Name}' is not on land.");
            }

            return landmass[cell];
        }

        var blocked = new bool[landmassCount];
        foreach (Place place in parameters.Unreached)
        {
            blocked[LandmassAt(place)] = true;
        }

        var reached = new bool[landmassCount];
        foreach (Place place in parameters.Reached)
        {
            int index = LandmassAt(place);
            if (blocked[index])
            {
                throw new InvalidDataException($"The foragers file lists '{place.Name}' as both reached and unreached.");
            }

            reached[index] = true;
        }

        // Breadth-first search from reached land. seaSteps is how many sea or lake cells in a row
        // lead to a cell; reaching land of a new landmass marks the whole landmass as reached.
        var members = new List<int>[landmassCount];
        for (int cell = 0; cell < count; cell++)
        {
            if (landmass[cell] >= 0)
            {
                (members[landmass[cell]] ??= new List<int>()).Add(cell);
            }
        }

        var seaSteps = new int[count];
        Array.Fill(seaSteps, int.MaxValue);
        var queue = new Queue<int>();
        for (int cell = 0; cell < count; cell++)
        {
            if (map.IsLand(cell) && reached[landmass[cell]])
            {
                seaSteps[cell] = 0;
                queue.Enqueue(cell);
            }
        }

        int maxSea = parameters.MaxSeaCellsCrossed;
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            foreach (int other in grid.Neighbours(cell))
            {
                if (map.IsLand(other))
                {
                    int index = landmass[other];
                    if (blocked[index] || reached[index])
                    {
                        continue;
                    }

                    reached[index] = true;
                    foreach (int c in members[index])
                    {
                        seaSteps[c] = 0;
                        queue.Enqueue(c);
                    }
                }
                else
                {
                    int steps = seaSteps[cell] + 1;
                    if (steps <= maxSea && steps < seaSteps[other])
                    {
                        seaSteps[other] = steps;
                        queue.Enqueue(other);
                    }
                }
            }
        }

        return reached;
    }
}
