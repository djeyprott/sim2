using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

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

    public int RebuildYears { get; set; } = 5;

    public double MinMargin { get; set; } = 0.05;

    /// <summary>Sea cells in a row anyone's boats cross (paddling), and the cost of each against a cell of lowland.</summary>
    public int PaddleSeaCells { get; set; } = 2;

    public double PaddleSeaCost { get; set; } = 1.0;

    /// <summary>The most sea cells in a row a trade link crosses, whatever the boats.</summary>
    public int MaxSeaCells { get; set; } = 10;

    public static TradeParameters LoadDefault() => Parse(ContentFiles.ReadText("trade.json"));

    public static TradeParameters Parse(string json)
    {
        TradeParameters p = ContentFiles.Parse<TradeParameters>(json, "trade");
        if (p.MaxLinkCost <= 0.0 || p.LinksPerSettlement < 1 || p.RiverFactor <= 0.0 || p.CostPerStep < 0.0
            || p.SendShare < 0.0 || p.SendShare > 1.0 || p.RouteValue < 0.0 || p.RebuildYears < 1 || p.MinMargin < 0.0 || p.MinMargin >= 1.0
            || p.PaddleSeaCells < 0 || p.PaddleSeaCost <= 0.0 || p.MaxSeaCells < p.PaddleSeaCells)
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
/// it can reach most cheaply over land; links are rebuilt every few Januaries (trade.json), around
/// settlements founded or abandoned since. Each season (every three months, for speed) goods sent last season enter
/// the stores; then, along each link, food and luxuries move from the cheaper end to the dearer end
/// when the price gap beats the carrying cost, arriving the next season. Other goods move the same
/// way once a year, in January, sending what four seasons would (also for speed: they change slowly). Links and route volumes are worked out
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
    private int[] _linkedPlusOne = Array.Empty<int>();

    // Saved: the boats each settlement had when it last chose its links (sea cells in a row, and
    // the cost of each), so links come out the same after a load.
    private int[] _boatRange = Array.Empty<int>();
    private double[] _boatCost = Array.Empty<double>();
    private Crossings? _crossings;
    private bool[] _shore = Array.Empty<bool>();
    private TechnologyEffect[] _rangeEffects = Array.Empty<TechnologyEffect>();
    private TechnologyEffect[] _seaCostEffects = Array.Empty<TechnologyEffect>();
    private bool[] _viaSea = Array.Empty<bool>();
    private bool[] _linkSea = Array.Empty<bool>();
    private double[] _stepCost = Array.Empty<double>();

    private bool _built;
    private (double Cost, int Id, bool Sea)[]?[] _chosen = Array.Empty<(double, int, bool)[]?>();

    // Paths are costed by the cells entered, so the cost back can differ by up to this much.
    private double _maxStepDifference;
    private double _bestSeaCost = 1.0;
    private bool[] _aliveWhenBuilt = Array.Empty<bool>();
    private int[] _linkA = Array.Empty<int>();
    private int[] _linkB = Array.Empty<int>();
    private double[] _linkCost = Array.Empty<double>();
    private double[] _linkValue = Array.Empty<double>();

    // The same for each good separately: [link * goods + good], as of the season in _goodSeason,
    // decayed only when the link next carries the good or is read (most link-goods carry nothing
    // most seasons, and decaying them all each season cost about a third of trade's time).
    private double[] _goodValue = Array.Empty<double>();
    private int[] _goodSeason = Array.Empty<int>();
    private int _season;
    private static readonly double[] Decay = Enumerable.Range(0, 400).Select(k => Math.Pow(0.9, k)).ToArray();
    private int[] _degree = Array.Empty<int>();

    // Each settlement's links (by settlement id), rebuilt with the links: those of settlement id
    // run from _partnerStart[id] to _partnerStart[id + 1] in _partner.
    private int[] _partnerStart = new int[1];
    private int[] _partner = Array.Empty<int>();

    // Prices of the linked settlements this month, by settlement id then good.
    private double[] _price = Array.Empty<double>();
    private double[] _wanted = Array.Empty<double>();
    private double[] _carry = Array.Empty<double>();
    private int[] _allGoods = Array.Empty<int>();
    private int[] _seasonalGoods = Array.Empty<int>();
    private double _yearlySendShare;

    // Land carrying technology (the wheel, riding): each settlement's factor on carrying costs,
    // worked out every season from the world state; a link costs the larger of its two ends'.
    private TechnologyUse? _technology;
    private TechnologyEffect[] _carryingEffects = Array.Empty<TechnologyEffect>();
    private double[] _carryFactor = Array.Empty<double>();

    // Search scratch space for building links.
    private double[] _best = Array.Empty<double>();
    private int[] _seen = Array.Empty<int>();
    private int _stamp;
    // Sends worth less than this much food are not made: they move almost nothing and were four
    // fifths of trade's time (800 million sends in 5,000 years).
    private const double MinimumSendValue = 0.05;
    private readonly List<int> _destinations = new List<int>();
    private int[] _destinationStamp = Array.Empty<int>();
    private int _destinationRound = 1;
    private readonly PriorityQueue<int, double> _queue = new PriorityQueue<int, double>();

    // Average relative price gap per good along links, before January's trade and before
    // February's (after January's goods arrived), summed over the run; for exit tests only.
    private double[] _gapJanuary = Array.Empty<double>();
    private double[] _gapFebruary = Array.Empty<double>();
    private double[] _sentJanuary = Array.Empty<double>();
    private bool _january;
    private readonly List<(int Link, int Good)> _tradedPairs = new List<(int, int)>();

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

    /// <summary>Settlement id plus one in each cell, as recorded when trade links were last rebuilt.</summary>
    public const string LinkedSettlementField = "trade.linkedSettlement";

    public void Register(WorldState state)
    {
        _linkedPlusOne = state.AddIntField(LinkedSettlementField);
        _boatRange = state.AddIntField(BoatRangeField);
        _boatCost = state.AddDoubleField(BoatCostField);
    }

    /// <summary>Sea cells in a row, and the cost of each, of each settlement's boats when it last chose its links.</summary>
    public const string BoatRangeField = "trade.boatRange";

    public const string BoatCostField = "trade.boatCost";

    public void Initialize(SimContext context)
    {
        WorldState state = context.State;
        _map = context.Map;
        _goods = GoodSet.LoadDefault();
        _stockpiles = new Stockpiles(state, _goods);
        _prices = new Prices(state, _map, PriceParameters.LoadDefault(), CraftRules.LoadDefault(_goods), _occupations);
        _settlements = state.Table(SettlementSystem.TableName);
        _settlementCell = _settlements.IntColumn("cell");
        _wanted = new double[_goods.Count];
        _carry = _goods.Goods.Select(g => g.Weight * _parameters.CostPerStep).ToArray();
        _allGoods = Enumerable.Range(0, _goods.Count).ToArray();
        _seasonalGoods = _goods.Goods.Where(Seasonal).Select(g => g.Index).ToArray();

        // Everyday goods and materials move once a year, sending what four seasons of sendShare would.
        _yearlySendShare = 1.0 - Math.Pow(1.0 - _parameters.SendShare, 4.0);
        _technology = TechnologyUse.TryCreate(state, _map);
        if (_technology != null)
        {
            _carryingEffects = _technology.WithEffect(TechnologySet.LandCarrying);
            _rangeEffects = _technology.WithEffect(TechnologySet.SeaRange);
            _seaCostEffects = _technology.WithEffect(TechnologySet.SeaCost);
        }

        SeaMap sea = SeaMap.Of(_map);
        _shore = sea.Shore;
        _crossings = _parameters.MaxSeaCells > 0 ? sea.CrossingsWithin(_parameters.MaxSeaCells) : null;
        _viaSea = new bool[_map.CellCount];

        _gapJanuary = new double[_goods.Count];
        _gapFebruary = new double[_goods.Count];
        _sentJanuary = new double[_goods.Count];

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

        _bestSeaCost = Math.Min(_parameters.PaddleSeaCost, _seaCostEffects.Select(e => e.Size).DefaultIfEmpty(_parameters.PaddleSeaCost).Min());
        double cheapest = _parameters.TerrainCost.Values.Min() * Math.Min(1.0, _parameters.RiverFactor);
        _maxStepDifference = _parameters.TerrainCost.Values.Max() - cheapest;
        _best = new double[cells];
        _destinationStamp = new int[cells];
        _seen = new int[cells];
    }

    public void Update(SimContext context)
    {
        EntityTable settlements = _settlements!;
        Stockpiles stockpiles = _stockpiles!;

        // Last season's goods arrive. After a load, every settlement is checked once, since the
        // list of where goods were sent is not saved. Either way they arrive in cell order, so the
        // ledger's sums are added in the same order and a loaded run matches to the last bit.
        var arriving = new List<int>();
        if (!_built)
        {
            for (int id = 0; id < settlements.IdCount; id++)
            {
                if (settlements.IsAlive(id))
                {
                    arriving.Add(_settlementCell![id]);
                }
            }
        }
        else
        {
            arriving.AddRange(_destinations);
        }

        arriving.Sort();
        int previous = -1;
        foreach (int cell in arriving)
        {
            if (cell != previous)
            {
                stockpiles.Arrive(cell);
                previous = cell;
            }
        }

        _destinations.Clear();
        int month = (int)(context.Tick % GameDate.MonthsPerYear);
        if (month == 0)
        {
            for (int id = 0; id < settlements.IdCount; id++)
            {
                if (settlements.IsAlive(id))
                {
                    stockpiles.AgeSales(_settlementCell![id]);
                }
            }
        }
        long year = context.Tick / GameDate.MonthsPerYear;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_built)
        {
            BuildLinks(update: false, new List<int>());
        }

        if (month == 0 && year % _parameters.RebuildYears == 0)
        {
            List<int> newBoats = BoatsChanged();
            if (newBoats.Count > 0 || SettlementsChanged())
            {
                BuildLinks(update: true, newBoats);
            }
        }

        BuildSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
        if (_linkA.Length == 0)
        {
            stockpiles.FlushTrade();
            return;
        }

        FillCarryFactors();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        WorkPrices(context.Tick, _january = month == 0);
        PriceSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
        _destinationRound++;
        int goods = _goods.Count;
        int[] traded = _january ? _allGoods : _seasonalGoods;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int link = 0; link < _linkA.Length; link++)
        {
            int a = _linkA[link];
            int b = _linkB[link];
            if (!settlements.IsAlive(a) || !settlements.IsAlive(b))
            {
                continue;
            }

            int cellA = _settlementCell![a];
            int cellB = _settlementCell[b];
            double cost = _linkCost[link] * Math.Max(_carryFactor[a], _carryFactor[b]);
            double value = 0.0;
            foreach (int g in traded)
            {
                double pa = _price[a * goods + g];
                double pb = _price[b * goods + g];
                double carry = _carry[g] * cost;
                double moved = 0.0;
                if (pb - pa > carry)
                {
                    moved = Move(cellA, cellB, _degree[a], g, pa, pb, carry);
                }
                else if (pa - pb > carry)
                {
                    moved = Move(cellB, cellA, _degree[b], g, pb, pa, carry);
                }

                if (moved > 0.0)
                {
                    int slot = link * goods + g;
                    _goodValue[slot] = GoodValue(slot) * 0.9 + 0.1 * moved;
                    _goodSeason[slot] = _season + 1;
                    value += moved;
                    if (_january && MeasureGaps)
                    {
                        _tradedPairs.Add((link, g));
                    }
                }
            }

            _linkValue[link] = 0.9 * _linkValue[link] + 0.1 * value;
            LinkGoodVisits += traded.Length;
        }

        _season++;

        LoopSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
        _stockpiles!.FlushTrade();

        // In January, the gaps again, counting goods on their way as already there.
        if (month == 0)
        {
            start = System.Diagnostics.Stopwatch.GetTimestamp();
            AddGapsAfterTrade(context.Tick);
            GapSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
    }

    private void AddGapsAfterTrade(long tick)
    {
        if (_tradedPairs.Count == 0)
        {
            return;
        }

        int goods = _goods.Count;
        EntityTable settlements = _settlements!;
        double[] row = new double[goods];
        var after = new double[settlements.IdCount * goods];
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (id < _degree.Length && _degree[id] > 0 && settlements.IsAlive(id))
            {
                _prices!.Work(_settlementCell![id], tick, _wanted);
                for (int g = 0; g < goods; g++)
                {
                    after[id * goods + g] = _prices.PriceWithIncoming(g);
                }
            }
        }

        // Along each link that carried a good this January: its price gap before the trade, and after
        // it (counting goods on their way as arrived), relative to the good's value.
        foreach ((int link, int g) in _tradedPairs)
        {
            int a = _linkA[link] * goods + g;
            int b = _linkB[link] * goods + g;
            double value = _goods.Get(g).Value;
            _gapJanuary[g] += Math.Abs(_price[a] - _price[b]) / value;
            _gapFebruary[g] += Math.Abs(after[a] - after[b]) / value;
        }

        _tradedPairs.Clear();
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
        if (scale < _parameters.MinMargin)
        {
            return 0.0;
        }

        double amount = (Seasonal(_goods.Get(good)) ? _parameters.SendShare : _yearlySendShare) * scale * stock / fromDegree;
        if (amount * dear < MinimumSendValue)
        {
            return 0.0;
        }

        Sends++;
        double sent = _stockpiles.Send(from, to, good, amount);
        if (_january)
        {
            _sentJanuary[good] += sent;
        }

        if (_destinationStamp[to] != _destinationRound)
        {
            _destinationStamp[to] = _destinationRound;
            _destinations.Add(to);
        }

        return sent * 0.5 * (cheap + dear);
    }

    // Food and luxuries trade every season: food because hunger cannot wait, luxuries because they are
    // light and valuable and travel far by being passed on, a step each season. Other goods trade
    // only in January.
    private static bool Seasonal(Good good) => good.Food || good.NeedTier == NeedTier.Luxury;

    // Every season, the prices of the goods traded every season; each January, all prices.
    private void WorkPrices(long tick, bool all)
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
                if (all)
                {
                    _prices!.Work(_settlementCell![id], tick, _wanted, row);
                    row.CopyTo(prices.Slice(id * goods, goods));
                }
                else
                {
                    Span<double> slice = prices.Slice(id * goods, goods);
                    slice.CopyTo(row);
                    _prices!.WorkSeasonal(_settlementCell![id], tick, row);
                    row.CopyTo(slice);
                }
            }
        }
    }

    // A standing settlement's boats now: the paddling everyone has, or better from the technology
    // its people know (the longest range and the cheapest crossing).
    private (int Range, double Cost) Boats(int cell)
    {
        int range = _parameters.PaddleSeaCells;
        double cost = _parameters.PaddleSeaCost;
        if (_technology != null)
        {
            foreach (TechnologyEffect e in _rangeEffects)
            {
                if (e.Size > range && _technology.Conditions.Allows(cell, e.Technology) && _technology.KnownInCell(cell, e.Technology))
                {
                    range = (int)e.Size;
                }
            }

            foreach (TechnologyEffect e in _seaCostEffects)
            {
                if (e.Size < cost && _technology.Conditions.Allows(cell, e.Technology) && _technology.KnownInCell(cell, e.Technology))
                {
                    cost = e.Size;
                }
            }
        }

        return (Math.Min(range, _parameters.MaxSeaCells), cost);
    }

    // Standing settlements recorded at the last rebuild whose boats have changed since: their new
    // boats are recorded, and they choose their links again.
    private List<int> BoatsChanged()
    {
        var changed = new List<int>();
        EntityTable settlements = _settlements!;
        for (int id = 0; id < settlements.IdCount && id < _aliveWhenBuilt.Length; id++)
        {
            if (!settlements.IsAlive(id) || !_aliveWhenBuilt[id])
            {
                continue;
            }

            int cell = _settlementCell![id];
            (int range, double cost) = Boats(cell);
            if (range != _boatRange[cell] || cost != _boatCost[cell])
            {
                _boatRange[cell] = range;
                _boatCost[cell] = cost;
                changed.Add(id);
            }
        }

        return changed;
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

    // Each linked settlement's land carrying factor from the technology its people use (1: none).
    private void FillCarryFactors()
    {
        EntityTable settlements = _settlements!;
        if (_carryFactor.Length < settlements.IdCount)
        {
            _carryFactor = new double[settlements.IdCount];
        }

        for (int id = 0; id < settlements.IdCount; id++)
        {
            _carryFactor[id] = _carryingEffects.Length == 0 || !settlements.IsAlive(id)
                ? 1.0 : _technology!.CellFactor(_settlementCell![id], _carryingEffects);
        }
    }

    // Each settlement chooses its cheapest-to-reach settlements; a link counts if either end chose it.
    // Links are built over the settlements recorded in the saved "trade.linkedSettlement" field when
    // links were last rebuilt, so after a load they come out exactly as before. A settlement's
    // choice depends only on which settlements stand within its reach, so on a rebuild only those
    // near one founded or abandoned since choose again. Links are kept in (lower id, higher id)
    // order, so the result does not depend on how it was reached.
    private void BuildLinks(bool update, List<int> newBoats)
    {
        EntityTable settlements = _settlements!;
        int ids = settlements.IdCount;
        if (_chosen.Length < ids)
        {
            Array.Resize(ref _chosen, ids);
        }

        var choose = new SortedSet<int>(newBoats);
        var reached = new List<(double Cost, int Id, bool Sea)>();
        if (!update)
        {
            // Everything recorded at the last rebuild chooses afresh.
            _aliveWhenBuilt = new bool[ids];
            for (int cell = 0; cell < _linkedPlusOne.Length; cell++)
            {
                if (_linkedPlusOne[cell] > 0)
                {
                    int id = _linkedPlusOne[cell] - 1;
                    _aliveWhenBuilt[id] = true;
                    choose.Add(id);
                }
            }
        }
        else
        {
            // Record the settlements standing now, and find who must choose again.
            var changed = new List<int>();
            for (int id = 0; id < ids; id++)
            {
                bool alive = settlements.IsAlive(id);
                bool wasAlive = id < _aliveWhenBuilt.Length && _aliveWhenBuilt[id];
                if (alive == wasAlive)
                {
                    continue;
                }

                int cell = _settlementCell![id];
                if (alive)
                {
                    _linkedPlusOne[cell] = id + 1;
                    (_boatRange[cell], _boatCost[cell]) = Boats(cell);
                    choose.Add(id);
                }
                else
                {
                    if (_linkedPlusOne[cell] == id + 1)
                    {
                        _linkedPlusOne[cell] = 0;
                    }

                    _chosen[id] = null;
                }

                changed.Add(cell);
            }

            _aliveWhenBuilt = new bool[ids];
            for (int id = 0; id < ids; id++)
            {
                _aliveWhenBuilt[id] = settlements.IsAlive(id);
            }

            // Whoever could reach a changed place may choose differently: searched with the best boats
            // anyone may have, so none is missed.
            foreach (int cell in changed)
            {
                Reach(cell, reached, _parameters.MaxLinkCost + _maxStepDifference, _parameters.MaxSeaCells, _bestSeaCost);
                foreach ((_, int other, _) in reached)
                {
                    choose.Add(other);
                }
            }
        }

        foreach (int id in choose)
        {
            int from = _settlementCell![id];
            Reach(from, reached, _parameters.MaxLinkCost, _boatRange[from], _boatCost[from]);
            reached.Sort();
            int count = Math.Min(_parameters.LinksPerSettlement, reached.Count);
            _chosen[id] = reached.GetRange(0, count).ToArray();
        }

        var pairs = new SortedDictionary<(int A, int B), (double Cost, bool Sea)>();
        for (int id = 0; id < ids; id++)
        {
            if (!_aliveWhenBuilt[id])
            {
                continue;
            }

            foreach ((double cost, int other, bool sea) in _chosen[id] ?? Array.Empty<(double, int, bool)>())
            {
                var key = id < other ? (id, other) : (other, id);
                if (!pairs.TryGetValue(key, out var known) || cost < known.Cost || (cost == known.Cost && sea && !known.Sea))
                {
                    pairs[key] = (cost, sea);
                }
            }
        }

        var oldLink = new Dictionary<(int, int), int>();
        for (int link = 0; link < _linkA.Length; link++)
        {
            oldLink[(_linkA[link], _linkB[link])] = link;
        }

        int goods = _goods.Count;
        double[] oldValue = _linkValue;
        double[] oldGoodValue = _goodValue;
        int[] oldGoodSeason = _goodSeason;
        _linkA = new int[pairs.Count];
        _linkB = new int[pairs.Count];
        _linkCost = new double[pairs.Count];
        _linkSea = new bool[pairs.Count];
        _linkValue = new double[pairs.Count];
        _goodValue = new double[pairs.Count * goods];
        _goodSeason = new int[pairs.Count * goods];
        _degree = new int[ids];
        int n = 0;
        foreach (KeyValuePair<(int A, int B), (double Cost, bool Sea)> pair in pairs)
        {
            (int a, int b) = pair.Key;
            _linkA[n] = a;
            _linkB[n] = b;
            _linkCost[n] = pair.Value.Cost;
            _linkSea[n] = pair.Value.Sea;
            if (oldLink.TryGetValue((a, b), out int old))
            {
                _linkValue[n] = oldValue[old];
                Array.Copy(oldGoodValue, old * goods, _goodValue, n * goods, goods);
                Array.Copy(oldGoodSeason, old * goods, _goodSeason, n * goods, goods);
            }

            _degree[a]++;
            _degree[b]++;
            n++;
        }

        _partnerStart = new int[ids + 1];
        for (int id = 0; id < ids; id++)
        {
            _partnerStart[id + 1] = _partnerStart[id] + _degree[id];
        }

        _partner = new int[_partnerStart[ids]];
        var next = (int[])_partnerStart.Clone();
        for (int link = 0; link < _linkA.Length; link++)
        {
            _partner[next[_linkA[link]]++] = _linkB[link];
            _partner[next[_linkB[link]]++] = _linkA[link];
        }

        _built = true;
    }

    /// <summary>
    /// Whether the settlement in a cell trades with a standing settlement in a cell for which
    /// the test holds (as <see cref="LinksByCell"/> would show).
    /// </summary>
    public bool TradesWith(int settlement, Func<int, bool> partnerCell)
    {
        if (settlement < 0 || settlement + 1 >= _partnerStart.Length || !_settlements!.IsAlive(settlement))
        {
            return false;
        }

        for (int k = _partnerStart[settlement]; k < _partnerStart[settlement + 1]; k++)
        {
            int other = _partner[k];
            if (_settlements.IsAlive(other) && partnerCell(_settlementCell![other]))
            {
                return true;
            }
        }

        return false;
    }

    // The settlements reachable from a cell within a cost limit, over land and across runs of at
    // most seaRange sea cells (each costing seaCost), with their costs and whether the cheapest way
    // crosses the sea.
    private void Reach(int start, List<(double Cost, int Id, bool Sea)> reached, double maxCost, int seaRange, double seaCost)
    {
        reached.Clear();
        WorldMap map = _map!;
        _stamp++;
        PriorityQueue<int, double> queue = _queue;
        queue.Clear();
        _best[start] = 0.0;
        _seen[start] = _stamp;
        _viaSea[start] = false;
        queue.Enqueue(start, 0.0);
        while (queue.TryDequeue(out int cell, out double cost))
        {
            if (cost > _best[cell])
            {
                continue;
            }

            bool sea = _viaSea[cell];
            if (cell != start && _linkedPlusOne[cell] > 0)
            {
                reached.Add((cost, _linkedPlusOne[cell] - 1, sea));
            }

            if (seaRange > 0 && _shore[cell] && _crossings != null)
            {
                for (int k = _crossings.Start[cell]; k < _crossings.Start[cell + 1]; k++)
                {
                    int steps = _crossings.Steps[k];
                    if (steps > seaRange)
                    {
                        continue;
                    }

                    int next = _crossings.Target[k];
                    double total = cost + steps * seaCost;
                    if (total > maxCost || (_seen[next] == _stamp && total >= _best[next]))
                    {
                        continue;
                    }

                    _seen[next] = _stamp;
                    _best[next] = total;
                    _viaSea[next] = true;
                    queue.Enqueue(next, total);
                }
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
                _viaSea[next] = sea;
                queue.Enqueue(next, total);
            }
        }
    }

    /// <summary>Every link between standing settlements, by their ids, with the food value it carries a season on average.</summary>
    /// <summary>
    /// Every link between standing settlements, by their cells. Links are derived from the world
    /// state and rebuilt identically after a load (route values are not, so they are left out); the
    /// technology system reads this to let knowledge travel along trade.
    /// </summary>
    public List<(int CellA, int CellB)> LinksByCell()
    {
        var links = new List<(int, int)>(_linkA.Length);
        for (int link = 0; link < _linkA.Length; link++)
        {
            if (_settlements!.IsAlive(_linkA[link]) && _settlements.IsAlive(_linkB[link]))
            {
                links.Add((_settlementCell![_linkA[link]], _settlementCell[_linkB[link]]));
            }
        }

        return links;
    }

    /// <summary>Links between standing settlements whose cheapest way crosses the sea, by their cells.</summary>
    public List<(int CellA, int CellB)> SeaLinksByCell()
    {
        var links = new List<(int, int)>();
        for (int link = 0; link < _linkA.Length; link++)
        {
            if (_linkSea[link] && _settlements!.IsAlive(_linkA[link]) && _settlements.IsAlive(_linkB[link]))
            {
                links.Add((_settlementCell![_linkA[link]], _settlementCell[_linkB[link]]));
            }
        }

        return links;
    }

    public List<(int A, int B, double Value)> Links()
    {
        var links = new List<(int, int, double)>(_linkA.Length);
        for (int link = 0; link < _linkA.Length; link++)
        {
            if (_settlements!.IsAlive(_linkA[link]) && _settlements.IsAlive(_linkB[link]))
            {
                links.Add((_linkA[link], _linkB[link], _linkValue[link]));
            }
        }

        return links;
    }

    /// <summary>Links that carry at least the route value a season on average, by their settlements' cells.</summary>
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

    /// <summary>
    /// Links that carry at least a tenth of the route value of one good a season on average, by their
    /// settlements' cells, with the food value of that good they carry.
    /// </summary>
    public List<TradeRoute> Routes(int good)
    {
        var routes = new List<TradeRoute>();
        int goods = _goods.Count;
        double least = 0.1 * _parameters.RouteValue;
        for (int link = 0; link < _linkA.Length; link++)
        {
            double value = GoodValue(link * goods + good);
            if (value >= least && _settlements!.IsAlive(_linkA[link]) && _settlements.IsAlive(_linkB[link]))
            {
                routes.Add(new TradeRoute(_settlementCell![_linkA[link]], _settlementCell[_linkB[link]], value));
            }
        }

        return routes;
    }

    // A link-good's average value as of the latest season traded (the next to come is _season).
    private double GoodValue(int slot)
    {
        int age = _season - _goodSeason[slot];
        return age <= 0 ? _goodValue[slot] : age < Decay.Length ? _goodValue[slot] * Decay[age] : 0.0;
    }

    public int LinkCount => _linkA.Length;

    /// <summary>Wall-clock seconds spent building links and working out prices (for profiling only).</summary>
    public double BuildSeconds { get; private set; }

    public double PriceSeconds { get; private set; }

    public double LoopSeconds { get; private set; }

    public double GapSeconds { get; private set; }

    /// <summary>
    /// When set (by the exit tests), each January's price gaps along the links that traded are
    /// measured before and after the trade (<see cref="GapBeforeTrade"/>). Off by default: it
    /// works out every settlement's prices a second time each year, for no effect on the run.
    /// </summary>
    public bool MeasureGaps { get; set; }

    /// <summary>How many sends trade has made (for profiling).</summary>
    public long Sends { get; private set; }

    public long LinkGoodVisits { get; private set; }

    /// <summary>For each good, the run's summed relative price gap, before January's trade, along the links that carried it then.</summary>
    public IReadOnlyList<double> GapBeforeTrade => _gapJanuary;

    /// <summary>For each good, the same gaps straight after January's trade, counting goods on their way as arrived.</summary>
    public IReadOnlyList<double> GapAfterTrade => _gapFebruary;

    /// <summary>For each good, how much was sent in the Januaries whose gaps are measured (since this object was made).</summary>
    public IReadOnlyList<double> SentInJanuary => _sentJanuary;
}
