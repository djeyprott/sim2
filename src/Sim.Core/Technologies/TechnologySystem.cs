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
    public const string StatusTableName = "technology.status";

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
    private int[]? _settlementPlusOne;
    private Stockpiles? _stockpiles;

    // Derived when initializing.
    private TechnologyConditions? _conditions;
    private int[] _cells = Array.Empty<int>();

    // Work arrays, per cell.
    private double[] _people = Array.Empty<double>();
    private readonly List<int> _peopled = new List<int>();
    private int[] _firstGroupPlusOne = Array.Empty<int>();
    private bool[][] _canUse = Array.Empty<bool[]>();
    private bool[] _anyUse = Array.Empty<bool>();

    // Saved: one row per technology, 1 where some group that can use it knew any of it after last
    // year (so the next year must count who knows it). Saved because groups can hold knowledge
    // unseen (people who changed occupation), so it cannot be worked out again after a load.
    private EntityTable? _status;
    private IntColumn? _statusKnown;
    private double[] _best = Array.Empty<double>();
    private double[] _users = Array.Empty<double>();
    private double[] _tradeBest = Array.Empty<double>();
    private double[] _tradeRate = Array.Empty<double>();
    private double[] _tradeUsers = Array.Empty<double>();

    // Cells whose trade values were set last year, cleared before this year's links are filled in
    // (links are rebuilt every few years, and a cell that lost its links must not keep old values).
    private readonly List<int> _tradeCells = new List<int>();
    private double[] _soldValue = Array.Empty<double>();

    // Per technology needing goods near: the cells whose trade partners hold or gather them, worked
    // out each year from the links and stores (so the same after a load), and the cells marked.
    private bool[]?[] _partnerSupplied = Array.Empty<bool[]?>();
    private List<int>[] _partnerMarked = Array.Empty<List<int>>();

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

    /// <summary>For profiling: discovery draws and group visits made so far.</summary>
    public long DiscoveryChecks { get; private set; }

    public long GroupVisits { get; private set; }

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
        _status = state.AddTable(StatusTableName);
        _statusKnown = _status.AddIntColumn("knownAnywhere");
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        WorldState state = context.State;
        _map = map;
        _groupTable = state.Table(PopulationGroups.TableName);
        _groups = new PopulationGroups(state);
        _settlementPlusOne = state.HasField(SettlementSystem.SettlementField) ? state.IntField(SettlementSystem.SettlementField) : null;
        if (state.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(state, GoodSet.LoadDefault());
        }

        _conditions = new TechnologyConditions(state, map, Technologies);
        int count = map.CellCount;
        var cells = new List<int>();
        for (int cell = 0; cell < count; cell++)
        {
            if (map.IsLand(cell))
            {
                cells.Add(cell);
            }
        }

        _cells = cells.ToArray();
        _firstGroupPlusOne = state.IntField(PopulationGroups.FirstGroupField);
        _canUse = Technologies.Technologies.Select(t => t.CanUse.ToArray()).ToArray();
        int occupations = _canUse.Length == 0 ? 0 : _canUse[0].Length;
        _anyUse = Enumerable.Range(0, occupations).Select(o => _canUse.Any(c => c[o])).ToArray();
        for (int t = 0; t < Technologies.Count; t++)
        {
            _statusKnown![_status!.Add()] = 0;
        }
        _people = new double[count];
        _best = new double[count];
        _users = new double[count];
        _tradeBest = new double[count];
        _tradeRate = new double[count];
        _tradeUsers = new double[count];
        _soldValue = new double[count];
        _partnerSupplied = Enumerable.Range(0, Technologies.Count)
            .Select(t => _conditions.NeedsSupply(t) ? new bool[count] : null).ToArray();
        _partnerMarked = Enumerable.Range(0, Technologies.Count).Select(_ => new List<int>()).ToArray();
    }

    /// <summary>
    /// Whether a cell meets a technology's conditions (not counting who knows what), counting the
    /// goods its trade partners hold or gather as near, as of the last update.
    /// </summary>
    public bool CellAllows(int cell, int technology) =>
        _conditions!.AllowsBesidesSupply(cell, technology)
        && (_conditions.SuppliedHere(cell, technology) || (_partnerSupplied[technology] is bool[] partner && partner[cell]));

    // Fills the cells whose trade partners have a technology's goods near (see TechnologyConditions.SuppliedHere).
    private void FillPartnerSupply(int tech, List<(int CellA, int CellB)> links)
    {
        bool[]? partner = _partnerSupplied[tech];
        if (partner == null)
        {
            return;
        }

        List<int> marked = _partnerMarked[tech];
        foreach (int cell in marked)
        {
            partner[cell] = false;
        }

        marked.Clear();
        if (Technologies.Get(tech).NearGoods.Count == 0)
        {
            return;
        }

        // Each pass reaches one link further: partners of supplied cells, then their partners.
        var added = new List<int>();
        for (int hop = 0; hop < _parameters.SupplyLinks; hop++)
        {
            added.Clear();
            foreach ((int a, int b) in links)
            {
                if (!partner[b] && (partner[a] || _conditions!.SuppliedHere(a, tech)))
                {
                    added.Add(b);
                }

                if (!partner[a] && (partner[b] || _conditions!.SuppliedHere(b, tech)))
                {
                    added.Add(a);
                }
            }

            foreach (int cell in added)
            {
                if (!partner[cell])
                {
                    partner[cell] = true;
                    marked.Add(cell);
                }
            }
        }
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
        Span<int> occupationOf = table.IntColumn("occupation").Values;
        Span<int> nextPlusOne = table.IntColumn("nextPlusOne").Values;
        Span<double> sizeOf = table.DoubleColumn("size").Values;
        int[] firstPlusOne = _firstGroupPlusOne;
        List<(int CellA, int CellB)> links = _trade?.LinksByCell() ?? new List<(int, int)>();
        foreach (int cell in _tradeCells)
        {
            _tradeBest[cell] = _tradeRate[cell] = _tradeUsers[cell] = 0.0;
        }

        _tradeCells.Clear();
        foreach ((int a, int b) in links)
        {
            _tradeCells.Add(a);
            _tradeCells.Add(b);
        }

        FillSoldValue(links);
        double inUse = _parameters.InUse;

        // Only cells holding a group that could use some technology are visited, through their own
        // lists of groups (raw arrays, for speed: this runs over every such group for every
        // technology every year). Other cells count as knowing and using nothing, for their neighbours.
        _peopled.Clear();
        foreach (int cell in _cells)
        {
            bool relevant = false;
            for (int g = firstPlusOne[cell] - 1; g >= 0 && !relevant; g = nextPlusOne[g] - 1)
            {
                relevant = _anyUse[occupationOf[g]] && sizeOf[g] > 0.0;
            }

            if (relevant)
            {
                _peopled.Add(cell);
            }
            else
            {
                _best[cell] = 0.0;
                _users[cell] = 0.0;
            }
        }

        for (int tech = 0; tech < Technologies.Count; tech++)
        {
            Technology t = Technologies.Get(tech);
            Span<double> share = _knowledgeColumns[tech].Values;

            // Per cell: the highest share among groups that can use it, and the people using it. If
            // nobody anywhere knew it last year, that is all zero and needs no counting.
            bool[] canUse = _canUse[tech];
            bool anyKnown = false;
            foreach (int cell in _peopled)
            {
                double best = 0.0;
                double users = 0.0;
                if (_statusKnown![tech] != 0)
                {
                    for (int g = firstPlusOne[cell] - 1; g >= 0; g = nextPlusOne[g] - 1)
                    {
                        if (canUse[occupationOf[g]])
                        {
                            double s = share[g];
                            best = Math.Max(best, s);
                            users += s >= inUse ? sizeOf[g] : 0.0;
                        }
                    }
                }

                _best[cell] = best;
                _users[cell] = users;
                anyKnown |= best > 0.0;
            }

            // Nobody knows it and nobody could discover it: nothing to do this year.
            if (!anyKnown && t.Chance <= 0.0)
            {
                continue;
            }

            FillTrade(links);
            FillPartnerSupply(tech, links);

            // Each group that can use it: discover, learn, or forget.
            bool knownAfter = false;
            foreach (int cell in _peopled)
            {
                // Where nobody near knows it and the cell does not allow it, nothing can happen.
                bool cellAllows = CellAllows(cell, tech);
                if (!cellAllows && _best[cell] <= 0.0)
                {
                    continue;
                }

                // Nobody here, next door or among its trade partners knows it, and the goods it
                // needs to be discovered are not near: nobody here can learn or find it.
                if (_best[cell] <= 0.0 && _tradeBest[cell] <= 0.0 && !_conditions!.NearForDiscovery(cell, tech, context.Tick) && !NeighbourKnows(cell))
                {
                    continue;
                }

                double neighbourBest = -1.0;
                for (int g = firstPlusOne[cell] - 1; g >= 0; g = nextPlusOne[g] - 1)
                {
                    double size = sizeOf[g];
                    if (!canUse[occupationOf[g]] || size <= 0.0)
                    {
                        continue;
                    }

                    GroupVisits++;
                    double s = share[g];
                    if (s <= 0.0 && !cellAllows)
                    {
                        continue;
                    }

                    bool usable = cellAllows && UsesAll(g, t.Requires);
                    if (neighbourBest < 0.0)
                    {
                        neighbourBest = 0.0;
                        foreach (int other in grid.Neighbours(cell))
                        {
                            neighbourBest = Math.Max(neighbourBest, _best[other]);
                        }
                    }

                    if (s <= 0.0)
                    {
                        if (!usable)
                        {
                            continue;
                        }

                        // Learning from others comes first; otherwise, a chance to discover it.
                        double pull = Pull(cell, tech, neighbourBest, 0.0);
                        if (pull > 0.0)
                        {
                            share[g] = Math.Min(1.0, pull);
                            knownAfter = true;
                            continue;
                        }

                        if (t.Chance <= 0.0 || !_conditions!.NearForDiscovery(cell, tech, context.Tick))
                        {
                            continue;
                        }

                        double reach = _people[cell];
                        foreach (int other in grid.Neighbours(cell))
                        {
                            reach += _people[other];
                        }

                        double chance = Math.Min(1.0, _chanceScale * t.Chance) * Math.Min(1.0, reach / _parameters.DiscoveryPeople);
                        DiscoveryChecks++;
                        if (context.Rng.Chance(chance))
                        {
                            share[g] = _parameters.DiscoveryShare;
                            knownAfter = true;
                            RecordDiscovery(tech, cell, context.Tick, size);
                        }

                        continue;
                    }

                    double before = s;
                    if (usable)
                    {
                        s += _parameters.Adoption * s * (1.0 - s);
                        s += Pull(cell, tech, neighbourBest, s);
                    }

                    // Where the cell does not meet its conditions, a group slowly forgets it.
                    if (!cellAllows)
                    {
                        s -= _parameters.UnusedLoss * s;
                    }

                    // Too few people in contact: the share falls. (The cell's users were counted from
                    // last year's shares, so whether this group was one of them goes by those too.)
                    double contact = Contact(cell, size, before >= inUse);
                    if (contact < _parameters.KeepPeople)
                    {
                        s -= _parameters.LossRate * (1.0 - contact / _parameters.KeepPeople) * s;
                    }

                    s = Math.Clamp(s, 0.0, 1.0);
                    if (s < 0.001)
                    {
                        s = 0.0;
                    }

                    // Recorded only where it could still be used: forgetting what the cell does not
                    // allow (no copper near, no river) is not a loss of knowledge kept alive by contact.
                    if (before >= inUse && s < inUse && cellAllows)
                    {
                        Record(tech, TechnologyEventKind.Lost, cell, context.Tick, size, contact);
                    }

                    share[g] = s;
                    knownAfter |= s > 0.0;
                }
            }

            // Whether any group that can use it held some, for next year.
            _statusKnown![tech] = knownAfter || anyKnown ? 1 : 0;
        }
    }

    private bool NeighbourKnows(int cell)
    {
        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            if (_best[other] > 0.0)
            {
                return true;
            }
        }

        return false;
    }

    // How far a group with share s moves toward those around it this year (for s = 0, the share it
    // picks up from them).
    private double Pull(int cell, int tech, double neighbourBest, double s) =>
        _parameters.WithinCell * Math.Max(0.0, _best[cell] - s)
        + Technologies.Get(tech).Spread * Math.Max(0.0, neighbourBest - s)
        + _tradeRate[cell] * Math.Max(0.0, _tradeBest[cell] - s);

    // The people keeping a group's knowledge alive: the group, the other users in its cell and in
    // neighbouring cells, and its settlement's trade partners' users.
    private double Contact(int cell, double size, bool usesIt)
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
