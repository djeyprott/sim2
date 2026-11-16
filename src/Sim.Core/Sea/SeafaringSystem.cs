using Sim.Core.Climate;
using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Technologies;

namespace Sim.Core.Sea;

/// <summary>Settling by sea, read from seafaring.json.</summary>
public sealed class SeafaringParameters
{
    public double SettleChance { get; set; } = 0.01;

    public double FullSourcePeople { get; set; } = 2000.0;

    public double SettlerShare { get; set; } = 0.05;

    public double MaxSettlers { get; set; } = 200.0;

    public int StartsPerLandmass { get; set; } = 64;

    public int MaxRange { get; set; } = 60;

    public static SeafaringParameters LoadDefault() => Parse(ContentFiles.ReadText("seafaring.json"));

    public static SeafaringParameters Parse(string json)
    {
        SeafaringParameters p = ContentFiles.Parse<SeafaringParameters>(json, "seafaring");
        if (p.SettleChance < 0.0 || p.SettleChance > 1.0 || p.SettlerShare <= 0.0 || p.SettlerShare > 1.0
            || p.FullSourcePeople <= 0.0 || p.MaxSettlers <= 0.0 || p.StartsPerLandmass < 1 || p.MaxRange < 1)
        {
            throw new InvalidDataException("seafaring.json: chances and shares 0 to 1, people, starts and range above 0.");
        }

        return p;
    }
}

/// <summary>A landing by sea on land nobody had reached: where, when, and where the settlers came from.</summary>
public readonly record struct Landing(int Landmass, int Cell, long Tick, int From, double Settlers);

/// <summary>
/// Settling by sea (milestone 3, step 6). Land not reached at the start stays empty until people on
/// a shore of reached land have boats that cross the sea to it (technologies with the "seaRange"
/// effect: the sail, outrigger canoes). Each year such a shore cell may send settlers, who land as
/// foragers on the nearest shore of the new land, carrying what they know; the whole landmass then
/// counts as reached (the forager system reads the landings), and settlers from farmland bring
/// their crop (the farming system reads them too). Runs once a year, after technology.
/// </summary>
public sealed class SeafaringSystem : ISimSystem
{
    public const string SystemName = "seafaring";
    public const string LandingTableName = ForagerSystem.LandingTableName;

    private readonly SeafaringParameters _parameters;
    private readonly bool[] _livable;

    private EntityTable? _landings;
    private IntColumn? _landingCell;
    private IntColumn? _landingTick;
    private IntColumn? _landingFrom;
    private IntColumn? _landingCrop;
    private DoubleColumn? _landingSettlers;

    private WorldMap? _map;
    private SeaMap? _sea;
    private PopulationGroups? _groups;
    private TechnologyUse? _technology;
    private TechnologyEffect[] _rangeEffects = Array.Empty<TechnologyEffect>();
    private int[] _biome = Array.Empty<int>();
    private int[]? _cropPlusOne;

    // Reached landmasses (at the start, or landed on since), rebuilt from the landings after a load.
    private bool[] _startLandmass = Array.Empty<bool>();
    private bool[] _reached = Array.Empty<bool>();
    private int _countedLandings = -1;

    // Per unreached landmass: its starting points, nearest first (shore cell of other land, the
    // landing cell, sea cells between).
    private int[] _targets = Array.Empty<int>();
    private (int From, int To, int Steps)[][] _starts = Array.Empty<(int, int, int)[]>();

    // Each shore cell's boat range this year (worked out when first needed).
    private int[] _rangeYear = Array.Empty<int>();
    private double[] _range = Array.Empty<double>();

    public SeafaringSystem(SeafaringParameters parameters, BiomeSet biomes)
    {
        _parameters = parameters;
        ForagerParameters foragers = ForagerParameters.LoadDefault();
        _livable = biomes.Biomes.Select(b => foragers.Density.GetValueOrDefault(b.Name) > 0.0).ToArray();
    }

