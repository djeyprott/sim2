using System.Runtime.CompilerServices;
using Sim.Core.Geo;

namespace Sim.Core.Map;

/// <summary>
/// Land and sea as boats see them (milestone 3): which landmass each land cell belongs to, which
/// land cells touch water, and the crossings from one shore to another over a run of water cells
/// (sea or lake). Worked out once per map and shared; fixed for a run.
/// </summary>
public sealed class SeaMap
{
    private static readonly ConditionalWeakTable<WorldMap, SeaMap> Cache = new ConditionalWeakTable<WorldMap, SeaMap>();
    private static readonly object CacheLock = new object();

    private readonly WorldMap _map;
    private readonly Dictionary<int, Crossings> _crossings = new Dictionary<int, Crossings>();

    private SeaMap(WorldMap map)
    {
        _map = map;
        GeodesicGrid grid = map.Grid;
        int count = map.CellCount;
        Landmass = new int[count];
        Array.Fill(Landmass, -1);
        var stack = new Stack<int>();
        var sizes = new List<int>();
        for (int cell = 0; cell < count; cell++)
        {
            if (!map.IsLand(cell) || Landmass[cell] >= 0)
            {
                continue;
            }

            int id = sizes.Count;
            int size = 0;
            Landmass[cell] = id;
            stack.Push(cell);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                size++;
                foreach (int other in grid.Neighbours(current))
                {
                    if (map.IsLand(other) && Landmass[other] < 0)
                    {
                        Landmass[other] = id;
                        stack.Push(other);
                    }
                }
            }

            sizes.Add(size);
        }

        LandmassCount = sizes.Count;
        LandmassSize = sizes.ToArray();
        Shore = new bool[count];
        var shore = new List<int>();
        for (int cell = 0; cell < count; cell++)
        {
            if (!map.IsLand(cell))
            {
                continue;
            }

            foreach (int other in grid.Neighbours(cell))
            {
                if (!map.IsLand(other))
                {
                    Shore[cell] = true;
                    shore.Add(cell);
                    break;
                }
            }
        }

        ShoreCells = shore.ToArray();
    }

    /// <summary>The sea map of a world map (worked out on first use, then shared).</summary>
    public static SeaMap Of(WorldMap map)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(map, out SeaMap? sea))
            {
                sea = new SeaMap(map);
                Cache.Add(map, sea);
            }

            return sea;
        }
    }

    /// <summary>By cell: the landmass (connected land) it belongs to, or -1 for water.</summary>
    public int[] Landmass { get; }

    public int LandmassCount { get; }

    /// <summary>Land cells in each landmass.</summary>
    public int[] LandmassSize { get; }

    /// <summary>By cell: true for land cells next to a water cell.</summary>
    public bool[] Shore { get; }

    /// <summary>The shore cells, in id order.</summary>
    public int[] ShoreCells { get; }

    /// <summary>
    /// For every shore cell, the shore cells it can reach over a run of at most <paramref name="maxSteps"/>
    /// water cells (and back onto land), with the fewest water cells needed; shore cells of the
    /// same landmass included (crossing a bay). Worked out on first use for each limit, then shared.
    /// </summary>
    public Crossings CrossingsWithin(int maxSteps)
    {
        lock (_crossings)
        {
            if (!_crossings.TryGetValue(maxSteps, out Crossings? crossings))
            {
                crossings = Build(ShoreCells, maxSteps);
                _crossings[maxSteps] = crossings;
            }

            return crossings;
        }
    }

    /// <summary>The shore cells reachable from each of the given shore cells over at most maxSteps water cells.</summary>
    public Crossings CrossingsFrom(IEnumerable<int> starts, int maxSteps) => Build(starts.ToArray(), maxSteps);

    private Crossings Build(int[] starts, int maxSteps)
    {
        GeodesicGrid grid = _map.Grid;
        int count = _map.CellCount;
        var steps = new int[count];
        var stamp = new int[count];
        int round = 0;
        var queue = new Queue<int>();
        var start = new int[count + 1];
        var targets = new List<int>();
        var lengths = new List<int>();
        var found = new SortedDictionary<int, int>();
        int next = 0;
        Array.Sort(starts);
        foreach (int from in starts)
        {
            for (; next <= from; next++)
            {
                start[next] = targets.Count;
            }

            round++;
            found.Clear();
            queue.Clear();
            foreach (int water in grid.Neighbours(from))
            {
                if (!_map.IsLand(water) && stamp[water] != round)
                {
                    stamp[water] = round;
                    steps[water] = 1;
                    queue.Enqueue(water);
                }
            }

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int s = steps[cell];
                foreach (int other in grid.Neighbours(cell))
                {
                    if (_map.IsLand(other))
                    {
                        if (other != from && (!found.TryGetValue(other, out int known) || s < known))
                        {
                            found[other] = s;
                        }
                    }
                    else if (s < maxSteps && stamp[other] != round)
                    {
                        stamp[other] = round;
                        steps[other] = s + 1;
                        queue.Enqueue(other);
                    }
                }
            }

            foreach (KeyValuePair<int, int> pair in found)
            {
                targets.Add(pair.Key);
                lengths.Add(pair.Value);
            }
        }

        for (; next <= count; next++)
        {
            start[next] = targets.Count;
        }

        return new Crossings(start, targets.ToArray(), lengths.ToArray());
    }
}

/// <summary>Crossings over water from shore cells: for cell c, entries Start[c] to Start[c + 1] - 1.</summary>
public sealed class Crossings
{
    public Crossings(int[] start, int[] target, int[] steps)
    {
        Start = start;
        Target = target;
        Steps = steps;
    }

    public int[] Start { get; }

    /// <summary>The shore cell reached.</summary>
    public int[] Target { get; }

    /// <summary>Water cells crossed (at least 1).</summary>
    public int[] Steps { get; }
}
