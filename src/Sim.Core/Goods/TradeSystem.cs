using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Goods;

/// <summary>Trade settings, read from trade.json.</summary>
public sealed class TradeParameters
{
    public double MaxLinkCost { get; set; } = 4.0;

    public int LinksPerSettlement { get; set; } = 6;

    public Dictionary<string, double> TerrainCost { get; set; } = new Dictionary<string, double>();

    public double RiverFactor { get; set; } = 0.5;

    public double CostPerStep { get; set; } = 0.02;

    public double SendShare { get; set; } = 0.05;

    public double RouteValue { get; set; } = 1.0;

    public static TradeParameters LoadDefault() => Parse(ContentFiles.ReadText("trade.json"));

    public static TradeParameters Parse(string json)
    {
        TradeParameters p = ContentFiles.Parse<TradeParameters>(json, "trade");
        if (p.MaxLinkCost <= 0.0 || p.LinksPerSettlement < 1 || p.RiverFactor <= 0.0 || p.CostPerStep < 0.0
            || p.SendShare < 0.0 || p.SendShare > 1.0 || p.RouteValue < 0.0)
        {
            throw new InvalidDataException("trade.json: maxLinkCost, linksPerSettlement and riverFactor must be above 0, sendShare 0 to 1, and no negative costs.");
        }

        foreach (string terrain in new[] { "lowland", "hills", "mountains" })
        {
            if (!p.TerrainCost.TryGetValue(terrain, out double cost) || cost <= 0.0)
            {
                throw new InvalidDataException($"trade.json: terrainCost needs a positive cost for {terrain}.");
            }
        }

        return p;
    }
}

/// <summary>A trade route as the viewer sees it: the two settlements' cells and the food value carried a month.</summary>
public readonly record struct TradeRoute(int CellA, int CellB, double Value);

/// <summary>
/// Trade between settlements (milestone 2, step 5). Each settlement links to the few settlements
/// it can reach most cheaply over land; links are rebuilt in January whenever settlements were
/// founded or abandoned. Each season (every three months, for speed) goods sent last season enter
/// the stores; then, along each link, each good moves from the cheaper end to the dearer end when
/// the price gap beats the carrying cost, arriving the next season. Links and route volumes are worked out
/// from the world state and are not saved: after a load they are rebuilt identically, and route
/// volumes (for display only) start again from zero.
/// </summary>
public sealed class TradeSystem : ISimSystem
{
    public const string SystemName = "trade";

    private readonly TradeParameters _parameters;
    private readonly OccupationSet _occupations;
    private GoodSet _goods = null!;
    private Stockpiles? _stockpiles;
    private Prices? _prices;
    private WorldMap? _map;
    private EntityTable? _settlements;
    private IntColumn? _settlementCell;
    private int[] _settlementPlusOne = Array.Empty<int>();
    private double[] _stepCost = Array.Empty<double>();

    private bool _built;
    private (double Cost, int Id)[]?[] _chosen = Array.Empty<(double, int)[]?>();

    // Paths are costed by the cells entered, so the cost back can differ by up to this much.
    private double _maxStepDifference;
    private bool[] _aliveWhenBuilt = Array.Empty<bool>();
    private int[] _linkA = Array.Empty<int>();
    private int[] _linkB = Array.Empty<int>();
    private double[] _linkCost = Array.Empty<double>();
    private double[] _linkValue = Array.Empty<double>();
    private int[] _degree = Array.Empty<int>();

    // Prices of the linked settlements this month, by settlement id then good.
    private double[] _price = Array.Empty<double>();
    private double[] _wanted = Array.Empty<double>();
    private double[] _carry = Array.Empty<double>();

    // Search scratch space for building links.
    private double[] _best = Array.Empty<double>();
    private int[] _seen = Array.Empty<int>();
    private int _stamp;

    // Average relative price gap per good along links, before January's trade and before
    // February's (after January's goods arrived), summed over the run; for exit tests only.
    private double[] _gapJanuary = Array.Empty<double>();
    private double[] _gapFebruary = Array.Empty<double>();

    public TradeSystem(TradeParameters parameters, OccupationSet occupations)
    {
        _parameters = parameters;
        _occupations = occupations;
    }

    public static TradeSystem CreateDefault(OccupationSet occupations) =>
        new TradeSystem(TradeParameters.LoadDefault(), occupations);

    public string Name => SystemName;

    public int IntervalMonths => 3;

    public TradeParameters Parameters => _parameters;

    public void Register(WorldState state)
    {
    }

