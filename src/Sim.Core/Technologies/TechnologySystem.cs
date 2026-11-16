using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Technologies;

/// <summary>What happened to a technology, and where and when.</summary>
public enum TechnologyEventKind
{
    /// <summary>The first discovery anywhere.</summary>
    First = 0,

    /// <summary>A later discovery far from every earlier one.</summary>
    Independent = 1,

    /// <summary>A group stopped using it because too few people around it kept it alive.</summary>
    Lost = 2,
}

/// <summary>A recorded discovery or loss.</summary>
public readonly record struct TechnologyEvent(int Technology, TechnologyEventKind Kind, int Cell, long Tick, double Group, double Contact);

/// <summary>
/// Technology (milestone 3). Population groups carry, for each technology, the share of their
/// people who know it (a condition on the group, averaged when groups merge and carried by people
/// who move). Each year: groups whose conditions are met may discover a technology, with a chance
/// that grows with the people in reach; shares grow within a group, move toward those of the other
/// groups in the cell and in neighbouring cells, and along trade links; and groups with too few
/// people in contact slowly forget. Discoveries and losses are recorded. Runs once a year, after
/// trade, whose links it reads.
/// </summary>
public sealed class TechnologySystem : ISimSystem
{
    public const string SystemName = "technology";
    public const string EventTableName = "technology.events";

    private readonly TechnologyParameters _parameters;
    private readonly TradeSystem? _trade;
    private readonly double _chanceScale;

    // Saved state: the knowledge columns on the groups table and the events table.
    private DoubleColumn[] _knowledgeColumns = Array.Empty<DoubleColumn>();
    private EntityTable? _events;
    private IntColumn? _eventTechnology;
    private IntColumn? _eventKind;
    private IntColumn? _eventCell;
    private IntColumn? _eventTick;
    private DoubleColumn? _eventGroup;
    private DoubleColumn? _eventContact;

    // Read from other systems.
    private WorldMap? _map;
    private EntityTable? _groupTable;
    private PopulationGroups? _groups;
    private double[] _rainfallFactor = Array.Empty<double>();
    private int[]? _animals;
    private int[]? _settlementPlusOne;
    private Stockpiles? _stockpiles;

    // Derived when initializing.
    private bool[] _coast = Array.Empty<bool>();
    private double[] _baseRain = Array.Empty<double>();
    private int[] _animalMask = Array.Empty<int>();
    private int[] _cells = Array.Empty<int>();

    // Work arrays, per cell.
    private double[] _people = Array.Empty<double>();
    private double[] _best = Array.Empty<double>();
    private double[] _users = Array.Empty<double>();
    private double[] _tradeBest = Array.Empty<double>();
    private double[] _tradeRate = Array.Empty<double>();
    private double[] _tradeUsers = Array.Empty<double>();
    private double[] _soldValue = Array.Empty<double>();

    /// <param name="chanceScale">Multiplies every discovery chance (tests use it to see discoveries in short runs).</param>
    public TechnologySystem(TechnologyParameters parameters, TechnologySet technologies, TradeSystem? trade, double chanceScale = 1.0)
    {
        _chanceScale = chanceScale;
        _parameters = parameters;
        Technologies = technologies;
        _trade = trade;
    }

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public TechnologySet Technologies { get; }

    public TechnologyParameters Parameters => _parameters;

    public static TechnologySystem CreateDefault(OccupationSet occupations, TradeSystem? trade) =>
        new TechnologySystem(TechnologyParameters.LoadDefault(), TechnologySet.LoadDefault(occupations), trade);