    public static SeafaringSystem CreateDefault(BiomeSet biomes) => new SeafaringSystem(SeafaringParameters.LoadDefault(), biomes);

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public SeafaringParameters Parameters => _parameters;

    public void Register(WorldState state)
    {
        _landings = state.AddTable(LandingTableName);
        _landingCell = _landings.AddIntColumn("cell");
        _landingTick = _landings.AddIntColumn("tick");
        _landingFrom = _landings.AddIntColumn("from");
        _landingCrop = _landings.AddIntColumn("crop");
        _landingSettlers = _landings.AddDoubleColumn("settlers");
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        WorldState state = context.State;
        _map = map;
        _sea = SeaMap.Of(map);
        _groups = new PopulationGroups(state);
        _biome = state.IntField(ClimateSystem.BiomeField);
        _cropPlusOne = state.HasField("farming.crop") ? state.IntField("farming.crop") : null;
        _technology = TechnologyUse.TryCreate(state, map);
        if (_technology != null)
        {
            _rangeEffects = _technology.WithEffect(TechnologySet.SeaRange);
        }

        _startLandmass = ForagerSystem.ReachedAtStart(map, ForagerParameters.LoadDefault());
        _rangeYear = new int[map.CellCount];
        Array.Fill(_rangeYear, -1);
        _range = new double[map.CellCount];
        FindStarts();
    }

    /// <summary>Every landing so far, in the order they happened.</summary>
    public IReadOnlyList<Landing> Landings()
    {
        var list = new List<Landing>();
        for (int id = 0; id < _landings!.IdCount; id++)
        {
            int cell = _landingCell![id];
            list.Add(new Landing(_sea!.Landmass[cell], cell, _landingTick![id], _landingFrom![id], _landingSettlers![id]));
        }

        return list;
    }

    /// <summary>The unreached landmasses that some boat could reach from other land, and their starting points, nearest first.</summary>
    public IReadOnlyList<(int Landmass, (int From, int To, int Steps)[] Starts)> Targets() =>
        _targets.Select((t, i) => (t, _starts[i])).ToList();

    /// <summary>Whether a landmass has been reached (at the start, or by sea since).</summary>
    public bool Reached(int landmass)
    {
        Recount();
        return _reached[landmass];
    }

    public void Update(SimContext context)
    {
        Recount();
        if (_technology == null || _rangeEffects.Length == 0)
        {
            return;
        }

        int year = (int)(context.Tick / GameDate.MonthsPerYear);
        int[] landmass = _sea!.Landmass;
        PopulationGroups groups = _groups!;
        for (int t = 0; t < _targets.Length; t++)
        {
            int target = _targets[t];
            if (_reached[target])
            {
                continue;
            }

            foreach ((int from, int to, int steps) in _starts[t])
            {
                if (!_reached[landmass[from]] || !Livable(to) || Range(from, year) < steps)
                {
                    continue;
                }

                // The cell's largest group sends the settlers.
                int source = -1;
                double size = 0.0;
                foreach (int g in groups.InCell(from))
                {
                    if (groups.Size(g) > size)
                    {
                        size = groups.Size(g);
                        source = g;
                    }
                }

                if (source < 0)
                {
                    continue;
                }

                double chance = _parameters.SettleChance * Math.Min(1.0, size / _parameters.FullSourcePeople);
                if (!context.Rng.Chance(chance))
                {
                    continue;
                }

                double settlers = Math.Min(_parameters.MaxSettlers, _parameters.SettlerShare * size);
                double food = groups.Food(source);
                int forager = groups.FindOrAdd(to, ForagerOccupation(groups));
                groups.SetSize(source, size - settlers);
                groups.AddPeople(forager, settlers, food, from: source);

                int id = _landings!.Add();
                _landingCell![id] = to;
                _landingTick![id] = (int)context.Tick;
                _landingFrom![id] = from;
                _landingCrop![id] = _cropPlusOne?[from] ?? 0;
                _landingSettlers![id] = settlers;
                _reached[target] = true;
                _countedLandings = _landings.IdCount;
                break;
            }
        }
    }