    public void Initialize(SimContext context)
    {
        WorldState state = context.State;
        _map = context.Map;
        _goods = GoodSet.LoadDefault();
        _stockpiles = new Stockpiles(state, _goods);
        _prices = new Prices(state, _map, PriceParameters.LoadDefault(), CraftRules.LoadDefault(_goods), _occupations);
        _settlements = state.Table(SettlementSystem.TableName);
        _settlementCell = _settlements.IntColumn("cell");
        _settlementPlusOne = state.IntField(SettlementSystem.SettlementField);
        _wanted = new double[_goods.Count];
        _carry = _goods.Goods.Select(g => g.Weight * _parameters.CostPerStep).ToArray();
        _gapJanuary = new double[_goods.Count];
        _gapFebruary = new double[_goods.Count];

        int cells = _map.CellCount;
        _stepCost = new double[cells];
        for (int cell = 0; cell < cells; cell++)
        {
            _stepCost[cell] = !_map.IsLand(cell) ? double.PositiveInfinity : _map.Terrain(cell) switch
            {
                TerrainClass.Hills => _parameters.TerrainCost["hills"],
                TerrainClass.Mountains => _parameters.TerrainCost["mountains"],
                _ => _parameters.TerrainCost["lowland"],
            };
        }

        double cheapest = _parameters.TerrainCost.Values.Min() * Math.Min(1.0, _parameters.RiverFactor);
        _maxStepDifference = _parameters.TerrainCost.Values.Max() - cheapest;
        _best = new double[cells];
        _seen = new int[cells];
    }

    public void Update(SimContext context)
    {
        EntityTable settlements = _settlements!;
        Stockpiles stockpiles = _stockpiles!;

        // Last month's goods arrive.
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (settlements.IsAlive(id))
            {
                stockpiles.Arrive(_settlementCell![id]);
            }
        }