    public void Register(WorldState state)
    {
        _knowledgeColumns = Technologies.Technologies
            .Select(t => PopulationGroups.AddKnowledgeColumn(state, t.Slug)).ToArray();
        _events = state.AddTable(EventTableName);
        _eventTechnology = _events.AddIntColumn("technology");
        _eventKind = _events.AddIntColumn("kind");
        _eventCell = _events.AddIntColumn("cell");
        _eventTick = _events.AddIntColumn("tick");
        _eventGroup = _events.AddDoubleColumn("group");
        _eventContact = _events.AddDoubleColumn("contact");
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        WorldState state = context.State;
        _map = map;
        _groupTable = state.Table(PopulationGroups.TableName);
        _groups = new PopulationGroups(state);
        _rainfallFactor = state.DoubleField(ClimateSystem.RainfallFactorField);
        _animals = state.HasField(HerdingSystem.AnimalsField) ? state.IntField(HerdingSystem.AnimalsField) : null;
        _settlementPlusOne = state.HasField(SettlementSystem.SettlementField) ? state.IntField(SettlementSystem.SettlementField) : null;
        if (state.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(state, GoodSet.LoadDefault());
        }

        LivestockSet livestock = LivestockSet.LoadDefault();
        _animalMask = Technologies.Technologies.Select(t =>
        {
            int mask = 0;
            foreach (string name in t.Animals)
            {
                mask |= 1 << livestock.IndexOf(name);
            }

            return mask;
        }).ToArray();

        int count = map.CellCount;
        _coast = new bool[count];
        _baseRain = new double[count];
        var cells = new List<int>();
        for (int cell = 0; cell < count; cell++)
        {
            if (!map.IsLand(cell))
            {
                continue;
            }

            cells.Add(cell);
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

        _cells = cells.ToArray();
        _people = new double[count];
        _best = new double[count];
        _users = new double[count];
        _tradeBest = new double[count];
        _tradeRate = new double[count];
        _tradeUsers = new double[count];
        _soldValue = new double[count];
    }

    /// <summary>Whether a cell meets a technology's conditions (not counting who knows what).</summary>
    public bool CellAllows(int cell, int technology)
    {
        Technology t = Technologies.Get(technology);
        WorldMap map = _map!;
        if (t.River && map.River(cell) == RiverSize.None)
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

        if (t.Terrain.Count > 0 && !t.Terrain.Contains(map.Terrain(cell)))
        {
            return false;
        }

        int animals = _animalMask[technology];
        return animals == 0 || (_animals != null && (_animals[cell] & animals) != 0);
    }

    /// <summary>Every recorded discovery and loss, in the order they happened.</summary>
    public IReadOnlyList<TechnologyEvent> Events()
    {
        var list = new List<TechnologyEvent>();
        for (int id = 0; id < _events!.IdCount; id++)
        {
            list.Add(new TechnologyEvent(_eventTechnology![id], (TechnologyEventKind)_eventKind![id], _eventCell![id], _eventTick![id], _eventGroup![id], _eventContact![id]));
        }

        return list;
    }

    /// <summary>The share of a group's people who know a technology.</summary>
    public double Knowledge(int group, int technology) => _knowledgeColumns[technology][group];

    public void Update(SimContext context)
    {
        PopulationGroups groups = _groups!;
        EntityTable table = _groupTable!;
        GeodesicGrid grid = _map!.Grid;
        groups.FillCellTotals(_people);
        Span<int> cellOf = table.IntColumn("cell").Values;
        Span<int> occupationOf = table.IntColumn("occupation").Values;
        Span<double> sizeOf = table.DoubleColumn("size").Values;
        List<(int CellA, int CellB)> links = _trade?.LinksByCell() ?? new List<(int, int)>();
        FillSoldValue(links);
        int groupCount = table.IdCount;
        double inUse = _parameters.InUse;

        for (int tech = 0; tech < Technologies.Count; tech++)
        {
            Technology t = Technologies.Get(tech);
            Span<double> share = _knowledgeColumns[tech].Values;

            // Per cell: the highest share among groups that can use it, and the people using it.
            foreach (int cell in _cells)
            {
                _best[cell] = 0.0;
                _users[cell] = 0.0;
            }

            for (int g = 0; g < groupCount; g++)
            {
                if (!table.IsAlive(g) || !t.CanUse[occupationOf[g]])
                {
                    continue;
                }

                double s = share[g];
                int cell = cellOf[g];
                if (s > _best[cell])
                {
                    _best[cell] = s;
                }

                if (s >= inUse)
                {
                    _users[cell] += sizeOf[g];
                }
            }

            FillTrade(links);

            // Each group that can use it: discover, learn, or forget.
            for (int g = 0; g < groupCount; g++)
            {
                if (!table.IsAlive(g) || !t.CanUse[occupationOf[g]] || sizeOf[g] <= 0.0)
                {
                    continue;
                }

                int cell = cellOf[g];
                double s = share[g];
                double size = sizeOf[g];
                bool usable = CellAllows(cell, tech) && UsesAll(g, t.Requires);

                if (s <= 0.0)
                {
                    if (!usable || t.Chance <= 0.0)
                    {
                        continue;
                    }

                    // Learning from others comes first; otherwise, a chance to discover it.
                    double pull = PullFrom(cell, tech);
                    if (pull > 0.0)
                    {
                        share[g] = Math.Min(1.0, pull);
                        continue;
                    }

                    double reach = _people[cell];
                    foreach (int other in grid.Neighbours(cell))
                    {
                        reach += _people[other];
                    }

                    double chance = Math.Min(1.0, _chanceScale * t.Chance) * Math.Min(1.0, reach / _parameters.DiscoveryPeople);
                    if (context.Rng.Chance(chance))
                    {
                        share[g] = _parameters.DiscoveryShare;
                        RecordDiscovery(tech, cell, context.Tick, size);
                    }

                    continue;
                }

                double before = s;
                if (usable)
                {
                    s += _parameters.Adoption * s * (1.0 - s);
                    s += PullFrom(cell, tech, s);
                }

                // Too few people in contact: the share falls.
                // (The cell's users were counted from last year's shares, so whether this group was one of them goes by those too.)
                double contact = Contact(g, cell, size, before >= inUse);
                if (contact < _parameters.KeepPeople)
                {
                    s -= _parameters.LossRate * (1.0 - contact / _parameters.KeepPeople) * s;
                }

                s = Math.Clamp(s, 0.0, 1.0);
                if (s < 0.001)
                {
                    s = 0.0;
                }

                if (before >= inUse && s < inUse)
                {
                    Record(tech, TechnologyEventKind.Lost, cell, context.Tick, size, contact);
                }

                share[g] = s;
            }
        }
    }

    // How far a group with share s moves toward those around it this year (for s = 0, the share it
    // picks up from them).
    private double PullFrom(int cell, int tech, double s = 0.0)
    {
        GeodesicGrid grid = _map!.Grid;
        double neighbourBest = 0.0;
        foreach (int other in grid.Neighbours(cell))
        {
            neighbourBest = Math.Max(neighbourBest, _best[other]);
        }

        double pull = _parameters.WithinCell * Math.Max(0.0, _best[cell] - s)
            + Technologies.Get(tech).Spread * Math.Max(0.0, neighbourBest - s)
            + _tradeRate[cell] * Math.Max(0.0, _tradeBest[cell] - s);
        return pull;
    }

    // The people keeping a group's knowledge alive: the group, the other users in its cell and in
    // neighbouring cells, and its settlement's trade partners' users.
    private double Contact(int group, int cell, double size, bool usesIt)
    {
        double contact = size + _users[cell] - (usesIt ? size : 0.0) + _tradeUsers[cell];
        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            contact += _users[other];
        }

        return contact;
    }