    private int _foragerOccupation = -1;

    private int ForagerOccupation(PopulationGroups groups)
    {
        if (_foragerOccupation < 0)
        {
            _foragerOccupation = OccupationSet.LoadDefault().IndexOf(OccupationSet.Forager);
        }

        return _foragerOccupation;
    }

    private bool Livable(int cell)
    {
        int biome = _biome[cell];
        return biome >= 0 && _livable[biome];
    }

    // The farthest a shore cell's people's boats reach (sea cells), from the technologies some group
    // there knows; 0 with none.
    private double Range(int cell, int year)
    {
        if (_rangeYear[cell] != year)
        {
            _rangeYear[cell] = year;
            _range[cell] = 0.0;
            foreach (TechnologyEffect e in _rangeEffects)
            {
                if (e.Size > _range[cell] && _technology!.Conditions.Allows(cell, e.Technology) && _technology.KnownInCell(cell, e.Technology))
                {
                    _range[cell] = e.Size;
                }
            }
        }

        return _range[cell];
    }

    // The reached landmasses: those reached at the start, and every one landed on.
    private void Recount()
    {
        int count = _landings!.IdCount;
        if (count == _countedLandings)
        {
            return;
        }

        _reached = (bool[])_startLandmass.Clone();
        for (int id = 0; id < count; id++)
        {
            _reached[_sea!.Landmass[_landingCell![id]]] = true;
        }

        _countedLandings = count;
    }

    // For each landmass not reached at the start: the shore cells of other land within the longest
    // boat range, by a search over the sea from all its shores at once; the nearest are kept.
    private void FindStarts()
    {
        WorldMap map = _map!;
        SeaMap sea = _sea!;
        GeodesicGrid grid = map.Grid;
        int count = map.CellCount;
        var members = new List<int>[sea.LandmassCount];
        foreach (int cell in sea.ShoreCells)
        {
            (members[sea.Landmass[cell]] ??= new List<int>()).Add(cell);
        }

        var steps = new int[count];
        var origin = new int[count];
        var stamp = new int[count];
        int round = 0;
        var queue = new Queue<int>();
        var targets = new List<int>();
        var starts = new List<(int, int, int)[]>();
        for (int target = 0; target < sea.LandmassCount; target++)
        {
            if (_startLandmass[target] || members[target] == null)
            {
                continue;
            }

            round++;
            queue.Clear();
            foreach (int shore in members[target])
            {
                foreach (int water in grid.Neighbours(shore))
                {
                    if (!map.IsLand(water) && stamp[water] != round)
                    {
                        stamp[water] = round;
                        steps[water] = 1;
                        origin[water] = shore;
                        queue.Enqueue(water);
                    }
                }
            }

            var found = new SortedDictionary<int, (int To, int Steps)>();
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int s = steps[cell];
                foreach (int other in grid.Neighbours(cell))
                {
                    if (map.IsLand(other))
                    {
                        if (sea.Landmass[other] != target && (!found.TryGetValue(other, out var known) || s < known.Steps))
                        {
                            found[other] = (origin[cell], s);
                        }
                    }
                    else if (s < _parameters.MaxRange && stamp[other] != round)
                    {
                        stamp[other] = round;
                        steps[other] = s + 1;
                        origin[other] = origin[cell];
                        queue.Enqueue(other);
                    }
                }
            }

            if (found.Count == 0)
            {
                continue;
            }

            targets.Add(target);
            starts.Add(found
                .Select(pair => (From: pair.Key, To: pair.Value.To, Steps: pair.Value.Steps))
                .OrderBy(p => p.Steps).ThenBy(p => p.From)
                .Take(_parameters.StartsPerLandmass)
                .ToArray());
        }

        _targets = targets.ToArray();
        _starts = starts.ToArray();
    }
}
