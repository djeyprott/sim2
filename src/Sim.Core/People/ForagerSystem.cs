using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Sim.Core.People;

/// <summary>
/// Hunter-gatherer populations. Each reached land cell can feed a number of foragers set by its
/// current biome, coast and rivers. Each year populations grow toward that number, shrink when
/// it falls, and move into neighbouring land cells that are less full. Runs yearly, after climate.
/// </summary>
public sealed class ForagerSystem : ISimSystem
{
    public const string SystemName = "foragers";
    public const string PopulationField = "people.foragers";
    public const string CapacityField = "people.foragerCapacity";

    // Below this many people a cell is treated as empty.
    private const double Extinct = 0.01;

    private readonly ForagerParameters _parameters;
    private readonly double[] _densityByBiome;

    private double[] _population = Array.Empty<double>();
    private double[] _capacity = Array.Empty<double>();
    private int[] _biome = Array.Empty<int>();

    private WorldMap? _map;
    private int[] _cells = Array.Empty<int>();
    private double[] _baseFactor = Array.Empty<double>();
    private bool[] _reachable = Array.Empty<bool>();
    private int[] _neighbourStart = Array.Empty<int>();
    private int[] _neighbours = Array.Empty<int>();
    private double[] _flowIn = Array.Empty<double>();
    private double[] _fullness = Array.Empty<double>();

    public ForagerSystem(ForagerParameters parameters, BiomeSet biomes)
    {
        _parameters = parameters;
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

    public static ForagerSystem CreateDefault(BiomeSet biomes) => new ForagerSystem(ForagerParameters.LoadDefault(), biomes);

    /// <summary>True for land cells people had reached at the start of the run. Fixed after initializing.</summary>
    public bool IsReachable(int cell) => _reachable.Length > cell && _reachable[cell];

    /// <summary>The reached land cells, in id order. Fixed after initializing.</summary>
    public IReadOnlyList<int> ReachableCells => _cells;

    public void Register(WorldState state)
    {
        _population = state.AddDoubleField(PopulationField);
        _capacity = state.AddDoubleField(CapacityField);
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        _map = map;
        _biome = context.State.IntField(ClimateSystem.BiomeField);
        _reachable = FindReachableLand(map);

        var cells = new List<int>();
        for (int cell = 0; cell < map.CellCount; cell++)
        {
            if (_reachable[cell])
            {
                cells.Add(cell);
            }
        }

        _cells = cells.ToArray();

        // Fixed part of each cell's capacity: area, coast or lake shore, and river.
        _baseFactor = new double[_cells.Length];
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

            double factor = map.Grid.AreaKm2(cell);
            factor *= coast ? _parameters.CoastFactor : lakeShore ? _parameters.LakeShoreFactor : 1.0;
            factor *= map.River(cell) switch
            {
                RiverSize.Major => _parameters.MajorRiverFactor,
                RiverSize.Minor => _parameters.MinorRiverFactor,
                _ => 1.0,
            };
            _baseFactor[i] = factor;
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
        _flowIn = new double[map.CellCount];
        _fullness = new double[map.CellCount];

        // Start every reached cell full.
        UpdateCapacity();
        foreach (int cell in _cells)
        {
            _population[cell] = _capacity[cell];
        }
    }

    public void Update(SimContext context)
    {
        UpdateCapacity();
        Grow();
        Migrate();
    }

    /// <summary>Total foragers in the world.</summary>
    public double WorldPopulation()
    {
        double total = 0.0;
        foreach (int cell in _cells)
        {
            total += _population[cell];
        }

        return total;
    }

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
            _capacity[cell] = biome >= 0 ? _baseFactor[i] * _densityByBiome[biome] : 0.0;
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

    // Land people had reached at the start: landmasses holding a reached place, plus any landmass
    // within a short sea crossing of a reached one, except landmasses listed as unreached.
    private bool[] FindReachableLand(WorldMap map)
    {
        GeodesicGrid grid = map.Grid;
        int count = map.CellCount;

        var landmass = new int[count];
        Array.Fill(landmass, -1);
        int landmassCount = 0;
        var stack = new Stack<int>();
        for (int cell = 0; cell < count; cell++)
        {
            if (!map.IsLand(cell) || landmass[cell] >= 0)
            {
                continue;
            }

            landmass[cell] = landmassCount;
            stack.Push(cell);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int other in grid.Neighbours(current))
                {
                    if (map.IsLand(other) && landmass[other] < 0)
                    {
                        landmass[other] = landmassCount;
                        stack.Push(other);
                    }
                }
            }

            landmassCount++;
        }

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
        foreach (Place place in _parameters.Unreached)
        {
            blocked[LandmassAt(place)] = true;
        }

        var reached = new bool[landmassCount];
        foreach (Place place in _parameters.Reached)
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

        int maxSea = _parameters.MaxSeaCellsCrossed;
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
                    for (int c = 0; c < count; c++)
                    {
                        if (landmass[c] == index)
                        {
                            seaSteps[c] = 0;
                            queue.Enqueue(c);
                        }
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

        var result = new bool[count];
        for (int cell = 0; cell < count; cell++)
        {
            result[cell] = map.IsLand(cell) && reached[landmass[cell]];
        }

        return result;
    }
}