    private bool UsesAll(int group, IReadOnlyList<int> technologies)
    {
        foreach (int tech in technologies)
        {
            if (_knowledgeColumns[tech][group] < _parameters.InUse)
            {
                return false;
            }
        }

        return true;
    }

    // Each settlement cell's food value of goods sold lately (a saved record, so the same after a load).
    private void FillSoldValue(List<(int CellA, int CellB)> links)
    {
        if (_stockpiles == null || links.Count == 0)
        {
            return;
        }

        foreach ((int a, int b) in links)
        {
            _soldValue[a] = SoldValue(a);
            _soldValue[b] = SoldValue(b);
        }
    }

    private double SoldValue(int cell)
    {
        Stockpiles stockpiles = _stockpiles!;
        double total = 0.0;
        for (int g = 0; g < stockpiles.Goods.Count; g++)
        {
            total += stockpiles.Sold(cell, g) * stockpiles.Goods.Get(g).Value;
        }

        return total;
    }

    // Per settlement cell: the highest share among its trade partners, how fast it learns from
    // them (busier trade, faster), and the people using it among them.
    private void FillTrade(List<(int CellA, int CellB)> links)
    {
        foreach ((int a, int b) in links)
        {
            _tradeBest[a] = _tradeBest[b] = 0.0;
            _tradeRate[a] = _tradeRate[b] = 0.0;
            _tradeUsers[a] = _tradeUsers[b] = 0.0;
        }

        foreach ((int a, int b) in links)
        {
            double rate = _parameters.TradeSpread * Math.Min(1.0, (_soldValue[a] + _soldValue[b]) / _parameters.BusyTrade);
            _tradeBest[a] = Math.Max(_tradeBest[a], _best[b]);
            _tradeBest[b] = Math.Max(_tradeBest[b], _best[a]);
            _tradeRate[a] = Math.Max(_tradeRate[a], rate);
            _tradeRate[b] = Math.Max(_tradeRate[b], rate);
            _tradeUsers[a] += _users[b];
            _tradeUsers[b] += _users[a];
        }
    }

    private void RecordDiscovery(int tech, int cell, long tick, double size)
    {
        bool first = true;
        bool far = true;
        GeodesicGrid grid = _map!.Grid;
        for (int id = 0; id < _events!.IdCount; id++)
        {
            if (_eventTechnology![id] != tech || _eventKind![id] == (int)TechnologyEventKind.Lost)
            {
                continue;
            }

            first = false;
            if (grid.DistanceKm(cell, _eventCell![id]) < _parameters.IndependentKm)
            {
                far = false;
                break;
            }
        }

        if (first || far)
        {
            Record(tech, first ? TechnologyEventKind.First : TechnologyEventKind.Independent, cell, tick, size, 0.0);
        }
    }

    private void Record(int tech, TechnologyEventKind kind, int cell, long tick, double size, double contact)
    {
        int id = _events!.Add();
        _eventTechnology![id] = tech;
        _eventKind![id] = (int)kind;
        _eventCell![id] = cell;
        _eventTick![id] = (int)tick;
        _eventGroup![id] = size;
        _eventContact![id] = contact;
    }
}