        int month = (int)(context.Tick % GameDate.MonthsPerYear);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_built || (month == 0 && SettlementsChanged()))
        {
            BuildLinks();
        }

        BuildSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (_linkA.Length == 0)
        {
            return;
        }

        start = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkPrices(context.Tick);
        PriceSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (month == 0 || month == 3)
        {
            AddGaps(month == 0 ? _gapJanuary : _gapFebruary);
        }

        int goods = _goods.Count;
        for (int link = 0; link < _linkA.Length; link++)
        {
            int a = _linkA[link];
            int b = _linkB[link];
            int cellA = _settlementCell![a];
            int cellB = _settlementCell[b];
            double cost = _linkCost[link];
            double value = 0.0;
            for (int g = 0; g < goods; g++)
            {
                double pa = _price[a * goods + g];
                double pb = _price[b * goods + g];
                double carry = _carry[g] * cost;
                if (pb - pa > carry)
                {
                    value += Move(cellA, cellB, _degree[a], g, pa, pb, carry);
                }
                else if (pa - pb > carry)
                {
                    value += Move(cellB, cellA, _degree[b], g, pb, pa, carry);
                }
            }

            _linkValue[link] = 0.9 * _linkValue[link] + 0.1 * value;
        }
    }

    // Sends a share of the cheaper end's stock toward the dearer end; returns the food value sent.
    private double Move(int from, int to, int fromDegree, int good, double cheap, double dear, double carry)
    {
        double stock = _stockpiles!.Stock(from, good);
        if (stock <= 0.0)
        {
            return 0.0;
        }

        double scale = Math.Min(1.0, (dear - cheap - carry) / dear);
        double amount = _parameters.SendShare * scale * stock / fromDegree;
        double sent = _stockpiles.Send(from, to, good, amount);
        return sent * 0.5 * (cheap + dear);
    }

    private void WorkPrices(long tick)
    {
        int goods = _goods.Count;
        EntityTable settlements = _settlements!;
        if (_price.Length < settlements.IdCount * goods)
        {
            Array.Resize(ref _price, Math.Max(settlements.IdCount * goods, _price.Length * 2));
        }

        var prices = new Span<double>(_price);
        double[] row = new double[goods];
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (id < _degree.Length && _degree[id] > 0 && settlements.IsAlive(id))
            {
                _prices!.Work(_settlementCell![id], tick, _wanted, row);
                row.CopyTo(prices.Slice(id * goods, goods));
            }
        }
    }

    private void AddGaps(double[] gaps)
    {
        int goods = _goods.Count;
        for (int g = 0; g < goods; g++)
        {
            double value = _goods.Get(g).Value;
            double total = 0.0;
            for (int link = 0; link < _linkA.Length; link++)
            {
                total += Math.Abs(_price[_linkA[link] * goods + g] - _price[_linkB[link] * goods + g]) / value;
            }

            gaps[g] += total / _linkA.Length;
        }
    }

    private bool SettlementsChanged()
    {
        EntityTable settlements = _settlements!;
        if (_aliveWhenBuilt.Length != settlements.IdCount)
        {
            return true;
        }

        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (_aliveWhenBuilt[id] != settlements.IsAlive(id))
            {
                return true;
            }
        }

        return false;
    }

    // Each settlement chooses its cheapest-to-reach settlements; a link counts if either end chose it.
    // A settlement's choice depends only on which settlements stand within its reach, so only the
    // settlements near one that was founded or abandoned choose again. Links are kept in
    // (lower id, higher id) order, so the result is the same as building everything afresh.
    private void BuildLinks()
    {
        EntityTable settlements = _settlements!;
        int ids = settlements.IdCount;
        if (_chosen.Length < ids)
        {
            Array.Resize(ref _chosen, ids);
        }

        var choose = new SortedSet<int>();
        var reached = new List<(double Cost, int Id)>();
        bool all = !_built;
        for (int id = 0; id < ids; id++)
        {
            bool alive = settlements.IsAlive(id);
            bool wasAlive = id < _aliveWhenBuilt.Length && _aliveWhenBuilt[id];
            if (!alive)
            {
                _chosen[id] = null;
            }

            if (all)
            {
                if (alive)
                {
                    choose.Add(id);
                }

                continue;
            }

            if (alive == wasAlive)
            {
                continue;
            }

            // Founded or abandoned: it chooses (if alive), and so does every settlement that might reach it.
            if (alive)
            {
                choose.Add(id);
            }

            Reach(_settlementCell![id], reached, _parameters.MaxLinkCost + _maxStepDifference);
            foreach ((_, int other) in reached)
            {
                if (settlements.IsAlive(other))
                {
                    choose.Add(other);
                }
            }
        }

        foreach (int id in choose)
        {
            Reach(_settlementCell![id], reached, _parameters.MaxLinkCost);
            reached.Sort();
            int count = Math.Min(_parameters.LinksPerSettlement, reached.Count);
            _chosen[id] = reached.GetRange(0, count).ToArray();
        }

        _aliveWhenBuilt = new bool[ids];
        var pairs = new SortedDictionary<(int A, int B), double>();
        for (int id = 0; id < ids; id++)
        {
            if (!settlements.IsAlive(id))
            {
                continue;
            }

            _aliveWhenBuilt[id] = true;
            foreach ((double cost, int other) in _chosen[id] ?? Array.Empty<(double, int)>())
            {
                var key = id < other ? (id, other) : (other, id);
                pairs[key] = pairs.TryGetValue(key, out double known) ? Math.Min(known, cost) : cost;
            }
        }

        var oldValue = new Dictionary<(int, int), double>();
        for (int link = 0; link < _linkA.Length; link++)
        {
            oldValue[(_linkA[link], _linkB[link])] = _linkValue[link];
        }

        _linkA = new int[pairs.Count];
        _linkB = new int[pairs.Count];
        _linkCost = new double[pairs.Count];
        _linkValue = new double[pairs.Count];
        _degree = new int[ids];
        int n = 0;
        foreach (KeyValuePair<(int A, int B), double> pair in pairs)
        {
            (int a, int b) = pair.Key;
            _linkA[n] = a;
            _linkB[n] = b;
            _linkCost[n] = pair.Value;
            _linkValue[n] = oldValue.GetValueOrDefault((a, b));
            _degree[a]++;
            _degree[b]++;
            n++;
        }

        _built = true;
    }

    // The settlements reachable from a cell over land within a cost limit, with their costs.
    private void Reach(int start, List<(double Cost, int Id)> reached, double maxCost)
    {
        reached.Clear();
        WorldMap map = _map!;
        _stamp++;
        var queue = new PriorityQueue<int, double>();
        _best[start] = 0.0;
        _seen[start] = _stamp;
        queue.Enqueue(start, 0.0);
        while (queue.TryDequeue(out int cell, out double cost))
        {
            if (cost > _best[cell])
            {
                continue;
            }

            if (cell != start && _settlementPlusOne[cell] > 0)
            {
                reached.Add((cost, _settlementPlusOne[cell] - 1));
            }

            bool river = map.River(cell) != RiverSize.None;
            foreach (int next in map.Grid.Neighbours(cell))
            {
                double step = _stepCost[next];
                if (double.IsInfinity(step))
                {
                    continue;
                }

                if (river && map.River(next) != RiverSize.None)
                {
                    step *= _parameters.RiverFactor;
                }

                double total = cost + step;
                if (total > maxCost || (_seen[next] == _stamp && total >= _best[next]))
                {
                    continue;
                }

                _seen[next] = _stamp;
                _best[next] = total;
                queue.Enqueue(next, total);
            }
        }
    }

    /// <summary>Links that carry at least the route value a month on average, by their settlements' cells.</summary>
    public List<TradeRoute> Routes()
    {
        var routes = new List<TradeRoute>();
        for (int link = 0; link < _linkA.Length; link++)
        {
            if (_linkValue[link] >= _parameters.RouteValue && _settlements!.IsAlive(_linkA[link]) && _settlements.IsAlive(_linkB[link]))
            {
                routes.Add(new TradeRoute(_settlementCell![_linkA[link]], _settlementCell[_linkB[link]], _linkValue[link]));
            }
        }

        return routes;
    }

    public int LinkCount => _linkA.Length;

    /// <summary>Wall-clock seconds spent building links and working out prices (for profiling only).</summary>
    public double BuildSeconds { get; private set; }

    public double PriceSeconds { get; private set; }

    /// <summary>For each good, the run's summed average relative price gap along links before January's trade.</summary>
    public IReadOnlyList<double> GapBeforeTrade => _gapJanuary;

    /// <summary>For each good, the same a season later (April), after January's goods arrived.</summary>
    public IReadOnlyList<double> GapAfterTrade => _gapFebruary;
}
